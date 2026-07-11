using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Economy
{
    // Last save id each player reported, so a change (new colony/scenario) can trigger the anti-exploit economy reset.
    // Persisted to KMH-Data/Players/SaveIds.json.
    internal static class EconomyResetStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _lastSaveId
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private sealed class State
        {
            public Dictionary<string, string> SaveIds { get; set; }
                = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.SaveIdsFile, out State s) || s?.SaveIds == null) return;
            lock (_lock) _lastSaveId = new Dictionary<string, string>(s.SaveIds, StringComparer.OrdinalIgnoreCase);
        }

        public static void SaveToDisk()
        {
            State s = new State();
            lock (_lock) foreach (KeyValuePair<string, string> kv in _lastSaveId) s.SaveIds[kv.Key] = kv.Value;
            JsonFileStore.Save(KmhDataPaths.SaveIdsFile, s);
        }

        // Record the player's current save id. Returns true only when it changed from a known previous id (a real
        // reset); a first-time or unchanged id returns false. Empty ids are ignored.
        public static bool RecordAndDetectReset(string username, string saveId)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(saveId)) return false;
            bool changed;
            lock (_lock)
            {
                _lastSaveId.TryGetValue(username, out string prev);
                changed = !string.IsNullOrEmpty(prev) && !string.Equals(prev, saveId, StringComparison.Ordinal);
                _lastSaveId[username] = saveId;
            }
            SaveToDisk();
            return changed;
        }
    }
}
