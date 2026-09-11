namespace KMHServerAddon.Policy
{
    // Item, player and guild targets are NOT here: they belong to the target-rule engine and are referenced by name.
    internal static class KmhPolicyKeys
    {
        public const string Enabled           = "enabled";
        public const string Access            = "access";              // KmhAccessMode value
        public const string RequireLocation   = "requireLocation";     // must act at a required spot (e.g. guild hall)
        public const string AllowRemote       = "allowRemote";         // usable from off-map / caravan
        public const string FeePercent        = "feePercent";
        public const string MaxPerTransaction = "maxPerTransaction";   // -1 = unlimited
        public const string CooldownSeconds   = "cooldownSeconds";
        public const string BlockedDuringRaid = "blockedDuringRaid";
        public const string Logging           = "logging";
        public const string TargetRuleSet     = "targetRuleSet";       // name of the target-rule set that applies, or ""

        // These strings are the on-disk section keys in Policies.json - renaming one orphans owner overrides.
        public static class System
        {
            public const string PersonalTreasury = "personal_treasury";
            public const string GuildTreasury     = "guild_treasury";
            public const string Marketplace       = "marketplace";
            public const string Auctions          = "auctions";
            public const string WantBoard         = "want_board";
            public const string Sites             = "sites";
            public const string Quests            = "quests";
            public const string WorldEvents       = "world_events";
            public const string Delivery          = "delivery";
            public const string Recovery          = "recovery";
        }
    }

    internal static class KmhAccessMode
    {
        public const string Open       = "open";        // any connected player
        public const string GuildOnly  = "guild_only";  // guild members only
        public const string OwnerOnly  = "owner_only";  // the owner of the vault/listing only
        public const string Restricted = "restricted";  // gated by a target/permission rule
        public const string Disabled   = "disabled";    // no access
    }
}
