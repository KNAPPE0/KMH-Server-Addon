using System;
using System.Collections.Generic;

namespace KMHServerAddon.SubProtocol
{
    // Tracks which feature each player has touched recently, so mutation broadcasts go only to clients actually
    // viewing that feature (their dialog polls every ~8s while open) instead of every connected client. Cuts network
    // fan-out and the client-side deserialization each broadcast costs. Feature = the middle segment of a kind
    // ("kmh.<feature>.<action>"), so request and snapshot kinds map to the same key automatically.
    internal static class KmhInterest
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, long> _lastTouchUtc =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // > 2x the client's 8s auto-refresh, so a viewing client (which re-requests every 8s real-time) always stays
        // inside the window even under frame drops.
        private static readonly long WindowTicks = TimeSpan.FromSeconds(20).Ticks;

        // Record that this player just interacted with the kind's feature (any inbound message counts).
        public static void Touch(string username, string kind)
        {
            if (string.IsNullOrEmpty(username)) return;
            string f = FeatureOf(kind);
            if (f == null) return;
            lock (_lock) _lastTouchUtc[Key(username, f)] = DateTime.UtcNow.Ticks;
        }

        // True if the player touched this kind's feature within the interest window.
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
