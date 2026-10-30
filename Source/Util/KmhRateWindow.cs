using System;
using System.Collections.Generic;

namespace KMHServerAddon.Util
{
    // Not self-synchronizing: callers hold their own lock, matching how the stores already serialize their state.
    internal sealed class KmhRateWindow
    {
        private readonly Dictionary<string, List<long>> _stamps = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        // Keys idle past the longest window seen are dropped now and then, or one entry per name ever seen lives for ever.
        private const int SweepEvery = 512;
        private int _calls;
        private long _longestWindowTicks;

        internal int Tracked => _stamps.Count;

        // Records the action as a side effect, so a caller that ignores the result still consumes a slot.
        public bool Allow(string user, long nowTicks, int maxPerWindow, int windowSeconds)
        {
            if (string.IsNullOrEmpty(user)) return true;
            if (maxPerWindow <= 0) return false;

            long windowTicks = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)).Ticks;
            if (windowTicks > _longestWindowTicks) _longestWindowTicks = windowTicks;
            if (++_calls >= SweepEvery) Sweep(nowTicks);
            long windowStart = nowTicks - windowTicks;
            if (!_stamps.TryGetValue(user, out List<long> stamps)) { stamps = new List<long>(); _stamps[user] = stamps; }
            stamps.RemoveAll(t => t < windowStart);
            if (stamps.Count >= maxPerWindow) return false;
            stamps.Add(nowTicks);
            return true;
        }

        private void Sweep(long nowTicks)
        {
            _calls = 0;
            long cutoff = nowTicks - _longestWindowTicks;
            List<string> idle = null;
            foreach (KeyValuePair<string, List<long>> kv in _stamps)
                if (kv.Value.Count == 0 || kv.Value[kv.Value.Count - 1] < cutoff) (idle ??= new List<string>()).Add(kv.Key);
            if (idle != null) foreach (string k in idle) _stamps.Remove(k);
        }

        public void Clear() => _stamps.Clear();

        // Drops one key's history, so a caller can start it over without waiting out the window.
        public void Forget(string user) { if (!string.IsNullOrEmpty(user)) _stamps.Remove(user); }
    }
}
