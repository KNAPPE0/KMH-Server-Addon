using System;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global.Treasury;
using KMHServerAddon.Features.Guilds;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Guilds
{
    // Personal money rules plus who may ask (a proved link and a rank that may move guild silver); authority comes
    // from GuildStore and the federated balance from Global, never inferred here.
    internal static class ConnectedGuildService
    {
        private const string StatusPath  = "/api/v1/guilds/status";
        private const string DepositPath = "/api/v1/guilds/treasury/deposit";
        private const string ReservePath = "/api/v1/guilds/treasury/reserve";
        private const string AckPath     = "/api/v1/guilds/treasury/ack";

        public static bool Available => ConnectedTreasuryService.Available;

        private static Task<GlobalCallResult> SendAsync(string path, object body, CancellationToken ct)
            => ConnectedTreasuryService.SendForGuild(path, body, ct);

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static string Nonce() => Guid.NewGuid().ToString("N");
        private static int Generation() => GlobalConfig.Current.SigningGeneration;
        private static string ServerId() => KmhServerIdentity.Id;


        // Signed under its own domain, so a read can never be re-presented as proof of current authority.
        public static async Task<ConnectedResult> RefreshAsync(string linkId, CancellationToken ct)
        {
            if (!Available) return ConnectedResult.Fail("global.unavailable");
            FederatedGuildLink link = FederatedGuildStore.ById(linkId);
            if (link == null) return ConnectedResult.Fail("guild.unlinked");
            if (!link.MayRead) return ConnectedResult.Fail("guild.revoked");

            GuildStatusBody body = new GuildStatusBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(StatusPath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok)
            {
                // Global owns link state, so a refusal it names is adopted; that is how its re-proof demand shows here.
                if (r != null && r.ErrorCode == "KMH-GLD-959")
                    FederatedGuildStore.SetState(link.LinkId, FederatedLinkState.ReproofRequired, FederatedLinkDetail.ReproofRequired);
                else if (r != null && r.ErrorCode == "KMH-GLD-958")
                    FederatedGuildStore.SetState(link.LinkId, FederatedLinkState.Revoked, FederatedLinkDetail.LinkUnknown);
                return Remote(r);
            }

            AdoptState(link, r.Body);
            RememberBalance(link, r.Body);
            return ConnectedResult.Good(link.LinkId);
        }


        public static async Task<ConnectedResult> DepositAsync(string actor, long amount, CancellationToken ct)
        {
            ConnectedResult bad = Validate(actor, amount, out FederatedGuildLink link);
            if (bad != null) return bad;

            // Durable before anything moves: null means unwritten, and an unrecorded op could never be reconciled.
            ConnectedOp op = ConnectedTreasuryStore.OpenGuild(actor, link.LinkId, link.LocalGuild,
                                                              ConnectedOpKind.Deposit, amount);
            if (op == null) return ConnectedResult.Fail("persistence.refused");
            if (!ConnectedTreasuryStore.TryClaimWork(op.Id)) return ConnectedResult.Waiting(op, "operation.busy");
            try
            {
                // Local-withdrawal checks, idempotent on the op id so a retry takes the silver once.
                if (!GuildStore.EscrowForFederation(actor, (int)amount, op.Id, out string _, out string why))
                {
                    ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled, detail: why,
                                                   expectFrom: ConnectedOpPhase.Opening);
                    return Refuse(why);
                }

                if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Escrowed, expectFrom: ConnectedOpPhase.Opening))
                {
                    // Escrowed but the phase is not on disk: sending now would credit an escrow this server cannot prove
                    // after a restart, so reconciliation picks it up instead.
                    ConnectedTreasuryStore.Note(op.Id, "escrowed, phase unwritable - resumed by reconciliation");
                    return ConnectedResult.Waiting(op, "persistence.degraded");
                }

                return await PushDepositAsync(op, ct).ConfigureAwait(false);
            }
            finally { ConnectedTreasuryStore.ReleaseWork(op.Id); }
        }

        private static async Task<ConnectedResult> PushDepositAsync(ConnectedOp op, CancellationToken ct)
        {
            if (op.Phase != ConnectedOpPhase.Escrowed) return ConnectedResult.Fail("phase.invalid");

            GuildDepositBody body = new GuildDepositBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = op.LinkId, Amount = op.Amount, OperationId = op.Id,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Waiting(op, "signing.unavailable");

            GlobalCallResult r = await SendAsync(DepositPath, body, ct).ConfigureAwait(false);
            if (r != null && r.Ok)
            {
                ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Credited, expectFrom: ConnectedOpPhase.Escrowed);
                RememberBalance(FederatedGuildStore.ById(op.LinkId), r.Body);
                return ConnectedResult.Good(op);
            }

            string reason = Why(r);
            // A refusal proving nothing landed refunds the vault; the escrow has no other way out.
            if (ConnectedTreasuryService.ProvesNothingLanded(r, guild: true))
            {
                if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Refunding, detail: reason,
                                                    expectFrom: ConnectedOpPhase.Escrowed))
                    return ConnectedResult.Waiting(op, "persistence.degraded");
                ConnectedResult refunded = RefundDeposit(op);
                return refunded.Ok ? ConnectedResult.Fail(reason) : refunded;
            }

            // A re-proof demand mid-escrow leaves the op open, not lost; it completes once an officer re-proves the link.
            AdoptRefusal(op, r);
            ConnectedTreasuryStore.Note(op.Id, reason);
            return ConnectedResult.Waiting(op, reason);
        }

        // Its own marker, because the escrow's id is already committed on the vault and would short-circuit.
        internal static ConnectedResult RefundDeposit(ConnectedOp op)
        {
            if (op.Phase != ConnectedOpPhase.Refunding) return ConnectedResult.Fail("phase.invalid");
            if (!GuildStore.RefundFederationEscrow(op.Guild, (int)op.Amount, op.Player, op.Id + ":refund"))
                return ConnectedResult.Waiting(op, "refund.pending");
            ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled, expectFrom: ConnectedOpPhase.Refunding);
            ServerLog.Info($"Connected guild: Global refused deposit {op.Id} for '{op.Guild}' ({op.Detail}); "
                         + $"{op.Amount} silver went back to the guild vault.");
            return ConnectedResult.Good(op);
        }


        public static async Task<ConnectedResult> WithdrawAsync(string actor, long amount, CancellationToken ct)
        {
            ConnectedResult bad = Validate(actor, amount, out FederatedGuildLink link);
            if (bad != null) return bad;

            ConnectedOp op = ConnectedTreasuryStore.OpenGuild(actor, link.LinkId, link.LocalGuild,
                                                              ConnectedOpKind.Withdraw, amount);
            if (op == null) return ConnectedResult.Fail("persistence.refused");
            if (!ConnectedTreasuryStore.TryClaimWork(op.Id)) return ConnectedResult.Waiting(op, "operation.busy");
            try { return await PushReserveAsync(op, ct).ConfigureAwait(false); }
            finally { ConnectedTreasuryStore.ReleaseWork(op.Id); }
        }

        // Hold first: nothing reaches the vault until Global holds the silver, so no credit lacks a debit.
        private static async Task<ConnectedResult> PushReserveAsync(ConnectedOp op, CancellationToken ct)
        {
            GuildReserveBody body = new GuildReserveBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = op.LinkId, Amount = op.Amount, OperationId = op.Id,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Waiting(op, "signing.unavailable");

            GlobalCallResult r = await SendAsync(ReservePath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok)
            {
                string reason = Why(r);
                // Nothing is held and nothing has moved, so ending it frees the guild to move silver again.
                if (ConnectedTreasuryService.ProvesNothingLanded(r, guild: true)
                    && ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled, detail: reason,
                                                      expectFrom: ConnectedOpPhase.Reserving))
                    return ConnectedResult.Fail(reason);
                // Asking again with the same operation id returns the first hold rather than taking a second.
                AdoptRefusal(op, r);
                ConnectedTreasuryStore.Note(op.Id, reason);
                return ConnectedResult.Waiting(op, reason);
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
                // Held at Global but unsaved here: crediting now would be against a hold it cannot settle, so it
                // waits and re-asks, which is idempotent on the op id.
                ConnectedTreasuryStore.Note(op.Id, "reserved, phase unwritable - resumed by reconciliation");
                return ConnectedResult.Waiting(op, "persistence.degraded");
            }

            return await DeliverAsync(op, ct).ConfigureAwait(false);
        }

        // One idempotent local commit: marker and balance are written together so a replay credits once;
        // the proof is read back from the treasury, never remembered.
        private static async Task<ConnectedResult> DeliverAsync(ConnectedOp op, CancellationToken ct)
        {
            FederatedGuildLink owning = FederatedGuildStore.ById(op.LinkId);
            if (!GuildStore.GuildExists(op.Guild)
                || owning == null || !string.Equals(owning.Incarnation, GuildStore.IncarnationOf(op.Guild), StringComparison.Ordinal))
            {
                // Nothing can be credited, so release the hold: Global keeps the silver and the vault gets nothing.
                await AckAsync(op, false, ct).ConfigureAwait(false);
                return ConnectedResult.Fail("guild.missing");
            }

            if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Delivering, deliveryId: op.Id))
            {
                ConnectedTreasuryStore.Note(op.Id, "about to credit, phase unwritable - resumed by reconciliation");
                return ConnectedResult.Waiting(op, "persistence.degraded");
            }

            if (!GuildStore.CreditFromFederation(op.Guild, (int)op.Amount, op.Player, op.Id))
            {
                ConnectedTreasuryStore.Note(op.Id, "the guild vault could not be credited");
                return ConnectedResult.Waiting(op, "delivery.pending");
            }

            return await SettleIfDeliveredAsync(op, ct).ConfigureAwait(false);
        }

        // Proof is the treasury marker, not memory: a crash between credit and ack lands here and must not credit twice.
        public static async Task<ConnectedResult> SettleIfDeliveredAsync(ConnectedOp op, CancellationToken ct)
        {
            if (op.Phase != ConnectedOpPhase.Delivering) return ConnectedResult.Waiting(op, "phase.invalid");
            if (!Features.Treasury.TreasuryStore.KnowsGuildTxnMarker(op.Guild, op.Id))
                return ConnectedResult.Waiting(op, "delivery.pending");

            if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Delivered))
                return ConnectedResult.Waiting(op, "persistence.degraded");

            return await AckAsync(op, true, ct).ConfigureAwait(false);
        }

        // delivered=true only from Delivered, or Global finalizes silver that never reached a vault. Deliberately not
        // gated on the link being movable: the vault is credited, so refusing would book a real delivery as debt.
        private static async Task<ConnectedResult> AckAsync(ConnectedOp op, bool delivered, CancellationToken ct)
        {
            if (delivered && op.Phase != ConnectedOpPhase.Delivered) return ConnectedResult.Fail("phase.invalid");
            if (string.IsNullOrEmpty(op.ReservationId)) return ConnectedResult.Fail("reservation.unknown");

            GuildAckBody body = new GuildAckBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                ReservationId = op.ReservationId, Delivered = delivered,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return ConnectedResult.Waiting(op, "signing.unavailable");

            GlobalCallResult r = await SendAsync(AckPath, body, ct).ConfigureAwait(false);
            if (r != null && r.Ok)
            {
                ConnectedTreasuryStore.Advance(op.Id, delivered ? ConnectedOpPhase.Settled : ConnectedOpPhase.Cancelled);
                RememberBalance(FederatedGuildStore.ById(op.LinkId), r.Body);
                return ConnectedResult.Good(op);
            }

            // Acknowledging twice settles once, so retrying is safe and the operation stays open until it lands.
            string reason = Why(r);
            ConnectedTreasuryStore.Note(op.Id, reason);
            return ConnectedResult.Waiting(op, reason);
        }


        // Every decision reads a durable store; nothing is inferred from what this process remembers doing.
        public static async Task ResumeAsync(ConnectedOp op, CancellationToken ct)
        {
            if (op.IsDeposit)
            {
                if (op.Phase == ConnectedOpPhase.Opening)
                {
                    // The vault marker says if the escrow happened; a crash before the phase write lands here.
                    if (Features.Treasury.TreasuryStore.KnowsGuildTxnMarker(op.Guild, op.Id))
                    {
                        if (!ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Escrowed,
                                                            expectFrom: ConnectedOpPhase.Opening)) return;
                    }
                    else
                    {
                        ConnectedTreasuryStore.Advance(op.Id, ConnectedOpPhase.Cancelled,
                                                       detail: "no silver was ever escrowed",
                                                       expectFrom: ConnectedOpPhase.Opening);
                        return;
                    }
                }
                if (op.Phase == ConnectedOpPhase.Escrowed)
                    await PushDepositAsync(op, ct).ConfigureAwait(false);
                else if (op.Phase == ConnectedOpPhase.Refunding)
                    RefundDeposit(op);
                return;
            }

            if (op.Phase == ConnectedOpPhase.Reserving)
                await PushReserveAsync(op, ct).ConfigureAwait(false);
            else if (op.Phase == ConnectedOpPhase.Reserved)
                await DeliverAsync(op, ct).ConfigureAwait(false);
            else if (op.Phase == ConnectedOpPhase.Delivering)
                await SettleIfDeliveredAsync(op, ct).ConfigureAwait(false);
            else if (op.Phase == ConnectedOpPhase.Delivered)
                await AckAsync(op, true, ct).ConfigureAwait(false);
        }


        // Bound to the guild itself, not its name: a guild re-created under a disbanded one's name gets nothing.
        public static FederatedGuildLink LinkFor(string actor)
        {
            string guild = GuildStore.CurrentGuildOf(actor);
            return string.IsNullOrEmpty(guild) ? null
                 : FederatedGuildStore.ForGuildInstance(guild, GuildStore.IncarnationOf(guild));
        }

        private static ConnectedResult Validate(string actor, long amount, out FederatedGuildLink link)
        {
            link = null;
            if (!ConnectedTreasuryService.MayStartNew) return ConnectedResult.Fail("treasury.disabled");
            if (!Available) return ConnectedResult.Fail("global.unavailable");
            if (string.IsNullOrEmpty(actor)) return ConnectedResult.Fail("player.unknown");
            if (amount <= 0 || amount > ConnectedTreasuryService.MaxOperationSilver)
                return ConnectedResult.Fail("amount.invalid");

            link = LinkFor(actor);
            if (link == null) return ConnectedResult.Fail("guild.unlinked");
            if (link.State == FederatedLinkState.Revoked) return ConnectedResult.Fail("guild.revoked");
            // Stale authority is refused here as well as at Global, so it costs nothing and says why.
            if (!link.MayMoveValue) return ConnectedResult.Fail("guild.reproof");
            if (!GuildStore.CanMoveGuildSilver(actor, out string why)) return Refuse(why);
            if (ConnectedTreasuryStore.OpenOpForLink(link.LinkId) != null)
                return ConnectedResult.Fail("operation.inflight");
            return null;
        }

        private static void AdoptRefusal(ConnectedOp op, GlobalCallResult r)
        {
            if (r == null || r.Unreachable || string.IsNullOrEmpty(op.LinkId)) return;
            if (r.ErrorCode == "KMH-GLD-959")
                FederatedGuildStore.SetState(op.LinkId, FederatedLinkState.ReproofRequired, FederatedLinkDetail.ReproofRequired);
        }

        private static void AdoptState(FederatedGuildLink link, JObject body)
        {
            string state = ValueOf(body, "state");
            if (string.IsNullOrEmpty(state) || state == link.State) return;
            FederatedGuildStore.SetState(link.LinkId, state, ValueOf(body, "detail"));
        }

        private static void RememberBalance(FederatedGuildLink link, JObject body)
        {
            if (link == null || body == null) return;
            JToken b = body["balance"];
            if (b == null || b.Type != JTokenType.Object) return;
            FederatedGuildStore.RememberBalance(link.LinkId, new FederatedBalance
            {
                Settled   = LongOf(b, "settled"),
                Reserved  = LongOf(b, "reserved"),
                Available = LongOf(b, "available"),
                Debt      = LongOf(b, "debt"),
                Frozen    = BoolOf(b, "frozen"),
            });
        }

        private static ConnectedResult Refuse(string why)
            => new ConnectedResult { Code = "refused", Detail = why ?? "" };

        private static ConnectedResult Remote(GlobalCallResult r)
            => new ConnectedResult { Code = Why(r), Detail = r == null ? "" : (r.ErrorCode ?? "") };

        private static string Why(GlobalCallResult r)
            => r == null ? "no response" : (r.Unreachable ? "global unreachable" : r.ErrorCode);

        private static string ValueOf(JObject body, string key)
        {
            if (body == null) return "";
            JToken t = body[key];
            return t == null || t.Type == JTokenType.Null ? "" : t.ToString();
        }

        private static long LongOf(JToken o, string key)
        {
            JToken t = o?[key];
            return t == null || t.Type == JTokenType.Null ? 0 : (long?)t ?? 0;
        }

        private static bool BoolOf(JToken o, string key)
        {
            JToken t = o?[key];
            return t != null && t.Type != JTokenType.Null && ((bool?)t ?? false);
        }
    }
}
