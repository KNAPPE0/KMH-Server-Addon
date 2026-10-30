using System;

namespace KMHServerAddon.Util
{
    // Must mirror the patch mod's UI/ItemKeys.cs, or vault keys stop matching.
    internal static class ItemKey
    {
        public const char Sep = '|';

        private static readonly string[] QualityNames =
            { "", "Awful", "Poor", "Normal", "Good", "Excellent", "Masterwork", "Legendary" };

        public static string Compose(string defName, string stuffDefName, int qualityIndex)
            => Compose(defName, stuffDefName, qualityIndex, null);

        public static string Compose(string defName, string stuffDefName, int qualityIndex, string innerDefName)
        {
            if (string.IsNullOrEmpty(innerDefName))
            {
                if (string.IsNullOrEmpty(stuffDefName) && qualityIndex <= 0) return defName ?? "";
                return $"{defName}{Sep}{stuffDefName ?? ""}{Sep}{qualityIndex}";
            }
            return $"{defName}{Sep}{stuffDefName ?? ""}{Sep}{qualityIndex}{Sep}{innerDefName}";
        }

        public static void Split(string key, out string defName, out string stuffDefName, out int qualityIndex)
            => Split(key, out defName, out stuffDefName, out qualityIndex, out _);

        public static void Split(string key, out string defName, out string stuffDefName, out int qualityIndex,
                                 out string innerDefName)
        {
            defName = key ?? ""; stuffDefName = ""; qualityIndex = 0; innerDefName = "";
            if (string.IsNullOrEmpty(key) || key.IndexOf(Sep) < 0) return;
            string[] parts = key.Split(Sep);
            defName = parts[0];
            if (parts.Length > 1) stuffDefName = parts[1];
            if (parts.Length > 2 && int.TryParse(parts[2], out int q) && q >= 0 && q <= 7) qualityIndex = q;
            if (parts.Length > 3) innerDefName = parts[3];
        }

        // The def whose label a key should show: the building inside, when there is one.
        public static string DisplayDefOf(string key)
        {
            Split(key, out string def, out _, out _, out string inner);
            return string.IsNullOrEmpty(inner) ? def : inner;
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
