using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Economy
{
    // A changed save id is what tells the server a player started over, which the economy reset keys on.
    internal static class EconomyResetStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _lastSaveId
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, long> _lastResetUtc
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // A modified client can claim a fresh save at will, so the id is only evidence - this is the rate the server acts on it.
        public const int MinHoursBetweenResets = 6;

        private sealed class State
        {
            public Dictionary<string, string> SaveIds { get; set; }
                = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> LastResetUtc { get; set; }
                = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.SaveIdsFile, out State s) || s == null) return;
            lock (_lock)
            {
                if (s.SaveIds != null) _lastSaveId = new Dictionary<string, string>(s.SaveIds, StringComparer.OrdinalIgnoreCase);
                if (s.LastResetUtc != null) _lastResetUtc = new Dictionary<string, long>(s.LastResetUtc, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static bool SaveToDisk()
        {
            State s = new State();
            lock (_lock)
            {
                foreach (KeyValuePair<string, string> kv in _lastSaveId) s.SaveIds[kv.Key] = kv.Value;
                foreach (KeyValuePair<string, long> kv in _lastResetUtc) s.LastResetUtc[kv.Key] = kv.Value;
            }
            return JsonFileStore.Save(KmhDataPaths.SaveIdsFile, s);
        }

        // The stored id is NOT advanced here - only when the reset completes, or a crash mid-reset strands the work unfinished.
        public static bool ShouldReset(string username, string saveId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(saveId)) { reason = "no save id"; return false; }

            bool firstSighting = false;
            long lastReset;
            lock (_lock)
            {
                _lastSaveId.TryGetValue(username, out string prev);
                if (string.IsNullOrEmpty(prev)) { _lastSaveId[username] = saveId; firstSighting = true; }
                else if (string.Equals(prev, saveId, StringComparison.Ordinal)) { reason = "same save"; return false; }
                _lastResetUtc.TryGetValue(username, out lastReset);
            }
            if (firstSighting) { SaveToDisk(); reason = "first sighting"; return false; }

            long since = DateTime.UtcNow.Ticks - lastReset;
            if (lastReset > 0 && since < TimeSpan.FromHours(MinHoursBetweenResets).Ticks)
            {
                reason = $"a reset for '{username}' already ran {new TimeSpan(since).TotalHours:0.#}h ago";
                return false;
            }
            if (KmhEconomyReset.InProgress) { reason = "a reset is already running"; return false; }
            return true;
        }

        // Called by the reset operation once its destructive work is done, so the id and the state agree.
        internal static void ConfirmReset(string username, string saveId)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(saveId)) _lastSaveId[username] = saveId;
                _lastResetUtc[username] = DateTime.UtcNow.Ticks;
            }
            SaveToDisk();
        }

        // With no history to compare against, the next save id is a first sighting rather than a change that wipes them again.
        internal static void Forget(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock) { _lastSaveId.Remove(username); _lastResetUtc.Remove(username); }
        }
    }
}
