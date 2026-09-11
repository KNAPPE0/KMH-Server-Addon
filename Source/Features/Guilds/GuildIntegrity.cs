using System.Collections.Generic;
using KMHServerAddon.Features.Guilds.Dto;

namespace KMHServerAddon.Features.Guilds
{
    // Every other guild operation assumes exactly one owner, which an old or corrupted save can violate.
    internal static class GuildIntegrity
    {
        internal enum OwnerFix { None, PromotedNoOwner, DemotedExtraOwners }

        internal readonly struct Result
        {
            public readonly OwnerFix Fix;
            public readonly int      OwnersBefore;
            public readonly string   KeptOrPromoted;   // the surviving/promoted owner's username
            public Result(OwnerFix fix, int before, string who) { Fix = fix; OwnersBefore = before; KeptOrPromoted = who; }
        }

        // Deterministic, so running it twice repairs to the same owner rather than rotating one.
        public static Result EnsureSingleOwner(GuildSnapshot g)
        {
            if (g?.Members == null || g.Members.Count == 0) return new Result(OwnerFix.None, 0, "");

            List<GuildMemberDto> owners = g.Members.FindAll(
                m => string.Equals(m?.Rank, GuildMemberDto.RankOwner, System.StringComparison.OrdinalIgnoreCase));

            if (owners.Count == 1) return new Result(OwnerFix.None, 1, owners[0].Username);

            if (owners.Count == 0)
            {
                GuildMemberDto pick = g.Members.Find(
                    m => string.Equals(m?.Rank, GuildMemberDto.RankAdmin, System.StringComparison.OrdinalIgnoreCase))
                    ?? g.Members[0];
                pick.Rank = GuildMemberDto.RankOwner;
                return new Result(OwnerFix.PromotedNoOwner, 0, pick.Username);
            }

            for (int i = 1; i < owners.Count; i++) owners[i].Rank = GuildMemberDto.RankAdmin;
            return new Result(OwnerFix.DemotedExtraOwners, owners.Count, owners[0].Username);
        }
    }
}
