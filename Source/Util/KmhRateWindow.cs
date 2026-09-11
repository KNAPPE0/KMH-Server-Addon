using System;
using System.Collections.Generic;

namespace KMHServerAddon.Util
{
    // Not self-synchronizing: callers hold their own lock, matching how the stores already serialize their state.
    internal sealed class KmhRateWindow
    {
        private readonly Dictionary<string, List<long>> _stamps = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        // Records the action as a side effect, so a caller that ignores the result still consumes a slot.
        public bool Allow(string user, long nowTicks, int maxPerWindow, int windowSeconds)
        {
            if (string.IsNullOrEmpty(user)) return true;
            if (maxPerWindow <= 0) return false;

            long windowStart = nowTicks - TimeSpan.FromSeconds(Math.Max(1, windowSeconds)).Ticks;
            if (!_stamps.TryGetValue(user, out List<long> stamps)) { stamps = new List<long>(); _stamps[user] = stamps; }
            stamps.RemoveAll(t => t < windowStart);
            if (stamps.Count >= maxPerWindow) return false;
            stamps.Add(nowTicks);
            return true;
        }

        public void Clear() => _stamps.Clear();

        // Drops one key's history, so a caller can start it over without waiting out the window.
        public void Forget(string user) { if (!string.IsNullOrEmpty(user)) _stamps.Remove(user); }
    }
}
