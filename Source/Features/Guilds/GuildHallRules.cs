using KMHServerAddon.Features.Economy;
using KMHServerAddon.Features.Guilds.Dto;

namespace KMHServerAddon.Features.Guilds
{
    // Proximity is client-computed because the standalone server cannot do world-tile maths of its own.
    internal static class GuildHallRules
    {
        private static bool HasHall(GuildSnapshot g) => g?.Hall != null && g.Hall.HasHall;

        public static bool CanCreate(int hallTile, out string reason)
        {
            reason = null;
            if (EconomyConfig.Current.RequireGuildHallToCreateGuild && hallTile < 0)
            {
                reason = "This server requires a Guild Hall to create a guild - pick a world tile for your hall in the create dialog.";
                return false;
            }
            return true;
        }

        public static bool CanContribute(GuildSnapshot g, EconomyContext ctx, out string reason)
        {
            reason = null;
            EconomyConfig cfg = EconomyConfig.Current;
            if (cfg.RequireGuildHallForGuildContributions && !HasHall(g))
            {
                reason = "Your guild needs a Guild Hall before members can contribute - a leader must set one first.";
                return false;
            }
            if (cfg.RequireCaravanNearGuildHallForContribution && HasHall(g) && ctx.Known && !ctx.CaravanIsNearHall)
            {
                reason = "Contributions require a caravan near your Guild Hall.";
                return false;
            }
            return true;
        }

        public static bool CanAccessTreasury(GuildSnapshot g, out string reason)
        {
            reason = null;
            if (EconomyConfig.Current.RequireGuildHallForGuildTreasury && !HasHall(g))
            {
                reason = "Your guild needs a Guild Hall before using the guild treasury - a leader must set one first.";
                return false;
            }
            return true;
        }

        public static bool CanJoin(GuildSnapshot g, EconomyContext ctx, out string reason)
        {
            reason = null;
            if (EconomyConfig.Current.RequireMemberNearGuildHallToJoin && HasHall(g) && ctx.Known && !ctx.NearGuildHall)
            {
                reason = "You must be near this guild's Guild Hall to join it.";
                return false;
            }
            return true;
        }

        public static bool CanInvite(GuildSnapshot g, EconomyContext ctx, out string reason)
        {
            reason = null;
            if (!EconomyConfig.Current.AllowRemoteGuildInvites && HasHall(g) && ctx.Known && !ctx.NearGuildHall)
            {
                reason = "Remote guild invites are disabled on this server - you must be near your Guild Hall to invite.";
                return false;
            }
            return true;
        }
    }
}
