using System;
using System.Collections.Generic;

namespace KMHServerAddon.Diagnostics
{
    // Keyed on a caller-supplied key, not the rendered text, which carries timestamps and ids that never repeat.
    internal static class KmhLogThrottle
    {
        private const int MaxKeys = 512;

        private sealed class Window
        {
            public long FirstTicks;
            public int  Suppressed;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Window> _keys = new Dictionary<string, Window>(StringComparer.Ordinal);

        // Suppressed lines are counted out on the next one that passes, so "quiet" reads differently from "repeating fast".
        public static bool Allow(string key, TimeSpan window, long nowTicks, out int suppressed)
        {
            suppressed = 0;
            if (string.IsNullOrEmpty(key)) return true;
            lock (_lock)
            {
                if (!_keys.TryGetValue(key, out Window w))
                {
                    if (_keys.Count >= MaxKeys) _keys.Clear();   // bounded; a reset costs one extra line per key
                    _keys[key] = new Window { FirstTicks = nowTicks };
                    return true;
                }
                if (nowTicks - w.FirstTicks < window.Ticks) { w.Suppressed++; return false; }
                suppressed = w.Suppressed;
                w.FirstTicks = nowTicks;
                w.Suppressed = 0;
                return true;
            }
        }

        internal static void ResetForTest() { lock (_lock) _keys.Clear(); }
        internal static int KeyCountForTest { get { lock (_lock) return _keys.Count; } }
    }
}
