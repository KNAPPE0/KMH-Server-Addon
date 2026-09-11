using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Persistence
{
    internal static class JsonFileStore
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting        = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
        };

        // One gate per canonical path, case-insensitive so different casings of the same file still serialize.
        private static readonly ConcurrentDictionary<string, object> SaveGates
            = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static long _saveSequence;
        private static readonly ConcurrentDictionary<string, long> LastWrittenSeq
            = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Capture under the store's own lock at snapshot time, or the ticket does not order what it is meant to.
        public static long NextSequence() => System.Threading.Interlocked.Increment(ref _saveSequence);

        // Exposed so a round-trip self-test exercises the real settings rather than a stand-in.
        public static string ToJson<T>(T value) where T : class => JsonConvert.SerializeObject(value, Settings);
        public static T FromJson<T>(string json) where T : class => JsonConvert.DeserializeObject<T>(json, Settings);

        public static bool TryLoad<T>(string path, out T value) where T : class
        {
            value = null;
            if (string.IsNullOrEmpty(path))      return false;
            if (!File.Exists(path))              return false;

            try
            {
                string json = File.ReadAllText(path);
                value = JsonConvert.DeserializeObject<T>(json, Settings);
                if (value != null) return true;

                // Newtonsoft returns null for 0 bytes or "null" WITHOUT throwing, which would read as "not created yet".
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} exists but loaded as EMPTY ({size} bytes). " +
                                "Backing it up and starting this file from defaults - if it held live data, restore " +
                                "it from KMH-Data-Backups before players reconnect.");
                TryBackupCorrupt(path);
                return false;
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} is CORRUPT and could not be parsed ({ex.Message}). " +
                                "Backing it up and starting this file from defaults - your other data is unaffected.");
                TryBackupCorrupt(path);
                return false;
            }
        }

        // Corrupt bytes are moved aside, never discarded: they are the only copy of whatever the file held.
        private static void TryBackupCorrupt(string path)
        {
            try
            {
                string backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, backup);
                ServerLog.Warn($"Persistence: moved corrupt file aside to {Path.GetFileName(backup)} - recover values from it if needed.");
            }
            catch (Exception ex)
            {
                Unquarantined[path] = 0;
                ServerLog.Error($"Persistence: could not back up corrupt {Path.GetFileName(path)}: {ex.Message}. Saving to it is " +
                                "BLOCKED so the only copy survives - move the file aside by hand and saving resumes on its own.");
            }
        }

        // Quarantine failed, so these still hold the only copy - Save refuses rather than overwrite them.
        private static readonly ConcurrentDictionary<string, byte> Unquarantined
            = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Returns a reason to fail the write for that path, or null to let it through. Test-only.
        private static Func<string, string> _failWrites;

        internal static IDisposable FailWritesForTest(Func<string, string> reason)
        {
            _failWrites = reason;
            return new Restore();
        }

        private sealed class Restore : IDisposable { public void Dispose() { _failWrites = null; ClearFailureStateForTest(); } }

        // The degraded gate is global, so an injected failure has to leave no trace behind for the next suite.
        internal static void ClearFailureStateForTest()
        {
            SaveFailures.Clear();
            Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(false);
        }

        // Unordered save for low-churn files; strict snapshot ordering should pass a ticket captured under the store lock.
        public static bool Save<T>(string path, T value) where T : class
            => Save(path, value, NextSequence());

        public static bool Save<T>(string path, T value, long sequence) where T : class
        {
            if (string.IsNullOrEmpty(path) || value == null) return false;

            // The refusal has to be able to end on its own, or a freeze outlives the disk problem that caused it.
            if (Unquarantined.ContainsKey(path))
            {
                if (File.Exists(path))
                {
                    NoteSaveFailed(path, "corrupt file could not be quarantined - refusing to overwrite the only copy");
                    return false;
                }
                Unquarantined.TryRemove(path, out _);
                ServerLog.Info($"Persistence: corrupt {Path.GetFileName(path)} was moved aside - saving is allowed again.");
            }

            // Serialized outside the per-path lock: it is the slow part and touches no file.
            string json = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { json = JsonConvert.SerializeObject(value, Settings); break; }
                // Stores hand over LIVE objects, so another thread can restructure a collection mid-serialize.
                catch (InvalidOperationException) { System.Threading.Thread.Sleep(0); }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Persistence: serialize failed for {Path.GetFileName(path)}: {ex.Message}");
                    return false;
                }
            }
            if (json == null)
            {
                ServerLog.Warn($"Persistence: serialize for {Path.GetFileName(path)} kept racing a concurrent change - skipped this save (the next mutation's save will capture it).");
                return false;
            }

            object gate = SaveGates.GetOrAdd(path, _ => new object());
            string failure = null;
            lock (gate)
            {
                // A newer snapshot already persisted; letting this older one through would undo it.
                if (LastWrittenSeq.TryGetValue(path, out long last) && sequence <= last) return true;
                try
                {
                    // A full disk is the failure every value path has to survive, and faking it on demand beats filling the real machine.
                    string injected = _failWrites?.Invoke(path);
                    if (injected != null) throw new IOException(injected);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    // An OS-level rename, so a crash mid-save never leaves the canonical file half-written or absent.
                    File.Move(tmp, path, overwrite: true);
                    LastWrittenSeq[path] = sequence;
                }
                catch (Exception ex) { failure = ex.Message; }
            }

            // Outside the per-path lock, so this never takes a second lock while holding one.
            if (failure == null) { NoteSaveOk(path); return true; }
            NoteSaveFailed(path, failure);
            return false;
        }

        // One dropped save is survivable; a file that keeps failing loses every later change while memory looks fine.
        private const int DegradedAfterConsecutiveFailures = 3;

        private static readonly ConcurrentDictionary<string, int> SaveFailures
            = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static bool AnyStoreUnwritable => !SaveFailures.IsEmpty && Degraded();

        // Retry on the FIRST failure, not the third: the freeze needs repetition to be sure, but a memory-only change is already at risk.
        public static bool AnyStoreUnsaved => !SaveFailures.IsEmpty;

        // One line a support report can quote instead of a reader inferring health from scattered retry warnings.
        public static string DescribeHealth()
        {
            int unwritable = 0, unsaved = 0;
            foreach (KeyValuePair<string, int> kv in SaveFailures)
            {
                unsaved++;
                if (kv.Value >= DegradedAfterConsecutiveFailures) unwritable++;
            }
            return unsaved == 0
                ? "Persistence: all stores written"
                : $"Persistence: {unwritable} unwritable, {unsaved} unsaved/retrying";
        }

        private static bool Degraded()
        {
            foreach (KeyValuePair<string, int> kv in SaveFailures)
                if (kv.Value >= DegradedAfterConsecutiveFailures) return true;
            return false;
        }

        private static void NoteSaveOk(string path)
        {
            if (SaveFailures.IsEmpty) return;
            if (!SaveFailures.TryRemove(path, out int failed)) return;
            ServerLog.Info($"Persistence: {Path.GetFileName(path)} saved again after {failed} failed attempt(s).");
            Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(Degraded());
        }

        // Repeats are counted rather than reprinted: a crash loop floods the same console this must be noticed in.
        private static void NoteSaveFailed(string path, string reason)
        {
            string name = Path.GetFileName(path);
            int n = SaveFailures.AddOrUpdate(path, 1, (_, prev) => prev + 1);

            if (n == 1)
                ServerLog.Error($"Persistence: save FAILED for {name} ({reason}). That change exists only in memory and "
                              + "is lost on restart - check free disk space and permissions on KMH-Data.");
            else if (n == DegradedAfterConsecutiveFailures || n % 50 == 0)
                ServerLog.Error($"Persistence: {name} has failed {n} save(s) in a row ({reason}).");

            bool degraded = Degraded();
            Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(degraded);
            if (n == DegradedAfterConsecutiveFailures && degraded
                && Maintenance.MaintenanceConfig.Current.FreezeEconomyOnPersistenceFailure)
                ServerLog.Error("Persistence: refusing value-moving requests until KMH-Data is writable again - a "
                              + "deposit that cannot be saved would vanish on restart. Set "
                              + "FreezeEconomyOnPersistenceFailure=false in Config/Maintenance.json to allow them anyway.");
        }

        public static bool FileHasKey(string path, string key)
        {
            try { return File.Exists(path) && JObject.Parse(File.ReadAllText(path))[key] != null; }
            catch { return false; }
        }

        // Adds only what the file lacks, so every owner-set value survives an upgrade.
        public static int BackfillMissingFields(string path, object defaults)
        {
            if (string.IsNullOrEmpty(path) || defaults == null || !File.Exists(path)) return 0;
            try
            {
                JObject existing = JObject.Parse(File.ReadAllText(path));
                JObject def = JObject.FromObject(defaults, JsonSerializer.Create(Settings));
                List<string> added = new List<string>();
                if (AddMissing(existing, def, "", added))
                {
                    Save(path, existing);
                    ServerLog.Info($"Persistence: added new settings to {Path.GetFileName(path)} (kept your existing values): {string.Join(", ", added)}");
                }
                return added.Count;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Persistence: could not update {Path.GetFileName(path)} with new settings: {ex.Message}");
                return 0;
            }
        }

        private static bool AddMissing(JObject existing, JObject defaults, string prefix, List<string> added)
        {
            bool changed = false;
            foreach (JProperty p in defaults.Properties())
            {
                JToken cur = existing[p.Name];
                if (cur == null)
                {
                    existing[p.Name] = p.Value.DeepClone();
                    added.Add(prefix + p.Name);
                    changed = true;
                }
                else if (cur.Type == JTokenType.Object && p.Value.Type == JTokenType.Object)
                    changed |= AddMissing((JObject)cur, (JObject)p.Value, prefix + p.Name + ".", added);
            }
            return changed;
        }

        // Pruning an alias discards the owner's value before the migration step that reads it runs.
        public static int PruneUnknownFields(string path, object defaults, ICollection<string> aliases = null)
        {
            if (string.IsNullOrEmpty(path) || defaults == null || !File.Exists(path)) return 0;
            try
            {
                string pruned = PruneUnknownJson(File.ReadAllText(path), defaults, aliases, out List<string> removed);
                if (removed.Count > 0)
                {
                    Save(path, JObject.Parse(pruned));
                    ServerLog.Info($"Persistence: removed obsolete settings from {Path.GetFileName(path)} (backed up on boot): {string.Join(", ", removed)}");
                }
                return removed.Count;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Persistence: could not prune obsolete settings from {Path.GetFileName(path)}: {ex.Message}");
                return 0;
            }
        }

        public static string PruneUnknownJson(string json, object defaults, ICollection<string> aliases, out List<string> removed)
        {
            removed = new List<string>();
            JObject existing = JObject.Parse(json);
            JObject schema   = JObject.FromObject(defaults, JsonSerializer.Create(Settings));
            PruneUnknown(existing, schema, "", aliases, removed);
            return existing.ToString();
        }

        // Recurses only where the schema also nests, or a config's free-form dictionary keys read as obsolete settings.
        private static bool PruneUnknown(JObject existing, JObject schema, string prefix, ICollection<string> aliases, List<string> removed)
        {
            bool changed = false;
            foreach (JProperty p in new List<JProperty>(existing.Properties()))
            {
                JToken schemaVal = schema[p.Name];
                if (schemaVal == null)
                {
                    if (aliases != null && aliases.Contains(p.Name)) continue;
                    // An underscore key ("_readme") is an owner-facing note on no DTO - pruning once deleted the shipped instructions on first boot.
                    if (p.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                    existing.Remove(p.Name);
                    removed.Add(prefix + p.Name);
                    changed = true;
                }
                else if (p.Value.Type == JTokenType.Object && schemaVal.Type == JTokenType.Object)
                    changed |= PruneUnknown((JObject)p.Value, (JObject)schemaVal, prefix + p.Name + ".", aliases, removed);
            }
            return changed;
        }

        private sealed class WriteProbe { public long Stamp { get; set; } public string Token { get; set; } = ""; }

        public static bool SelfTest(out string detail)
        {
            string path = Path.Combine(KmhDataPaths.Folder, ".kmh-selftest.json");
            try
            {
                WriteProbe sample = new WriteProbe { Stamp = DateTime.UtcNow.Ticks, Token = Guid.NewGuid().ToString("N") };
                if (!Save(path, sample))
                { detail = "could not write a probe file (folder not writable?)"; return false; }
                if (!TryLoad(path, out WriteProbe loaded) || loaded == null || loaded.Token != sample.Token)
                { detail = "wrote a probe but read-back did not match (JSON round-trip failed)"; return false; }
                try { File.Delete(path); } catch { /* leftover probe is harmless */ }
                detail = $"KMH-Data writable, JSON round-trip OK ({KmhDataPaths.Folder})";
                return true;
            }
            catch (Exception ex)
            {
                try { File.Delete(path); } catch { }
                detail = ex.Message;
                return false;
            }
        }
    }
}
