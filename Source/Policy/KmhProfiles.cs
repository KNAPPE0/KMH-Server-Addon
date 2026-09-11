using System.Collections.Generic;
using K = KMHServerAddon.Policy.KmhPolicyKeys;

namespace KMHServerAddon.Policy
{
    // Sparse overrides on the shared defaults, so a new default field reaches every profile without being listed.
    internal static class KmhProfiles
    {
        public const string Balanced = "Balanced";
        public const string Casual   = "Casual";
        public const string Hardcore = "Hardcore";
        public const string Legacy   = "Legacy";
        public const string Custom   = "Custom";

        public static readonly string[] All = { Balanced, Casual, Hardcore, Legacy, Custom };

        public static bool IsKnown(string profile)
        {
            foreach (string p in All) if (string.Equals(p, profile, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Every policy key has a value here, so a later layer only ever overrides and never introduces a new key.
        public static Dictionary<string, object> Defaults(string system)
        {
            var d = new Dictionary<string, object>
            {
                [K.Enabled]           = true,
                [K.Access]            = KmhAccessMode.Open,
                [K.RequireLocation]   = false,
                [K.AllowRemote]       = true,
                [K.FeePercent]        = 0.0,
                [K.MaxPerTransaction] = -1L,
                [K.CooldownSeconds]   = 0,
                [K.BlockedDuringRaid] = false,
                [K.Logging]           = true,
                [K.TargetRuleSet]     = "",
            };
            switch (system)
            {
                case K.System.GuildTreasury:
                    d[K.Access] = KmhAccessMode.GuildOnly;
                    break;
                case K.System.Marketplace:
                    d[K.FeePercent] = 5.0;   // a small house cut keeps silver from inflating unboundedly
                    break;
                case K.System.Auctions:
                    d[K.FeePercent] = 5.0;
                    break;
            }
            return d;
        }

        // Balanced returns empty because Defaults already IS Balanced, not because it is unfinished.
        public static Dictionary<string, object> Overrides(string profile, string system)
        {
            var o = new Dictionary<string, object>();
            switch (Canonical(profile))
            {
                case Casual:
                    o[K.FeePercent]      = 0.0;
                    o[K.CooldownSeconds] = 0;
                    o[K.MaxPerTransaction] = -1L;
                    break;

                case Hardcore:
                    o[K.FeePercent]        = system == K.System.Marketplace || system == K.System.Auctions ? 12.0 : 3.0;
                    o[K.CooldownSeconds]   = 300;
                    o[K.MaxPerTransaction] = 5000L;
                    o[K.BlockedDuringRaid] = true;
                    if (system == K.System.PersonalTreasury || system == K.System.GuildTreasury)
                        o[K.AllowRemote] = false;   // must be at a settlement/hall to move goods
                    break;

                case Legacy:
                    o[K.FeePercent]        = 0.0;
                    o[K.CooldownSeconds]   = 0;
                    o[K.MaxPerTransaction] = -1L;
                    o[K.BlockedDuringRaid] = false;
                    break;

                // Custom's changes live in the owner layer, not here.
            }
            return o;
        }

        private static string Canonical(string profile)
        {
            foreach (string p in All)
                if (string.Equals(p, profile, System.StringComparison.OrdinalIgnoreCase)) return p;
            return Balanced;   // unknown profile name falls back to the shipped default
        }
    }
}
