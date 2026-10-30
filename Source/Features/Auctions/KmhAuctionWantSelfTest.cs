using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Features.WantBoard;

namespace KMHServerAddon.Features.Auctions
{
    // Both boards hold buyer silver and seller goods at once: one settlement, escrow only falls, nothing paid twice.
    internal static class KmhAuctionWantSelfTest
    {
        private static string User(string tag) => $"selftest_{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private const string Steel = "Steel";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            long SilverOf(string u) => TreasuryStore.GetPersonalSilver(u);
            int ItemsOf(string u)
            {
                int n = 0;
                var snap = TreasuryStore.GetSnapshotFor(u);
                if (snap?.Items != null)
                    foreach (KeyValuePair<string, int> kv in snap.Items)
                        if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
                return n;
            }
            int PayloadUnits(string u)
            {
                int n = 0;
                var snap = TreasuryStore.GetSnapshotFor(u);
                if (snap?.ItemPayloads != null)
                    foreach (Items.KmhThingPayload p in snap.ItemPayloads)
                        if (p.DefName == "MeleeWeapon_LongSword") n += p.StackCount;
                return n;
            }
            void Cleanup(params string[] users) { foreach (string u in users) TreasuryStore.PurgeOwnerForTest(u); }

            Check("Auction: a buyout settles the moment the bid reaches it, and only once", () =>
            {
                string seller = User("as"), buyer = User("ab");
                if (!TreasuryStore.DepositItem(seller, Steel, 10, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(buyer, 5000, "seed")) return (false, "seed silver");

                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 10, 100, 10, 1000, 24, "public");
                if (id == 0) return (false, "post: " + why);

                long sellerBefore = SilverOf(seller);
                long buyerBefore = SilverOf(buyer);
                AuctionStore.BidResult bid = AuctionStore.Bid(buyer, id, 1000);
                if (!bid.Ok) return (false, "bid: " + bid.Reason);

                bool settledNow = bid.Settled && bid.Settlement.Done;
                int buyerGoods = ItemsOf(buyer);
                long sellerPaid = SilverOf(seller) - sellerBefore;
                long buyerSpent = buyerBefore - SilverOf(buyer);

                AuctionStore.SettleOutcome again = AuctionStore.SettleNow(id);
                int goodsAfter = ItemsOf(buyer);
                long sellerAfter = SilverOf(seller) - sellerBefore;

                Cleanup(seller, buyer);
                bool ok = settledNow && buyerGoods == 10 && buyerSpent == 1000 && sellerPaid > 0
                       && !again.Done && goodsAfter == 10 && sellerAfter == sellerPaid;
                return (ok, ok ? $"buyout settled once: buyer paid 1000 for 10, seller kept {sellerPaid} after tax"
                              : $"settledOnBid={settledNow}, buyerGoods={buyerGoods}, buyerSpent={buyerSpent}, "
                                + $"sellerPaid={sellerPaid}, secondSettle={again.Done}, goodsAfter={goodsAfter}, "
                                + $"sellerAfter={sellerAfter}");
            });

            Check("Auction: a bid can never exceed the buyout it is capped to", () =>
            {
                string seller = User("as"), buyer = User("ab");
                if (!TreasuryStore.DepositItem(seller, Steel, 5, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(buyer, 100000, "seed")) return (false, "seed silver");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 5, 100, 10, 500, 24, "public");
                if (id == 0) return (false, "post: " + why);

                long before = SilverOf(buyer);
                AuctionStore.BidResult bid = AuctionStore.Bid(buyer, id, 90000);
                long spent = before - SilverOf(buyer);

                Cleanup(seller, buyer);
                bool ok = bid.Ok && spent == 500;
                return (ok, ok ? "a 90000 bid on a 500 buyout takes 500" : $"ok={bid.Ok} ({bid.Reason}), spent={spent}");
            });

            Check("Auction: an outbid bidder is refunded exactly once", () =>
            {
                string seller = User("as"), first = User("a1"), second = User("a2");
                if (!TreasuryStore.DepositItem(seller, Steel, 4, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(first, 3000, "seed")) return (false, "seed 1");
                if (!TreasuryStore.DepositSilver(second, 3000, "seed")) return (false, "seed 2");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 4, 100, 10, 0, 24, "public");
                if (id == 0) return (false, "post: " + why);

                long firstBefore = SilverOf(first);
                if (!AuctionStore.Bid(first, id, 100).Ok) return (false, "first bid");
                long afterFirst = SilverOf(first);
                AuctionStore.BidResult b2 = AuctionStore.Bid(second, id, 200);
                if (!b2.Ok) return (false, "second bid: " + b2.Reason);
                long afterOutbid = SilverOf(first);

                Cleanup(seller, first, second);
                bool ok = afterFirst == firstBefore - 100 && afterOutbid == firstBefore
                       && string.Equals(b2.OutbidUser, first, StringComparison.OrdinalIgnoreCase);
                return (ok, ok ? "100 escrowed then returned in full when outbid"
                              : $"held={firstBefore - afterFirst}, backTo={afterOutbid - afterFirst}, outbid={b2.OutbidUser}");
            });

            Check("Auction: a seller cannot bid on their own auction, and a closed one takes no bid", () =>
            {
                string seller = User("as"), buyer = User("ab");
                if (!TreasuryStore.DepositItem(seller, Steel, 3, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(seller, 2000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositSilver(buyer, 2000, "seed")) return (false, "seed buyer");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 3, 100, 10, 0, 24, "public");
                if (id == 0) return (false, "post: " + why);

                long sellerBefore = SilverOf(seller);
                bool selfRefused = !AuctionStore.Bid(seller, id, 500).Ok;
                bool nothingTaken = SilverOf(seller) == sellerBefore;
                bool unknownRefused = !AuctionStore.Bid(buyer, id + 999999, 500).Ok;
                bool lowRefused = !AuctionStore.Bid(buyer, id, 1).Ok;
                bool zeroRefused = !AuctionStore.Bid(buyer, id, 0).Ok;
                bool negativeRefused = !AuctionStore.Bid(buyer, id, -5).Ok;

                Cleanup(seller, buyer);
                bool ok = selfRefused && nothingTaken && unknownRefused && lowRefused && zeroRefused && negativeRefused;
                return (ok, ok ? "self, unknown, under-minimum, zero and negative bids all refused with nothing escrowed"
                              : $"self={selfRefused}, nothingTaken={nothingTaken}, unknown={unknownRefused}, "
                                + $"low={lowRefused}, zero={zeroRefused}, negative={negativeRefused}");
            });

            Check("Auction: many bidders racing one auction leave exactly one holding escrow", () =>
            {
                string seller = User("as");
                if (!TreasuryStore.DepositItem(seller, Steel, 6, "seed")) return (false, "seed goods");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 6, 100, 10, 0, 24, "public");
                if (id == 0) return (false, "post: " + why);

                var bidders = new string[8];
                for (int i = 0; i < bidders.Length; i++)
                {
                    bidders[i] = User("ar" + i);
                    if (!TreasuryStore.DepositSilver(bidders[i], 10000, "seed")) return (false, "seed bidder");
                }
                int accepted = 0;
                using (var gate = new ManualResetEventSlim(false))
                {
                    var tasks = new Task[bidders.Length];
                    for (int i = 0; i < bidders.Length; i++)
                    {
                        string who = bidders[i];
                        long amount = 200 + i * 50;
                        tasks[i] = Task.Run(() =>
                        {
                            gate.Wait();
                            if (AuctionStore.Bid(who, id, amount).Ok) Interlocked.Increment(ref accepted);
                        });
                    }
                    gate.Set();
                    Task.WaitAll(tasks, TimeSpan.FromSeconds(30));
                }

                // Whatever the order, at most one bidder is out of pocket: everyone else was refunded.
                int outOfPocket = 0;
                long held = 0;
                foreach (string b in bidders)
                {
                    long missing = 10000 - SilverOf(b);
                    if (missing > 0) { outOfPocket++; held += missing; }
                }
                Cleanup(seller);
                foreach (string b in bidders) TreasuryStore.PurgeOwnerForTest(b);
                bool ok = accepted >= 1 && outOfPocket == 1;
                return (ok, ok ? $"{accepted} of 8 bids accepted, exactly 1 bidder holds {held} in escrow"
                              : $"accepted={accepted}, bidders out of pocket={outOfPocket} (total {held}) - "
                                + "SILVER IS HELD BY MORE THAN THE STANDING BID");
            });

            Check("Want: filling a want spends its escrow, and the leftover is what is refunded", () =>
            {
                string buyer = User("wb"), seller = User("ws");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(seller, Steel, 10, "seed")) return (false, "seed goods");

                (long id, string why) = WantStore.Post(buyer, Steel, 10, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);
                long escrowed = 10000 - SilverOf(buyer);

                // Half the want, so the refund on expiry must be the other half and not the whole escrow.
                WantStore.FulfillResult fill = WantStore.Fulfill(seller, id, 5);
                if (!fill.Ok) return (false, "fulfill: " + fill.Reason);

                long beforeExpiry = SilverOf(buyer);
                WantStore.ExpireOutcome exp = WantStore.ExpireRefund(id);
                long refunded = SilverOf(buyer) - beforeExpiry;

                Cleanup(buyer, seller);
                bool ok = escrowed == 1000 && exp.Done && refunded == 500;
                return (ok, ok ? "1000 escrowed, 5 of 10 filled, 500 refunded"
                              : $"escrowed={escrowed} (expected 1000), expired={exp.Done}, refunded={refunded} "
                                + "(expected 500) - A PARTIALLY FILLED WANT REFUNDS SILVER IT ALREADY SPENT");
            });

            // A want must be able to offer fractional silver, since a listing can ask 0.55.
            Check("Want: a fractional unit price escrows the rounded-up total, and its fills plus the refund add up to that escrow exactly", () =>
            {
                string buyer = User("wb"), s1 = User("ws"), s2 = User("ws");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(s1, Steel, 10, "seed") || !TreasuryStore.DepositItem(s2, Steel, 10, "seed")) return (false, "seed goods");
                (long id, string why) = WantStore.Post(buyer, Steel, 7, 1, 24, "public", 0, "", unitPriceMilli: 550);
                if (id == 0) return (false, "post: " + why);
                long escrowed = 10000 - SilverOf(buyer);
                WantStore.FulfillResult a = WantStore.Fulfill(s1, id, 1);
                WantStore.FulfillResult b = WantStore.Fulfill(s2, id, 2);
                long beforeExpiry = SilverOf(buyer);
                WantStore.ExpireOutcome exp = WantStore.ExpireRefund(id);
                long refunded = SilverOf(buyer) - beforeExpiry;
                Cleanup(buyer, s1, s2);
                long full = 0;
                for (int f = 0; f < 7; f++) full += WantStore.FillGross(550, f, 1);
                bool math = WantStore.CostUpTo(550, 7) == 4 && full == 4 && WantStore.FillGross(550, 0, 1) == 1 && WantStore.FillGross(550, 1, 2) == 1
                         && WantStore.CostUpTo(100000, 10) == 1000 && WantStore.FillGross(100000, 3, 2) == 200;
                bool ok = math && a.Ok && b.Ok && exp.Done && escrowed == 4 && refunded == 2 && a.UnitPriceMilli == 550;
                return (ok, $"escrowed {escrowed} (want 4), fills ok={a.Ok}/{b.Ok}, refunded {refunded} (want 2), one by one {full} (want 4), math={math}");
            });

            Check("Want: a fill at a fractional price that another fill overtook is returned, not paid at the wrong share", () =>
            {
                string buyer = User("wb"), s1 = User("ws"), s2 = User("ws");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(s1, Steel, 10, "seed") || !TreasuryStore.DepositItem(s2, Steel, 10, "seed")) return (false, "seed goods");
                (long id, string why) = WantStore.Post(buyer, Steel, 6, 1, 24, "public", 0, "", unitPriceMilli: 550);
                if (id == 0) return (false, "post: " + why);
                long escrowed = 10000 - SilverOf(buyer);
                bool fired = false;
                WantStore.FulfillResult inner = null, outer;
                WantStore.AtFulfillPhaseForTest = phase => { if (phase == "taken" && !fired) { fired = true; inner = WantStore.Fulfill(s2, id, 1); } };
                try { outer = WantStore.Fulfill(s1, id, 2); }
                finally { WantStore.AtFulfillPhaseForTest = null; }
                int s1Left = ItemsOf(s1);
                long beforeExpiry = SilverOf(buyer);
                WantStore.ExpireRefund(id);
                long refunded = SilverOf(buyer) - beforeExpiry;
                Cleanup(buyer, s1, s2);
                bool ok = inner != null && inner.Ok && !outer.Ok && s1Left == 10 && escrowed == 4 && refunded == 3;
                return (ok, $"inner fill ok={inner?.Ok}, overtaken fill ok={outer.Ok} ({outer.Reason}), first seller keeps {s1Left}/10, refunded {refunded} of {escrowed}");
            });

            Check("Want: a want cannot be filled past what it asked for", () =>
            {
                string buyer = User("wb"), seller = User("ws");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(seller, Steel, 20, "seed")) return (false, "seed goods");
                (long id, string why) = WantStore.Post(buyer, Steel, 5, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);

                WantStore.FulfillResult fill = WantStore.Fulfill(seller, id, 20);
                int buyerGot = ItemsOf(buyer);
                int sellerLeft = ItemsOf(seller);
                bool closed = !WantStore.Fulfill(seller, id, 1).Ok;

                Cleanup(buyer, seller);
                bool ok = fill.Ok && buyerGot == 5 && sellerLeft == 15 && closed;
                return (ok, ok ? "20 offered against a want for 5: 5 delivered, 15 returned, the want is closed"
                              : $"ok={fill.Ok} ({fill.Reason}), buyerGot={buyerGot}, sellerLeft={sellerLeft}, closed={closed}");
            });

            Check("Want: two sellers racing the last unit never deliver more than was wanted", () =>
            {
                string buyer = User("wb");
                if (!TreasuryStore.DepositSilver(buyer, 20000, "seed")) return (false, "seed silver");
                (long id, string why) = WantStore.Post(buyer, Steel, 4, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);

                var sellers = new string[6];
                for (int i = 0; i < sellers.Length; i++)
                {
                    sellers[i] = User("wr" + i);
                    if (!TreasuryStore.DepositItem(sellers[i], Steel, 4, "seed")) return (false, "seed seller");
                }
                int filled = 0;
                using (var gate = new ManualResetEventSlim(false))
                {
                    var tasks = new Task[sellers.Length];
                    for (int i = 0; i < sellers.Length; i++)
                    {
                        string who = sellers[i];
                        tasks[i] = Task.Run(() =>
                        {
                            gate.Wait();
                            WantStore.FulfillResult f = WantStore.Fulfill(who, id, 4);
                            if (f.Ok) Interlocked.Add(ref filled, f.FilledQty > 0 ? f.FilledQty : 0);
                        });
                    }
                    gate.Set();
                    Task.WaitAll(tasks, TimeSpan.FromSeconds(30));
                }

                int buyerGot = ItemsOf(buyer);
                int sellersLeft = 0;
                foreach (string sname in sellers) sellersLeft += ItemsOf(sname);

                Cleanup(buyer);
                foreach (string sname in sellers) TreasuryStore.PurgeOwnerForTest(sname);
                bool ok = buyerGot <= 4 && buyerGot + sellersLeft == 24;
                return (ok, ok ? $"6 racing fills: buyer got {buyerGot} of 4, all 24 units accounted for"
                              : $"buyerGot={buyerGot} (max 4), sellersLeft={sellersLeft}, total={buyerGot + sellersLeft} "
                                + "(expected 24) - ITEMS WERE DUPLICATED OR LOST");
            });

            Check("Want: cancelling returns the unspent escrow and nothing more", () =>
            {
                string buyer = User("wb"), seller = User("ws");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(seller, Steel, 10, "seed")) return (false, "seed goods");
                (long id, string why) = WantStore.Post(buyer, Steel, 10, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);
                if (!WantStore.Fulfill(seller, id, 3).Ok) return (false, "fulfill");

                long before = SilverOf(buyer);
                (bool ok1, string r1) = WantStore.Cancel(buyer, id);
                long back = SilverOf(buyer) - before;
                (bool ok2, string _) = WantStore.Cancel(buyer, id);
                long backAgain = SilverOf(buyer) - before - back;

                Cleanup(buyer, seller);
                bool ok = ok1 && back == 700 && !ok2 && backAgain == 0;
                return (ok, ok ? "3 of 10 filled, 700 returned on cancel, the repeat returns nothing"
                              : $"cancelled={ok1} ({r1}), returned={back} (expected 700), "
                                + $"repeatRefused={!ok2}, returnedAgain={backAgain}");
            });

            Check("Want: a full-state fill spends escrow the same way a compact one does", () =>
            {
                // Quality and gear state exist only on payloads, so this fill path has its own copy of the escrow arithmetic.
                string buyer = User("wb"), seller = User("ws");
                if (!TreasuryStore.DepositSilver(buyer, 20000, "seed")) return (false, "seed silver");
                for (int i = 0; i < 4; i++)
                    if (!TreasuryStore.DepositPayload(seller, new Items.KmhThingPayload
                        {
                            DefName = "MeleeWeapon_LongSword", StuffDefName = "Plasteel", Quality = 5,
                            StackCount = 1, HitPoints = 200, MaxHitPoints = 200,   // pristine: the want refuses damage
                            ScribeXml = "<thing><def>MeleeWeapon_LongSword</def></thing>",
                            Fingerprint = "selftest-want-" + i,
                        }, "seed")) return (false, "seed payload");

                (long id, string why) = WantStore.Post(buyer, "MeleeWeapon_LongSword", 4, 500, 24, "public",
                                                       0, "", allowComplex: true);
                if (id == 0) return (false, "post: " + why);
                long escrowed = 20000 - SilverOf(buyer);

                WantStore.FulfillResult fill = WantStore.Fulfill(seller, id, 2);
                if (!fill.Ok) return (false, "fulfill: " + fill.Reason);

                long beforeExpiry = SilverOf(buyer);
                WantStore.ExpireOutcome exp = WantStore.ExpireRefund(id);
                long refunded = SilverOf(buyer) - beforeExpiry;
                // Counted in units, not entries: a vault may legitimately merge two identical instances into one row.
                int buyerUnits = PayloadUnits(buyer), sellerUnits = PayloadUnits(seller);

                Cleanup(buyer, seller);
                bool ok = escrowed == 2000 && fill.FilledQty == 2 && exp.Done && refunded == 1000
                       && buyerUnits == 2 && buyerUnits + sellerUnits == 4;
                return (ok, ok ? "2000 escrowed, 2 of 4 filled as full-state items, 1000 refunded, all 4 accounted for"
                              : $"escrowed={escrowed} (expected 2000), filled={fill.FilledQty}, expired={exp.Done}, "
                                + $"refunded={refunded} (expected 1000), buyerUnits={buyerUnits}, sellerUnits={sellerUnits} "
                                + "(expected 2 and 2)");
            });

            Check("Auction: a second settlement worker finds nothing left to settle", () =>
            {
                // Escrow rows open outside the store lock, so two workers can both reach the commit; exactly one may pay.
                string seller = User("as"), buyer = User("ab");
                if (!TreasuryStore.DepositItem(seller, Steel, 6, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(buyer, 5000, "seed")) return (false, "seed silver");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 6, 100, 10, 0, 24, "public");
                if (id == 0) return (false, "post: " + why);
                if (!AuctionStore.Bid(buyer, id, 500).Ok) return (false, "bid");

                long sellerBefore = SilverOf(seller);
                int reentered = 0;
                AuctionStore.SettleOutcome inner = default;
                AuctionStore.BeforeSettleCommitForTest = () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref reentered, 1) != 0) return;
                    inner = AuctionStore.SettleNow(id);      // the other worker, arriving first
                };
                AuctionStore.SettleOutcome outer;
                try { outer = AuctionStore.SettleNow(id); }
                finally { AuctionStore.BeforeSettleCommitForTest = null; }

                long sellerPaid = SilverOf(seller) - sellerBefore;
                int buyerGoods = ItemsOf(buyer);

                Cleanup(seller, buyer);
                bool exactlyOne = inner.Done ^ outer.Done;
                bool ok = exactlyOne && buyerGoods == 6 && sellerPaid > 0 && sellerPaid < 500;
                return (ok, ok ? $"one settlement of two; seller paid {sellerPaid} once, buyer got 6 once"
                              : $"innerDone={inner.Done}, outerDone={outer.Done}, sellerPaid={sellerPaid}, "
                                + $"buyerGoods={buyerGoods} - AN AUCTION SETTLED TWICE");
            });

            Check("Want: a fill that loses the race takes only what is left, not what it asked for", () =>
            {
                // Another filler can take the remainder between pre-check and commit; the commit re-clamps, or the buyer overpays.
                string buyer = User("wb"), first = User("w1"), second = User("w2");
                if (!TreasuryStore.DepositSilver(buyer, 20000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(first, Steel, 10, "seed")) return (false, "seed 1");
                if (!TreasuryStore.DepositItem(second, Steel, 10, "seed")) return (false, "seed 2");

                (long id, string why) = WantStore.Post(buyer, Steel, 6, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);

                int reentered = 0;
                WantStore.DuringFulfillForTest = () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref reentered, 1) != 0) return;
                    WantStore.Fulfill(second, id, 4);       // the other filler takes 4 of the 6
                };
                WantStore.FulfillResult mine;
                try { mine = WantStore.Fulfill(first, id, 6); }
                finally { WantStore.DuringFulfillForTest = null; }

                int buyerGot = ItemsOf(buyer);
                int firstKept = ItemsOf(first), secondKept = ItemsOf(second);

                long beforeExpiry = SilverOf(buyer);
                WantStore.ExpireRefund(id);
                long refunded = SilverOf(buyer) - beforeExpiry;

                Cleanup(buyer, first, second);
                bool ok = buyerGot == 6 && mine.FilledQty == 2 && firstKept == 8 && secondKept == 6
                       && refunded == 0;
                return (ok, ok ? "the loser committed 2, not 6; the buyer got exactly the 6 they asked for"
                              : $"buyerGot={buyerGot} (max 6), loserFilled={mine.FilledQty} (expected 2), "
                                + $"firstKept={firstKept}, secondKept={secondKept}, refundLeft={refunded} "
                                + "- A WANT TOOK MORE THAN IT ASKED FOR");
            });

            Check("Want: only the buyer may cancel their own want", () =>
            {
                string buyer = User("wb"), other = User("wo");
                if (!TreasuryStore.DepositSilver(buyer, 5000, "seed")) return (false, "seed silver");
                (long id, string why) = WantStore.Post(buyer, Steel, 5, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);

                long otherBefore = SilverOf(other);
                (bool refused, string _) = WantStore.Cancel(other, id);
                bool nothingPaid = SilverOf(other) == otherBefore;
                (bool own, string _) = WantStore.Cancel(buyer, id);

                Cleanup(buyer, other);
                bool ok = !refused && nothingPaid && own;
                return (ok, ok ? "a stranger's cancel is refused and pays them nothing"
                              : $"strangerRefused={!refused}, strangerPaid={!nothingPaid}, ownerCancelled={own}");
            });

            // Fulfill takes only an id and a quantity, so its filters must come off the stored want; wrong goods fill nothing.
            Check("Want: the filters a fill is matched against come off the want, not the filler", () =>
            {
                string buyer = User("wf"), seller = User("ws");
                const string Sword = "MeleeWeapon_LongSword";
                if (!TreasuryStore.DepositSilver(buyer, 20000, "seed")) return (false, "seed buyer");
                (long id, string why) = WantStore.Post(buyer, Sword, 2, 500, 24, "public",
                                                       minQuality: 4, requiredStuff: "Plasteel",
                                                       allowComplex: true, allowTainted: false, allowDamaged: false);
                if (id == 0) return (false, "post: " + why);

                // Right def, wrong material; right material but under quality; right in both but tainted.
                var wrongStuff = new Items.KmhThingPayload { DefName = Sword, StuffDefName = "Steel",   Quality = 5, StackCount = 1, HitPoints = -1 };
                var lowQuality = new Items.KmhThingPayload { DefName = Sword, StuffDefName = "Plasteel", Quality = 2, StackCount = 1, HitPoints = -1 };
                var tainted    = new Items.KmhThingPayload { DefName = Sword, StuffDefName = "Plasteel", Quality = 5, StackCount = 1, HitPoints = -1, Tainted = true };
                foreach (Items.KmhThingPayload p in new[] { wrongStuff, lowQuality, tainted })
                    if (!TreasuryStore.DepositPayload(seller, p, "seed")) return (false, "seed seller gear");

                long sellerSilver0 = SilverOf(seller);
                int sellerUnits0 = PayloadUnits(seller);
                WantStore.FulfillResult bad = WantStore.Fulfill(seller, id, 2);
                bool refusedAll = !bad.Ok && SilverOf(seller) == sellerSilver0 && PayloadUnits(seller) == sellerUnits0;

                // One that does match every filter goes through, which is what makes the refusals above meaningful.
                var good = new Items.KmhThingPayload { DefName = Sword, StuffDefName = "Plasteel", Quality = 5, StackCount = 1, HitPoints = -1 };
                if (!TreasuryStore.DepositPayload(seller, good, "seed")) return (false, "seed matching gear");
                WantStore.FulfillResult okFill = WantStore.Fulfill(seller, id, 1);

                WantStore.Cancel(buyer, id);
                Cleanup(buyer, seller);
                bool ok = refusedAll && okFill.Ok && okFill.FilledQty == 1;
                return (ok, ok ? "wrong material, low quality and tainted all refused; the matching one filled"
                              : $"refusedAll={refusedAll}, matchingFill={okFill.Ok}, qty={okFill.FilledQty} ({bad.Reason})");
            });

            return r;
        }
    }
}
