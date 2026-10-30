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

        // What a client may name and have persisted: def names and account names are short in every real case.
        public const int MaxDefLength = 128, MaxUsernameLength = 64, MaxLabelLength = 128;

        // Strict and bounded: a stored item name is drawn for other players (markup renders) and kept in the vault for good.
        public static bool IsDefName(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxDefLength) return false;
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        // def|stuff|quality|inner: only the def is required, and every part present is a def name or a number.
        public static bool IsItemKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 4 * MaxDefLength + 3) return false;
            string[] parts = key.Split('|');
            if (parts.Length > 4 || !IsDefName(parts[0])) return false;
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].Length > 0 && !IsDefName(parts[i])) return false;
            return true;
        }

        // A client-pushed catalog entry: a real def name and label are short.
        public static bool CatalogEntryFits(string key, string value)
            => !string.IsNullOrEmpty(key) && key.Length <= MaxDefLength
            && !string.IsNullOrEmpty(value) && value.Length <= MaxLabelLength;

        // Never ends on half an emoji, which Colony Nexus Global cannot store.
        public static string Cap(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || maxLen <= 0) return "";
            if (s.Length <= maxLen) return s;
            return s.Substring(0, char.IsHighSurrogate(s[maxLen - 1]) ? maxLen - 1 : maxLen);
        }

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
