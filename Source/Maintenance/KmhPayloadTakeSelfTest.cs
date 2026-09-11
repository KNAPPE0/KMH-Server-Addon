using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Items;

namespace KMHServerAddon.Maintenance
{
    // The instances only exist after they leave the vault, so their row is written second; these drive the crash between the two writes.
    internal static class KmhPayloadTakeSelfTest
    {
        private static KmhThingPayload Sword(string fingerprint)
            => new KmhThingPayload
            {
                DefName = "MeleeWeapon_LongSword", StuffDefName = "Plasteel", Quality = 5,
                StackCount = 1, HitPoints = 180, MaxHitPoints = 200,
                ScribeXml = "<thing><def>MeleeWeapon_LongSword</def></thing>",
                Fingerprint = fingerprint,
            };

        private static int PayloadCount(string who)
            => TreasuryStore.GetSnapshotFor(who)?.ItemPayloads?.Count ?? 0;

        private static int PendingTakeCount(string who) => TreasuryStore.PendingTakesFor(who).Count;

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            string Who() => "selftest_take_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            Check("Payload take: the vault records what left, in the commit that removed it", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                        who, Transactions.KmhTxType.Listing, "selftest post", 0);

                    List<KmhThingPayload> escrow = TreasuryStore.WithdrawPayloadsForTxn(
                        who, "fp1", 1, post.TakeMarker, note: "selftest escrow", refundMarker: post.RefundMarker);
                    if (escrow == null || escrow.Count != 1) return (false, "withdraw returned nothing");

                    // The row is deliberately NOT written: this is the instant the process is allowed to die.
                    Features.Treasury.Dto.PendingTake held =
                        TreasuryStore.PendingTakesFor(who).Find(p => p.TakeMarker == post.TakeMarker);
                    bool gone   = PayloadCount(who) == 0;
                    bool named  = held != null && held.Payloads.Count == 1;
                    // Reconstructing from a def name would lose exactly this, which is why the take is recorded whole.
                    bool intact = named && held.Payloads[0].Quality == 5 && held.Payloads[0].HitPoints == 180
                                  && held.Payloads[0].StuffDefName == "Plasteel"
                                  && !string.IsNullOrEmpty(held.Payloads[0].ScribeXml);
                    bool ok = gone && named && intact;
                    return (ok, ok ? "the vault holds the exact instances under the take marker"
                                  : $"leftVault={gone}, recorded={named}, stateIntact={intact}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: a crash before the row is written returns the goods, once", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                        who, Transactions.KmhTxType.Listing, "selftest post", 0);
                    TreasuryStore.WithdrawPayloadsForTxn(who, "fp1", 1, post.TakeMarker,
                                                         note: "selftest escrow", refundMarker: post.RefundMarker);

                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    bool back    = PayloadCount(who) == 1;
                    bool cleared = PendingTakeCount(who) == 0;
                    var restored = TreasuryStore.GetSnapshotFor(who)?.ItemPayloads;
                    bool intact  = back && restored[0].Quality == 5 && restored[0].HitPoints == 180;

                    // Recovery runs on every boot, and a second one must not mint a second sword.
                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    bool stillOne = PayloadCount(who) == 1;

                    bool ok = back && cleared && intact && stillOne;
                    return (ok, ok ? "returned with its state, the take forgotten, and idempotent on re-run"
                                  : $"returned={back}, cleared={cleared}, stateIntact={intact}, notDoubled={stillOne}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: once a row names the take, recovery hands back nothing", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                        who, Transactions.KmhTxType.Listing, "selftest post", 0);
                    List<KmhThingPayload> escrow = TreasuryStore.WithdrawPayloadsForTxn(
                        who, "fp1", 1, post.TakeMarker, note: "selftest escrow", refundMarker: post.RefundMarker);
                    if (!Transactions.KmhTransactionRepository.CommitTake(post, escrow)) return (false, "CommitTake");

                    // The row landed but the vault's entry was not cleared: returning them too is how one crash becomes two swords.
                    bool stillHeld = PendingTakeCount(who) == 1;
                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    bool notReturned = PayloadCount(who) == 0;
                    bool cleared     = PendingTakeCount(who) == 0;

                    Transactions.KmhTransactionRepository.Settle(post);
                    bool ok = stillHeld && notReturned && cleared;
                    return (ok, ok ? "the row owns them; the vault dropped its claim without paying"
                                  : $"heldBeforeRecovery={stillHeld}, notReturned={notReturned}, cleared={cleared}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: the inline refund and recovery cannot both pay it back", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                        who, Transactions.KmhTxType.Listing, "selftest post", 0);
                    List<KmhThingPayload> escrow = TreasuryStore.WithdrawPayloadsForTxn(
                        who, "fp1", 1, post.TakeMarker, note: "selftest escrow", refundMarker: post.RefundMarker);

                    // The caller's own failure path hands them straight back...
                    TreasuryStore.ReturnPendingTake(who, post.TakeMarker, post.RefundMarker, escrow,
                                                    "selftest inline refund");
                    bool backOnce = PayloadCount(who) == 1;

                    // ...and the same goods are offered again on the key recovery would use.
                    TreasuryStore.DepositEscrowOnce(who, post.RefundMarker, 0, null, escrow, "selftest replay");
                    bool stillOnce = PayloadCount(who) == 1;

                    bool ok = backOnce && stillOnce;
                    return (ok, ok ? "the refund marker is the single key, so the goods return exactly once"
                                  : $"returned={backOnce}, notDoubled={stillOnce} ({PayloadCount(who)} in vault)");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: a claim the disk would not release still stands, and says so", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                        who, Transactions.KmhTxType.Listing, "selftest post", 0);
                    TreasuryStore.WithdrawPayloadsForTxn(who, "fp1", 1, post.TakeMarker,
                                                         note: "selftest escrow", refundMarker: post.RefundMarker);

                    bool released;
                    using (Persistence.JsonFileStore.FailWritesForTest(_ => "injected: disk full"))
                        released = TreasuryStore.ClearPendingTake(who, post.TakeMarker);

                    // A claim outliving its row is how the seller gets a second copy, so callers refuse the post on this answer.
                    bool stillClaimed = PendingTakeCount(who) == 1;
                    bool ok = !released && stillClaimed;
                    return (ok, ok ? "a refused write reports false and leaves the claim in place"
                                  : $"reportedFailure={!released}, claimStillStands={stillClaimed}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); Persistence.JsonFileStore.ClearFailureStateForTest(); }
            });

            return r;
        }
    }
}
