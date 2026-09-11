using System.Collections.Generic;
using KMHServerAddon.Features.Recovery.Dto;

namespace KMHServerAddon.Features.Recovery
{
    // Must stay pure: the smoke test runs this with no file I/O or ServerLog available.
    internal static class KmhRecoverySelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            var heldOnly = Make(held: 10, resolved: 0);
            int dropped = RecoveryPrune.PruneResolved(heldOnly, 3);
            r.Add(("Recovery: held records never pruned", dropped == 0 && heldOnly.Count == 10, $"dropped {dropped}"));

            var resolvedOnly = Make(held: 0, resolved: 10);   // ids 1..10, oldest first
            dropped = RecoveryPrune.PruneResolved(resolvedOnly, 4);
            bool keptNewest = resolvedOnly.Count == 4 && resolvedOnly[0].Id == 7 && resolvedOnly[3].Id == 10;
            r.Add(("Recovery: resolved capped to newest N", dropped == 6 && keptNewest, $"dropped {dropped}, kept {Ids(resolvedOnly)}"));

            var both = new List<RecoveryRecord>
            {
                Rec(1, "resolved"), Rec(2, "held"), Rec(3, "resolved"), Rec(4, "held"), Rec(5, "resolved"), Rec(6, "resolved"),
            };
            dropped = RecoveryPrune.PruneResolved(both, 1);   // 4 resolved -> keep newest (id 6), drop 1,3,5
            bool heldSurvive = Contains(both, 2) && Contains(both, 4);
            bool onlyNewestResolved = Contains(both, 6) && !Contains(both, 1) && !Contains(both, 3) && !Contains(both, 5);
            r.Add(("Recovery: prune keeps all held + newest resolved",
                   dropped == 3 && heldSurvive && onlyNewestResolved && both.Count == 3, $"kept {Ids(both)}"));

            var mid = new List<RecoveryRecord>
            {
                Rec(1, "delivering"), Rec(2, "resolved"), Rec(3, "resolved"), Rec(4, "resolved"),
            };
            dropped = RecoveryPrune.PruneResolved(mid, 1);
            r.Add(("Recovery: an interrupted delivery is never pruned",
                   dropped == 2 && Contains(mid, 1) && Contains(mid, 4), $"kept {Ids(mid)}"));

            var few = Make(held: 0, resolved: 2);
            r.Add(("Recovery: under cap is a no-op", RecoveryPrune.PruneResolved(few, 5) == 0 && few.Count == 2, ""));
            r.Add(("Recovery: null list is safe", RecoveryPrune.PruneResolved(null, 5) == 0, ""));

            var rec = Rec(1, "held");
            bool claim1 = RecoveryStore.TryClaimRecord(rec) == rec && RecoveryPrune.IsDelivering(rec);
            r.Add(("Recovery: held record claims exactly once", claim1, rec.Status));
            // Neither held nor finished while the value is in flight, so it is not re-triaged and not pruned away.
            r.Add(("Recovery: a claimed record is in flight, not resolved",
                   !RecoveryPrune.IsHeld(rec) && RecoveryPrune.IsDelivering(rec), rec.Status));
            r.Add(("Recovery: second claim refused (no double-deliver)", RecoveryStore.TryClaimRecord(rec) == null, rec.Status));
            rec.Status = "held";   // Unclaim revert
            r.Add(("Recovery: reverted record is re-claimable", RecoveryStore.TryClaimRecord(rec) == rec, rec.Status));
            r.Add(("Recovery: claim of null is safe", RecoveryStore.TryClaimRecord(null) == null, ""));

            return r;
        }

        private static RecoveryRecord Rec(long id, string status) => new RecoveryRecord { Id = id, Status = status };

        private static List<RecoveryRecord> Make(int held, int resolved)
        {
            var list = new List<RecoveryRecord>();
            long id = 1;
            for (int i = 0; i < held; i++) list.Add(Rec(id++, "held"));
            for (int i = 0; i < resolved; i++) list.Add(Rec(id++, "resolved"));
            return list;
        }

        private static bool Contains(List<RecoveryRecord> l, long id) { foreach (var x in l) if (x.Id == id) return true; return false; }
        private static string Ids(List<RecoveryRecord> l) { var s = new List<string>(); foreach (var x in l) s.Add(x.Id.ToString()); return string.Join(",", s); }
    }
}
