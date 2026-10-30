using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Auctions;
using KMHServerAddon.Features.Mail;
using KMHServerAddon.Features.Quests;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Features.WantBoard;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Stores advance state first and pay second, unwinding when the write fails; these prove each unwind runs.
    internal static class KmhWriteFailureRollbackSelfTest
    {
        private static string User(string tag) => $"selftest_wf{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private const string Steel = "Steel";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                // One throwing check is one failure, not the end of the suite and of every suite run after it.
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { JsonFileStore.ClearFailureStateForTest(); }
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
            void Cleanup(params string[] users) { foreach (string u in users) TreasuryStore.PurgeOwnerForTest(u); }

            // Only the named store fails, so the treasury can still be read and credited around it.
            Func<string, string> FailOnly(string path)
                => p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? "injected: disk full" : null;

            Check("Rollback: a mail claim that cannot be written leaves the attachment claimable", () =>
            {
                string from = User("s"), to = User("r");
                if (!TreasuryStore.DepositSilver(from, 5000, "seed")) return (false, "seed");
                long id = MailStore.Send(from, to, "subject", "body", 500, null, null, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);

                long before = SilverOf(to);
                bool claimed;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.MailFile)))
                    claimed = MailStore.Claim(to, id, out _, out _, out string _);
                JsonFileStore.ClearFailureStateForTest();
                long afterFailed = SilverOf(to);

                // The write came back, so the same claim must now succeed - once.
                bool second = MailStore.Claim(to, id, out MailStore.AttachTotals t2, out _, out string why2);
                long afterOk = SilverOf(to);
                bool third = MailStore.Claim(to, id, out _, out _, out _);

                Cleanup(from, to);
                bool ok = !claimed && afterFailed == before && second && t2.Silver == 500
                       && afterOk - before == 500 && !third;
                return (ok, ok ? "the failed claim credited nothing and left the mail claimable; the retry paid once"
                              : $"failedClaimReported={claimed}, creditedOnFailure={afterFailed - before}, "
                                + $"retry={second} ({why2}), creditedOnRetry={afterOk - before}, thirdClaim={third}");
            });

            Check("Rollback: a mail recall that cannot be written returns nothing to the sender", () =>
            {
                string from = User("s"), to = User("r");
                if (!TreasuryStore.DepositSilver(from, 5000, "seed")) return (false, "seed");
                long id = MailStore.Send(from, to, "subject", "body", 400, null, null, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);

                long before = SilverOf(from);
                bool recalled;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.MailFile)))
                    recalled = MailStore.Recall(from, id, out _, out _, out string _);
                JsonFileStore.ClearFailureStateForTest();
                long afterFailed = SilverOf(from);

                bool second = MailStore.Recall(from, id, out MailStore.AttachTotals t2, out _, out _);
                long afterOk = SilverOf(from);
                // The recipient must not be able to claim what was returned to the sender.
                bool claimAfter = MailStore.Claim(to, id, out _, out _, out _);

                Cleanup(from, to);
                bool ok = !recalled && afterFailed == before && second && t2.Silver == 400
                       && afterOk - before == 400 && !claimAfter;
                return (ok, ok ? "the failed recall returned nothing; the retry returned 400 and closed the attachment"
                              : $"failedRecallReported={recalled}, returnedOnFailure={afterFailed - before}, "
                                + $"retry={second}, returnedOnRetry={afterOk - before}, claimableAfter={claimAfter}");
            });

            // Reverting every touched message to Escrowed on an unwritten prune reopens claimed attachments for refund.
            Check("Rollback: an unwritten prune of claimed mail does not pay the sender later", () =>
            {
                string from = User("s"), to = User("r");
                if (!TreasuryStore.DepositSilver(from, 5000, "seed")) return (false, "seed");
                long id = MailStore.Send(from, to, "subject", "body", 600, null, null, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);
                if (!MailStore.Claim(to, id, out _, out _, out string claimWhy)) return (false, "claim: " + claimWhy);
                MailStore.MarkRead(to, id);
                long senderBefore = SilverOf(from), recipientBefore = SilverOf(to);
                long later = DateTime.UtcNow.AddDays(MailStore.RetentionDays + 2).Ticks;

                int failedPrune;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.MailFile)))
                    failedPrune = MailStore.PruneOld(later);
                JsonFileStore.ClearFailureStateForTest();
                int pruned = MailStore.PruneOld(later);
                long senderGain = SilverOf(from) - senderBefore, recipientChange = SilverOf(to) - recipientBefore;

                Cleanup(from, to);
                bool ok = failedPrune == 0 && pruned >= 1 && senderGain == 0 && recipientChange == 0;
                return (ok, $"failed pass pruned {failedPrune}, retry pruned {pruned}, sender gained {senderGain}, recipient changed {recipientChange}");
            });

            Check("Rollback: an unwritten delete of claimed mail does not pay the sender later", () =>
            {
                string from = User("s"), to = User("r");
                if (!TreasuryStore.DepositSilver(from, 5000, "seed")) return (false, "seed");
                long id = MailStore.Send(from, to, "subject", "body", 700, null, null, out _, out _, out string why);
                if (id == 0) return (false, "send: " + why);
                if (!MailStore.Claim(to, id, out _, out _, out string claimWhy)) return (false, "claim: " + claimWhy);
                long senderBefore = SilverOf(from);

                bool failedDelete;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.MailFile)))
                    failedDelete = MailStore.Delete(to, id, out _, out _);
                JsonFileStore.ClearFailureStateForTest();
                bool deleted = MailStore.Delete(to, id, out MailStore.AttachTotals refunded, out _);
                long senderGain = SilverOf(from) - senderBefore;

                Cleanup(from, to);
                bool ok = !failedDelete && deleted && refunded.Silver == 0 && senderGain == 0;
                return (ok, $"failed delete reported {failedDelete}, retry deleted {deleted}, refunded {refunded.Silver}, sender gained {senderGain}");
            });

            Check("Rollback: a want fill that cannot be written returns the goods and spends no escrow", () =>
            {
                string buyer = User("b"), seller = User("f");
                if (!TreasuryStore.DepositSilver(buyer, 10000, "seed")) return (false, "seed silver");
                if (!TreasuryStore.DepositItem(seller, Steel, 10, "seed")) return (false, "seed goods");
                (long id, string why) = WantStore.Post(buyer, Steel, 10, 100, 24, "public", 0, "");
                if (id == 0) return (false, "post: " + why);

                WantStore.FulfillResult fill;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.WantsFile)))
                    fill = WantStore.Fulfill(seller, id, 5);
                JsonFileStore.ClearFailureStateForTest();

                int sellerKept = ItemsOf(seller), buyerGot = ItemsOf(buyer);
                // The escrow must be untouched, which the expiry refund is the only honest way to read.
                long beforeExpiry = SilverOf(buyer);
                WantStore.ExpireOutcome exp = WantStore.ExpireRefund(id);
                long refunded = SilverOf(buyer) - beforeExpiry;

                Cleanup(buyer, seller);
                bool ok = !fill.Ok && sellerKept == 10 && buyerGot == 0 && exp.Done && refunded == 1000;
                return (ok, ok ? "goods returned, escrow untouched, the whole 1000 refunded on expiry"
                              : $"fillReported={fill.Ok}, sellerKept={sellerKept} (expected 10), buyerGot={buyerGot}, "
                                + $"refunded={refunded} (expected 1000) - THE BUYER PAID FOR A FILL THAT WAS UNDONE");
            });

            Check("Rollback: an auction bid that cannot be written refunds the bidder and keeps the old high bid", () =>
            {
                string seller = User("s"), first = User("1"), second = User("2");
                if (!TreasuryStore.DepositItem(seller, Steel, 4, "seed")) return (false, "seed goods");
                if (!TreasuryStore.DepositSilver(first, 5000, "seed")) return (false, "seed 1");
                if (!TreasuryStore.DepositSilver(second, 5000, "seed")) return (false, "seed 2");
                (long id, string why) = AuctionStore.Post(seller, Steel, "", 0, 4, 100, 10, 0, 24, "public");
                if (id == 0) return (false, "post: " + why);
                if (!AuctionStore.Bid(first, id, 100).Ok) return (false, "first bid");

                long firstBefore = SilverOf(first), secondBefore = SilverOf(second);
                AuctionStore.BidResult bid;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.AuctionsFile)))
                    bid = AuctionStore.Bid(second, id, 500);
                JsonFileStore.ClearFailureStateForTest();

                long firstAfter = SilverOf(first), secondAfter = SilverOf(second);
                bool oldUntouched = firstAfter == firstBefore;
                bool newRefunded = secondAfter == secondBefore;

                // Silver alone cannot tell them apart: an unrolled bid leaves the second bidder high with no escrow held.
                string highBidder = null; long highBid = -1;
                foreach (Features.Auctions.Dto.AuctionDto a in AuctionStore.AllForAdmin())
                    if (a.Id == id) { highBidder = a.HighBidder; highBid = a.CurrentBid; }
                bool stateHeld = string.Equals(highBidder, first, StringComparison.OrdinalIgnoreCase) && highBid == 100;

                Cleanup(seller, first, second);
                bool ok = !bid.Ok && oldUntouched && newRefunded && stateHeld;
                return (ok, ok ? "the failed bid was refunded and the auction still shows the written bid"
                              : $"bidReported={bid.Ok}, oldBidderDelta={firstAfter - firstBefore} (expected 0), "
                                + $"newBidderDelta={secondAfter - secondBefore} (expected 0), "
                                + $"highBid={highBid} by {highBidder ?? "nobody"} (expected 100 by the first bidder) - "
                                + "AN UNWRITTEN BID IS STANDING WITH NO ESCROW BEHIND IT");
            });

            Check("Rollback: a quest cancel that cannot be written leaves the quest and its bounty alone", () =>
            {
                string poster = User("p"), claimer = User("c");
                if (!TreasuryStore.DepositSilver(poster, 5000, "seed")) return (false, "seed");
                long id = QuestStore.PostDraft(poster, new QuestEntry
                { Kind = QuestEntry.KindBounty, Title = "selftest", Description = "x", BountySilver = 600 },
                    24, out string why);
                if (id == 0) return (false, "post: " + why);

                long before = SilverOf(poster);
                bool cancelled;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.QuestsFile)))
                    cancelled = QuestStore.Cancel(poster, id);
                JsonFileStore.ClearFailureStateForTest();
                long afterFailed = SilverOf(poster);

                // Still on the board: a quest refunded but left claimable pays its bounty out a second time.
                bool stillClaimable = QuestStore.Claim(claimer, id);
                bool abandoned = QuestStore.Abandon(claimer, id, out _);
                bool cancelNow = QuestStore.Cancel(poster, id);
                long afterOk = SilverOf(poster);

                QuestStore.PurgePoster(poster);
                Cleanup(poster, claimer);
                bool ok = !cancelled && afterFailed == before && stillClaimable && abandoned
                       && cancelNow && afterOk - before == 600;
                return (ok, ok ? "nothing refunded on the failed cancel; the quest stayed claimable and refunded once later"
                              : $"cancelReported={cancelled}, refundedOnFailure={afterFailed - before}, "
                                + $"stillClaimable={stillClaimable}, abandoned={abandoned}, "
                                + $"cancelledLater={cancelNow}, refundedLater={afterOk - before}");
            });

            Check("Rollback: a quest expiry that cannot be written refunds nothing and expires later", () =>
            {
                // The sweeper retries every minute, so an unrecorded expiry refund would hand the bounty back every pass.
                string poster = User("p");
                if (!TreasuryStore.DepositSilver(poster, 5000, "seed")) return (false, "seed");
                long id = QuestStore.PostDraft(poster, new QuestEntry
                { Kind = QuestEntry.KindBounty, Title = "selftest", Description = "x", BountySilver = 700 },
                    24, out string why);
                if (id == 0) return (false, "post: " + why);

                long before = SilverOf(poster);
                bool expired;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.QuestsFile)))
                    expired = QuestStore.ExpireQuest(id, out _);
                JsonFileStore.ClearFailureStateForTest();
                long afterFailed = SilverOf(poster);

                bool retry = QuestStore.ExpireQuest(id, out _);
                long afterOk = SilverOf(poster);
                bool third = QuestStore.ExpireQuest(id, out _);
                long afterThird = SilverOf(poster);

                QuestStore.PurgePoster(poster);
                Cleanup(poster);
                bool ok = !expired && afterFailed == before && retry && afterOk - before == 700
                       && !third && afterThird == afterOk;
                return (ok, ok ? "nothing refunded on the failed expiry; the next sweep refunded 700 once"
                              : $"expiryReported={expired}, refundedOnFailure={afterFailed - before}, "
                                + $"retry={retry}, refundedOnRetry={afterOk - before}, "
                                + $"thirdSweep={third}, refundedAgain={afterThird - afterOk}");
            });

            Check("Rollback: an injected failure leaves no degraded flag behind for the next check", () =>
            {
                // The gate is global state, so a suite that forgets to clear it silently blocks everything after it.
                bool degradedDuring;
                using (JsonFileStore.FailWritesForTest(FailOnly(KmhDataPaths.MailFile)))
                {
                    string from = User("s"), to = User("r");
                    // Seeded, not asserted: this check is about the flag the scope leaves behind, not the mail.
                    degradedDuring = TreasuryStore.DepositSilver(from, 100, "seed");
                    MailStore.Send(from, to, "s", "b", 0, null, null, out _, out _, out _);
                    Cleanup(from, to);
                }
                bool clearAfter = !KmhMaintenanceGate.PersistenceDegraded;
                return (degradedDuring && clearAfter,
                        clearAfter ? "the injected failure is fully cleared when the scope ends"
                                   : "the persistence-degraded flag survived the injection scope");
            });

            return r;
        }
    }
}
