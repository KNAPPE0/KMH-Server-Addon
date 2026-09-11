using System;
using System.Collections.Concurrent;

namespace KMHServerAddon.Security
{
    internal sealed class KmhSeenGuard
    {
        private readonly ConcurrentDictionary<string, long> _seen = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly long _windowTicks;
        private readonly int  _pruneAt;

        public KmhSeenGuard(TimeSpan window, int pruneAt = 4096)
        {
            _windowTicks = window.Ticks;
            _pruneAt = pruneAt < 16 ? 16 : pruneAt;
        }

        public int Count => _seen.Count;

        // TryAdd/TryUpdate, not read-then-write: two copies arriving together would both read "not seen" and both proceed.
        public bool MarkIfNew(string key, long nowTicks)
        {
            if (string.IsNullOrEmpty(key)) return true;   // no key to dedup on: never suppress
            while (true)
            {
                if (_seen.TryAdd(key, nowTicks))
                {
                    if (_seen.Count >= _pruneAt) Prune(nowTicks);
                    return true;
                }
                if (!_seen.TryGetValue(key, out long seenAt)) continue;    // pruned underneath: try to claim again
                if (nowTicks - seenAt < _windowTicks) return false;        // duplicate within the window
                if (_seen.TryUpdate(key, nowTicks, seenAt)) return true;   // expired, and this call took it over
            }
        }

        public bool MarkIfNew(string key) => MarkIfNew(key, DateTime.UtcNow.Ticks);

        // For a caller that marks before it acts: nothing happened, so the key must work again rather than stay claimed for the window.
        public void Release(string key)
        {
            if (!string.IsNullOrEmpty(key)) _seen.TryRemove(key, out _);
        }

        // Never a wholesale clear at a size limit: a replayed id would read as new and could re-run a withdrawal.
        private void Prune(long nowTicks)
        {
            foreach (var kv in _seen)
                if (nowTicks - kv.Value >= _windowTicks)
                    _seen.TryRemove(kv.Key, out _);
        }
    }
}
