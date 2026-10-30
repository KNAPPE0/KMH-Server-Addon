namespace KMHServerAddon.Features.Sites
{
    internal static class KmhVocabulary
    {
        // Blank or unrecognised becomes the fallback, so a value from a newer build degrades rather than going live.
        public static string Canonical(string value, string[] known, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string k = value.Trim().ToLowerInvariant();
            foreach (string s in known) if (k == s) return k;
            return fallback;
        }
    }
}
