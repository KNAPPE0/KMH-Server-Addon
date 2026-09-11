using KMHServerAddon.Features.Guilds.Dto;

namespace KMHServerAddon.Features.Guilds
{
    // Each capability names its minimum rank once here, rather than as inline comparisons across GuildStore.
    internal enum GuildCapability
    {
        Invite,          // moderator+
        ManageMembers,   // kick/promote/demote: moderator+, and must outrank the target
        SetMotd,         // admin+
        SaveSettings,    // admin+
        BuyPerk,         // admin+
        SetOpenJoin,     // admin+
        SetRelationship, // alliance/hostility: admin+
        TransferOwner,   // owner only
        Disband,         // owner only
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

        // The rank gate only; a member action also has to clear OutranksTarget below.
        public static bool Can(string actorRank, GuildCapability cap)
            => RankOrder(actorRank) <= RankOrder(MinRank(cap));

        // Strictly outrank, so nobody can act on their own equal.
        public static bool CanActOnMember(string actorRank, string targetRank)
            => Can(actorRank, GuildCapability.ManageMembers) && RankOrder(actorRank) < RankOrder(targetRank);
    }
}
