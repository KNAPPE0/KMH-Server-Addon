using System.Collections.Generic;
using KMHServerAddon.Features.Guilds.Dto;
using R = KMHServerAddon.Features.Guilds.Dto.GuildMemberDto;

namespace KMHServerAddon.Features.Guilds
{
    internal static class GuildIntegritySelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var g1 = Guild("g1", (R.RankAdmin, "amy"), (R.RankMember, "ben"));
            var res1 = GuildIntegrity.EnsureSingleOwner(g1);
            r.Add(("GuildIntegrity: 0 owners promotes an admin",
                OwnerCount(g1) == 1 && RankOf(g1, "amy") == R.RankOwner && res1.Fix == GuildIntegrity.OwnerFix.PromotedNoOwner,
                "amy promoted"));

            var g2 = Guild("g2", (R.RankMember, "cat"), (R.RankMember, "dan"));
            GuildIntegrity.EnsureSingleOwner(g2);
            r.Add(("GuildIntegrity: 0 owners, no admin promotes first",
                OwnerCount(g2) == 1 && RankOf(g2, "cat") == R.RankOwner, "cat promoted"));

            var g3 = Guild("g3", (R.RankOwner, "eve"), (R.RankOwner, "fay"), (R.RankMember, "gus"));
            var res3 = GuildIntegrity.EnsureSingleOwner(g3);
            r.Add(("GuildIntegrity: extra owners demoted",
                OwnerCount(g3) == 1 && RankOf(g3, "eve") == R.RankOwner && RankOf(g3, "fay") == R.RankAdmin
                    && res3.Fix == GuildIntegrity.OwnerFix.DemotedExtraOwners,
                "eve kept, fay -> admin"));

            var g4 = Guild("g4", (R.RankOwner, "hal"), (R.RankMember, "ivy"));
            var res4 = GuildIntegrity.EnsureSingleOwner(g4);
            r.Add(("GuildIntegrity: single owner untouched",
                OwnerCount(g4) == 1 && res4.Fix == GuildIntegrity.OwnerFix.None, "no change"));

            var g5 = new GuildSnapshot { Name = "g5", Members = new List<GuildMemberDto>() };
            var res5 = GuildIntegrity.EnsureSingleOwner(g5);
            r.Add(("GuildIntegrity: empty guild safe",
                OwnerCount(g5) == 0 && res5.Fix == GuildIntegrity.OwnerFix.None, "no members"));

            // Reuses g3, already repaired above, so a second pass must find nothing to do.
            var again = GuildIntegrity.EnsureSingleOwner(g3);
            r.Add(("GuildIntegrity: idempotent",
                OwnerCount(g3) == 1 && again.Fix == GuildIntegrity.OwnerFix.None, "second pass no-op"));

            return r;
        }

        private static GuildSnapshot Guild(string name, params (string rank, string user)[] members)
        {
            var g = new GuildSnapshot { Name = name, Members = new List<GuildMemberDto>() };
            foreach (var m in members) g.Members.Add(new GuildMemberDto { Username = m.user, Rank = m.rank });
            return g;
        }

        private static int OwnerCount(GuildSnapshot g)
        {
            int n = 0;
            foreach (var m in g.Members) if (m.Rank == R.RankOwner) n++;
            return n;
        }

        private static string RankOf(GuildSnapshot g, string user)
            => g.Members.Find(m => m.Username == user)?.Rank;
    }
}
