using System;
using System.Collections.Concurrent;
using System.IO;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;

namespace KMHServerAddon.Persistence
{
    // Generic JSON-file persistence helper. Used by every feature's store
    // for SaveToDisk / LoadFromDisk.
    //
    // Atomic write pattern: serialize to a sibling .tmp file, then rename over the target with File.Move(overwrite:
    // true). On a single volume that maps to an OS-level replace (rename / MoveFileEx) - there is no window where
    // the canonical file is absent, so a crash mid-save can never leave it half-written OR missing
    //
    // Concurrency: RWT runs one listener thread per client, so two different clients' actions can both trigger a
    // save of the SAME file at once (e.g. two buyers each completing a trade both write marketplace.json). A
    // per-path lock serializes writes to a given file so the shared .tmp can't be interleaved into corruption.
    // Distinct files never contend
    //
    // Reads are tolerant - missing file = "fresh state, no load needed", malformed JSON = "log + return default".
    // Either case the caller falls back to its in-memory empty default. We never throw out to the calling store's
    // bootstrap path
    internal static class JsonFileStore
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting        = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
        };

        // One gate object per canonical path. Keyed case-insensitively so the same file referenced via different
        // casing still serializes
        private static readonly ConcurrentDictionary<string, object> SaveGates
            = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

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
                ServerLog.Warn($"Persistence: load failed for {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        public static bool Save<T>(string path, T value) where T : class
        {
            if (string.IsNullOrEmpty(path) || value == null) return false;

            // Serialize OUTSIDE the per-path lock - JSON serialization is the slow part and doesn't touch the file,
            // so we don't hold the gate across it. A bad value that throws here never reaches the file
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
                try
                {
                    // Ensure this file's own domain subfolder exists (KMH-Data/ Guilds/, KMH-Data/Config/, etc.)
                    // before writing
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);

                    // Atomic replace: File.Move(overwrite) maps to an OS-level rename on the same volume - no
                    // delete-gap, so the canonical file is never momentarily absent
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Persistence: save failed for {Path.GetFileName(path)}: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
