namespace KMHServerAddon.Diagnostics
{
    // A skip is not a pass: ok stays FALSE, so a harness unaware of the marker fails rather than counting it.
    internal static class KmhSelfTestResult
    {
        public const string SkipPrefix = "SKIP: ";

        public static (string, bool, string) Skipped(string name, string why)
            => (name, false, SkipPrefix + (string.IsNullOrEmpty(why) ? "prerequisite unavailable" : why));

        public static bool IsSkip(string detail)
            => detail != null && detail.StartsWith(SkipPrefix, System.StringComparison.Ordinal);
    }
}
