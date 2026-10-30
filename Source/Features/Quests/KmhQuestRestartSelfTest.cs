using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Quests
{
    // A quest close spans two files, so a crash between them must neither lose nor double the bounty, refund or goods.
    internal static class KmhQuestRestartSelfTest
    {
        private const string Steel = "Steel";
        private const int Bounty = 400, BountySteel = 3, Qty = 7;
        // Only a small silver-only bounty auto-pays on a verify report; "verify" is the large one, approved after review.
        private const int SmallBounty = 200;

        private static int SilverBountyOf(string action) => action == "verify-small" ? SmallBounty : Bounty;
        private static int SteelBountyOf(string action) => action == "verify-small" ? 0 : BountySteel;

        private static string User(string tag) => $"selftest_qr{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static int SteelOf(string user)
        {
            int n = 0;
            var snap = TreasuryStore.GetSnapshotFor(user);
            if (snap?.Items != null)
                foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
            if (snap?.ItemPayloads != null)
                foreach (Items.KmhThingPayload p in snap.ItemPayloads)
                    if (p != null && string.Equals(p.DefName, Steel, StringComparison.OrdinalIgnoreCase)) n += p.StackCount;
            return n;
        }

        private static (long Silver, int Units) Holds(string user)
        {
            long s = 0; int i = 0;
            foreach (Recovery.Dto.RecoveryRecord rec in Recovery.RecoveryStore.ForUser(user))
            {
                if (rec == null || rec.Status == "resolved") continue;
                if (rec.Kind == "silver") s += rec.Silver; else if (rec.Payload != null) i += rec.Payload.StackCount;
            }
            return (s, i);
        }

        private static (long Silver, int Units) Total(string p, string c)
        {
            (long esc, int escN) = QuestStore.EscrowedForTest(p);
            (long op, int opN) = QuestStore.UnpaidOwedForTest(p);
            (long oc, int ocN) = QuestStore.UnpaidOwedForTest(c);
            (long hp, int hpN) = Holds(p); (long hc, int hcN) = Holds(c);
            return (TreasuryStore.GetPersonalSilver(p) + TreasuryStore.GetPersonalSilver(c) + esc + op + oc + hp + hc,
                    SteelOf(p) + SteelOf(c) + escN + opN + ocN + hpN + hcN);
        }

        private static void Restart()
        {
            QuestStore.LoadFromDisk();
            TreasuryStore.LoadFromDisk();
        }

        private static void Cleanup(string p, string c)
        {
            QuestStore.AtSubmitPhaseForTest = null;
            QuestStore.AtPayoutPhaseForTest = null;
            QuestStore.PurgePoster(p);
            QuestStore.DropOwedFor(p); QuestStore.DropOwedFor(c);
            QuestStore.SaveToDisk();
            TreasuryStore.PurgeOwnerForTest(p); TreasuryStore.PurgeOwnerForTest(c);
            Recovery.RecoveryStore.ClearUser(p); Recovery.RecoveryStore.ClearUser(c);
        }

        private static long Post(string poster, string kind, string action, out string why)
            => QuestStore.PostDraft(poster, new QuestEntry
            {
                Kind = kind, Title = "selftest restart", Description = "selftest restart - a quest cut off mid-payout",
                BountySilver = SilverBountyOf(action),
                BountyItems = SteelBountyOf(action) > 0 ? new Dictionary<string, int> { [Steel] = SteelBountyOf(action) } : null,
                TargetItemDefName = Steel, TargetItemQty = Qty,
                EscortPickupTile = 11, EscortDropoffTile = 12,
            }, 24, out why);

        // Pinned, so an owner's own auto-pay limit cannot move a verify case onto the other path.
        private static QuestsConfig PinAutoVerify()
        {
            QuestsConfig cfg = KmhDeepCopy.Of(QuestsConfig.Current);
            cfg.AutoVerifyRequiresPosterReview = false;
            cfg.AutoVerifyMaxBountySilver = SmallBounty;
            return QuestsConfig.ApplyForTest(cfg);
        }

        private static long Prepare(string action, string p, string c, bool payloads, out string why)
        {
            why = null;
            if (!TreasuryStore.DepositSilver(p, Bounty + 1000, "selftest seed") || !TreasuryStore.DepositItem(p, Steel, BountySteel, "selftest seed"))
            { why = "seed poster"; return 0; }
            if (payloads)
            {
                for (int i = 0; i < Qty; i++)
                    if (!TreasuryStore.DepositPayload(c, new Items.KmhThingPayload { DefName = Steel, StackCount = 1, Quality = -1, HitPoints = -1 }, "selftest seed"))
                    { why = "seed claimer payloads"; return 0; }
            }
            else if (!TreasuryStore.DepositItem(c, Steel, Qty + 2, "selftest seed")) { why = "seed claimer"; return 0; }
            string kind = action == "submit" ? QuestEntry.KindDeliverItem
                        : action == "approve" ? QuestEntry.KindBounty
                        : action == "verify" || action == "verify-small" ? QuestEntry.KindEscort
                        : action == "review" ? QuestEntry.KindCustom
                        : QuestEntry.KindDeliverItem;
            long id = Post(p, kind, action, out why);
            if (id == 0) return 0;
            if (action == "cancel") return id;
            if (!QuestStore.Claim(c, id)) { why = "claim"; return 0; }
            if (action == "approve" && !QuestStore.Submit(c, id, out _)) { why = "submit for approval"; return 0; }
            if (action == "verify" || action == "verify-small") QuestStore.BackdateClaimForTest(id, TimeSpan.FromHours(2));
            if (action == "verify" && (!QuestStore.VerifyComplete(c, id, out _) || QuestStore.StateForTest(id) != QuestEntry.StatePendingReview))
            { why = "verify did not route the large bounty to review"; return 0; }
            if (action == "review" && !QuestStore.SubmitProof(c, id, "selftest proof", "", out _)) { why = "proof"; return 0; }
            return id;
        }

        private static bool Act(string action, string p, string c, long id)
        {
            switch (action)
            {
                case "submit":  return QuestStore.Submit(c, id, out _);
                case "expire":  return QuestStore.ExpireQuest(id, out _);
                case "cancel":  return QuestStore.Cancel(p, id);
                case "approve": return QuestStore.Approve(p, id, out _);
                case "verify":  return QuestStore.ReviewProof(p, id, true, "ok", out _);
                case "verify-small": return QuestStore.VerifyComplete(c, id, out _);
                case "review":  return QuestStore.ReviewProof(p, id, true, "ok", out _);
            }
            return false;
        }

        private static (bool ok, string detail) Final(string action, string p, string c, long id, long pSilver0, long cSilver0, int pSteel0, int cSteel0)
        {
            bool refund = action == "expire" || action == "cancel";
            int bounty = SilverBountyOf(action), bountySteel = SteelBountyOf(action);
            long wantP = refund ? pSilver0 + bounty : pSilver0;
            long wantC = refund ? cSilver0 : cSilver0 + bounty;
            int wantPS = refund ? pSteel0 + bountySteel : pSteel0 + (action == "submit" ? Qty : 0);
            int wantCS = refund ? cSteel0 : cSteel0 + bountySteel - (action == "submit" ? Qty : 0);
            long gp = TreasuryStore.GetPersonalSilver(p), gc = TreasuryStore.GetPersonalSilver(c);
            int sp = SteelOf(p), sc = SteelOf(c);
            string state = QuestStore.StateForTest(id);
            bool stateOk = refund ? state == "(gone)" : state == QuestEntry.StateCompleted;
            bool ok = gp == wantP && gc == wantC && sp == wantPS && sc == wantCS && stateOk
                   && QuestStore.OwedTotalsForTest(p) == (0, 0) && QuestStore.OwedTotalsForTest(c) == (0, 0);
            return (ok, $"poster {gp}s/{sp} (want {wantP}/{wantPS}), claimer {gc}s/{sc} (want {wantC}/{wantCS}), state {state}");
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            (bool, string) Cut(string action, string phase, bool restart)
            {
                string p = User("p"), c = User("c");
                QuestsConfig prevCfg = PinAutoVerify();
                try
                {
                    long id = Prepare(action, p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    (long s0, int u0) = Total(p, c);
                    long ps0 = TreasuryStore.GetPersonalSilver(p), cs0 = TreasuryStore.GetPersonalSilver(c);
                    int pu0 = SteelOf(p), cu0 = SteelOf(c);
                    Action<string> die = ph => { if (ph == phase) throw new OperationCanceledException("selftest: process died at " + ph); };
                    QuestStore.AtSubmitPhaseForTest = die;
                    QuestStore.AtPayoutPhaseForTest = die;
                    try { Act(action, p, c, id); } catch (OperationCanceledException) { }
                    finally { QuestStore.AtSubmitPhaseForTest = null; QuestStore.AtPayoutPhaseForTest = null; }
                    (long sCut, int uCut) = Total(p, c);
                    if (restart) Restart();
                    QuestStore.RecoverOnBoot();
                    QuestStore.RecoverOnBoot();
                    (long s1, int u1) = Total(p, c);
                    (bool fin, string fd) = Final(action, p, c, id, ps0 - (action == "expire" || action == "cancel" ? 0 : 0), cs0, pu0, cu0);
                    bool ok = sCut == s0 && uCut == u0 && s1 == s0 && u1 == u0 && fin;
                    return (ok, $"conserved {s0}s/{u0} -> cut {sCut}/{uCut} -> recovered {s1}/{u1}; {fd}");
                }
                finally { Cleanup(p, c); QuestsConfig.ApplyForTest(prevCfg); }
            }

            string[] owedPhases = { "none", "recorded", "credited", "cleared" };
            foreach (bool restart in new[] { false, true })
            {
                foreach (string phase in new[] { "none", "handed-over", "completed", "recorded", "credited", "cleared" })
                    Check($"Quest restart: delivery cut at '{phase}'{(restart ? ", reloaded from disk" : "")} - goods move once, bounty pays once",
                          () => Cut("submit", phase, restart));
                foreach (string action in new[] { "expire", "cancel", "approve", "verify", "verify-small", "review" })
                    foreach (string phase in owedPhases)
                        Check($"Quest restart: {action} cut at '{phase}'{(restart ? ", reloaded from disk" : "")} - paid exactly once",
                              () => Cut(action, phase, restart));
            }

            // The completion write fails after the goods moved: nothing is unwound, and the quest rolls forward once.
            foreach (string resume in new[] { "retry", "restart", "expiry", "abandon" })
                Check($"Quest restart: a delivery whose completion could not be saved completes once on {resume}", () =>
                {
                    string p = User("p"), c = User("c");
                    try
                    {
                        long id = Prepare("submit", p, c, payloads: false, out string why);
                        if (id == 0) return (false, "prepare: " + why);
                        // Enough for a second hand-over, so a retry that took the goods again would show.
                        if (!TreasuryStore.DepositItem(c, Steel, Qty, "selftest extra")) return (false, "seed extra");
                        (long s0, int u0) = Total(p, c);
                        long ps0 = TreasuryStore.GetPersonalSilver(p), cs0 = TreasuryStore.GetPersonalSilver(c);
                        int pu0 = SteelOf(p), cu0 = SteelOf(c);
                        bool first;
                        using (JsonFileStore.FailWritesForTest(f => f == KmhDataPaths.QuestsFile ? "selftest" : null))
                            first = QuestStore.Submit(c, id, out _);
                        bool handedOver = SteelOf(p) == pu0 + Qty && QuestStore.StateForTest(id) == QuestEntry.StateClaimed;
                        switch (resume)
                        {
                            case "retry":   QuestStore.Submit(c, id, out _); break;
                            case "restart": Restart(); QuestStore.RecoverOnBoot(); break;
                            case "expiry":  QuestStore.ExpireQuest(id, out _); break;
                            case "abandon": QuestStore.Abandon(c, id, out _); break;
                        }
                        QuestStore.RecoverOnBoot();
                        (long s1, int u1) = Total(p, c);
                        (bool fin, string fd) = Final("submit", p, c, id, ps0, cs0, pu0, cu0);
                        bool ok = !first && handedOver && s1 == s0 && u1 == u0 && fin;
                        return (ok, $"firstRefused={!first} handedOverAndStillClaimed={handedOver} conserved={s1 == s0 && u1 == u0}; {fd}");
                    }
                    finally { Cleanup(p, c); }
                });

            Check("Quest restart: a hand-over the treasury cannot write moves nothing and leaves the quest claimable", () =>
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare("submit", p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    int pu0 = SteelOf(p), cu0 = SteelOf(c);
                    bool ok1;
                    using (JsonFileStore.FailWritesForTest(f => f == KmhDataPaths.TreasuryFile ? "selftest" : null))
                        ok1 = QuestStore.Submit(c, id, out _);
                    bool untouched = SteelOf(p) == pu0 && SteelOf(c) == cu0 && QuestStore.StateForTest(id) == QuestEntry.StateClaimed;
                    bool later = QuestStore.Submit(c, id, out _);
                    bool ok = !ok1 && untouched && later && SteelOf(p) == pu0 + Qty;
                    return (ok, $"refused={!ok1} untouched={untouched} laterSubmit={later}");
                }
                finally { Cleanup(p, c); }
            });

            Check("Quest restart: a claimer short of the goods hands over nothing at all", () =>
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare("submit", p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    if (!TreasuryStore.WithdrawItem(c, Steel, 3, "selftest short")) return (false, "short the claimer");
                    int pu0 = SteelOf(p), cu0 = SteelOf(c);
                    bool submitted = QuestStore.Submit(c, id, out _);
                    bool ok = !submitted && SteelOf(p) == pu0 && SteelOf(c) == cu0 && QuestStore.StateForTest(id) == QuestEntry.StateClaimed;
                    return (ok, $"submitted={submitted} poster {pu0}->{SteelOf(p)} claimer {cu0}->{SteelOf(c)}");
                }
                finally { Cleanup(p, c); }
            });

            Check("Quest restart: full-state goods hand over whole, and a reload mid-way completes the delivery once", () =>
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare("submit", p, c, payloads: true, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    (long s0, int u0) = Total(p, c);
                    long ps0 = TreasuryStore.GetPersonalSilver(p), cs0 = TreasuryStore.GetPersonalSilver(c);
                    int pu0 = SteelOf(p), cu0 = SteelOf(c);
                    QuestStore.AtSubmitPhaseForTest = ph => { if (ph == "handed-over") throw new OperationCanceledException("selftest"); };
                    try { QuestStore.Submit(c, id, out _); } catch (OperationCanceledException) { }
                    finally { QuestStore.AtSubmitPhaseForTest = null; }
                    Restart();
                    QuestStore.RecoverOnBoot();
                    (long s1, int u1) = Total(p, c);
                    (bool fin, string fd) = Final("submit", p, c, id, ps0, cs0, pu0, cu0);
                    bool ok = s1 == s0 && u1 == u0 && fin;
                    return (ok, $"conserved={s1 == s0 && u1 == u0}; {fd}");
                }
                finally { Cleanup(p, c); }
            });

            Check("Quest restart: a payout the vault refuses stays owed and the next sweep pays it once", () =>
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare("cancel", p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    (long s0, int u0) = Total(p, c);
                    bool cancelled;
                    using (JsonFileStore.FailWritesForTest(f => f == KmhDataPaths.TreasuryFile ? "selftest" : null))
                        cancelled = QuestStore.Cancel(p, id);
                    (long owedS, _) = QuestStore.OwedTotalsForTest(p);
                    int paid = QuestStore.RetryOwed();
                    int again = QuestStore.RetryOwed();
                    (long s1, int u1) = Total(p, c);
                    bool ok = cancelled && owedS == Bounty && paid == 1 && again == 0 && s1 == s0 && u1 == u0 && QuestStore.OwedTotalsForTest(p) == (0, 0);
                    return (ok, $"cancelled={cancelled} owedWhileRefused={owedS} paid={paid} paidAgain={again} conserved={s1 == s0 && u1 == u0}");
                }
                finally { Cleanup(p, c); }
            });

            Check("Quest restart: a payout owed to a player whose economy is reset is dropped with it, never paid into the new one", () =>
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare("cancel", p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    long ps0 = TreasuryStore.GetPersonalSilver(p);
                    QuestStore.AtPayoutPhaseForTest = ph => { if (ph == "recorded") throw new OperationCanceledException("selftest"); };
                    try { QuestStore.Cancel(p, id); } catch (OperationCanceledException) { }
                    finally { QuestStore.AtPayoutPhaseForTest = null; }
                    int dropped = QuestStore.DropOwedFor(p);
                    QuestStore.SaveToDisk();
                    Restart();
                    QuestStore.RecoverOnBoot();
                    bool ok = dropped == 1 && TreasuryStore.GetPersonalSilver(p) == ps0 && QuestStore.OwedCountForTest == 0;
                    return (ok, $"dropped={dropped} posterSilver {ps0}->{TreasuryStore.GetPersonalSilver(p)}");
                }
                finally { Cleanup(p, c); }
            });

            // A submitted bounty needs an exit besides Approve, or the poster's escrow is held for ever.
            (bool, string) Review(string action, Func<string, string, long, bool> settle, string outcome)
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare(action, p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    long ps0 = TreasuryStore.GetPersonalSilver(p), cs0 = TreasuryStore.GetPersonalSilver(c);
                    int pu0 = SteelOf(p), cu0 = SteelOf(c);
                    (long s0, int u0) = Total(p, c);
                    bool settled = settle(p, c, id);
                    Restart(); QuestStore.RecoverOnBoot();
                    (long s1, int u1) = Total(p, c);
                    string state = QuestStore.StateForTest(id);
                    if (outcome == "reopened")
                    {
                        bool ok = settled && state == QuestEntry.StateOpen && TreasuryStore.GetPersonalSilver(c) == cs0
                               && QuestStore.EscrowedForTest(p).Silver == Bounty && s1 == s0 && u1 == u0;
                        return (ok, $"settled={settled} state={state} claimer {cs0}->{TreasuryStore.GetPersonalSilver(c)} escrow={QuestStore.EscrowedForTest(p).Silver} conserved={s1 == s0 && u1 == u0}");
                    }
                    (bool fin, string fd) = Final(outcome, p, c, id, ps0, cs0, pu0, cu0);
                    return (settled && fin && s1 == s0 && u1 == u0, $"settled={settled}; {fd}");
                }
                finally { Cleanup(p, c); }
            }

            Check("Quest review: a poster can turn down a submitted bounty - reopened, unpaid, escrow still held",
                  () => Review("approve", (p, c, id) => QuestStore.ReviewProof(p, id, false, "not done", out _), "reopened"));
            Check("Quest review: a submission unreviewed past the timeout reopens unpaid, and a re-submit does not restart the clock",
                  () => Review("approve", (p, c, id) =>
                  {
                      QuestStore.BackdateReviewForTest(id, TimeSpan.FromHours(QuestsConfig.Current.ReviewTimeoutHours + 1));
                      QuestStore.Submit(c, id, out _);
                      return QuestStore.ReleaseStaleReviews(DateTime.UtcNow.Ticks).Exists(x => x.Id == id);
                  }, "reopened"));
            Check("Quest review: a released submission's quest then expires and refunds the poster once",
                  () => Review("approve", (p, c, id) =>
                  {
                      QuestStore.BackdateReviewForTest(id, TimeSpan.FromHours(QuestsConfig.Current.ReviewTimeoutHours + 1));
                      return QuestStore.ReleaseStaleReviews(DateTime.UtcNow.Ticks).Exists(x => x.Id == id) && QuestStore.ExpireQuest(id, out _);
                  }, "expire"));
            Check("Quest review: an admin can pay out a submitted bounty its poster never reviews",
                  () => Review("approve", (p, c, id) => QuestStore.AdminReview("selftest-admin", id, true, out _, out _), "approve"));
            Check("Quest review: an admin refund returns the poster's escrow from review, once",
                  () => Review("review", (p, c, id) => QuestStore.AdminRefund(id, "selftest-admin", out _, out _) && !QuestStore.AdminRefund(id, "selftest-admin", out _, out _), "expire"));

            // The goods were the claimer's before they reached the poster, so the poster's reset owes them the bounty.
            Check("Quest restart: resetting a poster whose delivery already reached them still pays the claimer", () =>
            {
                string p = User("p"), c = User("c");
                try
                {
                    long id = Prepare("submit", p, c, payloads: false, out string why);
                    if (id == 0) return (false, "prepare: " + why);
                    long cs0 = TreasuryStore.GetPersonalSilver(c);
                    int cu0 = SteelOf(c);
                    using (JsonFileStore.FailWritesForTest(f => f == KmhDataPaths.QuestsFile ? "selftest" : null))
                        QuestStore.Submit(c, id, out _);
                    bool handedOver = QuestStore.StateForTest(id) == QuestEntry.StateClaimed && SteelOf(c) == cu0 - Qty;
                    if (!Economy.KmhEconomyReset.Begin(p, "", out string beginWhy)) return (false, "begin: " + beginWhy);
                    Economy.KmhEconomyReset.Run(p, "");
                    long paid = TreasuryStore.GetPersonalSilver(c) - cs0;
                    int steel = SteelOf(c);
                    bool ok = handedOver && !Economy.KmhEconomyReset.InProgress && paid == Bounty && steel == cu0 - Qty + BountySteel;
                    return (ok, $"handedOver={handedOver} claimer paid {paid}s (want {Bounty}), holds {steel} Steel (want {cu0 - Qty + BountySteel})"
                              + (paid == 0 ? " - THE CLAIMER'S GOODS WENT WITH THE POSTER'S RESET" : ""));
                }
                finally
                {
                    if (Economy.KmhEconomyReset.Resetting(p)) Economy.KmhEconomyReset.ResetForTest();
                    Economy.EconomyResetStore.Forget(p);
                    Cleanup(p, c);
                }
            });

            return r;
        }
    }
}
