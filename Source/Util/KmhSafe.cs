using System;

namespace KMHServerAddon.Util
{
    internal static class KmhSafe
    {
        public static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int  Clamp(int  v, int  lo, int  hi) => v < lo ? lo : (v > hi ? hi : v);

        // Saturating, because an unchecked `a += b` wraps a client-claimed fortune NEGATIVE and reverses every rule.
        public static int AddSaturating(int current, long add, out bool clamped)
        {
            long sum = (long)current + add;
            clamped = sum > int.MaxValue || sum < int.MinValue;
            if (sum > int.MaxValue) return int.MaxValue;
            if (sum < int.MinValue) return int.MinValue;
            return (int)sum;
        }

        public static int AddSaturating(int current, long add) => AddSaturating(current, add, out _);

        public static string Cap(string s, int maxLen)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= maxLen ? s : s.Substring(0, maxLen));

        public static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        // Returns -1, not 0, when the token is not a duration, so a word argument is distinguishable from a real 0.
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

        public static string FmtDuration(int minutes)
        {
            if (minutes <= 0) return "0m";
            if (minutes < 60) return $"{minutes}m";
            int h = minutes / 60, rem = minutes % 60;
            return rem == 0 ? $"{h}h" : $"{h}h {rem}m";
        }
    }
}
