using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Features.Marketplace
{
    // Every other marketplace check takes a path that RETURNS, so none reaches a crash state. Value is counted everywhere,
    // house pool and recovery holds included, or a kept payout reads as a refund.
    internal static class KmhMarketplaceRestartSelfTest
    {
        private const string Steel = "Steel";
        private static string User(string tag) => $"selftest_rs{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private struct Ledger
        {
            public long BuyerSilver, SellerSilver, HousePool, HeldSilver;
            public int BuyerItems, SellerItems, ListingItems, HeldItems;

            public long TotalSilver => BuyerSilver + SellerSilver + HousePool + HeldSilver;
            public int TotalItems => BuyerItems + SellerItems + ListingItems + HeldItems;
            public override string ToString()
                => $"silver(buyer {BuyerSilver} seller {SellerSilver} house {HousePool} held {HeldSilver} = {TotalSilver}) "
                 + $"items(buyer {BuyerItems} seller {SellerItems} listing {ListingItems} held {HeldItems} = {TotalItems})";
        }

        private static int ItemsOf(string user)
        {
            int n = 0;
            var snap = TreasuryStore.GetSnapshotFor(user);
            if (snap?.Items != null)
                foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
            return n;
        }

        private static int ItemsInListing(string seller, long listingId)
        {
            Dto.MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(seller);
            if (snap?.Listings != null)
                foreach (Dto.MarketplaceListing l in snap.Listings)
                    if (l.Id == listingId) return l.RemainingQty;
            return 0;
        }

        private static void AddHolds(ref Ledger g, string user)
        {
            // Value a failed delivery parked still counts, or "parked for triage" reads the same as "gone".
            foreach (Recovery.Dto.RecoveryRecord rec in Recovery.RecoveryStore.ForUser(user))
            {
                if (rec == null || rec.Status == "resolved") continue;
                if (rec.Kind == "silver") g.HeldSilver += rec.Silver;
                else if (rec.Payload != null) g.HeldItems += rec.Payload.StackCount;
            }
        }

        // A mid-flight sale is an IOU an operator can see, not a loss, but only while some open ledger row names it.
        private static (long Silver, int Items) OwedByLedger(string buyer, string seller)
        {
            long silver = 0;
            int items = 0;
            foreach (KmhTransaction t in KmhTransactionRepository.Pending())
            {
                bool mine = t.Player == buyer || t.Player == seller
                         || t.CounterPlayer == buyer || t.CounterPlayer == seller;
                if (!mine) continue;
                silver += t.EscrowSilver + t.CounterSilver;
                foreach (KeyValuePair<string, int> kv in t.EscrowItems)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) items += kv.Value;
                foreach (KeyValuePair<string, int> kv in t.CounterItems)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) items += kv.Value;
                items += Items.KmhPayloadEscrow.TotalUnits(t.EscrowPayloads);
                items += Items.KmhPayloadEscrow.TotalUnits(t.CounterPayloads);
            }
            return (silver, items);
        }

        private static Ledger Measure(string buyer, string seller, long listingId)
        {
            var g = new Ledger
            {
                BuyerSilver = TreasuryStore.GetPersonalSilver(buyer),
                SellerSilver = TreasuryStore.GetPersonalSilver(seller),
                HousePool = MarketplaceStore.HousePoolBalance(),
                BuyerItems = ItemsOf(buyer),
                SellerItems = ItemsOf(seller),
                ListingItems = ItemsInListing(seller, listingId),
            };
            AddHolds(ref g, buyer);
            AddHolds(ref g, seller);
            return g;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // A cancel must not return units a buy took in between, or that buy is delivered AND refunded; timing alone does it.
            foreach (bool expire in new[] { false, true })
            {
                string name = expire ? "Marketplace: a purchase landing in the middle of an expiry is never refunded too"
                                     : "Marketplace: a purchase landing in the middle of a cancel is never refunded too";
                string seller = User("s"), buyer = User("b");
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 10, "selftest seed")) { r.Add((name, false, "seed goods")); continue; }
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) { r.Add((name, false, "seed silver")); continue; }
                    long listingId = MarketplaceStore.Post(seller, Steel, 10, 10, "public", 0, out string postWhy);
                    if (listingId == 0) { r.Add((name, false, "post: " + postWhy)); continue; }
                    int bought = 0, interrupted = 0;
                    bool closed;
                    MarketplaceStore.AtStorePhaseForTest = p =>
                    {
                        if (p != "cancel-recorded" && p != "expiry-recorded") return;
                        if (interrupted++ > 0) return;
                        MarketplaceStore.Buy(buyer, listingId, 9, out _, out bought, out _);
                    };
                    try { closed = expire ? MarketplaceStore.ExpireListing(listingId, out _) : MarketplaceStore.Cancel(seller, listingId); }
                    finally { MarketplaceStore.AtStorePhaseForTest = null; }

                    int sellerItems = ItemsOf(seller), buyerItems = ItemsOf(buyer), listed = ItemsInListing(seller, listingId);
                    bool ok = closed && bought == 9 && sellerItems + buyerItems + listed == 10 && buyerItems == 9 && sellerItems == 1;
                    r.Add((name, ok, $"bought {bought}; seller {sellerItems} + buyer {buyerItems} + still listed {listed} = {sellerItems + buyerItems + listed} of 10"));
                }
                finally
                {
                    MarketplaceStore.AtStorePhaseForTest = null;
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                }
            }

            // The other order: a buy reserved before the cancel looked, then failed to pay and put its units back mid-cancel.
            {
                const string name = "Marketplace: units a failed purchase puts back during a cancel go home, not out with the listing";
                string seller = User("s"), buyer = User("b");
                var reserved = new System.Threading.ManualResetEventSlim(false);
                var go = new System.Threading.ManualResetEventSlim(false);
                System.Threading.Tasks.Task<bool> buy = null;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 10, "selftest seed")) throw new InvalidOperationException("seed goods");
                    long listingId = MarketplaceStore.Post(seller, Steel, 10, 10, "public", 0, out string postWhy);
                    if (listingId == 0) throw new InvalidOperationException("post: " + postWhy);
                    // The buyer has no silver: the buy reserves 4 units, then cannot pay.
                    MarketplaceStore.AtBuyPhaseForTest = p => { if (p == "recorded") { reserved.Set(); go.Wait(5000); } };
                    buy = System.Threading.Tasks.Task.Run(() => MarketplaceStore.Buy(buyer, listingId, 4, out _, out _, out _));
                    bool closed = false;
                    if (reserved.Wait(5000))
                    {
                        int interrupted = 0;
                        MarketplaceStore.AtStorePhaseForTest = p =>
                        {
                            if (p != "cancel-recorded" || interrupted++ > 0) return;
                            go.Set();
                            buy.Wait(5000);
                        };
                        try { closed = MarketplaceStore.Cancel(seller, listingId); }
                        finally { MarketplaceStore.AtStorePhaseForTest = null; }
                    }
                    go.Set();
                    bool bought = buy.Wait(5000) && buy.Result;
                    int sellerItems = ItemsOf(seller), listed = ItemsInListing(seller, listingId);
                    bool ok = closed && !bought && sellerItems == 10 && listed == 0;
                    r.Add((name, ok, ok ? "all 10 back with the seller"
                                        : $"closed={closed}, bought={bought}, seller {sellerItems} + still listed {listed} of 10"
                                          + (sellerItems + listed < 10 ? " - UNITS LOST WITH THE LISTING" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    go.Set();
                    buy?.Wait(5000);
                    MarketplaceStore.AtBuyPhaseForTest = null;
                    MarketplaceStore.AtStorePhaseForTest = null;
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                }
            }

            (bool ok, string detail) AtPhase(string phase)
            {
                string seller = User("s"), buyer = User("b");
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed")) return (false, "seed goods");
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "seed silver");
                    listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out string why);
                    if (listingId == 0) return (false, "post: " + why);

                    Ledger before = Measure(buyer, seller, listingId);

                    // Stops Buy exactly as a crash would, with every durable row so far written by the real code.
                    MarketplaceStore.AtBuyPhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    bool returned;
                    try
                    {
                        returned = MarketplaceStore.Buy(buyer, listingId, 8, out _, out _, out _,
                                                        "op-" + Guid.NewGuid().ToString("N"));
                    }
                    catch (OperationCanceledException) { returned = false; }
                    finally { MarketplaceStore.AtBuyPhaseForTest = null; }

                    // Main's boot order on the ledger as last written: a leg paid but not cleared on disk must not pay again.
                    KmhTransactionRepository.LoadFromDisk();
                    KmhTransactionRepository.RecoverOnBoot();
                    MarketplaceStore.RecoverPendingReserves();

                    Ledger after = Measure(buyer, seller, listingId);
                    (long owedSilver, int owedItems) = OwedByLedger(buyer, seller);

                    long silverDelta = after.TotalSilver - before.TotalSilver;
                    int itemsDelta = after.TotalItems - before.TotalItems;

                    bool noSilverMinted = silverDelta <= 0;
                    bool noItemsMinted = itemsDelta <= 0;
                    bool silverAccounted = -silverDelta <= owedSilver;
                    bool itemsAccounted = -itemsDelta <= owedItems;
                    // The one shape conservation cannot see: refunded in full, goods kept.
                    bool buyerKeptBoth = after.BuyerSilver == before.BuyerSilver && after.BuyerItems > 0;
                    // Boot either finishes the sale or undoes it; one left owed waits on an operator for nothing.
                    bool settled = owedSilver == 0 && owedItems == 0;

                    bool ok = noSilverMinted && noItemsMinted && silverAccounted && itemsAccounted && !buyerKeptBoth && settled;
                    return (ok, ok
                        ? $"silver {silverDelta:+#;-#;0}, items {itemsDelta:+#;-#;0}, ledger owes "
                          + $"{owedSilver} silver and {owedItems} item(s){(returned ? " (the buy completed)" : "")}"
                        : $"BEFORE {before} -> AFTER {after} | ledger owes {owedSilver} silver, {owedItems} item(s)"
                          + (noSilverMinted ? "" : $" | SILVER MINTED {silverDelta:+#;-#;0}")
                          + (noItemsMinted ? "" : $" | ITEMS MINTED {itemsDelta:+#;-#;0}")
                          + (silverAccounted ? "" : $" | {-silverDelta} SILVER LOST, NOTHING OWES IT")
                          + (itemsAccounted ? "" : $" | {-itemsDelta} ITEM(S) LOST, NOTHING OWES THEM")
                          + (buyerKeptBoth ? " | THE BUYER KEPT THE SILVER AND THE GOODS" : "")
                          + (settled ? "" : " | BOOT LEFT THE SALE OPEN"));
                }
                finally
                {
                    MarketplaceStore.AtBuyPhaseForTest = null;
                    if (listingId != 0) MarketplaceStore.Cancel(seller, listingId);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            void Phase(string phase, string what)
            {
                string name = $"Marketplace restart: a crash {what} conserves value";
                try { (bool ok, string detail) = AtPhase(phase); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Phase("reserved",  "after the units leave the listing, before the ledger knows");
            Phase("recorded",  "after the intent is durable, before the buyer is charged");
            Phase("charged",   "after the buyer is charged, before the record is approved");
            Phase("approved",  "after the record is approved, before the taxes are taken");
            Phase("settled",   "after the taxes are taken, before the seller is paid");
            Phase("paid",      "after the seller is paid, before the goods are delivered");
            Phase("delivered", "after the goods are delivered, before the record is closed");
            Phase("",          "nowhere (the sale completes)");

            {
                const string name = "Marketplace restart: a sale stranded before a reset closes unpaid at boot, never paying into the wiped vaults";
                string seller = User("s"), buyer = User("b");
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed") || !TreasuryStore.DepositSilver(buyer, 5000, "selftest seed"))
                        throw new InvalidOperationException("seed");
                    listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out string why);
                    if (listingId == 0) throw new InvalidOperationException("post: " + why);
                    long sellerBefore = TreasuryStore.GetPersonalSilver(seller);
                    MarketplaceStore.AtBuyPhaseForTest = p => { if (p == "settled") throw new OperationCanceledException("selftest: process died at " + p); };
                    try { MarketplaceStore.Buy(buyer, listingId, 8, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N")); }
                    catch (OperationCanceledException) { }
                    finally { MarketplaceStore.AtBuyPhaseForTest = null; }
                    int stranded = KmhTransactionRepository.PendingCountFor(buyer);

                    Economy.KmhEconomyReset.BumpDataGeneration("selftest: a reset between the crash and the boot");
                    KmhTransactionRepository.RecoverPendingForTest(buyer);
                    int open = KmhTransactionRepository.PendingCountFor(buyer);
                    long paid = TreasuryStore.GetPersonalSilver(seller) - sellerBefore;
                    bool ok = stranded >= 1 && open == 0 && paid == 0;
                    r.Add((name, ok, $"stranded {stranded}, open after the boot {open}, paid to the seller {paid}"));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.AtBuyPhaseForTest = null;
                    if (listingId != 0) MarketplaceStore.Cancel(seller, listingId);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            // The reset destroys one side on purpose; the other side was never theirs to lose.
            foreach (bool resetBuyer in new[] { true, false })
            {
                string name = resetBuyer ? "Marketplace restart: resetting the buyer of a stranded sale still pays the seller"
                                         : "Marketplace restart: resetting the seller of a stranded sale still hands the buyer the goods";
                string seller = User("s"), buyer = User("b");
                string reset = resetBuyer ? buyer : seller;
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed") || !TreasuryStore.DepositSilver(buyer, 5000, "selftest seed"))
                        throw new InvalidOperationException("seed");
                    listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out string why);
                    if (listingId == 0) throw new InvalidOperationException("post: " + why);
                    long sellerBefore = TreasuryStore.GetPersonalSilver(seller);
                    MarketplaceStore.AtBuyPhaseForTest = p => { if (p == "settled") throw new OperationCanceledException("selftest: process died at " + p); };
                    try { MarketplaceStore.Buy(buyer, listingId, 8, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N")); }
                    catch (OperationCanceledException) { }
                    finally { MarketplaceStore.AtBuyPhaseForTest = null; }
                    int stranded = KmhTransactionRepository.PendingCountFor(buyer);

                    if (!Economy.KmhEconomyReset.Begin(reset, "", out string beginWhy)) throw new InvalidOperationException("begin: " + beginWhy);
                    Economy.KmhEconomyReset.Run(reset, "");
                    bool finished = !Economy.KmhEconomyReset.InProgress;
                    long paid = TreasuryStore.GetPersonalSilver(seller) - sellerBefore;
                    int goods = ItemsOf(buyer);
                    bool whole = resetBuyer ? paid > 0 : goods == 8;
                    bool wiped = resetBuyer ? goods == 0 && TreasuryStore.GetPersonalSilver(buyer) == 0 : TreasuryStore.GetPersonalSilver(seller) == 0;
                    bool ok = stranded >= 1 && finished && whole && wiped && KmhTransactionRepository.PendingCountFor(buyer) == 0;
                    r.Add((name, ok, $"stranded {stranded}, reset finished {finished}, seller paid {paid}, buyer holds {goods} Steel"
                                   + (whole ? "" : resetBuyer ? " - THE SELLER LOST THE SALE" : " - THE BUYER PAID FOR NOTHING")
                                   + (wiped ? "" : " - THE RESET PLAYER KEPT VALUE")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.AtBuyPhaseForTest = null;
                    if (listingId != 0) MarketplaceStore.Cancel(seller, listingId);
                    KmhTransactionRepository.AbortAllFor(buyer);
                    if (Economy.KmhEconomyReset.Resetting(reset)) Economy.KmhEconomyReset.ResetForTest();
                    Economy.EconomyResetStore.Forget(reset);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            // Closing the step with the seller unpaid would leave a row the boot then closes as stale, unpaid for good.
            {
                const string name = "Marketplace restart: a reset that cannot pay the other side yet waits, and pays the seller once the disk recovers";
                string seller = User("s"), buyer = User("b");
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed") || !TreasuryStore.DepositSilver(buyer, 5000, "selftest seed"))
                        throw new InvalidOperationException("seed");
                    listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out string why);
                    if (listingId == 0) throw new InvalidOperationException("post: " + why);
                    long sellerBefore = TreasuryStore.GetPersonalSilver(seller);
                    MarketplaceStore.AtBuyPhaseForTest = p => { if (p == "settled") throw new OperationCanceledException("selftest: process died at " + p); };
                    try { MarketplaceStore.Buy(buyer, listingId, 8, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N")); }
                    catch (OperationCanceledException) { }
                    finally { MarketplaceStore.AtBuyPhaseForTest = null; }

                    if (!Economy.KmhEconomyReset.Begin(buyer, "", out string beginWhy)) throw new InvalidOperationException("begin: " + beginWhy);
                    using (Persistence.JsonFileStore.FailWritesForTest(p => p == Persistence.KmhDataPaths.TreasuryFile ? "selftest: disk full" : null))
                        Economy.KmhEconomyReset.Run(buyer, "");
                    bool waited = Economy.KmhEconomyReset.Resetting(buyer) && TreasuryStore.GetPersonalSilver(seller) == sellerBefore;
                    Economy.KmhEconomyReset.ResumeStalled();
                    long paid = TreasuryStore.GetPersonalSilver(seller) - sellerBefore;
                    bool ok = waited && !Economy.KmhEconomyReset.InProgress && paid > 0 && KmhTransactionRepository.PendingCountFor(buyer) == 0;
                    r.Add((name, ok, $"waitedWhileUnwritable={waited} finishedByRetry={!Economy.KmhEconomyReset.InProgress} seller paid {paid}"
                                   + (paid > 0 ? "" : " - THE SELLER LOST THE SALE")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.AtBuyPhaseForTest = null;
                    Persistence.JsonFileStore.ClearFailureStateForTest();
                    if (listingId != 0) MarketplaceStore.Cancel(seller, listingId);
                    KmhTransactionRepository.AbortAllFor(buyer);
                    if (Economy.KmhEconomyReset.Resetting(buyer)) Economy.KmhEconomyReset.ResetForTest();
                    Economy.EconomyResetStore.Forget(buyer);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            // Returned goods must carry a marker, or a crash before Settle lets boot recovery return them a second time.
            void ReturnPhase(string phase, string what, bool expire)
            {
                string name = $"Marketplace restart: a crash {what} returns the goods once";
                string seller = User("s");
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed")) { r.Add((name, false, "seed goods")); return; }
                    listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out string why);
                    if (listingId == 0) { r.Add((name, false, "post: " + why)); return; }
                    int afterPost = ItemsOf(seller);

                    MarketplaceStore.AtStorePhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    try
                    {
                        if (expire) MarketplaceStore.ExpireListing(listingId, out _);
                        else MarketplaceStore.Cancel(seller, listingId);
                    }
                    catch (OperationCanceledException) { }
                    finally { MarketplaceStore.AtStorePhaseForTest = null; }

                    KmhTransactionRepository.RecoverOnBoot();
                    MarketplaceStore.RecoverPendingReserves();

                    int back = ItemsOf(seller);
                    bool ok = afterPost == 0 && back == 20 && ItemsInListing(seller, listingId) == 0;
                    r.Add((name, ok, ok ? "20 posted, 20 back, nothing left listed"
                                        : $"afterPost={afterPost} back={back} stillListed={ItemsInListing(seller, listingId)}"
                                          + (back > 20 ? " - GOODS MINTED ON RECOVERY" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.AtStorePhaseForTest = null;
                    TreasuryStore.PurgeOwnerForTest(seller);
                    Recovery.RecoveryStore.ClearUser(seller);
                }
            }

            ReturnPhase("cancel-returned", "after a cancelled listing's goods go back, before the record is closed", expire: false);
            ReturnPhase("expire-returned", "after an expired listing's goods go back, before the record is closed", expire: true);

            // Recovery must not pay out a row whose listing still holds the goods, crashed before the store's own save.
            void BeforeRemoval(string phase, string what, bool crashPost)
            {
                string name = $"Marketplace restart: a crash {what} leaves the goods in one place";
                string seller = User("s");
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed")) { r.Add((name, false, "seed goods")); return; }
                    MarketplaceStore.AtStorePhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    try
                    {
                        if (crashPost) MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out _);
                        else
                        {
                            MarketplaceStore.AtStorePhaseForTest = null;
                            listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out _);
                            MarketplaceStore.AtStorePhaseForTest = p =>
                            {
                                if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                            };
                            MarketplaceStore.Cancel(seller, listingId);
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally { MarketplaceStore.AtStorePhaseForTest = null; }

                    if (listingId == 0)
                        foreach (Dto.MarketplaceListing l in MarketplaceStore.BuildSnapshot(seller)?.Listings ?? new List<Dto.MarketplaceListing>())
                            if (l.SellerUsername == seller) listingId = l.Id;

                    KmhTransactionRepository.RecoverOnBoot();
                    int inVault = ItemsOf(seller), listed = ItemsInListing(seller, listingId);

                    MarketplaceStore.Cancel(seller, listingId);
                    int finalVault = ItemsOf(seller), finalListed = ItemsInListing(seller, listingId);

                    bool ok = inVault + listed == 20 && finalVault == 20 && finalListed == 0;
                    r.Add((name, ok, ok ? $"after recovery {inVault} in vault + {listed} listed; after cancel 20 in vault"
                                        : $"after recovery vault={inVault} listed={listed}; after cancel vault={finalVault} listed={finalListed}"
                                          + (finalVault + finalListed > 20 ? " - GOODS MINTED ON RECOVERY" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.AtStorePhaseForTest = null;
                    TreasuryStore.PurgeOwnerForTest(seller);
                    Recovery.RecoveryStore.ClearUser(seller);
                }
            }

            // A refused Delivered write must fail the sale, or it is paid out and then refunded again at the next boot.
            {
                const string name = "Marketplace: a sale whose Delivered record is refused is refunded, not paid and then refunded too";
                string seller = User("s"), buyer = User("b");
                long listingId = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 20, "selftest seed")) throw new InvalidOperationException("seed goods");
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) throw new InvalidOperationException("seed silver");
                    listingId = MarketplaceStore.Post(seller, Steel, 20, 100, "public", 0, out string why);
                    if (listingId == 0) throw new InvalidOperationException("post: " + why);
                    Ledger before = Measure(buyer, seller, listingId);

                    bool ledgerDown = false, bought;
                    using (Persistence.JsonFileStore.FailWritesForTest(p => ledgerDown && p == Persistence.KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    {
                        MarketplaceStore.AtBuyPhaseForTest = p => { if (p == "approved") ledgerDown = true; };
                        try { bought = MarketplaceStore.Buy(buyer, listingId, 8, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N")); }
                        finally { MarketplaceStore.AtBuyPhaseForTest = null; }
                    }
                    KmhTransactionRepository.LoadFromDisk();
                    KmhTransactionRepository.RecoverOnBoot();
                    MarketplaceStore.RecoverPendingReserves();

                    Ledger after = Measure(buyer, seller, listingId);
                    bool ok = !bought && after.TotalSilver == before.TotalSilver && after.TotalItems == before.TotalItems && after.BuyerItems == 0;
                    r.Add((name, ok, ok ? "refused, and the restart put both sides back exactly"
                                        : $"bought={bought} | BEFORE {before} -> AFTER {after}"
                                          + (after.TotalSilver > before.TotalSilver || after.TotalItems > before.TotalItems ? " - PAID AND REFUNDED" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.AtBuyPhaseForTest = null;
                    if (listingId != 0) MarketplaceStore.Cancel(seller, listingId);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            BeforeRemoval("post-listed",     "after a listing is saved, before its post record is closed", crashPost: true);
            BeforeRemoval("cancel-recorded", "after a cancel is recorded, before the listing is removed", crashPost: false);

            {
                const string name = "House pool: a replayed credit lands once, however many credits came after the window filled";
                const int credits = 12;
                long before = MarketplaceStore.HousePoolBalance(), landed = 0;
                int window = MarketplaceStore.CreditMarkerWindow;
                try
                {
                    string run = Guid.NewGuid().ToString("N").Substring(0, 8);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    MarketplaceStore.CreditMarkerWindow = 8;
                    // Past the window, so trimming has begun; a wrong trim drops the newest marker on the spot.
                    for (int i = 0; i < credits; i++) MarketplaceStore.TryCreditHousePoolOnce($"selftest-hp-{run}-{i}", 1, "selftest");
                    long filled = MarketplaceStore.HousePoolBalance();
                    // Boot recovery replays the newest credits, after a crash between each credit and its row.
                    MarketplaceStore.TryCreditHousePoolOnce($"selftest-hp-{run}-{credits - 1}", 1, "selftest");
                    MarketplaceStore.TryCreditHousePoolOnce($"selftest-hp-{run}-{credits - 2}", 1, "selftest");
                    landed = MarketplaceStore.HousePoolBalance() - before;
                    bool persisted = System.IO.File.ReadAllText(Persistence.KmhDataPaths.MarketplaceFile).Contains($"\"selftest-hp-{run}-{credits - 1}\"");
                    bool ok = filled - before == credits && landed == credits && persisted;
                    r.Add((name, ok, ok ? $"{credits} credited, replays refused, newest marker on disk ({sw.ElapsedMilliseconds} ms)"
                                        : $"credited {filled - before} of {credits}, after replays {landed}, newestOnDisk={persisted}"
                                          + (landed > credits ? " - THE POOL WAS PAID TWICE" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    MarketplaceStore.CreditMarkerWindow = window;
                    if (landed > 0) MarketplaceStore.TryDebitHousePool(landed, "selftest credits removed");
                }
            }

            // Rounded to nearest, a 1.499-silver unit bought one at a time would cost 1 each: a third off the asking price.
            {
                const string name = "Marketplace: a fractional unit price is charged rounded up, and what the buyer pays is what the sale splits";
                string seller = User("s"), buyer = User("b");
                long poolBefore = MarketplaceStore.HousePoolBalance();
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 10, "selftest seed")) { r.Add((name, false, "seed goods")); }
                    else if (!TreasuryStore.DepositSilver(buyer, 100, "selftest seed")) { r.Add((name, false, "seed silver")); }
                    else
                    {
                        long listingId = MarketplaceStore.Post(seller, Steel, 10, 1, "public", 0, out string why, unitPriceMilli: 1499);
                        if (listingId == 0) r.Add((name, false, "post: " + why));
                        else
                        {
                            long sellerBefore = TreasuryStore.GetPersonalSilver(seller);
                            bool one = MarketplaceStore.Buy(buyer, listingId, 1, out _, out int got1, out int paid1);
                            bool three = MarketplaceStore.Buy(buyer, listingId, 3, out _, out int got3, out int paid3);
                            long buyerPaid = 100 - TreasuryStore.GetPersonalSilver(buyer);
                            long sellerGot = TreasuryStore.GetPersonalSilver(seller) - sellerBefore;
                            long houseGot = MarketplaceStore.HousePoolBalance() - poolBefore;
                            bool ok = one && three && got1 == 1 && paid1 == 2 && got3 == 3 && paid3 == 5
                                   && buyerPaid == 7 && sellerGot + houseGot == buyerPaid;
                            r.Add((name, ok, $"1 unit paid {paid1} (want 2), 3 units paid {paid3} (want 5); buyer -{buyerPaid}, seller +{sellerGot}, house +{houseGot}"));
                        }
                    }
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    long poolGain = MarketplaceStore.HousePoolBalance() - poolBefore;
                    if (poolGain > 0) MarketplaceStore.TryDebitHousePool(poolGain, "selftest tax removed");
                    MarketplaceStore.PurgeSeller(seller);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                }
            }

            return r;
        }
    }
}
