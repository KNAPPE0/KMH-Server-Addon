using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Features.World;
using KMHServerAddon.Features.World.Dto;

namespace KMHServerAddon.Maintenance
{
    // Everything a client says about itself is a claim; these pin where a claim used to become authoritative economy.
    internal static class KmhClientTrustSelfTest
    {
        private static string User() => "selftest_trust_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("World: a crafted delivery with an empty treasury credits nothing", () =>
            {
                string who = User();
                ServerQuestDto q = WorldStore.CreateQuest(ServerQuestDto.KindCooperative, ServerQuestDto.ObjDeliver,
                    "Steel", "selftest deliver", "probe", 100, 0, 60);
                if (q == null) return (false, "could not seed a deliver quest");

                // Exactly the packet a modified client would send, with nothing banked to back it.
                WorldEngine.ApplyDelivery(who, q.Id, "Steel", 100);

                q.Contributors.TryGetValue(who, out int credited);
                int minted = ItemCount(who, "Steel");
                long silver = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                // The old refund paths DEPOSITED the asserted goods, so this also proves nothing is minted.
                return (credited == 0 && minted == 0 && silver == 0,
                    credited == 0 && minted == 0 && silver == 0
                        ? "no progress, no items, no silver"
                        : $"credited={credited}, items={minted}, silver={silver} - A CRAFTED PACKET STILL PAYS");
            });

            Check("World: a delivery is paid out of the treasury and credits exactly that", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositItem(who, "Steel", 40, "selftest seed")) return (false, "could not seed the vault");
                ServerQuestDto q = WorldStore.CreateQuest(ServerQuestDto.KindCooperative, ServerQuestDto.ObjDeliver,
                    "Steel", "selftest deliver", "probe", 100, 0, 60);
                if (q == null) return (false, "could not seed a deliver quest");

                WorldEngine.ApplyDelivery(who, q.Id, "Steel", 40);

                q.Contributors.TryGetValue(who, out int credited);
                int left = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                return (credited == 40 && left == 0,
                    credited == 40 && left == 0 ? "40 credited, 40 withdrawn"
                                                : $"credited={credited}, {left} still in the vault");
            });

            Check("World: over-delivering past the goal returns the remainder", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositItem(who, "Steel", 50, "selftest seed")) return (false, "could not seed the vault");
                ServerQuestDto q = WorldStore.CreateQuest(ServerQuestDto.KindCooperative, ServerQuestDto.ObjDeliver,
                    "Steel", "selftest deliver", "probe", 20, 0, 60);
                if (q == null) return (false, "could not seed a deliver quest");

                WorldEngine.ApplyDelivery(who, q.Id, "Steel", 50);

                q.Contributors.TryGetValue(who, out int credited);
                int left = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                // Only the room is withdrawn, so the rest never leaves the vault in the first place.
                return (credited == 20 && left == 30, $"credited={credited} (goal 20), {left} left of 50");
            });

            Check("World: a self-reported objective completes without paying", () =>
            {
                string who = User();
                WorldConfig saved = WorldConfig.Current;
                try
                {
                    WorldConfig.ApplyForTest(new WorldConfig { PayRewardsForSelfReportedObjectives = false });
                    ServerQuestDto q = WorldStore.CreateQuest(ServerQuestDto.KindCooperative, ServerQuestDto.ObjHunt,
                        "Muffalo", "selftest hunt", "probe", 10, 5000, 60);
                    if (q == null) return (false, "could not seed a hunt quest");

                    // The whole exploit in one line: claim the goal, collect the pot.
                    WorldEngine.ApplyContribution(who, q.Id, 10);

                    long paid = TreasuryStore.GetPersonalSilver(who);
                    TreasuryStore.PurgeOwnerForTest(who);
                    return (paid == 0, paid == 0 ? "quest completed on the claim, but paid nothing"
                                                 : $"a claimed hunt paid {paid} silver");
                }
                finally { WorldConfig.ApplyForTest(saved); }
            });

            Check("World: reported colony wealth cannot change a generated reward", () =>
            {
                string who = User();
                WorldConfig cfg = new WorldConfig();
                long before = WorldEngine.ComputeAutoRewardForTest(cfg, "Steel", 50);

                // A modified client reporting the largest wealth the server will store.
                PlayerStatsStore.ApplyColonyReport(who, new ColonyReport { Wealth = 999_000_000_000L });
                long after = WorldEngine.ComputeAutoRewardForTest(cfg, "Steel", 50);
                PlayerStatsStore.RemoveUser(who);

                return (before == after,
                    before == after ? $"reward stayed at {before} with a trillion in claimed wealth"
                                    : $"{before} -> {after} - CLIENT WEALTH STILL SIZES THE PAYOUT");
            });

            Check("SDK: an extension cannot move value while maintenance is on", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositSilver(who, 1000, "selftest seed")) return (false, "could not seed the vault");
                long seeded = TreasuryStore.GetPersonalSilver(who);

                var api = new Extensibility.TreasuryApiImpl();
                KmhMaintenanceGate.Enter(KmhMaintenanceReason.OwnerMaintenance);
                bool tookDuring, gaveDuring;
                try
                {
                    // The router enforces the pause for client packets; an extension reaches the same stores without it.
                    tookDuring = api.WithdrawSilver(who, 100, "selftest");
                    gaveDuring = api.DepositSilver(who, 100, "selftest");
                }
                finally { KmhMaintenanceGate.Release(); }

                long during = TreasuryStore.GetPersonalSilver(who);
                bool afterOk = api.WithdrawSilver(who, 100, "selftest");
                long after = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);

                return (!tookDuring && !gaveDuring && during == seeded && afterOk && after == seeded - 100,
                    !tookDuring && !gaveDuring && during == seeded && afterOk
                        ? "refused while paused, allowed once maintenance ended"
                        : $"took={tookDuring} gave={gaveDuring} during={during}/{seeded} afterOk={afterOk}");
            });

            Check("Sites: a claimed pawn skill does not raise real output", () =>
            {
                SiteEntry honest = ProbeSite(baseSkill: 0, xp: 0);
                SiteEntry liar   = ProbeSite(baseSkill: 20, xp: 0);
                double a = SiteStore.OutputFactor(honest);
                double b = SiteStore.OutputFactor(liar);
                return (Math.Abs(a - b) < 1e-9,
                    Math.Abs(a - b) < 1e-9 ? $"skill 0 and claimed skill 20 both produce x{a:0.##}"
                                           : $"x{a:0.##} vs x{b:0.##} - A CLAIMED SKILL STILL RAISES OUTPUT");
            });

            Check("Sites: earned XP does raise output, so the check above is not vacuous", () =>
            {
                SiteEntry green   = ProbeSite(baseSkill: 0, xp: 0);
                SiteEntry veteran = ProbeSite(baseSkill: 0, xp: 210_000);
                double a = SiteStore.OutputFactor(green);
                double b = SiteStore.OutputFactor(veteran);
                return (b > a, $"x{a:0.##} at 0 XP -> x{b:0.##} at 210k XP");
            });

            Check("Sites: a claimed skill is still shown, just never counted", () =>
            {
                WorkerProgressDto wp = new WorkerProgressDto { BaseSkillLevel = 20, Xp = 0 };
                return (wp.CurrentLevel == 20 && wp.EarnedLevel == 0,
                    $"shown {wp.CurrentLevel}, earned {wp.EarnedLevel}");
            });

            // A payload's MarketValue and the catalog's first-seen figure both rode in from a client - neither sets a payout.
            Check("Recovery: a client-valued item cannot be refunded as silver", () =>
            {
                string who = "selftest_val_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string def = "SelftestGem_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    var payload = new Items.KmhThingPayload
                    { DefName = def, StackCount = 1, MarketValue = 999_999, Fingerprint = "fp" };
                    long id = Features.Recovery.RecoveryStore.HoldItem(who, payload, "selftest", "selftest");
                    if (id <= 0) return (false, "could not hold a record");

                    long before = TreasuryStore.GetPersonalSilver(who);
                    bool paid = Features.Recovery.RecoveryStore.RefundAsSilver(id, out string message);
                    long after = TreasuryStore.GetPersonalSilver(who);
                    bool named = message != null && message.IndexOf("server-owned", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Pinned by the owner, the same record pays - so the refusal above is about authority, not plumbing.
                    Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, 7);
                    bool paidPinned = Features.Recovery.RecoveryStore.RefundAsSilver(id, out _);
                    long pinnedTotal = TreasuryStore.GetPersonalSilver(who) - after;

                    bool ok = !paid && after == before && named && paidPinned && pinnedTotal == 7;
                    return (ok, ok ? "the client's 999,999 is refused; the owner's pinned 7 pays"
                                  : $"refusedClientValue={!paid}, silverUnchanged={after == before}, " +
                                    $"saysWhy={named}, pinnedPays={paidPinned} ({pinnedTotal})");
                }
                finally
                {
                    Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, 0);
                    TreasuryStore.PurgeOwnerForTest(who);
                }
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

        // One active pawn worker, so OutputFactor reads a real average rather than an empty site.
        private static SiteEntry ProbeSite(int baseSkill, double xp)
        {
            SiteEntry s = new SiteEntry { Tile = -1, OwnerUsername = "_selftest", ItemDefName = "Steel", BaseAmountPerCycle = 10 };
            s.Workers.Add("_selftest");
            s.WorkerProgress["_selftest"] = new WorkerProgressDto
            { BaseSkillLevel = baseSkill, Xp = xp, PawnLoadId = 1, BlockedReason = "", Legacy = false };
            return s;
        }
    }
}
