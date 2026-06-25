using System;

namespace KMHServerAddon.Util
{
    // Small pure helpers that were copy-pasted across stores, configs, and commands. Centralizing them means a
    // behaviour tweak (clamping, truncation, duration parsing) is a one-file change instead of N copies drifting.
    // Files opt in with `using static KMHServerAddon.Util.KmhSafe;` so existing call sites need no change.
    internal static class KmhSafe
    {
        public static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int  Clamp(int  v, int  lo, int  hi) => v < lo ? lo : (v > hi ? hi : v);

        // Truncate to at most maxLen chars; null/empty -> "".
        public static string Cap(string s, int maxLen)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= maxLen ? s : s.Substring(0, maxLen));

        // Case-insensitive equality treating null as "".
        public static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        // Duration token -> minutes. Bare number = minutes; m/h/d suffix ("30", "30m", "2h", "1d"). -1 when the token
        // isn't a duration at all, so callers can tell a missing/word arg apart from a real 0.
        public static int ParseDurationMinutes(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return -1;
            s = s.Trim().ToLowerInvariant();
            char unit = s[s.Length - 1];
            bool hasUnit = unit == 'm' || unit == 'h' || unit == 'd';
            string num = hasUnit ? s.Substring(0, s.Length - 1) : s;
            if (!int.TryParse(num, out int v) || v < 0) return -1;
            return unit == 'h' ? v * 60 : unit == 'd' ? v * 1440 : v;
        }

        // Friendly duration label: minutes under an hour, else "Xh" / "Xh Ym".
        public static string FmtDuration(int minutes)
        {
            if (minutes <= 0) return "0m";
            if (minutes < 60) return $"{minutes}m";
            int h = minutes / 60, rem = minutes % 60;
            return rem == 0 ? $"{h}h" : $"{h}h {rem}m";
        }
    }
}
