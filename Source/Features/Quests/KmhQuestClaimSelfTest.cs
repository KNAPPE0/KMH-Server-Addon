using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Features.Quests
{
    // A bounty is escrowed silver, so a wrong-player claim or a double release is a value defect, not a fairness one.
    internal static class KmhQuestClaimSelfTest
    {
        private static string User(string tag) => $"selftest_q{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // A silent seed failure would leave every check below passing against an empty vault.
            long PostBounty(string poster, int silver)
            {
                if (!TreasuryStore.DepositSilver(poster, silver, "selftest seed")) return 0;
                return QuestStore.PostDraft(poster, new QuestEntry
                {
                    Kind = QuestEntry.KindBounty,
                    Title = "selftest bounty",
                    Description = "selftest",
                    BountySilver = silver,
                }, 24, out _);
            }

            void Cleanup(params string[] users)
            {
                foreach (string u in users) { QuestStore.PurgePoster(u); TreasuryStore.PurgeOwnerForTest(u); }
            }

            Check("Quest: a stored quest with no bounty-items list still copies onto the board", () =>
            {
                QuestEntry copy = QuestStore.CopyForTest(new QuestEntry { Id = -1, BountyItems = null });
                bool ok = copy != null && copy.BountyItems != null && copy.BountyItems.Count == 0;
                return (ok, ok ? "copied with an empty list" : "copy lost its list");
            });

            Check("Quest: a poster cannot claim their own quest", () =>
            {
                string poster = User("p");
                long id = PostBounty(poster, 100);
                if (id == 0) return (false, "could not post");
                bool refused = !QuestStore.Claim(poster, id);
                Cleanup(poster);
                return (refused, refused ? "self-claim refused" : "A POSTER CAN CLAIM THEIR OWN BOUNTY");
            });

            Check("Quest: only the player who claimed it may submit", () =>
            {
                string poster = User("p"), claimer = User("c"), thief = User("t");
                long id = PostBounty(poster, 150);
                if (id == 0) return (false, "could not post");
                if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");

                bool thiefRefused = !QuestStore.Submit(thief, id, out _);
                bool posterRefused = !QuestStore.Submit(poster, id, out _);
                bool claimerOk = QuestStore.Submit(claimer, id, out _);

                Cleanup(poster, claimer, thief);
                bool ok = thiefRefused && posterRefused && claimerOk;
                return (ok, ok ? "another player and the poster are both refused; the claimer succeeds"
                              : $"strangerRefused={thiefRefused}, posterRefused={posterRefused}, claimerOk={claimerOk} "
                                + "- ANOTHER PLAYER CAN COMPLETE A CLAIMED QUEST");
            });

            Check("Quest: only the claimer may abandon, and only the poster may cancel or approve", () =>
            {
                string poster = User("p"), claimer = User("c"), other = User("o");
                long id = PostBounty(poster, 120);
                if (id == 0) return (false, "could not post");

                bool otherCancel = !QuestStore.Cancel(other, id);
                if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");
                bool otherAbandon = !QuestStore.Abandon(other, id, out _);
                bool posterAbandon = !QuestStore.Abandon(poster, id, out _);
                if (!QuestStore.Submit(claimer, id, out _)) return (false, "submit failed");
                bool otherApprove = !QuestStore.Approve(other, id, out _);
                bool claimerApprove = !QuestStore.Approve(claimer, id, out _);
                bool posterApprove = QuestStore.Approve(poster, id, out _);

                Cleanup(poster, claimer, other);
                bool ok = otherCancel && otherAbandon && posterAbandon && otherApprove && claimerApprove && posterApprove;
                return (ok, ok ? "cancel is the poster's, abandon the claimer's, approve the poster's"
                              : $"strangerCancel={otherCancel}, strangerAbandon={otherAbandon}, posterAbandon={posterAbandon}, "
                                + $"strangerApprove={otherApprove}, claimerApprove={claimerApprove}, posterApprove={posterApprove}");
            });

            Check("Quest: approving twice pays the bounty once", () =>
            {
                string poster = User("p"), claimer = User("c");
                long id = PostBounty(poster, 400);
                if (id == 0) return (false, "could not post");
                if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");
                if (!QuestStore.Submit(claimer, id, out _)) return (false, "submit failed");

                long before = TreasuryStore.GetPersonalSilver(claimer);
                bool first = QuestStore.Approve(poster, id, out _);
                long afterFirst = TreasuryStore.GetPersonalSilver(claimer);
                bool second = QuestStore.Approve(poster, id, out _);
                long afterSecond = TreasuryStore.GetPersonalSilver(claimer);

                Cleanup(poster, claimer);
                bool ok = first && !second && afterFirst - before == 400 && afterSecond == afterFirst;
                return (ok, ok ? "400 paid once; the repeat is refused and pays nothing"
                              : $"first={first}, secondRefused={!second}, paid={afterFirst - before}, "
                                + $"afterRepeat={afterSecond - afterFirst}");
            });

            // A delivery completes inside Submit, so its state guard is all that stops one escrowed bounty paying twice.
            Check("Quest: submitting a delivered quest twice takes the goods and pays the bounty once", () =>
            {
                string poster = User("dp"), claimer = User("dc");
                const int Bounty = 500, Qty = 6;
                if (!TreasuryStore.DepositSilver(poster, Bounty, "selftest seed")) return (false, "seed poster");
                if (!TreasuryStore.DepositItem(claimer, "Steel", Qty * 2, "selftest seed")) return (false, "seed claimer");

                long id = QuestStore.PostDraft(poster, new QuestEntry
                {
                    Kind = QuestEntry.KindDeliverItem,
                    Title = "selftest delivery",
                    Description = "selftest",
                    BountySilver = Bounty,
                    TargetItemDefName = "Steel",
                    TargetItemQty = Qty,
                }, 24, out string why);
                if (id == 0) return (false, "post: " + why);
                if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");

                long silverBefore = TreasuryStore.GetPersonalSilver(claimer);
                int itemsBefore = SteelHeld(claimer);

                bool first = QuestStore.Submit(claimer, id, out _);
                long silverOnce = TreasuryStore.GetPersonalSilver(claimer);
                int itemsOnce = SteelHeld(claimer);

                bool second = QuestStore.Submit(claimer, id, out _);
                long silverTwice = TreasuryStore.GetPersonalSilver(claimer);
                int itemsTwice = SteelHeld(claimer);

                Cleanup(poster, claimer);
                bool ok = first && !second
                       && silverOnce - silverBefore == Bounty && silverTwice == silverOnce
                       && itemsBefore - itemsOnce == Qty && itemsTwice == itemsOnce;
                return (ok, ok ? $"{Bounty} paid once and {Qty} taken once; the repeat is refused"
                              : $"first={first}, secondRefused={!second}, paid={silverOnce - silverBefore}, "
                                + $"paidAgain={silverTwice - silverOnce}, took={itemsBefore - itemsOnce}, "
                                + $"tookAgain={itemsOnce - itemsTwice}");
            });

            Check("Quest: two players claiming at the same instant produce exactly one claimer", () =>
            {
                string poster = User("p");
                long id = PostBounty(poster, 200);
                if (id == 0) return (false, "could not post");

                var claimers = new string[8];
                for (int i = 0; i < claimers.Length; i++) claimers[i] = User("r" + i);
                int won = 0;
                using (var gate = new ManualResetEventSlim(false))
                {
                    var tasks = new Task[claimers.Length];
                    for (int i = 0; i < claimers.Length; i++)
                    {
                        string who = claimers[i];
                        tasks[i] = Task.Run(() =>
                        {
                            gate.Wait();
                            if (QuestStore.Claim(who, id)) Interlocked.Increment(ref won);
                        });
                    }
                    gate.Set();
                    Task.WaitAll(tasks, TimeSpan.FromSeconds(20));
                }

                QuestStore.PurgePoster(poster);
                TreasuryStore.PurgeOwnerForTest(poster);
                foreach (string c in claimers) TreasuryStore.PurgeOwnerForTest(c);
                return (won == 1, won == 1 ? "8 racing claims, one winner"
                                           : $"{won} of 8 claims succeeded - A QUEST CAN BE CLAIMED BY SEVERAL PLAYERS");
            });

            Check("Quest: cancelling returns the escrowed bounty exactly once", () =>
            {
                string poster = User("p");
                if (!TreasuryStore.DepositSilver(poster, 500, "selftest seed")) return (false, "seed");
                long before = TreasuryStore.GetPersonalSilver(poster);
                long id = QuestStore.PostDraft(poster, new QuestEntry
                {
                    Kind = QuestEntry.KindBounty, Title = "selftest cancel", Description = "x", BountySilver = 500,
                }, 24, out string why);
                if (id == 0) return (false, "could not post: " + why);

                long escrowed = TreasuryStore.GetPersonalSilver(poster);
                bool first = QuestStore.Cancel(poster, id);
                long afterFirst = TreasuryStore.GetPersonalSilver(poster);
                bool second = QuestStore.Cancel(poster, id);
                long afterSecond = TreasuryStore.GetPersonalSilver(poster);

                Cleanup(poster);
                bool ok = first && !second && escrowed == before - 500
                       && afterFirst == before && afterSecond == afterFirst;
                return (ok, ok ? "the bounty is escrowed on post and returned once on cancel"
                              : $"escrowTaken={before - escrowed}, cancelled={first}, returned={afterFirst - escrowed}, "
                                + $"repeatRefused={!second}, afterRepeat={afterSecond - afterFirst}");
            });

            Check("Quest: expiry and a claim cannot both take the same open quest", () =>
            {
                string poster = User("p"), claimer = User("c");
                long id = PostBounty(poster, 300);
                if (id == 0) return (false, "could not post");

                long beforePoster = TreasuryStore.GetPersonalSilver(poster);
                bool expired = QuestStore.ExpireQuest(id, out _);
                long afterExpiry = TreasuryStore.GetPersonalSilver(poster);
                // The board already gave the bounty back, so a claim landing now would be claiming nothing.
                bool claimRefused = !QuestStore.Claim(claimer, id);
                bool expireAgain = !QuestStore.ExpireQuest(id, out _);
                long afterRepeat = TreasuryStore.GetPersonalSilver(poster);

                Cleanup(poster, claimer);
                bool ok = expired && claimRefused && expireAgain
                       && afterExpiry - beforePoster == 300 && afterRepeat == afterExpiry;
                return (ok, ok ? "expiry refunds once and closes the quest to claims"
                              : $"expired={expired}, claimRefused={claimRefused}, expireAgain={expireAgain}, "
                                + $"refunded={afterExpiry - beforePoster}, afterRepeat={afterRepeat - afterExpiry}");
            });

            Check("Quest: an abandoned quest returns to the board with no bounty paid", () =>
            {
                string poster = User("p"), claimer = User("c");
                long id = PostBounty(poster, 250);
                if (id == 0) return (false, "could not post");
                if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");

                long claimerBefore = TreasuryStore.GetPersonalSilver(claimer);
                bool abandoned = QuestStore.Abandon(claimer, id, out _);
                long claimerAfter = TreasuryStore.GetPersonalSilver(claimer);
                bool reclaimed = QuestStore.Claim(claimer, id);
                bool abandonAgain = !QuestStore.Abandon(poster, id, out _);

                Cleanup(poster, claimer);
                bool ok = abandoned && claimerAfter == claimerBefore && reclaimed && abandonAgain;
                return (ok, ok ? "abandon pays nothing and reopens the quest"
                              : $"abandoned={abandoned}, paid={claimerAfter - claimerBefore}, "
                                + $"reopened={reclaimed}, posterCannotAbandon={abandonAgain}");
            });

            Check("Quest: a quest that is no longer open cannot be cancelled out from under its claimer", () =>
            {
                // Cancel refunds the bounty; allowing it after a claim or payout hands back silver that is already elsewhere.
                string poster = User("p"), claimer = User("c");
                long id = PostBounty(poster, 900);
                if (id == 0) return (false, "could not post");
                if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");

                long posterBefore = TreasuryStore.GetPersonalSilver(poster);
                bool cancelClaimed = !QuestStore.Cancel(poster, id);
                long afterClaimedCancel = TreasuryStore.GetPersonalSilver(poster);

                if (!QuestStore.Submit(claimer, id, out _)) return (false, "submit failed");
                bool cancelSubmitted = !QuestStore.Cancel(poster, id);

                if (!QuestStore.Approve(poster, id, out _)) return (false, "approve failed");
                long claimerPaid = TreasuryStore.GetPersonalSilver(claimer);
                bool cancelCompleted = !QuestStore.Cancel(poster, id);
                long afterAll = TreasuryStore.GetPersonalSilver(poster);

                // And expiry must refuse it too, or the sweeper refunds a bounty that has been paid out.
                bool expireRefused = !QuestStore.ExpireQuest(id, out _);
                long afterExpiry = TreasuryStore.GetPersonalSilver(poster);

                Cleanup(poster, claimer);
                bool ok = cancelClaimed && cancelSubmitted && cancelCompleted && expireRefused
                       && afterClaimedCancel == posterBefore && afterAll == posterBefore
                       && afterExpiry == posterBefore && claimerPaid >= 900;
                return (ok, ok ? "claimed, submitted, completed and expired all refuse to refund the poster"
                              : $"claimedRefused={cancelClaimed}, submittedRefused={cancelSubmitted}, "
                                + $"completedRefused={cancelCompleted}, expiryRefused={expireRefused}, "
                                + $"posterDelta={afterExpiry - posterBefore} (expected 0), claimerPaid={claimerPaid} "
                                + "- A PAID BOUNTY WAS REFUNDED TO THE POSTER AS WELL");
            });

            // A raw claim+verify must not collect Escort/Defend/Hunt/Build escrow on the claimer's word alone.
            int SteelHeld(string u)
            {
                int n = 0;
                var snap = TreasuryStore.GetSnapshotFor(u);
                if (snap?.Items != null)
                    foreach (KeyValuePair<string, int> kv in snap.Items)
                        if (Util.ItemKey.Matches(kv.Key, "Steel", "", 0)) n += kv.Value;
                return n;
            }
            long PostEscort(string poster, int silver, int steel)
            {
                if (!TreasuryStore.DepositSilver(poster, silver, "selftest seed")) return 0;
                if (steel > 0 && !TreasuryStore.DepositItem(poster, "Steel", steel, "selftest seed")) return 0;
                return QuestStore.PostDraft(poster, new QuestEntry
                {
                    Kind = QuestEntry.KindEscort, Title = "selftest escort", Description = "selftest",
                    BountySilver = silver, EscortPickupTile = 21, EscortDropoffTile = 22,
                    BountyItems = steel > 0 ? new Dictionary<string, int> { ["Steel"] = steel } : null,
                }, 24, out _);
            }
            (bool, string) Verified(int silver, int steel, Func<string, string, long, (bool, string)> then)
            {
                string poster = User("vp"), claimer = User("vc");
                QuestsConfig pinned = Persistence.KmhDeepCopy.Of(QuestsConfig.Current);
                pinned.AutoVerifyRequiresPosterReview = false;
                pinned.AutoVerifyMaxBountySilver = 250;
                pinned.ReviewTimeoutHours = 72;
                QuestsConfig prev = QuestsConfig.ApplyForTest(pinned);
                try
                {
                    long id = PostEscort(poster, silver, steel);
                    if (id == 0) return (false, "could not post");
                    if (!QuestStore.Claim(claimer, id)) return (false, "claim failed");
                    QuestStore.BackdateClaimForTest(id, TimeSpan.FromHours(1));
                    return then(poster, claimer, id);
                }
                finally { QuestsConfig.ApplyForTest(prev); Cleanup(poster, claimer); }
            }

            Check("Quest auto-verify: a bounty over the auto-pay limit waits for the poster, and an unanswered review reopens unpaid", () =>
                Verified(1000, 0, (poster, claimer, id) =>
                {
                    long before = TreasuryStore.GetPersonalSilver(claimer);
                    bool reported = QuestStore.VerifyComplete(claimer, id, out _);
                    string state = QuestStore.StateForTest(id);
                    long afterReport = TreasuryStore.GetPersonalSilver(claimer);
                    QuestStore.BackdateReviewForTest(id, TimeSpan.FromHours(73));
                    bool released = QuestStore.ReleaseStaleReviews(DateTime.UtcNow.Ticks).Exists(x => x.Id == id);
                    long afterTimeout = TreasuryStore.GetPersonalSilver(claimer);
                    bool ok = reported && state == QuestEntry.StatePendingReview && afterReport == before
                           && released && QuestStore.StateForTest(id) == QuestEntry.StateOpen && afterTimeout == before
                           && QuestStore.EscrowedForTest(poster).Silver == 1000;
                    return (ok, $"reported={reported} state={state} paidOnReport={afterReport - before} released={released} "
                              + $"paidOnTimeout={afterTimeout - before} escrow={QuestStore.EscrowedForTest(poster).Silver}");
                }));

            Check("Quest auto-verify: an item bounty waits for the poster even when its silver is small, and approval pays both", () =>
                Verified(100, 3, (poster, claimer, id) =>
                {
                    long s0 = TreasuryStore.GetPersonalSilver(claimer); int i0 = SteelHeld(claimer);
                    bool reported = QuestStore.VerifyComplete(claimer, id, out _);
                    string state = QuestStore.StateForTest(id);
                    bool unpaid = TreasuryStore.GetPersonalSilver(claimer) == s0 && SteelHeld(claimer) == i0;
                    bool approved = QuestStore.ReviewProof(poster, id, true, "ok", out _);
                    long paid = TreasuryStore.GetPersonalSilver(claimer) - s0; int got = SteelHeld(claimer) - i0;
                    bool ok = reported && state == QuestEntry.StatePendingReview && unpaid && approved && paid == 100 && got == 3;
                    return (ok, $"reported={reported} state={state} unpaidOnReport={unpaid} approved={approved} paid={paid}s+{got} steel");
                }));

            Check("Quest auto-verify: a small silver-only bounty still pays on the report, once", () =>
                Verified(200, 0, (poster, claimer, id) =>
                {
                    long before = TreasuryStore.GetPersonalSilver(claimer);
                    bool first = QuestStore.VerifyComplete(claimer, id, out _);
                    long once = TreasuryStore.GetPersonalSilver(claimer);
                    bool second = QuestStore.VerifyComplete(claimer, id, out _);
                    bool ok = first && !second && once - before == 200 && TreasuryStore.GetPersonalSilver(claimer) == once
                           && QuestStore.StateForTest(id) == QuestEntry.StateCompleted;
                    return (ok, $"first={first} repeatRefused={!second} paid={once - before} state={QuestStore.StateForTest(id)}");
                }));

            Check("Quest: a season wipe leaves no quest, and its escrow does not survive", () =>
            {
                string poster = User("p");
                long id = PostBounty(poster, 180);
                if (id == 0) return (false, "could not post");
                bool hadEscrow = QuestStore.HasPosterEscrow(poster);

                QuestStore.ClearForNewSeason();
                bool gone = !QuestStore.Claim(User("c"), id);
                bool noEscrow = !QuestStore.HasPosterEscrow(poster);

                Cleanup(poster);
                bool ok = hadEscrow && gone && noEscrow;
                return (ok, ok ? "the board and its escrow are wiped together"
                              : $"escrowBefore={hadEscrow}, questGone={gone}, escrowGone={noEscrow}");
            });

            return r;
        }
    }
}
