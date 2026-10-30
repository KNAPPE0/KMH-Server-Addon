using KMHServerAddon.Features.Guilds.Dto;

namespace KMHServerAddon.Features.Guilds
{
    // Each capability names its minimum rank once here, rather than as inline comparisons across GuildStore.
    internal enum GuildCapability
    {
        Invite,
        ManageMembers,
        SetMotd,
        SaveSettings,
        BuyPerk,
        SetOpenJoin,
        SetRelationship,
        ManageSites,
        TransferOwner,
        Disband,
    }

    internal static class GuildPermissions
    {
        // Lower is higher, and an unknown rank sorts below Member so a bad value can never gain power.
        public static int RankOrder(string rank)
        {
            switch (rank)
            {
                case GuildMemberDto.RankOwner:     return 0;
                case GuildMemberDto.RankAdmin:     return 1;
                case GuildMemberDto.RankModerator: return 2;
                case GuildMemberDto.RankOfficer:   return 3;
                case GuildMemberDto.RankMember:    return 4;
                default:                           return 5;
            }
        }

        private static string MinRank(GuildCapability cap)
        {
            switch (cap)
            {
                case GuildCapability.ManageSites:     return GuildMemberDto.RankOfficer;
                case GuildCapability.Invite:
                case GuildCapability.ManageMembers:   return GuildMemberDto.RankModerator;
                case GuildCapability.SetMotd:
                case GuildCapability.SaveSettings:
                case GuildCapability.BuyPerk:
                case GuildCapability.SetOpenJoin:
                case GuildCapability.SetRelationship: return GuildMemberDto.RankAdmin;
                case GuildCapability.TransferOwner:
                case GuildCapability.Disband:         return GuildMemberDto.RankOwner;
                default:                              return GuildMemberDto.RankOwner;
            }
        }

        // The rank gate only; an action on a member must go through CanActOnMember.
        public static bool Can(string actorRank, GuildCapability cap)
            => RankOrder(actorRank) <= RankOrder(MinRank(cap));

        // Strictly outrank, so nobody can act on their own equal.
        public static bool CanActOnMember(string actorRank, string targetRank)
            => Can(actorRank, GuildCapability.ManageMembers) && RankOrder(actorRank) < RankOrder(targetRank);
    }
}
