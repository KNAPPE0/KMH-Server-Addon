namespace KMHServerAddon
{
    // Four independent identities: collapsing them would make a cosmetic patch demand a server update.
    internal static class KmhVersion
    {
        public const string Build        = "1.3.1";
        // What operators read; a patch release raises it while clients keep matching on Build.
        public const string Display      = "1.3.1.1";
        // Names the exact working build behind a public version, so a stale binary is identifiable from a log line.
        public const string BuildTag     = "rel-285";
        public const int    ConfigSchema = 7;
        public static int   DataSchema   => Persistence.KmhDataMeta.CurrentFormat;
        public const int    Protocol     = SubProtocol.KmhProtocol.CurrentVersion;

        public static string Summary
            => $"build {Display} ({BuildTag}), config schema {ConfigSchema}, data schema {DataSchema}, protocol {Protocol}";
    }
}
