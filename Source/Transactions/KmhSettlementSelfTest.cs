using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;
using KMHServerAddon.Results;

namespace KMHServerAddon.Transactions
{
    internal static class KmhSettlementSelfTest
    {
        private sealed class MockMove : IKmhValueMove
        {
            public bool ReserveOk = true, DeliverOk = true;
            public KmhErrorCode ReserveErr = KmhErrorCode.InsufficientFunds, DeliverErr = KmhErrorCode.DeliveryFailed;
            public bool Reserved, Delivered, Refunded;

            public string System => "marketplace";
            public KmhTxType Type => KmhTxType.Purchase;
            public string Describe() => "mock move";
            public bool Reserve(out KmhErrorCode e) { e = ReserveOk ? KmhErrorCode.None : ReserveErr; if (ReserveOk) Reserved = true; return ReserveOk; }
            public bool Deliver(out KmhErrorCode e) { e = DeliverOk ? KmhErrorCode.None : DeliverErr; if (DeliverOk) Delivered = true; return DeliverOk; }
            public void Refund() { Refunded = true; }
        }

        private static KmhTransaction NewTx() => KmhTransaction.Create("p", "marketplace", KmhTxType.Purchase, "x");

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Only Run (which records the row) reaches a player, so a move it could not write down must not succeed.
            {
                var m = new MockMove();
                KmhSettlementOutcome o;
                using (JsonFileStore.FailWritesForTest(_ => "injected: disk full"))
                    o = KmhSettlement.Run("selftest_settle_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                          "", "", m);
                JsonFileStore.ClearFailureStateForTest();

                // Reserve/Deliver must never run: a move with no ledger row is invisible to recovery and dedup.
                r.Add(("Settlement: a transaction that cannot be recorded never moves value",
                    !o.Ok && !m.Reserved && !m.Delivered,
                    !o.Ok && !m.Reserved && !m.Delivered
                        ? "refused before reserve"
                        : $"ok={o.Ok} reserved={m.Reserved} delivered={m.Delivered}"));
            }

            // Not a plain success: next boot the row is still Reserved/Approved, which recovery refunds - paying twice.
            {
                var m = new MockMove();
                int writes = 0;
                KmhSettlementOutcome o;
                bool degraded;
                // Read inside the scope: disposing the injector also clears the degraded flag.
                using (JsonFileStore.FailWritesForTest(_ => ++writes == 1 ? null : "injected: disk full"))
                {
                    o = KmhSettlement.Run("selftest_settle_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                          "", "", m);
                    degraded = Maintenance.KmhMaintenanceGate.PersistenceDegraded;
                }
                JsonFileStore.ClearFailureStateForTest();

                r.Add(("Settlement: a completed move whose record cannot be saved degrades persistence",
                    m.Delivered && degraded,
                    m.Delivered && degraded
                        ? "delivered, and further value moves are stopped"
                        : $"delivered={m.Delivered} degraded={degraded}"));
            }

            // Callers discard Update's answer, so the repository must notice: a failed WRITE degrades, a refusal must not.
            {
                KmhTransaction tx = KmhTransaction.Create("selftest_upd_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                                          "marketplace", KmhTxType.Purchase, "x");
                bool added = KmhTransactionRepository.Add(tx);
                tx.Advance(KmhTxState.Validating);

                bool degradedOnWriteFailure, updated;
                using (JsonFileStore.FailWritesForTest(_ => "injected: disk full"))
                {
                    updated = KmhTransactionRepository.Update(tx);
                    degradedOnWriteFailure = Maintenance.KmhMaintenanceGate.PersistenceDegraded;
                }
                JsonFileStore.ClearFailureStateForTest();

                r.Add(("Transactions: a state that cannot be written degrades persistence",
                    added && !updated && degradedOnWriteFailure,
                    added && !updated && degradedOnWriteFailure
                        ? "the next value move is refused"
                        : $"added={added} updated={updated} degraded={degradedOnWriteFailure}"));

                // An unknown row is a refusal, not a disk problem, so degrading on it would stop a healthy server.
                KmhTransaction stranger = KmhTransaction.Create("nobody", "marketplace", KmhTxType.Purchase, "x");
                bool ghost = KmhTransactionRepository.Update(stranger);
                bool quiet = !Maintenance.KmhMaintenanceGate.PersistenceDegraded;
                r.Add(("Transactions: a row the ledger never had is refused without degrading anything",
                    !ghost && quiet, !ghost && quiet ? "refused, nothing degraded" : $"ghost={ghost} quiet={quiet}"));
            }

            var m1 = new MockMove();
            var tx1 = NewTx();
            var o1 = KmhSettlement.RunCore(tx1, m1);
            r.Add(("Settlement: happy path confirms",
                o1.Ok && tx1.State == KmhTxState.Confirmed && m1.Reserved && m1.Delivered && !m1.Refunded, "reserve->deliver->confirm"));

            var m2 = new MockMove();
            var tx2 = NewTx();
            var o2 = KmhSettlement.RunCore(tx2, m2, () => KmhErrorCode.PermissionDenied);
            r.Add(("Settlement: policy refusal rejects",
                !o2.Ok && o2.Error == KmhErrorCode.PermissionDenied && tx2.State == KmhTxState.Rejected
                    && !m2.Reserved && o2.Message.Length > 0, "rejected before reserve"));

            var m3 = new MockMove { ReserveOk = false };
            var tx3 = NewTx();
            var o3 = KmhSettlement.RunCore(tx3, m3);
            r.Add(("Settlement: reserve failure rejects, no refund",
                !o3.Ok && o3.Error == KmhErrorCode.InsufficientFunds && tx3.State == KmhTxState.Rejected && !m3.Refunded, "no phantom refund"));

            var m4 = new MockMove { DeliverOk = false };
            var tx4 = NewTx();
            var o4 = KmhSettlement.RunCore(tx4, m4);
            r.Add(("Settlement: delivery failure refunds",
                !o4.Ok && m4.Reserved && !m4.Delivered && m4.Refunded && tx4.State == KmhTxState.Refunded, "reserved then refunded"));

            var thrower = new ThrowingMove();
            var tx5 = NewTx();
            var o5 = KmhSettlement.RunCore(tx5, thrower);
            r.Add(("Settlement: a throwing deliver still refunds",
                !o5.Ok && thrower.Refunded && tx5.State == KmhTxState.Refunded, "exception -> refund"));

            // A refund that threw must not record terminal Refunded, or the reservation strands behind a settled row.
            var badRefund = new RefundThrowsMove();
            var tx6 = NewTx();
            var o6 = KmhSettlement.RunCore(tx6, badRefund);
            bool stillOpen = !o6.Ok && tx6.State == KmhTxState.Compensating
                          && KmhTransactionRecovery.DecideForStuck(tx6.State) == KmhTxRecoveryAction.Refund;
            r.Add(("Settlement: a refund that throws is not recorded as refunded", stillOpen,
                stillOpen ? "left Compensating, which recovery retries"
                          : $"state={tx6.State} - STRANDED VALUE RECORDED AS RETURNED"));

            return r;
        }

        private sealed class ThrowingMove : IKmhValueMove
        {
            public bool Refunded;
            public string System => "marketplace";
            public KmhTxType Type => KmhTxType.Purchase;
            public string Describe() => "throwing move";
            public bool Reserve(out KmhErrorCode e) { e = KmhErrorCode.None; return true; }
            public bool Deliver(out KmhErrorCode e) { throw new System.Exception("boom"); }
            public void Refund() { Refunded = true; }
        }

        private sealed class RefundThrowsMove : IKmhValueMove
        {
            public string System => "marketplace";
            public KmhTxType Type => KmhTxType.Purchase;
            public string Describe() => "refund throws";
            public bool Reserve(out KmhErrorCode e) { e = KmhErrorCode.None; return true; }
            public bool Deliver(out KmhErrorCode e) { e = KmhErrorCode.DeliveryFailed; return false; }
            public void Refund() { throw new System.Exception("refund boom"); }
        }
    }
}
