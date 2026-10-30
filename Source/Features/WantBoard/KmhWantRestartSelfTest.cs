using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Features.WantBoard
{
    // Whatever a crash mid-fulfil leaves unpaid must still be named by the fill's row, or it is gone.
    internal static class KmhWantRestartSelfTest
    {
        private const string Steel = "Steel";
        private static string User(string tag) => $"selftest_wr{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static int ItemsOf(string user)
        {
            int n = 0;
            var snap = TreasuryStore.GetSnapshotFor(user);
            if (snap?.Items != null)
                foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
            return n;
        }

        // A payout that has been written down but not yet paid is still the seller's; boot recovery pays it.
        private static long OwedByLedger(string a, string b)
        {
            long silver = 0;
            foreach (Transactions.KmhTransaction t in Transactions.KmhTransactionRepository.Pending())
                if (t.Player == a || t.Player == b || t.CounterPlayer == a || t.CounterPlayer == b)
                    silver += t.EscrowSilver + t.CounterSilver + t.PayoutSilver + t.HouseTax + t.GuildTax;
            return silver;
        }

        private static (long Silver, int Items) Holds(string user)
        {
            long s = 0; int i = 0;
            foreach (Recovery.Dto.RecoveryRecord rec in Recovery.RecoveryStore.ForUser(user))
            {
                if (rec == null || rec.Status == "resolved") continue;
                if (rec.Kind == "silver") s += rec.Silver;
                else if (rec.Payload != null) i += rec.Payload.StackCount;
            }
            return (s, i);
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            (bool ok, string detail) AtPhase(string phase)
            {
                string buyer = User("b"), seller = User("s");
                const int Qty = 5, Unit = 100;
                try
                {
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "seed buyer");
                    if (!TreasuryStore.DepositItem(seller, Steel, Qty, "selftest seed")) return (false, "seed seller");
                    (long id, string why) = WantStore.Post(buyer, Steel, Qty, Unit, 1, "public", 0, "");
                    if (id == 0) return (false, "post: " + why);

                    long buyerSilver0 = TreasuryStore.GetPersonalSilver(buyer);
                    long sellerSilver0 = TreasuryStore.GetPersonalSilver(seller);
                    long house0 = Marketplace.MarketplaceStore.HousePoolBalance();
                    int buyerItems0 = ItemsOf(buyer), sellerItems0 = ItemsOf(seller);
                    (long bh0, int bi0) = Holds(buyer); (long sh0, int si0) = Holds(seller);
                    long escrowed = (long)Qty * Unit;

                    WantStore.AtFulfillPhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    try { WantStore.Fulfill(seller, id, Qty); }
                    catch (OperationCanceledException) { }
                    finally { WantStore.AtFulfillPhaseForTest = null; }

                    long buyerSilver1 = TreasuryStore.GetPersonalSilver(buyer);
                    long sellerSilver1 = TreasuryStore.GetPersonalSilver(seller);
                    long house1 = Marketplace.MarketplaceStore.HousePoolBalance();
                    int buyerItems1 = ItemsOf(buyer), sellerItems1 = ItemsOf(seller);
                    (long bh1, int bi1) = Holds(buyer); (long sh1, int si1) = Holds(seller);

                    // The want still holds whatever escrow it has not spent.
                    long stillEscrowed = 0;
                    foreach (Dto.WantDto w in WantStore.AllForAdmin())
                        if (w.Id == id) stillEscrowed = (long)(w.QtyWanted - w.QtyFilled) * w.UnitPriceSilver;

                    long silverBefore = buyerSilver0 + sellerSilver0 + house0 + bh0 + sh0 + escrowed;
                    long silverAfter = buyerSilver1 + sellerSilver1 + house1 + bh1 + sh1 + stillEscrowed
                                     + OwedByLedger(buyer, seller);
                    int itemsBefore = buyerItems0 + sellerItems0 + bi0 + si0;
                    int itemsAfter = buyerItems1 + sellerItems1 + bi1 + si1;

                    bool noMint = silverAfter <= silverBefore && itemsAfter <= itemsBefore;
                    bool noLoss = silverAfter == silverBefore && itemsAfter == itemsBefore;
                    return (noMint && noLoss,
                        noMint && noLoss
                            ? $"conserved: silver {silverAfter}, items {itemsAfter}"
                            : $"silver {silverBefore} -> {silverAfter} ({silverAfter - silverBefore:+#;-#;0}), "
                              + $"items {itemsBefore} -> {itemsAfter} ({itemsAfter - itemsBefore:+#;-#;0})"
                              + (noMint ? "" : " | MINTED"));
                }
                finally
                {
                    WantStore.AtFulfillPhaseForTest = null;
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                }
            }

            void Phase(string phase, string what)
            {
                string name = $"Want restart: a crash {what} conserves value";
                try { (bool ok, string detail) = AtPhase(phase); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Phase("goods-delivered", "after the buyer has the goods, before the seller is paid");
            Phase("taxes-taken",     "after the taxes are taken, before the seller is paid");

            // Closing a want and refunding it are separate writes, so a refund record must bridge a crash between them.
            foreach ((string phase, bool expire) in new[] { ("refund-recorded", false), ("refund-recorded", true), ("refund-credited", false), ("", false) })
            {
                string name = phase == ""
                    ? "Want: a cancel that lands leaves no refund record behind"
                    : $"Want: a crash {(phase == "refund-recorded" ? "before" : "after")} a{(expire ? "n expiry" : " cancel")} refund lands pays it exactly once at the next boot";
                string buyer = User("b");
                try
                {
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) throw new InvalidOperationException("seed");
                    (long id, string why) = WantStore.Post(buyer, Steel, 5, 100, 1, "public", 0, "");
                    if (id == 0) throw new InvalidOperationException("post: " + why);
                    long posted = TreasuryStore.GetPersonalSilver(buyer);
                    WantStore.AtRefundPhaseForTest = p => { if (p == phase) throw new OperationCanceledException("selftest: process died at " + p); };
                    try { if (expire) WantStore.ExpireRefund(id); else WantStore.Cancel(buyer, id); }
                    catch (OperationCanceledException) { }
                    finally { WantStore.AtRefundPhaseForTest = null; }
                    long crashed = TreasuryStore.GetPersonalSilver(buyer);
                    // A crash loses memory: only what reached Wants.json comes back.
                    if (phase != "") WantStore.LoadFromDisk();
                    int recorded = WantStore.PendingRefundCountForTest;
                    // The next boot, and the one after it.
                    WantStore.RecoverPendingRefunds();
                    WantStore.RecoverPendingRefunds();
                    long refunded = TreasuryStore.GetPersonalSilver(buyer) - posted;
                    bool ok = refunded == 500 && (phase == "" ? recorded == 0 : recorded == 1) && WantStore.PendingRefundCountForTest == 0;
                    r.Add((name, ok, ok ? $"500 refunded once ({crashed - posted} before the boot)"
                                        : $"refunded {refunded} of 500, records after the close {recorded}, after the boots {WantStore.PendingRefundCountForTest}"
                                          + (refunded == 0 ? " - THE ESCROW WAS LOST" : refunded > 500 ? " - REFUNDED TWICE" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    WantStore.AtRefundPhaseForTest = null;
                    WantStore.DropPendingRefundsFor(buyer);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            {
                const string name = "Want: a refund cut off before a season reset is dropped with the season, never paid into the new one";
                string buyer = User("b");
                try
                {
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) throw new InvalidOperationException("seed");
                    (long id, string why) = WantStore.Post(buyer, Steel, 5, 100, 1, "public", 0, "");
                    if (id == 0) throw new InvalidOperationException("post: " + why);
                    WantStore.AtRefundPhaseForTest = p => { if (p == "refund-recorded") throw new OperationCanceledException("selftest: process died at " + p); };
                    try { WantStore.Cancel(buyer, id); }
                    catch (OperationCanceledException) { }
                    finally { WantStore.AtRefundPhaseForTest = null; }
                    int before = WantStore.PendingRefundCountForTest;
                    WantStore.ClearForNewSeason();
                    int after = WantStore.PendingRefundCountForTest;
                    bool ok = before >= 1 && after == 0;
                    r.Add((name, ok, $"pending refunds: {before} before the reset, {after} after"));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    WantStore.AtRefundPhaseForTest = null;
                    WantStore.DropPendingRefundsFor(buyer);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    Recovery.RecoveryStore.ClearUser(buyer);
                }
            }

            {
                const string name = "Want fill: a fill left open writes down the legs that did land, so the next boot pays only the rest";
                string buyer = User("b"), seller = User("s");
                Transactions.KmhTransaction row = null;
                try
                {
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed") || !TreasuryStore.DepositItem(seller, Steel, 5, "selftest seed"))
                        throw new InvalidOperationException("seed");
                    (long id, string why) = WantStore.Post(buyer, Steel, 5, 100, 1, "public", 0, "");
                    if (id == 0) throw new InvalidOperationException("post: " + why);
                    long house0 = Marketplace.MarketplaceStore.HousePoolBalance();
                    bool vaultsFail = false;
                    // The vaults stop saving once the fill is recorded: the house leg lands, the goods and payout legs do not.
                    using (Persistence.JsonFileStore.FailWritesForTest(p => vaultsFail && p == Persistence.KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    {
                        WantStore.AtFulfillPhaseForTest = p => { if (p == "fill-recorded") vaultsFail = true; };
                        try { WantStore.Fulfill(seller, id, 5); }
                        finally { WantStore.AtFulfillPhaseForTest = null; }
                    }
                    long houseLanded = Marketplace.MarketplaceStore.HousePoolBalance() - house0;
                    foreach (Transactions.KmhTransaction t in Transactions.KmhTransactionRepository.Pending())
                        if (t.Player == seller && t.TargetKind == Transactions.KmhTransactionRepository.TargetWantFill) row = t;
                    long onDisk = -1;
                    if (row != null)
                        foreach (Newtonsoft.Json.Linq.JObject o in Newtonsoft.Json.Linq.JObject.Parse(
                                     System.IO.File.ReadAllText(Persistence.KmhDataPaths.TransactionLedgerFile)).Descendants().OfType<Newtonsoft.Json.Linq.JObject>())
                            if ((string)o["id"] == row.Id) onDisk = (long?)o["tax_house"] ?? 0;
                    bool ok = houseLanded > 0 && row != null && row.PayoutSilver > 0 && onDisk == 0;
                    r.Add((name, ok, ok ? $"house tax {houseLanded} landed and is cleared on disk; the payout is still owed"
                                        : $"houseLanded={houseLanded}, rowOpen={row != null}, payoutOwed={row?.PayoutSilver ?? 0}, houseTaxOnDisk={onDisk}"
                                          + (onDisk > 0 ? " - THE NEXT BOOT CREDITS IT AGAIN ONCE ITS MARKER AGES OUT" : "")));
                }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally
                {
                    WantStore.AtFulfillPhaseForTest = null;
                    if (row != null) WantStore.CompleteFill(row);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    Recovery.RecoveryStore.ClearUser(buyer);
                    Recovery.RecoveryStore.ClearUser(seller);
                }
            }

            return r;
        }
    }
}
