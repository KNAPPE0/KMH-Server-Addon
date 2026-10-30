using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Features.Auctions
{
    // Every auction delivery precedes the Settle closing its row, so this tests how recovery treats a row left open.
    internal static class KmhAuctionRestartSelfTest
    {
        private const string Steel = "Steel";
        private static string User(string tag) => $"selftest_ar{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private struct Ledger
        {
            public long SellerSilver, BuyerSilver, HousePool, HeldSilver, AuctionSilver;
            public int SellerItems, BuyerItems, HeldItems, AuctionItems;
            public long TotalSilver => SellerSilver + BuyerSilver + HousePool + HeldSilver + AuctionSilver;
            public int TotalItems => SellerItems + BuyerItems + HeldItems + AuctionItems;
            public override string ToString()
                => $"silver(seller {SellerSilver} buyer {BuyerSilver} house {HousePool} held {HeldSilver} "
                 + $"auction {AuctionSilver} = {TotalSilver}) "
                 + $"items(seller {SellerItems} buyer {BuyerItems} held {HeldItems} auction {AuctionItems} = {TotalItems})";
        }

        // The auction holds the goods and standing bid; omitting them makes a conserved settlement look like minting.
        private static void AddAuctionEscrow(ref Ledger g, long auctionId)
        {
            foreach (Dto.AuctionDto a in AuctionStore.AllForAdmin())
            {
                if (a.Id != auctionId) continue;
                g.AuctionItems += a.EscrowPayloads != null && a.EscrowPayloads.Count > 0
                    ? Items.KmhPayloadEscrow.TotalUnits(a.EscrowPayloads) : a.Qty;
                if (!string.IsNullOrEmpty(a.HighBidder)) g.AuctionSilver += a.CurrentBid;
            }
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

        private static void AddHolds(ref Ledger g, string user)
        {
            foreach (Recovery.Dto.RecoveryRecord rec in Recovery.RecoveryStore.ForUser(user))
            {
                if (rec == null || rec.Status == "resolved") continue;
                if (rec.Kind == "silver") g.HeldSilver += rec.Silver;
                else if (rec.Payload != null) g.HeldItems += rec.Payload.StackCount;
            }
        }

        private static Ledger Measure(string seller, string buyer, long auctionId)
        {
            var g = new Ledger
            {
                SellerSilver = TreasuryStore.GetPersonalSilver(seller),
                BuyerSilver = TreasuryStore.GetPersonalSilver(buyer),
                HousePool = Marketplace.MarketplaceStore.HousePoolBalance(),
                SellerItems = ItemsOf(seller),
                BuyerItems = ItemsOf(buyer),
            };
            AddHolds(ref g, seller);
            AddHolds(ref g, buyer);
            AddAuctionEscrow(ref g, auctionId);
            return g;
        }

        // Value in flight is still value, as long as some open row names it.
        private static (long Silver, int Items) OwedByLedger(string seller, string buyer)
        {
            long silver = 0; int items = 0;
            foreach (KmhTransaction t in KmhTransactionRepository.Pending())
            {
                bool mine = t.Player == seller || t.Player == buyer
                         || t.CounterPlayer == seller || t.CounterPlayer == buyer;
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

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            (bool ok, string detail) AtPhase(string phase, bool withBid, bool cancelInstead)
            {
                string seller = User("s"), buyer = User("b");
                long id = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 10, "selftest seed")) return (false, "seed goods");
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "seed silver");
                    (long posted, string why) = AuctionStore.Post(seller, Steel, "", 0, 10, 100, 0, 0, 1, "public");
                    id = posted;
                    if (id == 0) return (false, "post: " + why);

                    Ledger before = Measure(seller, buyer, id);
                    if (withBid && !AuctionStore.Bid(buyer, id, 400).Ok) return (false, "bid refused");

                    AuctionStore.AtValuePhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    try
                    {
                        if (cancelInstead) AuctionStore.Cancel(seller, id);
                        else AuctionStore.SettleNow(id);
                    }
                    catch (OperationCanceledException) { }
                    finally { AuctionStore.AtValuePhaseForTest = null; }

                    KmhTransactionRepository.RecoverOnBoot();

                    Ledger after = Measure(seller, buyer, id);
                    (long owedSilver, int owedItems) = OwedByLedger(seller, buyer);
                    long silverDelta = after.TotalSilver - before.TotalSilver;
                    int itemsDelta = after.TotalItems - before.TotalItems;

                    bool noSilverMinted = silverDelta <= 0;
                    bool noItemsMinted = itemsDelta <= 0;
                    bool silverAccounted = -silverDelta <= owedSilver;
                    bool itemsAccounted = -itemsDelta <= owedItems;

                    bool ok = noSilverMinted && noItemsMinted && silverAccounted && itemsAccounted;
                    return (ok, ok
                        ? $"silver {silverDelta:+#;-#;0}, items {itemsDelta:+#;-#;0}, ledger owes {owedSilver}/{owedItems}"
                        : $"BEFORE {before} -> AFTER {after} | ledger owes {owedSilver} silver, {owedItems} item(s)"
                          + (noSilverMinted ? "" : $" | SILVER MINTED {silverDelta:+#;-#;0}")
                          + (noItemsMinted ? "" : $" | ITEMS MINTED {itemsDelta:+#;-#;0}")
                          + (silverAccounted ? "" : $" | {-silverDelta} SILVER LOST, NOTHING OWES IT")
                          + (itemsAccounted ? "" : $" | {-itemsDelta} ITEM(S) LOST, NOTHING OWES THEM"));
                }
                finally
                {
                    AuctionStore.AtValuePhaseForTest = null;
                    if (id != 0) AuctionStore.Cancel(seller, id);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            void Phase(string phase, string what, bool withBid, bool cancelInstead = false)
            {
                string name = $"Auction restart: a crash {what} conserves value";
                try { (bool ok, string detail) = AtPhase(phase, withBid, cancelInstead); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Phase("goods-delivered",  "after the winner is given the goods, before the row is closed", withBid: true);
            Phase("payout-paid",     "after the seller is paid, before the row is closed",             withBid: true);
            Phase("unsold-returned", "after an unsold lot goes back, before the row is closed",        withBid: false);
            Phase("cancel-returned", "after a cancelled lot goes back, before the row is closed",      withBid: false, cancelInstead: true);
            Phase("outbid-refunded", "after a displaced bid is refunded, before the row is closed",    withBid: true);

            // Rows are durable but the removal is not: once recovery pays them, the auction still on disk must not settle again.
            void BeforeRemoval(string phase, string what, bool withBid, bool cancelInstead)
            {
                string name = $"Auction restart: a crash {what} is settled once";
                string seller = User("s"), buyer = User("b");
                long id = 0;
                try
                {
                    if (!TreasuryStore.DepositItem(seller, Steel, 10, "selftest seed")) { r.Add((name, false, "seed goods")); return; }
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) { r.Add((name, false, "seed silver")); return; }
                    (long posted, string why) = AuctionStore.Post(seller, Steel, "", 0, 10, 100, 0, 0, 1, "public");
                    id = posted;
                    if (id == 0) { r.Add((name, false, "post: " + why)); return; }
                    Ledger before = Measure(seller, buyer, id);
                    if (withBid && !AuctionStore.Bid(buyer, id, 400).Ok) { r.Add((name, false, "bid refused")); return; }

                    AuctionStore.AtValuePhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    try
                    {
                        if (cancelInstead) AuctionStore.Cancel(seller, id);
                        else AuctionStore.SettleNow(id);
                    }
                    catch (OperationCanceledException) { }
                    finally { AuctionStore.AtValuePhaseForTest = null; }

                    KmhTransactionRepository.RecoverOnBoot();
                    // What runs next: the sweeper settles the ended auction, or the seller cancels again.
                    if (cancelInstead) AuctionStore.Cancel(seller, id);
                    else AuctionStore.SettleNow(id);

                    Ledger after = Measure(seller, buyer, id);
                    long silverDelta = after.TotalSilver - before.TotalSilver;
                    int itemsDelta = after.TotalItems - before.TotalItems;
                    bool ok = silverDelta <= 0 && itemsDelta == 0 && !AuctionStore.Exists(id);
                    r.Add((name, ok, ok ? $"silver {silverDelta:+#;-#;0}, items {itemsDelta:+#;-#;0}, auction closed"
                                        : $"BEFORE {before} -> AFTER {after} open={AuctionStore.Exists(id)}"
                                          + (itemsDelta > 0 ? $" | ITEMS MINTED {itemsDelta:+#;-#;0}" : "")
                                          + (silverDelta > 0 ? $" | SILVER MINTED {silverDelta:+#;-#;0}" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    AuctionStore.AtValuePhaseForTest = null;
                    if (id != 0 && AuctionStore.Exists(id)) AuctionStore.Cancel(seller, id);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            BeforeRemoval("settle-recorded", "after a sale is recorded, before the auction is removed", withBid: true, cancelInstead: false);
            BeforeRemoval("settle-recorded", "after an unsold lot is recorded, before the auction is removed", withBid: false, cancelInstead: false);
            BeforeRemoval("cancel-recorded", "after a cancel is recorded, before the auction is removed", withBid: false, cancelInstead: true);

            return r;
        }
    }
}
