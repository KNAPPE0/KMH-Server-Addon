using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KMHServerAddon.Features.Transport
{
    // DoS guards for the API transport, split out of KmhApiServer so they're testable without real sockets.

    // Caps total + per-IP in-flight sockets. Every TryAdmit that returns true must be paired with a Release.
    internal sealed class ConnectionLimiter
    {
        private readonly int _maxTotal;
        private readonly int _maxPerIp;
        private int _total;
        private readonly ConcurrentDictionary<string, int> _perIp = new ConcurrentDictionary<string, int>();

        public ConnectionLimiter(int maxTotal, int maxPerIp)
        {
            _maxTotal = maxTotal;
            _maxPerIp = maxPerIp;
        }

        public int Total => Volatile.Read(ref _total);
        public int PerIp(string ip) => _perIp.TryGetValue(ip, out int n) ? n : 0;

        public bool TryAdmit(string ip)
        {
            if (Interlocked.Increment(ref _total) > _maxTotal) { Interlocked.Decrement(ref _total); return false; }
            if (_perIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > _maxPerIp)
            {
                _perIp.AddOrUpdate(ip, 0, (_, n) => n > 0 ? n - 1 : 0);
                Interlocked.Decrement(ref _total);
                return false;
            }
            return true;
        }

        public void Release(string ip)
        {
            Interlocked.Decrement(ref _total);
            if (_perIp.AddOrUpdate(ip, 0, (_, n) => n > 0 ? n - 1 : 0) <= 0)
                _perIp.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(ip, 0));
        }
    }

    // Per-IP failed-auth throttle: too many rejects inside the window trip a temporary block. A clean auth clears the IP.
    internal sealed class AuthFailThrottle
    {
        private sealed class Fails { public int Count; public long WindowStart; public long BlockUntil; }
        private readonly ConcurrentDictionary<string, Fails> _byIp
            = new ConcurrentDictionary<string, Fails>(StringComparer.OrdinalIgnoreCase);

        private readonly int  _threshold;
        private readonly long _windowTicks;
        private readonly long _blockTicks;
        private readonly Func<long> _now;     // UTC ticks; injectable so tests can fake time

        public AuthFailThrottle(int threshold, int windowSeconds, int blockSeconds, Func<long> nowTicks = null)
        {
            _threshold   = threshold;
            _windowTicks = TimeSpan.FromSeconds(windowSeconds).Ticks;
            _blockTicks  = TimeSpan.FromSeconds(blockSeconds).Ticks;
            _now         = nowTicks ?? (() => DateTime.UtcNow.Ticks);
        }

        public bool IsBlocked(string ip)
        {
            if (!_byIp.TryGetValue(ip, out Fails r)) return false;
            lock (r) return _now() < r.BlockUntil;
        }

        // Returns true on the call that trips the block; total is the running in-window count.
        public bool NoteFailure(string ip, out int total)
        {
            long now = _now();
            Fails r = _byIp.GetOrAdd(ip, _ => new Fails());
            lock (r)
            {
                if (now - r.WindowStart > _windowTicks) { r.WindowStart = now; r.Count = 0; }
                r.Count++;
                total = r.Count;
                if (r.Count >= _threshold && r.BlockUntil <= now)
                {
                    r.BlockUntil = now + _blockTicks;
                    return true;
                }
                return false;
            }
        }

        public void Clear(string ip) => _byIp.TryRemove(ip, out _);
    }
}
