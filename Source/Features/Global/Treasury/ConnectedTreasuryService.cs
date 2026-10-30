using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Delivery;
using KMHServerAddon.Features.Treasury;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Treasury
{
    internal sealed class ConnectedResult
    {
        public bool   Ok { get; set; }
        // Pending is not failure: the silver is accounted for and the operation resumes on its own.
        public bool   Pending { get; set; }
        public string Code { get; set; } = "";
        // Caller-facing refusal text when only the guild rules know the reason.
        public string Detail { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string Phase { get; set; } = "";

        public static ConnectedResult Fail(string code) => new ConnectedResult { Code = code };
        public static ConnectedResult Good(ConnectedOp op)
            => new ConnectedResult { Ok = true, OperationId = op.Id, Phase = op.Phase };
        // Federation actions succeed without an operation: there is a link to report, not silver in flight.
        public static ConnectedResult Good(string linkId)
            => new ConnectedResult { Ok = true, OperationId = linkId ?? "" };
        public static ConnectedResult Waiting(ConnectedOp op, string code)
            => new ConnectedResult { Pending = true, Code = code, OperationId = op.Id, Phase = op.Phase };
    }

    // Global never hears of silver whose removal from a colony is not durably recorded, and no colony gets silver
    // Global is not holding; retries, lost replies and crashes are safe because the op id never changes.
    internal static class ConnectedTreasuryService
    {
        // Mirrors Global's ceiling. Restated rather than fetched so a refusal is local and immediate.
        public const long MaxOperationSilver = 1_000_000;

        private const string DepositPath = "/api/v1/treasury/deposit";
        private const string ReservePath = "/api/v1/treasury/reserve";
        private const string AckPath     = "/api/v1/treasury/ack";

        // Self-test seam, shared with Guild Federation.
        internal static Func<string, object, CancellationToken, Task<GlobalCallResult>> SenderForTest
        {
            get => KmhGlobalPost.SenderForTest;
            set => KmhGlobalPost.SenderForTest = value;
        }

        // The offline self-test runner has no directory registration; nothing else assigns this.
        internal static bool? AvailableForTest;

        // Covers ops already in flight; starting one also needs MayStartNew, so the switch never stops settling.
        public static bool Available
        {
            get
            {
                if (AvailableForTest.HasValue) return AvailableForTest.Value;
                GlobalConfig cfg = GlobalConfig.Current;
                return cfg != null && cfg.Enabled
                    && GlobalSetupState.Current_.IsOwnerRegistered
                    && !string.IsNullOrEmpty(KmhServerIdentity.Id);
            }
        }

        // Reconciliation, acknowledgement and refunds never ask this.
        public static bool MayStartNew => FeaturesConfig.Current.ConnectedTreasury;

        private static Task<GlobalCallResult> SendAsync(string path, object body, CancellationToken ct)
            => KmhGlobalPost.SendAsync(path, body, ct);

        internal static Task<GlobalCallResult> SendForGuild(string path, object body, CancellationToken ct)
            => SendAsync(path, body, ct);

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static string Nonce() => Guid.NewGuid().ToString("N");


        public static async Task<ConnectedResult> DepositAsync(string player, string userId, long amount,
                                                               CancellationToken ct)
        {
            ConnectedResult bad = Validate(player, userId, amount);
            if (bad != null) return bad;
            if (TreasuryStore.GetPersonalSilver(player) < amount) return ConnectedResult.Fail("insufficient.local");

            // Durable before anything moves: null means unwritten, and an unrecorded op could never be reconciled.
            ConnectedOp op = ConnectedTreasuryStore.Open(player, userId, ConnectedOpKind.Deposit, amount);
            if (op == null) return ConnectedResult.Fail("persistence.refused");
            if (!ConnectedTreasuryStore.TryClaimWork(op.Id)) return ConnectedResult.Waiting(op, "operation.busy");
            try
            {
                // Idempotent on the op id; reconciliation reads its marker to tell whether an interrupted deposit escrowed.
                if (!TreasuryStore.WithdrawSilverForTxn(player, (int)amount, op.Id, "connected treasury deposit"))
                {
                    ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled,
                                                   detail: "local silver could not be escrowed",
                                                   expectFrom: ConnectedOpPhase.Opening);
                    return ConnectedResult.Fail("escrow.failed");
                }

                if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Escrowed, expectFrom: ConnectedOpPhase.Opening))
                {
                    // Escrowed but the phase is not on disk: sending now would credit an escrow this server cannot prove
                    // after a restart, so reconciliation finds it by the marker.
                    ConnectedTreasuryStore.Note(op.Id, "escrowed, phase unwritable - resumed by reconciliation");
                    return ConnectedResult.Waiting(op, "persistence.degraded");
                }

                return await PushDepositAsync(op, ct).ConfigureAwait(false);
            }
            finally { ConnectedTreasuryStore.ReleaseWork(op.Id); }
        }

        // Only ever called for an operation already at Escrowed, which is the proof Global's credit depends on.
        private static async Task<ConnectedResult> PushDepositAsync(ConnectedOp op, CancellationToken ct)
        {
            if (op.Phase != ConnectedOpPhase.Escrowed) return ConnectedResult.Fail("phase.invalid");

            TreasuryDepositBody body = new TreasuryDepositBody
            {
                ServerId = KmhServerIdentity.Id,
                Generation = GlobalConfig.Current.SigningGeneration,
                IssuedAtUnix = NowUnix(),
                Nonce = Nonce(),
                UserId = op.UserId,
                Amount = op.Amount,
                OperationId = op.Id,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Waiting(op, "signing.unavailable");

            GlobalCallResult r = await SendAsync(DepositPath, body, ct).ConfigureAwait(false);
            if (r != null && r.Ok)
            {
                ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Credited, expectFrom: ConnectedOpPhase.Escrowed);
                return ConnectedResult.Good(op);
            }

            string why = Why(r);
            // A refusal proving nothing landed refunds; anything else stays Escrowed and retries on the same op id.
            if (ProvesNothingLanded(r, guild: false))
            {
                if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Refunding, detail: why,
                                                    expectFrom: ConnectedOpPhase.Escrowed))
                    return ConnectedResult.Waiting(op, "persistence.degraded");
                ConnectedResult refunded = RefundDeposit(op);
                return refunded.Ok ? ConnectedResult.Fail(why) : refunded;
            }
            ConnectedTreasuryStore.Note(op.Id, why);
            return ConnectedResult.Waiting(op, why);
        }

        // Codes Global returns only before crediting this op id (request validation, or no ledger entry found). Auth
        // failures are excluded: decided before that lookup, a lost success could hide behind one. 965 (suspension) is
        // excluded too: it lifts and the escrow completes; 980 (switch off) is only answered after finding no entry.
        private static readonly HashSet<string> NothingLandedCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "KMH-TRS-951", "KMH-TRS-952", "KMH-TRS-953", "KMH-TRS-954", "KMH-TRS-956", "KMH-TRS-957",
            "KMH-TRS-964", "KMH-TRS-966", "KMH-TRS-967", "KMH-TRS-970", "KMH-TRS-972", "KMH-TRS-980",
        };

        // The guild routes check the link before the ledger, and have no owner check, so 953 proves nothing there.
        internal static bool ProvesNothingLanded(GlobalCallResult r, bool guild)
        {
            if (r == null || r.Ok || r.Unreachable || r.Status < 400 || r.Status >= 500) return false;
            if (r.Status == 408 || r.Status == 429) return false;
            if (guild && r.ErrorCode == "KMH-TRS-953") return false;
            return NothingLandedCodes.Contains(r.ErrorCode ?? "");
        }

        // A distinct marker, because the escrow's own id is already known to the vault and would short-circuit.
        internal static ConnectedResult RefundDeposit(ConnectedOp op)
        {
            if (op.Phase != ConnectedOpPhase.Refunding) return ConnectedResult.Fail("phase.invalid");
            if (!TreasuryStore.DepositEscrowOnce(op.Player, op.Id + ":refund", op.Amount, null, null,
                                                 "connected treasury deposit returned"))
                return ConnectedResult.Waiting(op, "refund.pending");
            ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled, expectFrom: ConnectedOpPhase.Refunding);
            ServerLog.Info($"Connected treasury: Global refused deposit {op.Id} for {op.Player} ({op.Detail}); "
                         + $"{op.Amount} silver went back to their vault.");
            return ConnectedResult.Good(op);
        }


        // grant is the player's Global consent; without it the reserve is refused regardless of server trust.
        public static async Task<ConnectedResult> WithdrawAsync(string player, string userId, long amount,
                                                                CancellationToken ct, string grant = "")
        {
            ConnectedResult bad = Validate(player, userId, amount);
            if (bad != null) return bad;
            if (string.IsNullOrWhiteSpace(grant)) return ConnectedResult.Fail("grant.missing");

            ConnectedOp op = ConnectedTreasuryStore.Open(player, userId, ConnectedOpKind.Withdraw, amount, grant.Trim());
            if (op == null) return ConnectedResult.Fail("persistence.refused");
            if (!ConnectedTreasuryStore.TryClaimWork(op.Id)) return ConnectedResult.Waiting(op, "operation.busy");
            try { return await PushReserveAsync(op, ct).ConfigureAwait(false); }
            finally { ConnectedTreasuryStore.ReleaseWork(op.Id); }
        }

        // Hold first: nothing is delivered until Global holds the silver, so no delivery lacks a debit.
        private static async Task<ConnectedResult> PushReserveAsync(ConnectedOp op, CancellationToken ct)
        {
            TreasuryReserveBody body = new TreasuryReserveBody
            {
                ServerId = KmhServerIdentity.Id,
                Generation = GlobalConfig.Current.SigningGeneration,
                IssuedAtUnix = NowUnix(),
                Nonce = Nonce(),
                UserId = op.UserId,
                Amount = op.Amount,
                OperationId = op.Id,
                Grant = op.Grant ?? "",
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Waiting(op, "signing.unavailable");

            GlobalCallResult r = await SendAsync(ReservePath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok)
            {
                string why = Why(r);
                // Nothing is held, so end it; left at Reserving, a definitive refusal would lock the player out for good.
                if (ProvesNothingLanded(r, guild: false)
                    && ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled, detail: why,
                                                      expectFrom: ConnectedOpPhase.Reserving))
                    return ConnectedResult.Fail(why);
                // Asking again with the same operation id returns the first hold rather than taking a second.
                ConnectedTreasuryStore.Note(op.Id, why);
                return ConnectedResult.Waiting(op, why);
            }

            string reservationId = ValueOf(r.Body, "reservation_id");
            if (string.IsNullOrEmpty(reservationId))
            {
                ConnectedTreasuryStore.Note(op.Id, "global accepted the reserve without naming a reservation");
                return ConnectedResult.Waiting(op, "reservation.missing");
            }

            if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Reserved, reservationId: reservationId,
                                                expectFrom: ConnectedOpPhase.Reserving))
            {
                // Held at Global but unsaved here: delivering now would be against a hold it cannot settle, so it
                // waits and re-asks, which is idempotent.
                ConnectedTreasuryStore.Note(op.Id, "reserved, phase unwritable - resumed by reconciliation");
                return ConnectedResult.Waiting(op, "persistence.degraded");
            }

            return await StartDeliveryAsync(op, ct).ConfigureAwait(false);
        }

        // One delivery per op, ever: a crash between Owe and the phase write must not grant twice.
        private static async Task<ConnectedResult> StartDeliveryAsync(ConnectedOp op, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(op.DeliveryId))
            {
                // Reserved with a delivery already named: only the phase write was lost, so finish that.
                if (op.Phase == ConnectedOpPhase.Reserved)
                    ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Delivering, expectFrom: ConnectedOpPhase.Reserved);
                return ConnectedResult.Waiting(op, "delivery.pending");
            }

            // Keyed on the op: the phase recording it is in another file, and a failed write there must not owe it twice.
            OutboundDelivery d = DeliveryStore.OweOnce(op.Player, op.Amount, op.Id);
            if (d == null)
            {
                // Nothing can be handed over, so release the hold: Global keeps the silver and the colony gets nothing.
                await AckAsync(op, false, ct).ConfigureAwait(false);
                return ConnectedResult.Fail("delivery.unwritable");
            }

            if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Delivering, deliveryId: d.Id,
                                                expectFrom: ConnectedOpPhase.Reserved))
            {
                ConnectedTreasuryStore.Note(op.Id, "delivery owed, phase unwritable - resumed by reconciliation");
                return ConnectedResult.Waiting(op, "persistence.degraded");
            }
            return ConnectedResult.Good(op);
        }

        // Only DeliveryStore proves landing: an owed delivery no longer owed is the sole proof the silver arrived.
        public static async Task<ConnectedResult> SettleIfDeliveredAsync(ConnectedOp op, CancellationToken ct)
        {
            if (op.Phase != ConnectedOpPhase.Delivering) return ConnectedResult.Waiting(op, "phase.invalid");
            if (string.IsNullOrEmpty(op.DeliveryId) || DeliveryStore.IsOwed(op.DeliveryId))
                return ConnectedResult.Waiting(op, "delivery.pending");

            if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Delivered, expectFrom: ConnectedOpPhase.Delivering))
                return ConnectedResult.Waiting(op, "persistence.degraded");

            return await AckAsync(op, true, ct).ConfigureAwait(false);
        }

        // delivered=true only from Delivered, or Global finalizes a withdrawal whose silver never reached a colony.
        private static async Task<ConnectedResult> AckAsync(ConnectedOp op, bool delivered, CancellationToken ct)
        {
            if (delivered && op.Phase != ConnectedOpPhase.Delivered) return ConnectedResult.Fail("phase.invalid");
            if (string.IsNullOrEmpty(op.ReservationId)) return ConnectedResult.Fail("reservation.unknown");

            TreasuryAckBody body = new TreasuryAckBody
            {
                ServerId = KmhServerIdentity.Id,
                Generation = GlobalConfig.Current.SigningGeneration,
                IssuedAtUnix = NowUnix(),
                Nonce = Nonce(),
                ReservationId = op.ReservationId,
                Delivered = delivered,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Waiting(op, "signing.unavailable");

            GlobalCallResult r = await SendAsync(AckPath, body, ct).ConfigureAwait(false);
            if (r != null && r.Ok)
            {
                ConnectedTreasuryStore.Advance(op.Id, delivered ? ConnectedOpPhase.Settled : ConnectedOpPhase.Cancelled,
                                               expectFrom: delivered ? ConnectedOpPhase.Delivered : null);
                return ConnectedResult.Good(op);
            }

            // Acknowledging twice settles once, so retrying is safe and the operation stays open until it lands.
            string why = Why(r);
            ConnectedTreasuryStore.Note(op.Id, why);
            return ConnectedResult.Waiting(op, why);
        }


        // Every decision reads a durable store; nothing is inferred from what this process remembers doing.
        public static async Task<int> ReconcileAsync(CancellationToken ct)
        {
            if (!Available) return 0;

            List<ConnectedOp> open = ConnectedTreasuryStore.OpenOps();
            int advanced = 0;

            foreach (ConnectedOp op in open)
            {
                if (!ConnectedTreasuryStore.TryClaimWork(op.Id)) continue;
                try
                {
                    string before = op.Phase;

                    if (op.IsGuild)
                    {
                        await Guilds.ConnectedGuildService.ResumeAsync(op, ct).ConfigureAwait(false);
                        if (op.Phase != before) advanced++;
                        continue;
                    }

                    if (op.IsDeposit)
                    {
                        if (op.Phase == ConnectedOpPhase.Opening)
                        {
                            // The treasury marker says if the escrow happened; a crash before the phase write lands here.
                            if (TreasuryStore.KnowsTxnMarker(op.Player, op.Id))
                            {
                                if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Escrowed,
                                                                    expectFrom: ConnectedOpPhase.Opening)) continue;
                            }
                            else
                            {
                                ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled,
                                                               detail: "no silver was ever escrowed",
                                                               expectFrom: ConnectedOpPhase.Opening);
                                advanced++;
                                continue;
                            }
                        }
                        if (op.Phase == ConnectedOpPhase.Escrowed)
                            await PushDepositAsync(op, ct).ConfigureAwait(false);
                        else if (op.Phase == ConnectedOpPhase.Refunding)
                            RefundDeposit(op);
                    }
                    else
                    {
                        if (op.Phase == ConnectedOpPhase.Reserving)
                            await PushReserveAsync(op, ct).ConfigureAwait(false);
                        else if (op.Phase == ConnectedOpPhase.Reserved)
                            await StartDeliveryAsync(op, ct).ConfigureAwait(false);
                        else if (op.Phase == ConnectedOpPhase.Delivering)
                            await SettleIfDeliveredAsync(op, ct).ConfigureAwait(false);
                        else if (op.Phase == ConnectedOpPhase.Delivered)
                            await AckAsync(op, true, ct).ConfigureAwait(false);
                    }

                    if (op.Phase != before) advanced++;
                }
                catch (Exception ex)
                {
                    // One stuck operation must not stop the others being reconciled.
                    ConnectedTreasuryStore.Note(op.Id, ex.GetType().Name);
                    ServerLog.Verbose($"Connected treasury: reconciling {op.Id} threw ({ex.GetType().Name}).");
                }
                finally { ConnectedTreasuryStore.ReleaseWork(op.Id); }
            }

            if (advanced > 0)
                ServerLog.Info($"Connected treasury: reconciled {advanced} federated operation(s) with Global.");
            return advanced;
        }


        private static ConnectedResult Validate(string player, string userId, long amount)
        {
            if (!MayStartNew) return ConnectedResult.Fail("treasury.disabled");
            if (!Available) return ConnectedResult.Fail("global.unavailable");
            if (string.IsNullOrEmpty(player)) return ConnectedResult.Fail("player.unknown");
            // The caller takes this from the verified session, never from anything the client sent.
            if (string.IsNullOrEmpty(userId)) return ConnectedResult.Fail("account.unlinked");
            if (amount <= 0 || amount > MaxOperationSilver) return ConnectedResult.Fail("amount.invalid");
            if (ConnectedTreasuryStore.OpenOpFor(player) != null) return ConnectedResult.Fail("operation.inflight");
            return null;
        }

        private static string Why(GlobalCallResult r)
            => r == null ? "no response" : (r.Unreachable ? "global unreachable" : r.ErrorCode);

        private static string ValueOf(JObject body, string key)
        {
            if (body == null) return "";
            JToken t = body[key];
            return t == null || t.Type == JTokenType.Null ? "" : t.ToString();
        }
    }
}
