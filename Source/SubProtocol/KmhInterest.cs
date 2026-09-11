using System;
using System.Collections.Generic;

namespace KMHServerAddon.SubProtocol
{
    // Keyed on the middle segment of a kind ("kmh.<feature>.<action>"), so request and snapshot kinds share one key.
    internal static class KmhInterest
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, long> _lastTouchUtc =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Over 2x the client's 8s auto-refresh, so a viewing client stays inside the window even under frame drops.
        private static readonly long WindowTicks = TimeSpan.FromSeconds(20).Ticks;

        public static void Touch(string username, string kind)
        {
            if (string.IsNullOrEmpty(username)) return;
            string f = FeatureOf(kind);
            if (f == null) return;
            lock (_lock) _lastTouchUtc[Key(username, f)] = DateTime.UtcNow.Ticks;
        }

        public static bool IsInterested(string username, string kind)
        {
            if (string.IsNullOrEmpty(username)) return false;
            string f = FeatureOf(kind);
            if (f == null) return false;
            lock (_lock)
                return _lastTouchUtc.TryGetValue(Key(username, f), out long t) && DateTime.UtcNow.Ticks - t <= WindowTicks;
        }

        private static string Key(string user, string feature) => user + "|" + feature;

        private static string FeatureOf(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            int a = kind.IndexOf('.');
            if (a < 0) return null;
            int b = kind.IndexOf('.', a + 1);
            return b > a ? kind.Substring(a + 1, b - a - 1) : kind.Substring(a + 1);
        }
    }
}
