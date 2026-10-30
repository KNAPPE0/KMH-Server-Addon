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

        public static bool TryLoad<T>(string path, out T value) where T : class => TryLoad(path, null, out value);

        // Keys that moved between releases: each old key is read into its new place on every load, the old value
        // winning over a default backfilled there, so an owner's setting survives no matter which write comes first.
        public static bool TryLoad<T>(string path, IReadOnlyList<(string From, string To)> moves, out T value) where T : class
        {
            value = null;
            if (string.IsNullOrEmpty(path))      return false;
            if (!File.Exists(path))              return false;

            // A file that cannot be read may be perfectly healthy, so it is never moved aside or written over.
            string json;
            try { json = ReadWithRetry(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Unquarantined[path] = 0;
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} could not be READ ({ex.GetType().Name}: {ex.Message}). "
                              + "It was left in place and saving to it is blocked - close whatever holds it, then restart.");
                NoteDamage(path, null, $"it could not be read ({ex.GetType().Name})");
                return false;
            }

            try
            {
                if (moves != null && moves.Count > 0 && JToken.Parse(json) is JObject doc && ApplyMoves(doc, moves) > 0)
                    json = doc.ToString(Formatting.None);
                value = JsonConvert.DeserializeObject<T>(json, Settings);
                if (value != null) return true;

                // Newtonsoft returns null for 0 bytes or "null" WITHOUT throwing, which would read as "not created yet".
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} exists but loaded as EMPTY ({size} bytes). " +
                                "Backing it up and starting this file from defaults - if it held live data, restore " +
                                "it from KMH-Data-Backups before players reconnect.");
                NoteDamage(path, TryBackupCorrupt(path), $"it loaded as empty ({size} bytes)");
                return false;
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} is CORRUPT and could not be parsed ({ex.Message}). " +
                                "Backing it up and starting this file from defaults - your other data is unaffected.");
                NoteDamage(path, TryBackupCorrupt(path), ex.Message);
                return false;
            }
        }

        // From/To are dotted paths.
        internal static int ApplyMoves(JObject doc, IReadOnlyList<(string From, string To)> moves)
        {
            int moved = 0;
            foreach ((string from, string to) in moves)
            {
                JToken old = doc.SelectToken(from);
                if (old == null || !(old.Parent is JProperty prop)) continue;
                string[] parts = to.Split('.');
                JObject parent = doc;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!(parent[parts[i]] is JObject child)) { child = new JObject(); parent[parts[i]] = child; }
                    parent = child;
                }
                parent[parts[parts.Length - 1]] = old.DeepClone();
                prop.Remove();
                moved++;
            }
            return moved;
        }

        // A sharing violation from a backup tool or AV scan usually clears in moments.
        private static string ReadWithRetry(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (ReadFailureForTest?.Invoke(path) == true) throw new IOException("selftest: simulated sharing violation");
                    return File.ReadAllText(path);
                }
                catch (IOException) when (attempt < ReadAttempts && !(ReadFailureForTest?.Invoke(path) ?? false))
                { System.Threading.Thread.Sleep(250); }
            }
        }

        private const int ReadAttempts = 3;

        // True makes a read of that path fail as a held file would, without holding it, and skips the retry wait.
        internal static Func<string, bool> ReadFailureForTest;

        // An irreplaceable store that came up as defaults must not be played on as merely empty, and the next boot would
        // find a clean empty file; so the damage is recorded, and KMH stays non-operational while this file exists.
        internal static string DamageMarkerPath => DamageMarkerOverrideForTest ?? Path.Combine(KmhDataPaths.Folder, "DAMAGED.txt");

        // The self-test can run on a live server, so it points both of these at files of its own.
        internal static string DamageMarkerOverrideForTest;
        internal static Func<string, bool> IrreplaceableOverrideForTest;

        internal static bool DamageReported(out string why)
        {
            why = null;
            try { if (!File.Exists(DamageMarkerPath)) return false; } catch { return false; }
            why = "KMH-Data is damaged - see " + DamageMarkerPath + ". Restore the listed file(s) from KMH-Data-Backups, "
                + "then delete DAMAGED.txt and restart";
            return true;
        }

        // Null for a file that rebuilds itself, or one KMH does not track.
        internal static string IrreplaceableLabel(string path)
        {
            foreach (KmhDataPaths.DataFile f in KmhDataPaths.KnownDataFiles)
                if (!f.Regenerable && string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    return f.Label;
            return null;
        }

        private static void NoteDamage(string path, string movedTo, string what)
        {
            string label = IrreplaceableOverrideForTest != null
                ? (IrreplaceableOverrideForTest(path) ? "selftest store" : null)
                : IrreplaceableLabel(path);
            if (label == null) return;
            try
            {
                if (!File.Exists(DamageMarkerPath))
                    File.WriteAllText(DamageMarkerPath,
                        "Colony Nexus will not accept player value moves while this file exists.\r\n"
                      + "Restore each file below from KMH-Data-Backups (the newest backup from before the time shown),\r\n"
                      + "then delete this file and restart the server.\r\n\r\n");
                File.AppendAllText(DamageMarkerPath, $"{DateTime.UtcNow:O}  {label} ({Path.GetFileName(path)}): {what}. "
                                                   + $"The unreadable copy is {(movedTo == null ? "still in place" : Path.GetFileName(movedTo))}.\r\n");
            }
            catch (Exception ex) { ServerLog.Error($"Persistence: could not write {DamageMarkerPath}: {ex.Message}"); }
        }

        // Corrupt bytes are moved aside, never discarded: they are the only copy of whatever the file held.
        private static string TryBackupCorrupt(string path)
        {
            try
            {
                string backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, backup);
                ServerLog.Warn($"Persistence: moved corrupt file aside to {Path.GetFileName(backup)} - recover values from it if needed.");
                return backup;
            }
            catch (Exception ex)
            {
                Unquarantined[path] = 0;
                ServerLog.Error($"Persistence: could not back up corrupt {Path.GetFileName(path)}: {ex.Message}. Saving to it is " +
                                "BLOCKED so the only copy survives - move the file aside by hand and saving resumes on its own.");
                return null;
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

        // Unordered, for low-churn files; strict snapshot ordering passes a ticket captured under the store lock.
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
                    NoteSaveFailed(path, "the file could not be read or moved aside at load - refusing to overwrite the only copy");
                    return false;
                }
                Unquarantined.TryRemove(path, out _);
                ServerLog.Info($"Persistence: {Path.GetFileName(path)} was moved aside - saving is allowed again.");
            }

            // Serialized outside the per-path lock: it is the slow part and touches no file. A store hands over a deep
            // copy taken under its own lock (KmhDeepCopy), so nothing changes underneath the serializer.
            StoreStats stats = StatsFor(path);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            string json;
            try { json = JsonConvert.SerializeObject(value, Settings); }
            // Counted like a failed write, or nothing retries the store and nothing clears a freeze it caused.
            catch (InvalidOperationException ex)
            {
                // A collection changed mid-serialize: that store passed a live object instead of its snapshot.
                System.Threading.Interlocked.Increment(ref _liveObjectRaces);
                System.Threading.Interlocked.Increment(ref stats.Failures);
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} changed while being written ({ex.Message}) - that store must save a snapshot. This save was skipped.");
                NoteSaveFailed(path, "it changed while being written");
                return false;
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref stats.Failures);
                ServerLog.Warn($"Persistence: serialize failed for {Path.GetFileName(path)}: {ex.Message}");
                NoteSaveFailed(path, "it could not be serialized");
                return false;
            }
            long serialized = System.Diagnostics.Stopwatch.GetTimestamp();
            stats.NoteSerialize(serialized - started, json.Length);

            object gate = SaveGates.GetOrAdd(path, _ => new object());
            string failure = null;
            lock (gate)
            {
                // A newer snapshot already persisted; letting this older one through would undo it.
                if (LastWrittenSeq.TryGetValue(path, out long last) && sequence <= last)
                {
                    System.Threading.Interlocked.Increment(ref stats.Superseded);
                    return true;
                }
                try
                {
                    // Injected full-disk failure: every value path must survive one, and faking it beats a full disk.
                    string injected = _failWrites?.Invoke(path);
                    if (injected != null) throw new IOException(injected);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    // On disk before the rename: a rename is atomic, but an OS crash can make it point at bytes the
                    // disk never received, and the file comes back as zeros.
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(flushToDisk: true);
                    }
                    // An OS-level rename, so a crash mid-save never leaves the canonical file half-written or absent.
                    File.Move(tmp, path, overwrite: true);
                    LastWrittenSeq[path] = sequence;
                }
                catch (Exception ex) { failure = ex.Message; }
            }

            // Outside the per-path lock, so this never takes a second lock while holding one.
            if (failure == null)
            {
                stats.NoteWrite(System.Diagnostics.Stopwatch.GetTimestamp() - serialized);
                NoteSaveOk(path);
                return true;
            }
            System.Threading.Interlocked.Increment(ref stats.Failures);
            NoteSaveFailed(path, failure);
            return false;
        }

        private static long _liveObjectRaces;
        // Saves that failed because a store handed over a live object; must stay 0.
        public static long LiveObjectRaces => System.Threading.Interlocked.Read(ref _liveObjectRaces);

        internal sealed class StoreStats
        {
            public string File = "";
            public long Saves, Bytes, MaxBytes, SerializeTicks, MaxSerializeTicks, WriteTicks, MaxWriteTicks, Failures, Superseded, Coalesced;

            public void NoteSerialize(long ticks, long chars)
            {
                System.Threading.Interlocked.Increment(ref Saves);
                System.Threading.Interlocked.Add(ref Bytes, chars);
                System.Threading.Interlocked.Add(ref SerializeTicks, ticks);
                Max(ref MaxBytes, chars);
                Max(ref MaxSerializeTicks, ticks);
            }

            public void NoteWrite(long ticks)
            {
                System.Threading.Interlocked.Add(ref WriteTicks, ticks);
                Max(ref MaxWriteTicks, ticks);
            }

            private static void Max(ref long field, long value)
            {
                long seen;
                while (value > (seen = System.Threading.Interlocked.Read(ref field))
                       && System.Threading.Interlocked.CompareExchange(ref field, value, seen) != seen) { }
            }
        }

        private static readonly ConcurrentDictionary<string, StoreStats> Stats
            = new ConcurrentDictionary<string, StoreStats>(StringComparer.OrdinalIgnoreCase);

        internal static StoreStats StatsFor(string path)
            => Stats.GetOrAdd(Path.GetFileName(path) ?? path, name => new StoreStats { File = name });

        // A caller that folds several changes into one write says so, so the count shows what coalescing saved.
        internal static void NoteCoalesced(string path, long requests)
        {
            if (requests > 0) System.Threading.Interlocked.Add(ref StatsFor(path).Coalesced, requests);
        }

        internal static List<StoreStats> StatsSnapshot()
        {
            List<StoreStats> all = new List<StoreStats>(Stats.Values);
            all.Sort((a, b) => (b.SerializeTicks + b.WriteTicks).CompareTo(a.SerializeTicks + a.WriteTicks));
            return all;
        }

        // One dropped save is survivable; a file that keeps failing loses every later change while memory looks fine.
        private const int DegradedAfterConsecutiveFailures = 3;

        private static readonly ConcurrentDictionary<string, int> SaveFailures
            = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static bool AnyStoreUnwritable => !SaveFailures.IsEmpty && Degraded();

        // Retry on the FIRST failure: the freeze waits for repeats, but a memory-only change is already at risk.
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
                    // An underscore key ("_readme") is an owner note on no DTO; pruning it deletes the shipped instructions.
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
