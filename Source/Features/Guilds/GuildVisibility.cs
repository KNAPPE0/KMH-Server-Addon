using System;

namespace KMHServerAddon.Features.Guilds
{
    // Shared visibility filter used by the Marketplace and Quest snapshot builders. Decides whether a post (listing /
    // quest) is visible to a caller from its visibility flag ('public' / 'guild_only'), its PosterTreasuryKey
    // ('_personal:<user>' or '<guild_name>'), and the caller's guild. Rules: public -> always; guild_only with a
    // personal poster -> never (no guild scope); guild_only -> visible to the poster's guild and its allies; else hidden.
    internal static class GuildVisibility
    {
        public const string Public    = "public";
        public const string GuildOnly = "guild_only";

        private const string PersonalKeyPrefix = "_personal:";

        // Per-post check. Convenience overload that resolves callerGuild on every call - fine for one-off lookups
        // but pays a GuildStore lookup per invocation. Snapshot builders that filter many posts at once should
        // resolve callerGuild once and use the (string callerGuild) overload below to avoid that O(N) overhead
        public static bool IsVisibleTo(string posterTreasuryKey, string visibility, string callerUsername)
        {
            string callerGuild = GuildStore.CurrentGuildOf(callerUsername);
            return IsVisibleTo(posterTreasuryKey, visibility, callerGuild, prefetched: true);
        }

        // Hot-path overload - caller pre-resolved their guild membership once and passes the result in. Avoids
        // re-querying GuildStore for every listing/quest the snapshot builder walks
        public static bool IsVisibleTo(string posterTreasuryKey, string visibility, string callerGuild, bool prefetched)
        {
            // Default to public on missing visibility (preserves pre- visibility behavior for any legacy
            // snapshots)
            if (string.IsNullOrEmpty(visibility)) return true;
            if (string.Equals(visibility, Public, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(visibility, GuildOnly, StringComparison.OrdinalIgnoreCase)) return true;

            // Guild-only from a personal-vault poster makes no sense - hide.
            if (string.IsNullOrEmpty(posterTreasuryKey)) return false;
            if (posterTreasuryKey.StartsWith(PersonalKeyPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            // posterTreasuryKey == poster's guild name.
            if (string.IsNullOrEmpty(callerGuild)) return false;

            if (string.Equals(callerGuild, posterTreasuryKey, StringComparison.OrdinalIgnoreCase))
                return true;

            return GuildStore.AreAllied(callerGuild, posterTreasuryKey);
        }
    }
}
