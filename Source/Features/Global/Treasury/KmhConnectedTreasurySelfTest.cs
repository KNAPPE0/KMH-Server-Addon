using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Delivery;
using KMHServerAddon.Features.Treasury;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Treasury
{
    // Each case is a real-world interruption (lost reply, crash between writes, retry, expired hold); silver must
    // exist exactly once - in a colony or in a balance, never both and never neither.
    internal static class KmhConnectedTreasurySelfTest
    {
        private static readonly CancellationToken Ct = CancellationToken.None;

        // Counts what it was asked, so "exactly once" is measured, not assumed.
        private sealed class FakeGlobal
        {
            public int Deposits, Reserves, Acks;
            public readonly List<string> DepositOps = new List<string>();
            public readonly List<string> ReserveOps = new List<string>();
            public readonly List<bool>   AckDelivered = new List<bool>();

            public bool Unreachable;
            public string RefuseWith = "";
            public int RefuseStatus = 409;
            public string LastReserveGrant = "";
            public string ReservationId = "res-1";
            public bool OmitReservationId;

            public Task<GlobalCallResult> Send(string path, object body, CancellationToken ct)
            {
                if (path.EndsWith("/deposit", StringComparison.Ordinal))
                {
                    Deposits++;
                    DepositOps.Add(((TreasuryDepositBody)body).OperationId);
                }
                else if (path.EndsWith("/reserve", StringComparison.Ordinal))
                {
                    Reserves++;
                    ReserveOps.Add(((TreasuryReserveBody)body).OperationId);
                    LastReserveGrant = ((TreasuryReserveBody)body).Grant;
                }
                else if (path.EndsWith("/ack", StringComparison.Ordinal))
                {
                    Acks++;
                    AckDelivered.Add(((TreasuryAckBody)body).Delivered);
                }

                if (Unreachable) return Task.FromResult(GlobalCallResult.Offline("offline"));
                if (!string.IsNullOrEmpty(RefuseWith))
                    return Task.FromResult(new GlobalCallResult { Ok = false, Status = RefuseStatus, ErrorCode = RefuseWith });

                JObject b = new JObject();
                if (!OmitReservationId) b["reservation_id"] = ReservationId;
                return Task.FromResult(new GlobalCallResult { Ok = true, Status = 200, Body = b });
            }
        }

        private sealed class Bench : IDisposable
        {
            public readonly FakeGlobal Global = new FakeGlobal();
            public readonly string Player;

            private readonly string _hadIdentity;
            private readonly bool _hadTreasury;

            public Bench(string player, int startingSilver)
            {
                Player = player;
                // Op ids are namespaced by the data-set identity, which the offline runner boots without.
                _hadIdentity = KmhServerIdentity.Id;
                if (string.IsNullOrEmpty(_hadIdentity))
                    Persistence.KmhDataMeta.SetInstanceIdForTest("selftestsrv");
                ConnectedTreasuryStore.ClearForTest();
                DeliveryStore.ClearForTest();
                ConnectedTreasuryService.AvailableForTest = true;
                ConnectedTreasuryService.SenderForTest = Global.Send;
                // Money rules run with the release switch on; the switch has its own cases in KmhConnectedGuildSelfTest.
                _hadTreasury = FeaturesConfig.Current.ConnectedTreasury;
                FeaturesConfig.Current.ConnectedTreasury = true;
                if (startingSilver > 0) TreasuryStore.DepositSilver(player, startingSilver, "selftest seed");
            }

            public void Dispose()
            {
                if (string.IsNullOrEmpty(_hadIdentity)) Persistence.KmhDataMeta.SetInstanceIdForTest(_hadIdentity);
                FeaturesConfig.Current.ConnectedTreasury = _hadTreasury;
                ConnectedTreasuryService.SenderForTest = null;
                ConnectedTreasuryService.AvailableForTest = null;
                ConnectedTreasuryStore.ClearForTest();
            }

            public long Silver => TreasuryStore.GetPersonalSilver(Player);
            public ConnectedOp Only => ConnectedTreasuryStore.For(Player).Count > 0
                                     ? ConnectedTreasuryStore.For(Player)[0] : null;
        }

        // Unique per run: a suite executed twice in one process must not inherit the first run's vault.
        private static readonly string Salt = Guid.NewGuid().ToString("N").Substring(0, 6);
        private static string P(string tag) => "kmh-selftest-ct-" + tag + "-" + Salt;

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            Deposits(r);
            Withdrawals(r);
            Reconciliation(r);
            Guards(r);
            AuditRegressions(r);
            return r;
        }


        // A refused deposit must come back, and a withdrawal must never be owed twice.
        private static void AuditRegressions(List<(string, bool, string)> r)
        {
            // A restore brings back an older sequence, and Global answers a seen id with that op's result.
            using (Bench b = new Bench(P("ids"), 0))
            {
                string file = Persistence.KmhDataPaths.ConnectedTreasuryFile;
                ConnectedOp first = ConnectedTreasuryStore.Open(b.Player, "user-1", ConnectedOpKind.Deposit, 10);
                string backup = System.IO.File.ReadAllText(file);
                ConnectedOp seen = ConnectedTreasuryStore.Open(b.Player, "user-1", ConnectedOpKind.Deposit, 20);
                System.IO.File.WriteAllText(file, backup);
                ConnectedTreasuryStore.ClearForTest();
                ConnectedTreasuryStore.LoadFromDisk();
                ConnectedOp next = ConnectedTreasuryStore.Open(b.Player, "user-1", ConnectedOpKind.Deposit, 30);
                bool ok = first != null && seen != null && next != null && next.Id != seen.Id && next.Id.Length <= 64;
                r.Add(("ids: a restored data set never reissues an operation id Global has already seen", ok,
                       ok ? $"fresh id after the restore ({next.Id.Length} chars)" : $"seen={seen?.Id} next={next?.Id} - REISSUED"));
            }

            using (Bench b = new Bench(P("ref1"), 500))
            {
                b.Global.RefuseWith = "KMH-TRS-964"; b.Global.RefuseStatus = 403;
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 200, Ct).GetAwaiter().GetResult();
                long afterRefusal = b.Silver;
                string phase = b.Only?.Phase;
                // A second pass must not pay the refund again.
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                bool ok = !res.Ok && !res.Pending && res.Code == "KMH-TRS-964"
                       && afterRefusal == 500 && b.Silver == 500 && phase == ConnectedOpPhase.Cancelled;
                r.Add(("deposit: a refusal that proves nothing landed returns the silver, exactly once",
                       ok, $"code={res.Code} pending={res.Pending} silver={afterRefusal}->{b.Silver} phase={phase}"));
            }

            using (Bench b = new Bench(P("ref2"), 500))
            {
                // Auth refusals precede Global's op lookup, so a lost earlier success could sit behind one.
                b.Global.RefuseWith = "KMH-SRV-319"; b.Global.RefuseStatus = 403;
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 200, Ct).GetAwaiter().GetResult();
                bool ok = res.Pending && b.Silver == 300 && b.Only?.Phase == ConnectedOpPhase.Escrowed;
                r.Add(("deposit: a refusal that cannot prove nothing landed keeps the silver escrowed",
                       ok, $"pending={res.Pending} silver={b.Silver} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench(P("ref3"), 500))
            {
                b.Global.RefuseWith = "KMH-TRS-964"; b.Global.RefuseStatus = 403;
                ConnectedResult first = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-selftest").GetAwaiter().GetResult();
                b.Global.RefuseWith = "";
                ConnectedResult second = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-selftest").GetAwaiter().GetResult();
                bool ok = !first.Ok && !first.Pending && second.Ok;
                r.Add(("withdraw: a refused reserve ends the operation, so the player is not locked out",
                       ok, $"first={first.Code}/{first.Pending} second={second.Ok}/{second.Code}"));
            }

            using (Bench b = new Bench(P("owe1"), 0))
            {
                // The Delivering write fails after the delivery row is saved, the gap where a reconcile could re-owe.
                ConnectedResult res;
                using (Persistence.JsonFileStore.FailWritesForTest(path =>
                           path == Persistence.KmhDataPaths.ConnectedTreasuryFile && DeliveryStore.OwedFor(b.Player).Count > 0
                               ? "injected: disk full" : null))
                    res = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 250, Ct, "grant-selftest").GetAwaiter().GetResult();
                Persistence.JsonFileStore.ClearFailureStateForTest();
                string stuckAt = b.Only?.Phase;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                int rows = DeliveryStore.OwedFor(b.Player).Count;
                bool ok = res.Pending && stuckAt == ConnectedOpPhase.Reserved && rows == 1
                       && b.Only?.Phase == ConnectedOpPhase.Delivering;
                r.Add(("withdraw: a lost phase write after the delivery is saved still owes the silver once",
                       ok, $"first={res.Code} stuckAt={stuckAt} owedRows={rows} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench(P("clm1"), 500))
            {
                b.Global.Unreachable = true;
                ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct).GetAwaiter().GetResult();
                int sentBefore = b.Global.Deposits;
                string id = b.Only?.Id;
                bool claimed = ConnectedTreasuryStore.TryClaimWork(id);
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                int sentWhileClaimed = b.Global.Deposits - sentBefore;
                ConnectedTreasuryStore.ReleaseWork(id);
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                int sentAfter = b.Global.Deposits - sentBefore;
                bool ok = claimed && sentWhileClaimed == 0 && sentAfter == 1;
                r.Add(("reconcile: an operation a request is working on is left alone until it is released",
                       ok, $"claimed={claimed} whileClaimed={sentWhileClaimed} afterRelease={sentAfter}"));
            }

            using (Bench b = new Bench(P("grant1"), 0))
            {
                ConnectedResult none = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct).GetAwaiter().GetResult();
                ConnectedResult with = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-abc").GetAwaiter().GetResult();
                bool ok = !none.Ok && none.Code == "grant.missing" && b.Global.Reserves == 1 && with.Ok
                       && b.Global.LastReserveGrant == "grant-abc";
                r.Add(("withdraw: nothing is asked of Global without the player's grant, and the grant rides the reserve",
                       ok, $"none={none.Code} reserves={b.Global.Reserves} grant={b.Global.LastReserveGrant}"));
            }

            using (Bench b = new Bench(P("grant2"), 0))
            {
                // Refused by Global for want of a valid grant: nothing is held, so the player is free to ask again.
                b.Global.RefuseWith = "KMH-TRS-970"; b.Global.RefuseStatus = 403;
                ConnectedResult first = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "stale").GetAwaiter().GetResult();
                b.Global.RefuseWith = "";
                ConnectedResult second = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "fresh").GetAwaiter().GetResult();
                bool ok = !first.Ok && !first.Pending && second.Ok;
                r.Add(("withdraw: a refused grant ends the operation instead of locking the player out",
                       ok, $"first={first.Code}/{first.Pending} second={second.Ok}"));
            }

            using (Bench b = new Bench(P("cas1"), 500))
            {
                ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct).GetAwaiter().GetResult();
                string id = b.Only?.Id;
                bool stale = ConnectedTreasuryStore.Advance(id, ConnectedOpPhase.Escrowed, expectFrom: ConnectedOpPhase.Opening);
                bool ok = !stale && b.Only?.Phase == ConnectedOpPhase.Credited;
                r.Add(("store: a transition from a phase the operation has already left is refused",
                       ok, $"staleApplied={stale} phase={b.Only?.Phase}"));
            }
        }

        private static void Deposits(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench(P("dep1"), 500))
            {
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 200, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("deposit: a first deposit escrows the silver locally and is credited once",
                       res.Ok && b.Silver == 300 && b.Global.Deposits == 1 && b.Only.Phase == ConnectedOpPhase.Credited,
                       $"ok={res.Ok} silver={b.Silver} sent={b.Global.Deposits} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench(P("dep2"), 500))
            {
                // Global stays silent so the first is still in flight; a completed one would rightly let the next start.
                b.Global.Unreachable = true;
                ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct).GetAwaiter().GetResult();
                ConnectedResult second = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("deposit: a duplicate request while one is in flight is refused, not queued",
                       !second.Ok && second.Code == "operation.inflight" && b.Silver == 400
                       && ConnectedTreasuryStore.For(b.Player).Count == 1,
                       $"code={second.Code} silver={b.Silver} ops={ConnectedTreasuryStore.For(b.Player).Count}"));
            }

            using (Bench b = new Bench(P("dep2b"), 500))
            {
                ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct).GetAwaiter().GetResult();
                ConnectedResult second = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("deposit: a settled deposit does not block the next one",
                       second.Ok && b.Silver == 300 && b.Global.Deposits == 2,
                       $"ok={second.Ok} silver={b.Silver} sent={b.Global.Deposits}"));
            }

            using (Bench b = new Bench(P("dep3"), 500))
            {
                b.Global.Unreachable = true;
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 150, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("deposit: a lost response leaves the deposit pending, escrowed and retryable",
                       res.Pending && b.Silver == 350 && b.Only.Phase == ConnectedOpPhase.Escrowed,
                       $"pending={res.Pending} silver={b.Silver} phase={b.Only?.Phase}"));

                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("deposit: reconciliation retries the same operation id and credits exactly once",
                       b.Only.Phase == ConnectedOpPhase.Credited && b.Global.Deposits == 2
                       && b.Global.DepositOps[0] == b.Global.DepositOps[1],
                       $"phase={b.Only?.Phase} sends={b.Global.Deposits} sameId={b.Global.DepositOps[0] == b.Global.DepositOps[1]}"));
            }

            using (Bench b = new Bench(P("dep4"), 50))
            {
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 200, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("deposit: more than the colony holds is refused before anything is written",
                       !res.Ok && res.Code == "insufficient.local" && b.Silver == 50
                       && ConnectedTreasuryStore.For(b.Player).Count == 0,
                       $"code={res.Code} silver={b.Silver}"));
            }
        }


        private static void Withdrawals(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench(P("wd1"), 0))
            {
                ConnectedResult res = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 300, Ct, "grant-selftest")
                    .GetAwaiter().GetResult();
                ConnectedOp op = b.Only;
                r.Add(("withdraw: the hold is taken before anything is owed to the colony",
                       res.Ok && b.Global.Reserves == 1 && op.Phase == ConnectedOpPhase.Delivering
                       && !string.IsNullOrEmpty(op.ReservationId) && !string.IsNullOrEmpty(op.DeliveryId),
                       $"reserves={b.Global.Reserves} phase={op?.Phase} res={op?.ReservationId}"));

                r.Add(("withdraw: nothing is acknowledged while the delivery is still owed",
                       b.Global.Acks == 0 && DeliveryStore.IsOwed(op.DeliveryId), $"acks={b.Global.Acks}"));

                DeliveryStore.Ack(b.Player, op.DeliveryId);
                ConnectedTreasuryService.SettleIfDeliveredAsync(op, Ct).GetAwaiter().GetResult();
                r.Add(("withdraw: the finalize is sent only after the delivery is durably proven",
                       b.Global.Acks == 1 && b.Global.AckDelivered[0] && b.Only.Phase == ConnectedOpPhase.Settled,
                       $"acks={b.Global.Acks} delivered={(b.Global.Acks > 0 ? b.Global.AckDelivered[0] : false)} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench(P("wd2"), 0))
            {
                b.Global.Unreachable = true;
                ConnectedResult res = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-selftest")
                    .GetAwaiter().GetResult();
                // A delivery without a reservation is silver nobody debited.
                r.Add(("withdraw: a lost reserve response owes the colony nothing",
                       res.Pending && b.Only.Phase == ConnectedOpPhase.Reserving
                       && string.IsNullOrEmpty(b.Only.DeliveryId) && DeliveryStore.OwedCount == 0,
                       $"phase={b.Only?.Phase} owed={DeliveryStore.OwedCount}"));

                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("withdraw: reconciliation re-asks with the same operation id and takes one hold",
                       b.Global.Reserves == 2 && b.Global.ReserveOps[0] == b.Global.ReserveOps[1]
                       && b.Only.Phase == ConnectedOpPhase.Delivering,
                       $"reserves={b.Global.Reserves} sameId={b.Global.ReserveOps[0] == b.Global.ReserveOps[1]}"));
            }

            using (Bench b = new Bench(P("wd3"), 0))
            {
                ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-selftest").GetAwaiter().GetResult();
                ConnectedOp op = b.Only;
                DeliveryStore.Ack(b.Player, op.DeliveryId);

                ConnectedTreasuryService.SettleIfDeliveredAsync(op, Ct).GetAwaiter().GetResult();
                int afterFirst = b.Global.Acks;
                ConnectedTreasuryService.SettleIfDeliveredAsync(b.Only, Ct).GetAwaiter().GetResult();
                r.Add(("withdraw: acknowledging an already settled hold sends nothing further",
                       afterFirst == 1 && b.Global.Acks == 1, $"acks={b.Global.Acks}"));
            }
        }


        private static void Reconciliation(List<(string, bool, string)> r)
        {
            // Simulates a crash between opening the op and escrowing the silver.
            using (Bench b = new Bench(P("rc1"), 500))
            {
                ConnectedOp op = ConnectedTreasuryStore.Open(b.Player, "user-1", ConnectedOpKind.Deposit, 100);
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("reconcile: a deposit that never escrowed anything is cancelled, not credited",
                       ConnectedTreasuryStore.Find(op.Id).Phase == ConnectedOpPhase.Cancelled
                       && b.Global.Deposits == 0 && b.Silver == 500,
                       $"phase={ConnectedTreasuryStore.Find(op.Id).Phase} sent={b.Global.Deposits}"));
            }

            // Simulates a crash after escrow, before the phase write: only the treasury marker proves the escrow.
            using (Bench b = new Bench(P("rc2"), 500))
            {
                ConnectedOp op = ConnectedTreasuryStore.Open(b.Player, "user-1", ConnectedOpKind.Deposit, 120);
                TreasuryStore.WithdrawSilverForTxn(b.Player, 120, op.Id, "selftest escrow");
                r.Add(("reconcile: the interrupted deposit really is mid-flight",
                       op.Phase == ConnectedOpPhase.Opening && b.Silver == 380, $"phase={op.Phase} silver={b.Silver}"));

                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("reconcile: an escrow proven by its marker is resumed and credited",
                       ConnectedTreasuryStore.Find(op.Id).Phase == ConnectedOpPhase.Credited
                       && b.Global.Deposits == 1 && b.Global.DepositOps[0] == op.Id,
                       $"phase={ConnectedTreasuryStore.Find(op.Id).Phase} sent={b.Global.Deposits}"));
            }

            // A crash after the hold was taken but before anything was owed to the colony.
            using (Bench b = new Bench(P("rc3"), 0))
            {
                ConnectedOp op = ConnectedTreasuryStore.Open(b.Player, "user-1", ConnectedOpKind.Withdraw, 90);
                ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Reserved, reservationId: "res-crash");

                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                ConnectedOp after = ConnectedTreasuryStore.Find(op.Id);
                r.Add(("reconcile: a hold with no delivery yet gets exactly one owed delivery",
                       after.Phase == ConnectedOpPhase.Delivering && !string.IsNullOrEmpty(after.DeliveryId)
                       && DeliveryStore.OwedCount == 1,
                       $"phase={after.Phase} owed={DeliveryStore.OwedCount}"));
            }

            // A crash after the silver physically landed but before Global was told.
            using (Bench b = new Bench(P("rc4"), 0))
            {
                ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 70, Ct, "grant-selftest").GetAwaiter().GetResult();
                ConnectedOp op = b.Only;
                DeliveryStore.Ack(b.Player, op.DeliveryId);

                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("reconcile: a delivery that already landed is finalized, and only once",
                       b.Only.Phase == ConnectedOpPhase.Settled && b.Global.Acks == 1 && b.Global.AckDelivered[0],
                       $"phase={b.Only?.Phase} acks={b.Global.Acks}"));
            }

            using (Bench b = new Bench(P("rc5"), 500))
            {
                b.Global.Unreachable = true;
                ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 60, Ct).GetAwaiter().GetResult();
                b.Global.Unreachable = false;

                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("reconcile: running twice credits once, because a settled operation is terminal",
                       b.Global.Deposits == 2 && b.Only.Phase == ConnectedOpPhase.Credited && b.Silver == 440,
                       $"sends={b.Global.Deposits} phase={b.Only?.Phase} silver={b.Silver}"));
            }
        }


        private static void Guards(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench(P("g1"), 500))
            {
                foreach (long bad in new long[] { 0, -1, -1000, ConnectedTreasuryService.MaxOperationSilver + 1 })
                {
                    ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", bad, Ct)
                        .GetAwaiter().GetResult();
                    r.Add(($"guard: an amount of {bad} is refused and writes nothing",
                           !res.Ok && res.Code == "amount.invalid" && ConnectedTreasuryStore.For(b.Player).Count == 0,
                           $"code={res.Code}"));
                }
            }

            using (Bench b = new Bench(P("g2"), 500))
            {
                // The caller takes the account from the verified session; an unlinked player has none.
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "", 100, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("guard: a player with no linked Global account moves no silver",
                       !res.Ok && res.Code == "account.unlinked" && b.Silver == 500, $"code={res.Code}"));
            }

            using (Bench b = new Bench(P("g3"), 500))
            {
                ConnectedTreasuryService.AvailableForTest = false;
                ConnectedResult res = ConnectedTreasuryService.DepositAsync(b.Player, "user-1", 100, Ct)
                    .GetAwaiter().GetResult();
                r.Add(("guard: with Global unavailable the local vault is untouched and still spendable",
                       !res.Ok && res.Code == "global.unavailable" && b.Silver == 500
                       && TreasuryStore.WithdrawSilver(b.Player, 50, "local still works") && b.Silver == 450,
                       $"code={res.Code} silver={b.Silver}"));
                ConnectedTreasuryService.AvailableForTest = true;
            }

            // Finalize is reachable only from Delivered.
            using (Bench b = new Bench(P("g4"), 0))
            {
                ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 80, Ct, "grant-selftest").GetAwaiter().GetResult();
                ConnectedOp op = b.Only;
                int before = b.Global.Acks;

                ConnectedResult res = ConnectedTreasuryService.SettleIfDeliveredAsync(op, Ct).GetAwaiter().GetResult();
                r.Add(("guard: a delivery still owed cannot be finalized",
                       res.Pending && res.Code == "delivery.pending" && b.Global.Acks == before
                       && b.Only.Phase == ConnectedOpPhase.Delivering,
                       $"code={res.Code} acks={b.Global.Acks}"));
            }

            using (Bench b = new Bench(P("g5"), 0))
            {
                b.Global.OmitReservationId = true;
                ConnectedResult res = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-selftest")
                    .GetAwaiter().GetResult();
                r.Add(("guard: a reserve with no reservation id owes the colony nothing",
                       res.Pending && res.Code == "reservation.missing" && DeliveryStore.OwedCount == 0
                       && b.Only.Phase == ConnectedOpPhase.Reserving,
                       $"code={res.Code} owed={DeliveryStore.OwedCount}"));
            }

            using (Bench b = new Bench(P("g6"), 0))
            {
                b.Global.RefuseWith = "KMH-GLB-118";
                ConnectedResult res = ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 100, Ct, "grant-selftest")
                    .GetAwaiter().GetResult();
                r.Add(("guard: a refusal such as a stale signing generation leaves the hold untaken",
                       res.Pending && b.Only.Phase == ConnectedOpPhase.Reserving && DeliveryStore.OwedCount == 0
                       && b.Only.Detail == "KMH-GLB-118",
                       $"code={res.Code} phase={b.Only?.Phase} detail={b.Only?.Detail}"));
            }

            using (Bench b = new Bench(P("g7"), 0))
            {
                ConnectedTreasuryService.WithdrawAsync(b.Player, "user-1", 60, Ct, "grant-selftest").GetAwaiter().GetResult();
                int owed = DeliveryStore.OwedCount;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guard: reconciling a delivering withdrawal never owes the colony a second grant",
                       owed == 1 && DeliveryStore.OwedCount == 1, $"owed={DeliveryStore.OwedCount}"));
            }
        }
    }
}
