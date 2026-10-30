using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Auctions;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Quests;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Features.WantBoard;

namespace KMHServerAddon.Maintenance
{
    // A crash between a value move and its record replays the row, so every sweep runs twice and the second moves nothing.
    internal static class KmhJobDuplicateRunSelfTest
    {
        private static string User(string tag) => $"selftest_dr{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private const string Steel = "Steel";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            long Silver(string u) => TreasuryStore.GetPersonalSilver(u);
            int Items(string u)
            {
                int n = 0;
                var snap = TreasuryStore.GetSnapshotFor(u);
                if (snap?.Items != null)
                    foreach (KeyValuePair<string, int> kv in snap.Items)
                        if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
                return n;
            }
            void Cleanup(params string[] users) { foreach (string u in users) TreasuryStore.PurgeOwnerForTest(u); }

            // Far enough ahead that everything seeded below has ended.
            long Later() => DateTime.UtcNow.Ticks + TimeSpan.FromDays(400).Ticks;

            Check("Jobs: marketplace expiry run twice refunds the seller once", () =>
            {
                string seller = User("m");
                if (!TreasuryStore.DepositItem(seller, Steel, 12, "seed")) return (false, "seed");
                long id = MarketplaceStore.Post(seller, Steel, 12, 10, "public", 1);
                if (id == 0) return (false, "post refused");

                int escrowed = Items(seller);
                ExpirySweeper.TickForTest(Later());
                int afterFirst = Items(seller);
                ExpirySweeper.TickForTest(Later());
                int afterSecond = Items(seller);

                Cleanup(seller);
                bool ok = escrowed == 0 && afterFirst == 12 && afterSecond == 12;
                return (ok, ok ? "12 escrowed, 12 returned once, the second pass returned nothing"
                              : $"escrowed leaves {escrowed}, afterFirstSweep={afterFirst}, afterSecond={afterSecond}");
            });

            Check("Jobs: quest expiry run twice refunds the bounty once", () =>
            {
                string poster = User("q");
                if (!TreasuryStore.DepositSilver(poster, 5000, "seed")) return (false, "seed");
                long before = Silver(poster);
                long id = QuestStore.PostDraft(poster, new QuestEntry
                { Kind = QuestEntry.KindBounty, Title = "selftest", Description = "x", BountySilver = 800 },
                    1, out string why);
                if (id == 0) return (false, "post: " + why);

                long escrowed = Silver(poster);
                ExpirySweeper.TickForTest(Later());
                long afterFirst = Silver(poster);
                ExpirySweeper.TickForTest(Later());
                long afterSecond = Silver(poster);

                QuestStore.PurgePoster(poster);
                Cleanup(poster);
                bool ok = escrowed == before - 800 && afterFirst == before && afterSecond == before;
                return (ok, ok ? "800 escrowed, refunded once, the second pass refunded nothing"
                              : $"escrowTaken={before - escrowed}, afterFirst={afterFirst - before}, "
                                + $"afterSecond={afterSecond - before}");
            });

            Check("Jobs: want expiry run twice refunds the unspent escrow once", () =>
            {
                string buyer = User("w");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed");
                long before = Silver(buyer);
                (long id, string why) = WantStore.Post(buyer, Steel, 8, 100, 1, "public", 0, "");
                if (id == 0) return (false, "post: " + why);

                long escrowed = Silver(buyer);
                ExpirySweeper.TickForTest(Later());
                long afterFirst = Silver(buyer);
                ExpirySweeper.TickForTest(Later());
                long afterSecond = Silver(buyer);

                Cleanup(buyer);
                bool ok = escrowed == before - 800 && afterFirst == before && afterSecond == before;
                return (ok, ok ? "800 escrowed, refunded once, the second pass refunded nothing"
                              : $"escrowTaken={before - escrowed}, afterFirst={afterFirst - before}, "
                                + $"afterSecond={afterSecond - before}");
            });

            Check("Jobs: auction settlement run twice pays the seller and moves the goods once", () =>
            {
                string seller = User("as"), buyer = User("ab");
                if (!TreasuryStore.DepositItem(seller, Steel, 9, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(buyer, 5000, "seed")) return (false, "seed silver");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 9, 100, 10, 0, 1, "public");
                if (id == 0) return (false, "post: " + why);
                if (!AuctionStore.Bid(buyer, id, 300).Ok) return (false, "bid");

                long sellerBefore = Silver(seller);
                ExpirySweeper.TickForTest(Later());
                long paidFirst = Silver(seller) - sellerBefore;
                int buyerFirst = Items(buyer);
                ExpirySweeper.TickForTest(Later());
                long paidTotal = Silver(seller) - sellerBefore;
                int buyerTotal = Items(buyer);

                Cleanup(seller, buyer);
                bool ok = paidFirst > 0 && buyerFirst == 9 && paidTotal == paidFirst && buyerTotal == 9;
                return (ok, ok ? $"seller paid {paidFirst} once, buyer got 9 once"
                              : $"paidFirst={paidFirst}, paidTotal={paidTotal}, buyerFirst={buyerFirst}, "
                                + $"buyerTotal={buyerTotal} - A SETTLEMENT RAN TWICE");
            });

            Check("Jobs: a stale pending deposit is reverted once, however often the sweep runs", () =>
            {
                string who = User("d");
                if (!TreasuryStore.DepositSilver(who, 1000, "seed")) return (false, "seed");
                long before = Silver(who);

                // Three passes over the same state: the first may revert, the rest must find nothing.
                ExpirySweeper.TickForTest(Later());
                long afterOne = Silver(who);
                ExpirySweeper.TickForTest(Later());
                ExpirySweeper.TickForTest(Later());
                long afterThree = Silver(who);

                Cleanup(who);
                bool ok = afterThree == afterOne && afterOne == before;
                return (ok, ok ? "three passes, no movement on a vault with nothing pending"
                              : $"before={before}, afterOne={afterOne}, afterThree={afterThree}");
            });

            Check("Jobs: the whole sweep is idempotent over a fleet with nothing due", () =>
            {
                // With nothing expired, ten passes must change no balance: catches a job that moves value unconditionally.
                string a = User("i1"), b = User("i2");
                if (!TreasuryStore.DepositSilver(a, 4321, "seed")) return (false, "seed a");
                if (!TreasuryStore.DepositItem(b, Steel, 17, "seed")) return (false, "seed b");
                long silverBefore = Silver(a);
                int itemsBefore = Items(b);

                long now = DateTime.UtcNow.Ticks;
                for (int i = 0; i < 10; i++) ExpirySweeper.TickForTest(now);

                long silverAfter = Silver(a);
                int itemsAfter = Items(b);
                Cleanup(a, b);
                bool ok = silverBefore == silverAfter && itemsBefore == itemsAfter;
                return (ok, ok ? "10 passes, nothing due, nothing moved"
                              : $"silver {silverBefore} -> {silverAfter}, items {itemsBefore} -> {itemsAfter}");
            });

            Check("Jobs: owed house fee run twice credits the pool once", () =>
            {
                string who = User("hf");
                string marker = TreasuryStore.FeeMarkerFor(TreasuryStore.PersonalKeyFor(who), "dup-run");
                try
                {
                    // Anything else due at that instant settles first, so the pool moves only for the fee below.
                    ExpirySweeper.TickForTest(Later());
                    long pool0 = MarketplaceStore.HousePoolBalance();
                    if (!TreasuryStore.OweHouseFee(marker, 9, who, "selftest owed fee")) return (false, "owe");
                    ExpirySweeper.TickForTest(Later());
                    long afterFirst = MarketplaceStore.HousePoolBalance() - pool0;
                    ExpirySweeper.TickForTest(Later());
                    long afterSecond = MarketplaceStore.HousePoolBalance() - pool0;
                    bool ok = afterFirst == 9 && afterSecond == 9 && TreasuryStore.OwedFeeCountForTest(marker) == 0;
                    return (ok, ok ? "9 owed, credited once, the second pass credited nothing"
                                  : $"afterFirst=+{afterFirst}, afterSecond=+{afterSecond}, still owed={TreasuryStore.OwedFeeCountForTest(marker)}");
                }
                finally { TreasuryStore.ForgetOwedFeesForTest(marker); Cleanup(who); }
            });

            Check("Jobs: mail expiry run twice returns the attachment once", () =>
            {
                string from = User("ma"), to = User("mb");
                if (!TreasuryStore.DepositItem(from, Steel, 9, "seed")) return (false, "seed");
                long id = Features.Mail.MailStore.Send(from, to, "s", "b", 0,
                              new Dictionary<string, int> { [Steel] = 9 }, null, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);

                int escrowed = Items(from);
                ExpirySweeper.TickForTest(Later());
                int afterFirst = Items(from);
                ExpirySweeper.TickForTest(Later());
                int afterSecond = Items(from);

                Features.Mail.MailStore.PurgeUser(from);
                Features.Mail.MailStore.PurgeUser(to);
                Features.Mail.MailStore.RecoverPendingPayouts();
                Cleanup(from, to);
                bool ok = escrowed == 0 && afterFirst == 9 && afterSecond == 9;
                return (ok, ok ? "9 escrowed, returned once, the second pass returned nothing"
                              : $"escrowed leaves {escrowed}, afterFirst={afterFirst}, afterSecond={afterSecond}");
            });

            Check("Jobs: the site reward cycle run twice pays one cycle", () =>
            {
                // Two passes in one instant are what a crash-and-retry produces; the second must find no cycle due.
                var first = Features.Sites.SiteStore.RunRewardCycle();
                var second = Features.Sites.SiteStore.RunRewardCycle();
                bool ok = second.Count == 0;
                return (ok, ok ? $"first pass paid {first.Count} owner(s), the immediate second paid none"
                              : $"the second pass paid {second.Count} owner(s) with no cycle elapsed");
            });

            Check("Jobs: the world engine run twice returns an expired quest's reserve once", () =>
            {
                Features.World.Dto.ServerQuestDto q = Features.World.WorldStore.CreateQuest(
                    Features.World.Dto.ServerQuestDto.KindCooperative,
                    Features.World.Dto.ServerQuestDto.ObjDeliver,
                    Steel, "selftest expiring", "probe", 100, 0, 60, reservedFromPool: 0);
                if (q == null) return (false, "could not seed a world quest");
                q.EndsUtcTicks = DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(1).Ticks;

                long pool0 = MarketplaceStore.HousePoolBalance();
                Features.World.WorldEngine.TickForTest();
                long pool1 = MarketplaceStore.HousePoolBalance();
                Features.World.WorldEngine.TickForTest();
                long pool2 = MarketplaceStore.HousePoolBalance();

                bool ok = pool2 == pool1 && q.State == Features.World.Dto.ServerQuestDto.StateExpired;
                return (ok, ok ? $"expired once, house pool {pool0} -> {pool1} and held"
                              : $"house pool {pool0} -> {pool1} -> {pool2}, state '{q.State}'");
            });

            Check("Jobs: the frontier director run twice opens one operation", () =>
            {
                int before = Features.World.WorldStore.ActiveQuests().Count;
                Features.Frontier.KmhWorldDirector.Tick();
                int afterFirst = Features.World.WorldStore.ActiveQuests().Count;
                Features.Frontier.KmhWorldDirector.Tick();
                int afterSecond = Features.World.WorldStore.ActiveQuests().Count;

                // Its cooldown is what makes a retried tick harmless; a second one must open nothing new.
                bool ok = afterSecond == afterFirst;
                return (ok, ok ? $"{afterFirst - before} opened on the first tick, none on the second"
                              : $"quests {before} -> {afterFirst} -> {afterSecond}");
            });

            Check("Jobs: the economy reset retry run twice finishes a stalled reset once", () =>
            {
                string u = User("er");
                try
                {
                    if (!TreasuryStore.DepositSilver(u, 500, "selftest seed")) return (false, "seed");
                    if (!Features.Economy.KmhEconomyReset.Begin(u, "selftest-save", out string why)) return (false, "begin: " + why);
                    using (Persistence.JsonFileStore.FailWritesForTest(p => p == Persistence.KmhDataPaths.TreasuryFile ? "selftest" : null))
                        Features.Economy.KmhEconomyReset.Run(u, "selftest-save");
                    long genBefore = Features.Economy.KmhEconomyReset.GenerationOf(u);
                    bool first = Features.Economy.KmhEconomyReset.ResumeStalled();
                    long genAfterFirst = Features.Economy.KmhEconomyReset.GenerationOf(u);
                    bool second = Features.Economy.KmhEconomyReset.ResumeStalled();
                    long genAfterSecond = Features.Economy.KmhEconomyReset.GenerationOf(u);
                    bool ok = first && !second && genAfterFirst == genBefore + 1 && genAfterSecond == genAfterFirst
                           && Silver(u) == 0 && !Features.Economy.KmhEconomyReset.InProgress;
                    return (ok, $"first={first} second={second} gen {genBefore}->{genAfterFirst}->{genAfterSecond} silver={Silver(u)}");
                }
                finally { Cleanup(u); }
            });

            Check("Jobs: the season reset retry run twice finishes a stalled wipe once", () =>
            {
                int rolls = 0, clears = 0;
                IDisposable diskFull = null;
                KmhSeasonReset.RollForTest = () => { rolls++; return (99, 1); };
                KmhSeasonReset.ClearersForTest = () => new Action[] { () => clears++ };
                KmhSeasonReset.ClearVaultsForTest = () => 0;
                KmhSeasonReset.AfterRollForTest = () =>
                    diskFull = Persistence.JsonFileStore.FailWritesForTest(p => p == Persistence.KmhDataPaths.SeasonResetJournalFile ? null : "selftest: disk full");
                try
                {
                    bool ran;
                    try { ran = KmhSeasonReset.Run("selftest", out _); }
                    finally { diskFull?.Dispose(); KmhSeasonReset.AfterRollForTest = null; }
                    long gen = Features.Economy.KmhEconomyReset.Generation;
                    bool first = KmhSeasonReset.ResumePending();
                    int clearsAfterFirst = clears;
                    bool second = KmhSeasonReset.ResumePending();
                    bool ok = !ran && first && !second && rolls == 1 && clearsAfterFirst == 2 && clears == 2
                           && Features.Economy.KmhEconomyReset.Generation == gen && KmhSeasonReset.PendingJournal() == null;
                    return (ok, $"ran={ran} first={first} second={second} rolls={rolls} wipes {clearsAfterFirst}->{clears} generation moved={Features.Economy.KmhEconomyReset.Generation != gen}");
                }
                finally
                {
                    KmhSeasonReset.RollForTest = null; KmhSeasonReset.ClearersForTest = null; KmhSeasonReset.ClearVaultsForTest = null;
                    KmhSeasonReset.AfterRollForTest = null;
                    diskFull?.Dispose();
                    Persistence.JsonFileStore.ClearFailureStateForTest();
                    if (System.IO.File.Exists(Persistence.KmhDataPaths.SeasonResetJournalFile)) System.IO.File.Delete(Persistence.KmhDataPaths.SeasonResetJournalFile);
                    KmhSeasonReset.ReleaseHeldForTest();
                }
            });

            return r;
        }
    }
}
