using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KMHServerAddon.Features.Transport
{
    // Every TryAdmit that returns true must be paired with a Release, or the count leaks a slot.
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
        public int PerIp(string ip) => _perIp.TryGetValue(AuthFailThrottle.KeyFor(ip), out int n) ? n : 0;

        // Keyed like the auth throttle: one IPv6 host rotating through its /64 must not take every slot.
        public bool TryAdmit(string ip)
        {
            ip = AuthFailThrottle.KeyFor(ip);
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
            ip = AuthFailThrottle.KeyFor(ip);
            Interlocked.Decrement(ref _total);
            if (_perIp.AddOrUpdate(ip, 0, (_, n) => n > 0 ? n - 1 : 0) <= 0)
                _perIp.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(ip, 0));
        }
    }

    // A clean auth clears the network so a mistyping player is not left blocked; active blocks are evicted last.
    internal sealed class AuthFailThrottle
    {
        private sealed class Fails { public int Count; public long WindowStart; public long BlockUntil; }
        private readonly ConcurrentDictionary<string, Fails> _byNet
            = new ConcurrentDictionary<string, Fails>(StringComparer.OrdinalIgnoreCase);

        internal const int MaxTracked = 10_000;
        private const int SweepEvery = 256;

        private readonly int  _threshold;
        private readonly long _windowTicks;
        private readonly long _blockTicks;
        private readonly Func<long> _now;     // UTC ticks; injectable so tests can fake time
        private int _sinceSweep;

        public AuthFailThrottle(int threshold, int windowSeconds, int blockSeconds, Func<long> nowTicks = null)
        {
            _threshold   = threshold;
            _windowTicks = TimeSpan.FromSeconds(windowSeconds).Ticks;
            _blockTicks  = TimeSpan.FromSeconds(blockSeconds).Ticks;
            _now         = nowTicks ?? (() => DateTime.UtcNow.Ticks);
        }

        public int Tracked => _byNet.Count;

        // An IPv6 host rotates through its /64 at will, so the /64 is the key and rotating neither evades nor grows it.
        internal static string KeyFor(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip ?? "", out System.Net.IPAddress a)) return ip ?? "";
            if (a.IsIPv4MappedToIPv6) return a.MapToIPv4().ToString();
            if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return a.ToString();
            byte[] b = a.GetAddressBytes();
            for (int i = 8; i < 16; i++) b[i] = 0;
            return new System.Net.IPAddress(b).ToString() + "/64";
        }

        public bool IsBlocked(string ip)
        {
            if (!_byNet.TryGetValue(KeyFor(ip), out Fails r)) return false;
            lock (r) return _now() < r.BlockUntil;
        }

        // True only on the call that trips the block, so a caller cannot log it once per failure.
        public bool NoteFailure(string ip, out int total)
        {
            long now = _now();
            if (_byNet.Count >= MaxTracked || Interlocked.Increment(ref _sinceSweep) >= SweepEvery) Sweep(now);
            Fails r = _byNet.GetOrAdd(KeyFor(ip), _ => new Fails());
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

        public void Clear(string ip) => _byNet.TryRemove(KeyFor(ip), out _);

        private long ExpiresAt(Fails f) { lock (f) return Math.Max(f.WindowStart + _windowTicks, f.BlockUntil); }

        private void Sweep(long now)
        {
            Interlocked.Exchange(ref _sinceSweep, 0);
            foreach (var kv in _byNet)
                if (ExpiresAt(kv.Value) < now) _byNet.TryRemove(kv.Key, out _);
            int over = _byNet.Count - MaxTracked * 9 / 10;
            if (over <= 0) return;
            var byExpiry = new System.Collections.Generic.List<(long At, string Key)>(_byNet.Count);
            foreach (var kv in _byNet) byExpiry.Add((ExpiresAt(kv.Value), kv.Key));
            byExpiry.Sort((a, b) => a.At.CompareTo(b.At));
            for (int i = 0; i < over && i < byExpiry.Count; i++) _byNet.TryRemove(byExpiry[i].Key, out _);
        }
    }
}
