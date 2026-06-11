using System;

namespace KMHServerAddon.Util
{
    // Composed item keys carry material + quality through every item flow (treasury, marketplace, quests):
    //   "Steel"                             plain item, no stuff/quality
    //   "MeleeWeapon_LongSword|Plasteel|5"  def | stuff defName (may be empty) | quality index
    // Quality index: 0 = none/any, 1..7 = Awful..Legendary. Mirrors the patch mod's UI/ItemKeys.cs - same format
    // on both sides or vault keys stop matching
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

        // true when an actual quality satisfies a requirement (0 = no requirement; otherwise required-or-better)
        public static bool Meets(int actualIndex, int requiredIndex)
            => requiredIndex <= 0 || actualIndex >= requiredIndex;

        public static string QualityName(int idx)
            => idx >= 1 && idx <= 7 ? QualityNames[idx] : "";

        // 1..7 from a quality word ("excellent" -> 5), 0 when the word isn't a quality
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
