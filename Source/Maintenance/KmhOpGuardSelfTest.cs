using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Features.World;
using KMHServerAddon.Features.World.Dto;
using KMHServerAddon.Security;

namespace KMHServerAddon.Maintenance
{
    // One logical action can reach the server twice; these pin what "twice" costs: one effect, only for the caller who sent the id.
    internal static class KmhOpGuardSelfTest
    {
        private static string User() => "selftest_op_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // The shape every guarded handler uses, so what is measured here is what they do.
        private static bool Guarded(string scope, string actor, string opId, Func<bool> act)
        {
            if (!KmhOpGuard.TryBegin(scope, actor, opId)) return false;
            bool applied = act();
            if (!applied) KmhOpGuard.Release(scope, actor, opId);
            return applied;
        }

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("IDEMPOTENCY: the same op id twice claims once", () =>
            {
                string who = User();
                bool first  = KmhOpGuard.TryBegin("probe", who, "op-1");
                bool second = KmhOpGuard.TryBegin("probe", who, "op-1");
                return (first && !second, first && !second ? "second refused" : $"first={first}, second={second}");
            });

            Check("A second caller's identical id is its own", () =>
            {
                string a = User(), b = User();
                bool mine   = KmhOpGuard.TryBegin("probe", a, "shared-id");
                bool theirs = KmhOpGuard.TryBegin("probe", b, "shared-id");
                return (mine && theirs, mine && theirs
                    ? "one player cannot burn another's ids"
                    : $"a={mine}, b={theirs} - ONE PLAYER CAN BLOCK ANOTHER");
            });

            Check("The same id in another scope is another operation", () =>
            {
                string who = User();
                bool buy  = KmhOpGuard.TryBegin("mkt.buy",  who, "op-2");
                bool post = KmhOpGuard.TryBegin("mkt.post", who, "op-2");
                return (buy && post, buy && post ? "scopes do not collide" : $"buy={buy}, post={post}");
            });

            Check("An id-less request is never suppressed", () =>
            {
                string who = User();
                bool first  = KmhOpGuard.TryBegin("probe", who, "");
                bool second = KmhOpGuard.TryBegin("probe", who, "");
                return (first && second, first && second ? "older clients still work" : "an id-less request was dropped");
            });

            Check("A released id works again", () =>
            {
                string who = User();
                bool first = KmhOpGuard.TryBegin("probe", who, "op-3");
                KmhOpGuard.Release("probe", who, "op-3");
                bool retry = KmhOpGuard.TryBegin("probe", who, "op-3");
                return (first && retry, first && retry ? "a refused action can be retried" : $"first={first}, retry={retry}");
            });

            Check("A claim expires with its window", () =>
            {
                string who = User();
                long t0 = DateTime.UtcNow.Ticks;
                bool first  = KmhOpGuard.TryBegin("probe", who, "op-4", t0);
                bool inside = KmhOpGuard.TryBegin("probe", who, "op-4", t0 + TimeSpan.FromMinutes(5).Ticks);
                bool after  = KmhOpGuard.TryBegin("probe", who, "op-4", t0 + TimeSpan.FromMinutes(11).Ticks);
                return (first && !inside && after, first && !inside && after
                    ? "10-minute window" : $"first={first}, at 5min={inside}, at 11min={after}");
            });

            Check("Copies arriving together still claim once", () =>
            {
                string who = User();
                int won = 0;
                var tasks = new Task[16];
                for (int i = 0; i < tasks.Length; i++)
                    tasks[i] = Task.Run(() => { if (KmhOpGuard.TryBegin("probe", who, "op-race")) System.Threading.Interlocked.Increment(ref won); });
                Task.WaitAll(tasks);
                return (won == 1, won == 1 ? "exactly one of 16 concurrent copies" : $"{won} of 16 claimed the same id");
            });

            Check("IDEMPOTENCY: the same buy twice buys once", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 10, "selftest seed")) return (false, "could not seed the seller");
                if (!TreasuryStore.DepositSilver(buyer, 1000, "selftest seed")) return (false, "could not seed the buyer");
                long listing = MarketplaceStore.Post(seller, "Steel", 10, 10, "public", 0, out string why);
                if (listing == 0) return (false, "could not post the listing: " + why);

                int bought = 0, paid = 0;
                bool Buy() { bool ok = MarketplaceStore.Buy(buyer, listing, 5, out _, out int q, out int s); bought += q; paid += s; return ok; }

                bool first  = Guarded("mkt.buy", buyer, "buy-1", Buy);
                bool second = Guarded("mkt.buy", buyer, "buy-1", Buy);

                long left = TreasuryStore.GetPersonalSilver(buyer);
                MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller);
                TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = first && !second && bought == 5 && paid == 50 && left == 950;
                return (ok, ok ? "5 bought, 50 paid" : $"first={first}, second={second}, bought={bought}, paid={paid}, silver left {left}");
            });

            Check("A refused buy leaves its id usable", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 5, "selftest seed")) return (false, "could not seed the seller");
                long listing = MarketplaceStore.Post(seller, "Steel", 5, 10, "public", 0, out string why);
                if (listing == 0) return (false, "could not post the listing: " + why);

                int bought = 0;
                bool Buy() { bool ok = MarketplaceStore.Buy(buyer, listing, 5, out _, out int q, out _); bought += q; return ok; }

                bool broke = Guarded("mkt.buy", buyer, "buy-2", Buy);            // no silver banked yet
                if (!TreasuryStore.DepositSilver(buyer, 100, "selftest top-up")) return (false, "could not top the buyer up");
                bool afterTopUp = Guarded("mkt.buy", buyer, "buy-2", Buy);       // same logical action, retried

                MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller);
                TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = !broke && afterTopUp && bought == 5;
                return (ok, ok ? "a refusal does not burn the id" : $"refused={!broke}, retry={afterTopUp}, bought={bought}");
            });

            Check("IDEMPOTENCY: the same global-quest delivery twice credits once", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositItem(who, "Steel", 80, "selftest seed")) return (false, "could not seed the vault");
                ServerQuestDto q = WorldStore.CreateQuest(ServerQuestDto.KindCooperative, ServerQuestDto.ObjDeliver,
                    "Steel", "selftest deliver", "probe", 100, 0, 60);
                if (q == null) return (false, "could not seed a deliver quest");

                bool first  = Guarded("world.deliver", who, "deliver-1", () => WorldEngine.ApplyDelivery(who, q.Id, "Steel", 40));
                bool second = Guarded("world.deliver", who, "deliver-1", () => WorldEngine.ApplyDelivery(who, q.Id, "Steel", 40));

                q.Contributors.TryGetValue(who, out int credited);
                int left = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = first && !second && credited == 40 && left == 40;
                return (ok, ok ? "40 credited, 40 left in the vault" : $"first={first}, second={second}, credited={credited}, left={left}");
            });

            Check("IDEMPOTENCY: the same withdrawal twice debits once", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositSilver(who, 500, "selftest seed")) return (false, "could not seed the vault");

                bool first  = Guarded("treasury.withdraw_silver", who, "wd-1", () => TreasuryStore.WithdrawSilver(who, 200, "selftest"));
                bool second = Guarded("treasury.withdraw_silver", who, "wd-1", () => TreasuryStore.WithdrawSilver(who, 200, "selftest"));

                long left = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = first && !second && left == 300;
                return (ok, ok ? "300 left, debited once" : $"first={first}, second={second}, left={left}");
            });

            return r;
        }

        private static int ItemCount(string who, string def)
        {
            var s = TreasuryStore.GetSnapshotFor(who);
            if (s?.Items == null) return 0;
            int n = 0;
            foreach (KeyValuePair<string, int> kv in s.Items)
                if (Util.ItemKey.Matches(kv.Key, def, "", 0)) n += kv.Value;
            return n;
        }
    }
}
