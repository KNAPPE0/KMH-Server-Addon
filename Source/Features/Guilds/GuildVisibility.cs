using System;

namespace KMHServerAddon.Features.Guilds
{
    // One filter shared by the Marketplace and Quest builders, so neither can drift into its own visibility rule.
    internal static class GuildVisibility
    {
        public const string Public    = "public";
        public const string GuildOnly = "guild_only";

        // Null means build alone, and every uncertain case returns it, because sharing wrongly leaks a guild-only post.
        public static string SnapshotShareKey(string caller, System.Collections.Generic.HashSet<string> ownersOfNonPublic)
        {
            if (string.IsNullOrEmpty(caller)) return null;
            if (ownersOfNonPublic != null && ownersOfNonPublic.Contains(caller)) return null;
            return "g:" + (GuildStore.CurrentGuildOf(caller) ?? "");
        }

        private const string PersonalKeyPrefix = "_personal:";

        // Resolves the caller's guild per call, so a snapshot builder should use the overload below instead.
        public static bool IsVisibleTo(string posterTreasuryKey, string visibility, string callerUsername)
        {
            string callerGuild = GuildStore.CurrentGuildOf(callerUsername);
            return IsVisibleTo(posterTreasuryKey, visibility, callerGuild, prefetched: true);
        }

        public static bool IsVisibleTo(string posterTreasuryKey, string visibility, string callerGuild, bool prefetched)
        {
            // Missing visibility reads as public, which is how rows predating the field behaved.
            if (string.IsNullOrEmpty(visibility)) return true;
            if (string.Equals(visibility, Public, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(visibility, GuildOnly, StringComparison.OrdinalIgnoreCase)) return true;

            // A guild-only post from a personal vault has no guild to reach, so it reaches nobody.
            if (string.IsNullOrEmpty(posterTreasuryKey)) return false;
            if (posterTreasuryKey.StartsWith(PersonalKeyPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            // For a guild post the treasury key is the guild name, which is what the comparisons below rely on.
            if (string.IsNullOrEmpty(callerGuild)) return false;

            if (string.Equals(callerGuild, posterTreasuryKey, StringComparison.OrdinalIgnoreCase))
                return true;

            return GuildStore.AreAllied(callerGuild, posterTreasuryKey);
        }
    }
}
