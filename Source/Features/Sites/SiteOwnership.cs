using System;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // Guild and system sites leave OwnerUsername empty, so a bare comparison hands control to a nameless caller.
    internal static class SiteOwnership
    {
        public static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return SiteEntry.OwnerPlayer;
            string k = kind.Trim().ToLowerInvariant();
            switch (k)
            {
                case SiteEntry.OwnerGuildKind:
                case SiteEntry.OwnerSystem:
                case SiteEntry.OwnerNeutral: return k;
                default:                     return SiteEntry.OwnerPlayer;   // unknown degrades to the safe kind
            }
        }

        public static string KindOf(SiteEntry s) => s == null ? SiteEntry.OwnerPlayer : NormalizeKind(s.OwnerKind);

        public static bool IsPlayerControlled(SiteEntry s)
        {
            string k = KindOf(s);
            return k == SiteEntry.OwnerPlayer || k == SiteEntry.OwnerGuildKind;
        }

        public static bool IsSystemControlled(SiteEntry s) => !IsPlayerControlled(s);

        // Personal ownership only. A blank caller never owns anything, whatever the site says.
        public static bool IsOwnedBy(SiteEntry s, string username)
        {
            if (s == null || string.IsNullOrEmpty(username)) return false;
            if (KindOf(s) != SiteEntry.OwnerPlayer) return false;
            return !string.IsNullOrEmpty(s.OwnerUsername)
                && string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase);
        }

        // Guild sites answer from CURRENT membership, never a stored name.
        public static bool CanManage(SiteEntry s, string username)
        {
            if (s == null || string.IsNullOrEmpty(username)) return false;
            switch (KindOf(s))
            {
                case SiteEntry.OwnerPlayer:    return IsOwnedBy(s, username);
                // Membership of whichever guild holds the name is not enough: it must be the incarnation that took it.
                case SiteEntry.OwnerGuildKind: return !string.IsNullOrEmpty(s.ControllingGuild)
                                                   && string.Equals(Guilds.GuildStore.CurrentGuildOf(username) ?? "",
                                                                    s.ControllingGuild, StringComparison.OrdinalIgnoreCase)
                                                   && SiteStore.GuildBindingIsLive(s);
                default:                       return false;   // nobody manages a system or neutral outpost
            }
        }

        // Destructive or one-time changes: a guild site needs an officer or above, not just any member.
        public static bool CanAdminister(SiteEntry s, string username)
        {
            if (!CanManage(s, username)) return false;
            if (KindOf(s) != SiteEntry.OwnerGuildKind) return true;
            return Guilds.GuildPermissions.Can(Guilds.GuildStore.RankOf(username), Guilds.GuildCapability.ManageSites);
        }

        // Derived live, never stored, so quotas and perks follow the guild that holds the site now.
        public static string OwningGuildLive(SiteEntry s)
        {
            if (s == null) return "";
            switch (KindOf(s))
            {
                case SiteEntry.OwnerPlayer:    return Guilds.GuildStore.CurrentGuildOf(s.OwnerUsername) ?? "";
                // An orphaned site consumes no guild's quota or perks, least of all a successor's that reused the name.
                case SiteEntry.OwnerGuildKind: return SiteStore.GuildBindingIsLive(s) ? s.ControllingGuild ?? "" : "";
                default:                       return "";
            }
        }

        public static bool IsOrphanedGuildSite(SiteEntry s) => IsGuildOwned(s) && !SiteStore.GuildBindingIsLive(s);

        // Empty for a guild or system site, so per-player accounting never attributes one to a person.
        public static string PayoutAccount(SiteEntry s)
            => s != null && KindOf(s) == SiteEntry.OwnerPlayer ? (s.OwnerUsername ?? "") : "";

        public static bool IsGuildOwned(SiteEntry s) => KindOf(s) == SiteEntry.OwnerGuildKind;

        // Distinct from PayoutAccount: a guild site has no owner username, and reading one loses the whole owner share.
        public static string ProductionAccount(SiteEntry s)
        {
            if (s == null) return "";
            switch (KindOf(s))
            {
                case SiteEntry.OwnerPlayer:    return s.OwnerUsername ?? "";
                case SiteEntry.OwnerGuildKind: return s.ControllingGuild ?? "";
                default:                       return "";
            }
        }

        // Presentation only - never consulted for permission.
        public static string ControllerLabel(SiteEntry s)
        {
            if (s == null) return "";
            switch (KindOf(s))
            {
                case SiteEntry.OwnerPlayer:    return s.OwnerUsername ?? "";
                case SiteEntry.OwnerGuildKind: return s.ControllingGuild ?? "";
                case SiteEntry.OwnerNeutral:   return "Unclaimed";
                default:                       return string.IsNullOrEmpty(s.ControllerFaction) ? "Hostile" : s.ControllerFaction;
            }
        }
    }
}
