using System;

namespace KMHServerAddon.Util
{
    // "def|stuff|quality", mirroring the patch mod's UI/ItemKeys.cs - if the two formats drift, vault keys stop matching.
    internal static class ItemKey
    {
        public const char Sep = '|';

        private static readonly string[] QualityNames =
            { "", "Awful", "Poor", "Normal", "Good", "Excellent", "Masterwork", "Legendary" };

        public static string Compose(string defName, string stuffDefName, int qualityIndex)
        {
            if (string.IsNullOrEmpty(stuffDefName) && qualityIndex <= 0) return defName ?? "";
            return $"{defName}{Sep}{stuffDefName ?? ""}{Sep}{qualityIndex}";
        }

        public static void Split(string key, out string defName, out string stuffDefName, out int qualityIndex)
        {
            defName = key ?? ""; stuffDefName = ""; qualityIndex = 0;
            if (string.IsNullOrEmpty(key) || key.IndexOf(Sep) < 0) return;
            string[] parts = key.Split(Sep);
            defName = parts[0];
            if (parts.Length > 1) stuffDefName = parts[1];
            if (parts.Length > 2 && int.TryParse(parts[2], out int q) && q >= 0 && q <= 7) qualityIndex = q;
        }

        // Quality 0 means no requirement, not "Awful".
        public static bool Meets(int actualIndex, int requiredIndex)
            => requiredIndex <= 0 || actualIndex >= requiredIndex;

        // Withdrawals and their self-test both route through this, so the rule cannot drift between them.
        public static bool Matches(string key, string targetDefName, string requiredStuff, int requiredQualityIndex)
        {
            Split(key, out string def, out string stuff, out int q);
            if (!string.Equals(def, targetDefName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Meets(q, requiredQualityIndex)) return false;
            return string.IsNullOrEmpty(requiredStuff)
                || string.Equals(stuff ?? "", requiredStuff, StringComparison.OrdinalIgnoreCase);
        }

        public static string QualityName(int idx)
            => idx >= 1 && idx <= 7 ? QualityNames[idx] : "";

        // 0 when the word is not a quality at all, which callers must not read as "Awful".
        public static int QualityIndexOf(string word)
        {
            if (string.IsNullOrEmpty(word)) return 0;
            for (int i = 1; i <= 7; i++)
                if (string.Equals(QualityNames[i], word, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        public static int Clamp(int qualityIndex) => qualityIndex < 0 ? 0 : (qualityIndex > 7 ? 7 : qualityIndex);
    }
}
