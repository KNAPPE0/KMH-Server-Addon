using System;

namespace KMHServerAddon.Features.Guilds
{
    // Shared visibility-filter helper used by both Marketplace and Quest snapshot builders. Determines whether a
    // posted item (listing / quest) should be visible to a given caller given:
    //   - the post's visibility flag ('public' / 'guild_only')
    //   - the post's PosterTreasuryKey ('_personal:<user>' for personal
    //     posters, '<guild_name>' for guild posters)
    //   - the caller's current guild membership
    //
    // Rules: Public -> always visible GuildOnly + posterKey is personal
    // ('_personal:...') -> never visible (invalid config; no guild scope to apply) GuildOnly + caller is in
    // poster's guild -> visible GuildOnly + caller's guild is Allied to poster's -> visible else -> hidden
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
