namespace KMHServerAddon
{
    // Four independent identities: collapsing them would make a cosmetic patch demand a server update.
    internal static class KmhVersion
    {
        public const string Build        = "1.3.0";
        // Names the exact working build behind a public version, so a stale binary is identifiable from a log line.
        public const string BuildTag     = "rel-212";
        public const int    ConfigSchema = 3;
        public static int   DataSchema   => Persistence.KmhDataMeta.CurrentFormat;
        public const int    Protocol     = SubProtocol.KmhProtocol.CurrentVersion;

        public static string Summary
            => $"build {Build} ({BuildTag}), config schema {ConfigSchema}, data schema {DataSchema}, protocol {Protocol}";
    }
}
