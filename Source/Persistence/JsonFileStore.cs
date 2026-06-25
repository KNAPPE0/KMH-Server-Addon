using System;
using System.Collections.Concurrent;
using System.IO;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;

namespace KMHServerAddon.Persistence
{
    // Generic JSON persistence for every feature store. Atomic write: serialize to a .tmp sibling, then
    // File.Move(overwrite) - an OS-level rename, so a crash mid-save never leaves the file half-written or missing.
    // A per-path lock serializes concurrent saves of the same file (RWT runs one thread per client). Reads are
    // tolerant: a missing file or bad JSON returns default so the store falls back to its empty in-memory state.
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

        // Save-order guard: monotonic tickets stop older snapshots from overwriting newer saves.
        private static long _saveSequence;
        private static readonly ConcurrentDictionary<string, long> LastWrittenSeq
            = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Monotonic save ticket - capture under the store's lock at snapshot time. Global, since only per-path order matters.
        public static long NextSequence() => System.Threading.Interlocked.Increment(ref _saveSequence);

        public static bool TryLoad<T>(string path, out T value) where T : class
        {
            value = null;
            if (string.IsNullOrEmpty(path))      return false;
            if (!File.Exists(path))              return false;

            try
            {
                string json = File.ReadAllText(path);
                value = JsonConvert.DeserializeObject<T>(json, Settings);
                return value != null;
            }
            catch (Exception ex)
            {
                // Corrupt JSON: move it aside (keeping it for recovery) so the next save can't overwrite it, log
                // loudly, and return default so this one store starts clean and regenerates on its next save.
                ServerLog.Error($"Persistence: {Path.GetFileName(path)} is CORRUPT and could not be parsed ({ex.Message}). " +
                                "Backing it up and starting this file from defaults - your other data is unaffected.");
                TryBackupCorrupt(path);
                return false;
            }
        }

        // Rename a corrupt file aside (kept for recovery) so the canonical path regenerates and we don't re-hit it each boot.
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
                ServerLog.Warn($"Persistence: could not back up corrupt {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        // Unordered save for low-churn files; strict snapshot ordering should pass a ticket captured under the store lock.
        public static bool Save<T>(string path, T value) where T : class
            => Save(path, value, NextSequence());

        public static bool Save<T>(string path, T value, long sequence) where T : class
        {
            if (string.IsNullOrEmpty(path) || value == null) return false;

            // Serialize outside the per-path lock - it's the slow part and doesn't touch the file.
            string json;
            try { json = JsonConvert.SerializeObject(value, Settings); }
            catch (Exception ex)
            {
                ServerLog.Warn($"Persistence: serialize failed for {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }

            object gate = SaveGates.GetOrAdd(path, _ => new object());
            lock (gate)
            {
                // Ordering guard: a newer snapshot already persisted, so don't let this older one clobber it.
                if (LastWrittenSeq.TryGetValue(path, out long last) && sequence <= last) return true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    // Atomic replace via OS-level rename - the canonical file is never momentarily absent.
                    File.Move(tmp, path, overwrite: true);
                    LastWrittenSeq[path] = sequence;
                    return true;
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Persistence: save failed for {Path.GetFileName(path)}: {ex.Message}");
                    return false;
                }
            }
        }

        private sealed class WriteProbe { public long Stamp { get; set; } public string Token { get; set; } = ""; }

        // Boot storage probe: save, read, and delete through the real path, returning false with a reason if writes fail.
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
