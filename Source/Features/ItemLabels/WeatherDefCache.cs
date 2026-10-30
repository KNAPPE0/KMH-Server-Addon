using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.ItemLabels
{
    internal static class WeatherDefCache
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _defs
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal const int MaxDefs = 2_000;

        public static void Apply(Dictionary<string, string> incoming)
        {
            if (incoming == null || incoming.Count == 0) return;
            int added = 0;
            lock (_lock)
            {
                // First-seen, bounded like the item labels: these feed the weather pool every client sees.
                foreach (KeyValuePair<string, string> kv in incoming)
                {
                    if (!Util.KmhSafe.CatalogEntryFits(kv.Key, kv.Value) || _defs.ContainsKey(kv.Key)) continue;
                    if (_defs.Count >= MaxDefs) break;
                    _defs[kv.Key] = kv.Value; added++;
                }
            }
            if (added > 0)
            {
                SaveToDisk();
                ServerLog.Verbose($"WeatherDefs: cache updated ({added} new/changed, {Count} total)");
            }
        }

        public static List<KeyValuePair<string, string>> All()
        {
            lock (_lock) return new List<KeyValuePair<string, string>>(_defs);
        }

        public static int Count { get { lock (_lock) return _defs.Count; } }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.WeatherDefsFile, out Dictionary<string, string> loaded) && loaded != null)
            {
                lock (_lock) _defs = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                ServerLog.Diag($"WeatherDefs: loaded {loaded.Count} condition def(s) from disk");
            }
        }

        private static void SaveToDisk()
        {
            Dictionary<string, string> copy;
            lock (_lock) copy = new Dictionary<string, string>(_defs, StringComparer.OrdinalIgnoreCase);
            JsonFileStore.Save(KmhDataPaths.WeatherDefsFile, copy);
        }
    }
}
