using System.Collections.Generic;

namespace KMHServerAddon.SubProtocol
{
    // Tokens are added, never renamed or repurposed: one means the same thing for the life of the protocol.
    internal static class KmhCapabilities
    {
        public const string DecimalPrices = "decimal_prices";
        public const string WealthFlag = "wealth_flag";
        public const string WorldEventEnd = "world_event_end";
        public const string ConfigMigration = "config_migration";
        public const string Roadworks = "roadworks";
        public const string Frontier = "frontier";
        // Without this token a client must not push item metadata - the facts would land nowhere.
        public const string SiteMeta = "site_meta";
        // Advertised so an older client never offers the button, rather than offering one nothing answers.
        public const string ConfigEditor = "config_editor";
        // Gates the kmh.federation.* kinds; a server without it speaks only the older guild treasury.
        public const string Federation = "federation";
        // Wants priced in milli-silver: without it a client posts whole silver, which an older server reads.
        public const string WantDecimalPrices = "want_decimal_prices";

        private static readonly List<string> _all = new List<string>
        {
            ConfigEditor,
            Federation,
            WantDecimalPrices,
            DecimalPrices,
            WealthFlag,
            WorldEventEnd,
            ConfigMigration,
            Roadworks,
            Frontier,
            SiteMeta,
            Features.Enforcement.EnforcementAdmission.Capability,
        };

        public static string Manifest => string.Join(",", _all);
    }
}
