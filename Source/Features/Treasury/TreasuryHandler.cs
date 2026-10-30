using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Treasury.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Treasury
{
    // A deposit amount is client-asserted, so the per-transaction cap applies whatever the access mode says.
    internal static class TreasuryHandler
    {
        // Guards a just-made deposit against the reconnect that races its first save.
        private const int ReconcileGraceSeconds = 120;

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryOpen,             OnOpen);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryRequest,          OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositSilver,    OnDepositSilver);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryWithdrawSilver,   OnWithdrawSilver);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositItem,      OnDepositItem);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryWithdrawItem,     OnWithdrawItem);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositConfirm,   OnDepositConfirm);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositReconcile, OnDepositReconcile);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositPreflight, OnDepositPreflight);
        }

        // Owner is the exact session, not the name: a token waives the cooldown and per-deposit cap, so a reconnect can't spend it.
        private struct PreApproval
        {
            public string User, Kind, ItemDefName;
            public long   Amount;
            public int    Qty;
            public long   ExpiryTicks;
            public ServerClient Owner;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PreApproval> _preflight
            = new System.Collections.Concurrent.ConcurrentDictionary<string, PreApproval>();
        private const int PreflightTtlSeconds = 30;

        private static void OnDepositPreflight(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            string reqId = env?.GetString("req_id") ?? "";
            string kind  = env?.GetString("kind") ?? "silver";
            bool   isItem   = kind == "item";
            long   amount   = isItem ? 0 : (env?.GetInt("amount", 0) ?? 0);
            string itemDef  = isItem ? (env?.GetString("item_def_name") ?? "") : "";
            int    qty      = isItem ? (env?.GetInt("qty", 0) ?? 0) : 0;
            bool   isPayload= env?.GetBool("is_payload", false) ?? false;

            // Before the client pulls goods out of the colony, which is what the preflight is for.
            if (!HasAccess(client, username)) { DenyPreflight(client, reqId, "The treasury is not open.", false); return; }

            if (CheckDepositAllowed(username, isItem, amount, itemDef, qty, isPayload, env, out string reason, out bool needsPayload))
            {
                string token = IssuePreflight(client, username, kind, amount, itemDef, qty);
                ReapPreflight();
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryDepositApproval, new { req_id = reqId, ok = true, token, ttl = PreflightTtlSeconds });
            }
            // needs_payload marks the compact-safety rejection so the client retries via the payload path, not gives up.
            else DenyPreflight(client, reqId, reason, needsPayload);
        }

        private static void DenyPreflight(ServerClient client, string reqId, string reason, bool needsPayload)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryDepositApproval,
                new { req_id = reqId, ok = false, reason = reason ?? "Deposit not allowed right now.", needs_payload = needsPayload });

        private static string IssuePreflight(ServerClient client, string user, string kind, long amount, string itemDef, int qty)
        {
            string token = System.Guid.NewGuid().ToString("N");
            _preflight[token] = new PreApproval { User = user, Kind = kind, Amount = amount, ItemDefName = itemDef, Qty = qty,
                ExpiryTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(PreflightTtlSeconds).Ticks, Owner = client };
            return token;
        }

        // Read-only, so a preflight can answer without moving anything.
        private static bool CheckDepositAllowed(string username, bool isItem, long silver, string itemDef, int qty, bool isPayload, KmhEnvelope env, out string reason, out bool needsPayload)
        {
            reason = null;
            needsPayload = false;
            var cfg = Features.Economy.EconomyConfig.Current;

            // A non-positive approval would sit in ReservedValue and cancel the daily limit's count of real ones.
            if (isItem ? qty <= 0 : silver <= 0) { reason = "There is nothing to deposit."; return false; }

            // Refused before the client removes anything, or parking the goods in recovery duplicates what it still holds.
            if (TooManyUnconfirmed(username, out string backlog)) { reason = backlog; return false; }

            if (!isItem)
            {
                long cap = cfg.Treasury.MaxSilverDepositPerTx;
                if (cap > 0 && silver > cap) { reason = $"That deposit is over the per-deposit limit ({Util.SilverFmt.Format(cap)})."; return false; }
            }
            else
            {
                int qtyCap = cfg.Treasury.MaxItemDepositQtyPerTx;
                if (qtyCap > 0 && qty > qtyCap) { reason = $"That item deposit is over the per-deposit limit ({qtyCap})."; return false; }
                if (!Util.KmhSafe.IsItemKey(itemDef)) { reason = "That isn't an item this server can store."; return false; }
                // Checked before the client takes the goods out: at commit a new kind past the cap has nowhere to go.
                if (!isPayload && TreasuryStore.NewItemKeyOverCap(username, itemDef))
                { reason = $"Your vault already holds {TreasuryStore.MaxItemKeysPerVault} kinds of item - withdraw some first."; return false; }
                // Not needs_payload: the full-state path would take it as an item just the same.
                if (Items.KmhItemSafety.IsSilverDef(itemDef)) { reason = Items.KmhItemSafety.SilverAsItemRefusal; return false; }
                if (!isPayload && !Items.KmhItemSafety.IsCompactDepositSafe((itemDef ?? "").Split('|')[0], out reason)) { needsPayload = true; return false; }
            }
            Features.Economy.EconomyContext ctx = Features.Economy.EconomyContext.FromEnvelope(env);
            if (!Features.Economy.EconomyAccess.CheckAccess(username, false, false, isItem, ctx, out reason)) return false;
            if (Features.Economy.EconomyAccess.OnCooldown(username, false, out int cd)) { reason = $"Deposit is on cooldown - wait {cd}s."; return false; }
            if (!isItem)
            {
                int fee = Features.Economy.EconomyAccess.DepositFeeOn((int)silver);
                if (Features.Economy.EconomyAccess.WouldExceedCap(false, TreasuryStore.GetPersonalSilver(username), (int)silver - fee, out reason)) return false;
            }
            // Last, so a deposit refused for any other reason never reaches staff as a limit hit.
            long value = isItem ? Features.Moderation.KmhEconomyGuard.ValueOfItem(itemDef, qty) : silver;
            return Features.Moderation.KmhEconomyGuard.AllowsDeposit(username, value, ReservedValue(username),
                isItem ? $"x{qty} {itemDef}" : $"{silver} silver", Features.Moderation.KmhEconomyGuard.AtPreflight, out reason);
        }

        // Approved for this account but not yet sent, so two approvals in flight cannot each fit under the daily limit.
        private static long ReservedValue(string username)
        {
            long now = DateTime.UtcNow.Ticks, sum = 0;
            foreach (var kv in _preflight)
            {
                PreApproval a = kv.Value;
                if (a.ExpiryTicks < now || !string.Equals(a.User, username, StringComparison.OrdinalIgnoreCase)) continue;
                long v = Math.Max(0, a.Kind == "item" ? Features.Moderation.KmhEconomyGuard.ValueOfItem(a.ItemDefName, a.Qty) : a.Amount);
                sum = v > long.MaxValue - sum ? long.MaxValue : sum + v;
            }
            return sum;
        }

        // Test seam: an approval waives the cooldown and the per-deposit cap, so prove who may spend one.
        internal static long ReservedValueForTest(string user) => ReservedValue(user);

        internal static string IssuePreflightForTest(ServerClient client, string user, string kind, long amount, string itemDef, int qty)
            => IssuePreflight(client, user, kind, amount, itemDef, qty);

        internal static bool ConsumePreflightForTest(ServerClient client, string user, string kind, long amount, string itemDef, int qty, string token)
            => TryConsumePreflight(client, user, kind, amount, itemDef, qty, token);

        internal static int OutstandingPreflightForTest => _preflight.Count;
        internal static bool ItemPreflightForTest(string user, string itemDef, int qty, bool isPayload, out string reason, out bool needsPayload)
            => CheckDepositAllowed(user, true, 0, itemDef, qty, isPayload, null, out reason, out needsPayload);
        internal static void DepositItemForTest(ServerClient client, KmhEnvelope env) => OnDepositItem(client, env);
        internal static bool SilverPreflightForTest(string user, long amount, out string reason)
            => CheckDepositAllowed(user, false, amount, "", 0, false, null, out reason, out _);
        internal static void DepositSilverForTest(ServerClient client, KmhEnvelope env) => OnDepositSilver(client, env);
        internal static void ReapPreflightForTest() => ReapPreflight();

        private static bool TryConsumePreflight(ServerClient client, string username, string kind, long amount, string itemDef, int qty, string token)
        {
            if (string.IsNullOrEmpty(token) || !_preflight.TryRemove(token, out PreApproval a)) return false;
            if (a.ExpiryTicks < DateTime.UtcNow.Ticks) return false;
            // The session that was approved, not merely the name on it.
            if (!ReferenceEquals(a.Owner, client)) return false;
            return string.Equals(a.User, username, StringComparison.OrdinalIgnoreCase) && a.Kind == kind
                && a.Amount == amount && a.Qty == qty
                && string.Equals(a.ItemDefName ?? "", itemDef ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static void ReapPreflight()
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (var kv in _preflight)
                // Dropped with the session too, so an approval never outlives the connection it was given to.
                if (kv.Value.ExpiryTicks < now || !KmhRouter.IsLive(kv.Value.Owner)) _preflight.TryRemove(kv.Key, out _);
        }

        // Only reconcile acts on a true result; the confirm path has no complete durable set to compare against.
        private static bool NoteSaveGeneration(string username, KmhEnvelope env, string source)
        {
            long gen = env?.GetLong("epoch", 0) ?? 0;
            if (gen <= 0) return false;                       // client doesn't report one - never treated as a rollback
            string ledger = LedgerOf(env);
            long seen = TreasuryStore.NoteSaveGeneration(username, gen, ledger);
            if (!TreasuryStore.SaveGenerationWentBackwards(seen, gen)) return false;
            // Without a ledger id a generation spans every colony, so a different colony also reports a low number.
            ServerLog.Error($"Treasury: {username} reported save generation {gen} after trusted generation {seen} on {source}" +
                            (ledger.Length > 0 ? $" for colony ledger {ledger.Substring(0, 8)} - that colony loaded an older save." : " - possible rollback or a different save.") +
                            $" Backwards state was NOT accepted (still trusting {seen}). Review with 'kmh audit-player {username}'.");
            return true;
        }

        // The colony a confirm or reconcile speaks for; "" from an older client or a save made before ledgers had ids.
        private static string LedgerOf(KmhEnvelope env) => TreasuryStore.LedgerIdOrEmpty(env?.GetString("ledger"));

        private static void OnDepositConfirm(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            System.Collections.Generic.List<string> ids = env?.GetStringList("txn_ids");
            NoteSaveGeneration(username, env, "confirm");
            int committed = TreasuryStore.ConfirmDeposits(username, ids, LedgerOf(env), env?.GetLong("epoch", 0) ?? 0);
            if (committed > 0)
            {
                ServerLog.Info($"Treasury: {username} confirmed {committed} durable deposit(s) - now spendable.");
                SendSnapshotTo(client);
            }
        }

        private static void OnDepositReconcile(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            System.Collections.Generic.List<string> ids = env?.GetStringList("committed") ?? new System.Collections.Generic.List<string>();
            // Goods already removed but not yet saved must not be reverted; an older client omits this and uses the grace window.
            System.Collections.Generic.List<string> unsaved = env?.GetStringList("pending") ?? new System.Collections.Generic.List<string>();
            bool rolledBack = NoteSaveGeneration(username, env, "reconcile");
            string ledger = LedgerOf(env);
            long gen = env?.GetLong("epoch", 0) ?? 0;
            (int committed, int reverted) = TreasuryStore.ReconcileDeposits(username, ids, unsaved, ReconcileGraceSeconds, ledger, gen);

            // Gated on an observed rollback, or a client merely omitting an id would cost a player their vault.
            int undone = 0, flagged = 0;
            if (rolledBack)
                (undone, flagged) = TreasuryStore.ReverseRolledBackCommits(username, ids, unsaved, ReconcileGraceSeconds, ledger, gen);

            if (committed > 0 || reverted > 0 || undone > 0 || flagged > 0)
            {
                ServerLog.Info($"Treasury: reconciled {username} deposits - {committed} committed, {reverted} reverted (local rollback)" +
                               (undone + flagged > 0 ? $", {undone} committed deposit(s) undone, {flagged} flagged" : "") + ".");
                SendSnapshotTo(client);
            }
            // Otherwise the vault just looks emptier after a crash, with nothing saying why.
            if (undone > 0)
                KmhRouter.Notify(client, "neutral", $"Your game loaded an older save, so {undone} treasury deposit(s) made after it were undone - those goods are back in your colony.");
        }

        // The only kind that may charge; Request stays free because it is also the auto-refresh.
        private static void OnOpen(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            // A retried packet or a double click answers the first outcome again rather than debiting twice.
            var op = new Security.KmhOpClaim("treasury.open", username, env);
            if (!op.Begin()) { SendAccessTo(client, username, 0, ""); return; }

            TreasuryAccess.Result r = TreasuryAccess.Open(username, DateTime.UtcNow.Ticks, out int charged, out long balance);
            if (r == TreasuryAccess.Result.InsufficientSilver)
            {
                // The id must work again once they have the silver, or a top-up stays locked out.
                op.Release();
                int fee = Features.Economy.EconomyConfig.Current.Treasury.AccessFeeSilver;
                KmhRouter.Notify(client, "negative",
                    $"Opening the treasury costs {Util.SilverFmt.Format(fee)}; your vault holds {Util.SilverFmt.Format(balance)}.");
                SendAccessTo(client, username, 0, "insufficient_silver");
                return;
            }

            if (charged > 0)
                KmhRouter.Notify(client, "neutral",
                    $"Treasury access fee: {Util.SilverFmt.Format(charged)}. Open for {(int)TreasuryAccess.Lease.TotalMinutes} minutes.");
            SendAccessTo(client, username, charged, r.ToString());
            SendSnapshotTo(client);
        }

        private static void SendAccessTo(ServerClient client, string username, int charged, string result)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryAccess, new
            {
                ok            = TreasuryAccess.Has(username),
                fee           = Features.Economy.EconomyConfig.Current.Treasury.AccessFeeSilver,
                charged,
                result,
                expires_at    = TreasuryAccess.ExpiresAtUnix(username),
                lease_minutes = (int)TreasuryAccess.Lease.TotalMinutes,
            });

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        // Always true with no fee configured, so an unset server behaves as it always did.
        internal static bool HasAccess(ServerClient client, string username)
        {
            if (TreasuryAccess.Has(username))
            {
                int late = TreasuryAccess.SettleWaived(username);
                if (late > 0)
                    KmhRouter.Notify(client, "neutral", $"Treasury access fee: {Util.SilverFmt.Format(late)} (this session opened on an empty vault).");
                return true;
            }
            KmhRouter.Notify(client, "negative",
                $"Open the treasury first - access costs {Util.SilverFmt.Format(Features.Economy.EconomyConfig.Current.Treasury.AccessFeeSilver)}.");
            SendAccessTo(client, username, 0, "closed");
            return false;
        }

        private static void OnDepositSilver(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            int    amount   = env?.GetInt("amount", 0) ?? 0;
            if (amount <= 0)
            {
                ServerLog.Verbose($"Treasury deposit_silver rejected (amount={amount}) for {username}");
                return;
            }
            // A preflight token skips the re-check; without one the Recovery hold below still prevents any loss.
            bool preApproved = TryConsumePreflight(client, username, "silver", amount, "", 0, env?.GetString("deposit_token") ?? "");
            long cap = Features.Economy.EconomyConfig.Current.Treasury.MaxSilverDepositPerTx;
            if (!preApproved && cap > 0 && amount > cap)
            {
                // No grant-back: a modified client may never have removed the silver, so returning it could mint.
                ServerLog.Warn($"Treasury: {username} deposit_silver {amount}s REJECTED - over cap {cap}s (possible cheat)");
                KmhRouter.Notify(client, "negative", $"That deposit is over the server's per-deposit limit ({Util.SilverFmt.Format(cap)}).");
                SendSnapshotTo(client);
                return;
            }
            // Every current client asks first, so an unapproved deposit is never parked: it may never have left the colony.
            if (!preApproved && !Features.Moderation.KmhEconomyGuard.AllowsDeposit(username, amount, ReservedValue(username), $"{amount} silver",
                    Features.Moderation.KmhEconomyGuard.NoApproval, out string limitWhy))
            {
                ServerLog.Warn($"Treasury: {username} deposit_silver {amount}s REJECTED - over the daily deposit limit without an approval (possible cheat)");
                KmhRouter.Notify(client, "negative", limitWhy);
                SendSnapshotTo(client);
                return;
            }
            // Refused, never parked in Recovery: without an approval the silver may never have left the colony.
            if (!preApproved && !Gate(client, username, isWithdraw: false, isGuild: false, isItem: false, env)) return;
            int fee = Features.Economy.EconomyAccess.DepositFeeOn(amount);
            int net = amount - fee;
            if (!preApproved && Features.Economy.EconomyAccess.WouldExceedCap(false, TreasuryStore.GetPersonalSilver(username), net, out string capReason))
            { KmhRouter.Notify(client, "negative", capReason); SendSnapshotTo(client); return; }

            // Held pending until the save confirms, and the fee rides along so it only lands if the deposit commits.
            string txnId = env?.GetString("txn_id") ?? "";
            if (Features.Economy.EconomyConfig.Current.Treasury.RequireDurableLocalSave && !string.IsNullOrEmpty(txnId))
            {
                // Preflight already refused this before an honest client removed anything, so nothing is parked.
                if (TooManyUnconfirmed(username, out string backlog))
                { KmhRouter.Notify(client, "negative", backlog); SendSnapshotTo(client); return; }
                if (TreasuryStore.BeginPendingDeposit(username, new Dto.PendingDeposit
                        { TxnId = txnId, Kind = Dto.PendingDeposit.KindSilver, Silver = net, Fee = fee }))
                {
                    ServerLog.Info($"Treasury: {username} deposit {net}s PENDING (txn {txnId}, fee {fee}s -> house pool on commit) - awaiting durable-save confirm");
                    NoteDeposit(username, amount);
                    Features.Economy.EconomyAccess.Audit(username, false, false, amount);
                }
                SendSnapshotTo(client);
                return;
            }
            if (string.IsNullOrEmpty(txnId))
                ServerLog.Warn($"Treasury: {username} deposited {amount}s immediately (old client, no durable txn) - rollback dupe risk.");
            // The fee is owed in the deposit's own commit, so a crash before the pool's save cannot lose it.
            if (TreasuryStore.DepositSilverOwingFee(username, net, fee, note: fee > 0 ? $"deposit (fee {fee}s)" : ""))
            {
                ServerLog.Info($"Treasury: {username} deposited {net}s (fee {fee}s -> house pool)");
                NoteDeposit(username, amount);
                Features.Economy.EconomyAccess.Audit(username, false, false, amount);
                SendSnapshotTo(client);
            }
        }

        // Pending-deposit safety is separate from this gate and applies regardless of what it answers.
        private static bool Gate(ServerClient client, string username, bool isWithdraw, bool isGuild, bool isItem, KmhEnvelope env)
        {
            // Server-side, so a client that never sends kmh.treasury.open cannot move value for free.
            if (!HasAccess(client, username)) return false;
            Features.Economy.EconomyContext ctx = Features.Economy.EconomyContext.FromEnvelope(env);
            if (!Features.Economy.EconomyAccess.CheckAccess(username, isWithdraw, isGuild, isItem, ctx, out string reason))
            {
                ServerLog.Verbose($"Treasury {(isWithdraw ? "withdraw" : "deposit")} denied for {username}: {reason}");
                KmhRouter.Notify(client, "negative", reason);
                SendSnapshotTo(client);
                return false;
            }
            if (Features.Economy.EconomyAccess.OnCooldown(username, isWithdraw, out int cdSec))
            {
                KmhRouter.Notify(client, "negative", $"Treasury {(isWithdraw ? "withdraw" : "deposit")} is on cooldown - wait {cdSec}s.");
                SendSnapshotTo(client);
                return false;
            }
            if (isWithdraw && Delivery.DeliveryStore.TooManyOwed(username, out string owedWhy))
            {
                KmhRouter.Notify(client, "negative", owedWhy);
                SendSnapshotTo(client);
                return false;
            }
            return true;
        }

        internal static bool WouldRefuseForBacklogForTest(string username, out string reason) => TooManyUnconfirmed(username, out reason);

        // Mint-safe: the value stays server-side, so a fabricated deposit only fills the queue and gains nothing.
        private static bool TooManyUnconfirmed(string username, out string reason)
        {
            reason = null;
            int cap = Features.Economy.EconomyConfig.Current.Treasury.MaxUnconfirmedPerPlayer;
            if (cap <= 0) return false;
            int open = TreasuryStore.UnconfirmedDepositCount(username);
            if (open < cap) return false;
            reason = $"Deposit paused: {open} earlier deposit(s) are still waiting for your game to save. "
                   + "Save your game to finalize them, then deposit again.";
            return true;
        }

        // An accepted deposit starts the cooldown and counts against the daily deposit limit.
        private static void NoteDeposit(string username, long value)
        {
            Features.Economy.EconomyAccess.RecordAction(username, false);
            Features.Moderation.KmhEconomyGuard.Record(username, value);
        }

        private static void OnWithdrawSilver(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            int    amount   = env?.GetInt("amount", 0) ?? 0;
            if (amount <= 0) return;
            if (!Gate(client, username, isWithdraw: true, isGuild: false, isItem: false, env)) return;
            // Extension veto, asked before the debit so a denial has no side effects.
            KMH.Sdk.Server.Hooks.KmhHookVerdict wv = Extensibility.KmhHooks.Instance.CheckTreasuryWithdraw(
                new KMH.Sdk.Server.Hooks.KmhTreasuryWithdrawContext(username, isItem: false, itemDefName: "", quantity: 0, silverAmount: amount));
            if (wv.Denied) { KmhRouter.Notify(client, "negative", wv.Reason); return; }

            var op = new Security.KmhOpClaim("treasury.withdraw_silver", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); return; }

            switch (WithdrawSilverToColony(username, amount, out Delivery.OutboundDelivery d))
            {
                case WithdrawOutcome.Delivered:
                    Features.Economy.EconomyAccess.RecordAction(username, true);
                    Features.Economy.EconomyAccess.Audit(username, true, false, amount);
                    Delivery.DeliveryHandler.Send(client, d);
                    break;
                case WithdrawOutcome.Compensated:
                    KmhRouter.Notify(client, "negative", "The server couldn't hand that over - the silver is back in your vault.");
                    break;
                case WithdrawOutcome.Unrecorded:
                    op.Release();
                    KmhRouter.Notify(client, "negative", "The server couldn't record that withdrawal - nothing was taken.");
                    break;
                default:
                    op.Release();
                    ServerLog.Verbose($"Treasury withdraw_silver rejected (insufficient) for {username}");
                    KmhRouter.Notify(client, "negative", "Withdraw failed - not enough silver, or you've hit your guild withdraw limit.");
                    break;
            }
            // Corrects the client's optimistic UI.
            SendSnapshotTo(client);
        }

        internal enum WithdrawOutcome { Delivered, Insufficient, Unrecorded, Compensated }

        // The whole withdrawal without the connection, so a crash between any two of its steps can be driven. The row
        // comes first and the debit carries its take marker, so no step is ever unnamed; the fee is taken on the row.
        internal static WithdrawOutcome WithdrawSilverToColony(string username, int amount, out Delivery.OutboundDelivery delivery)
        {
            delivery = null;
            Transactions.KmhTransaction t = Transactions.KmhTransactionRepository.OpenWithdrawal(username, $"withdraw {amount}s", amount, null);
            if (t == null) return WithdrawOutcome.Unrecorded;
            Delivery.DeliveryStore.AtWithdrawPhaseForTest?.Invoke("row-open");
            if (!TreasuryStore.WithdrawSilverForTxn(username, amount, t.TakeMarker, note: ""))
            {
                Transactions.KmhTransactionRepository.Abort(t);
                return WithdrawOutcome.Insufficient;
            }
            Delivery.DeliveryStore.AtWithdrawPhaseForTest?.Invoke("debited");

            // Withdraw fee: debited gross from the vault, net delivered, the difference credited to the house pool.
            int fee = Features.Economy.EconomyAccess.WithdrawFeeOn(amount);
            int net = amount - fee;
            delivery = Delivery.DeliveryStore.OweForWithdrawal(t, net, null, null, fee);
            if (delivery == null) return WithdrawOutcome.Compensated;
            ServerLog.Info($"Treasury: {username} withdrew {amount}s (fee {fee}s -> house pool, delivered {net}s)");
            return WithdrawOutcome.Delivered;
        }

        private static void OnDepositItem(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            Dto.TreasuryItemRequest req = env?.DataAs<Dto.TreasuryItemRequest>();
            if (req == null) return;
            bool preApproved = TryConsumePreflight(client, username, "item", 0, req.ItemDefName ?? "", req.Qty, env?.GetString("deposit_token") ?? "");
            int  qtyCap  = Features.Economy.EconomyConfig.Current.Treasury.MaxItemDepositQtyPerTx;
            bool durable = Features.Economy.EconomyConfig.Current.Treasury.RequireDurableLocalSave && !string.IsNullOrEmpty(req.TxnId);

            // Every count here is the client's claim, so it is checked before anything is parked or held, and a
            // preflight token (which approved a qty, not these stacks) waives none of it.
            string claimWhy = ItemClaimRefusal(req, qtyCap, out int total);
            if (claimWhy != null)
            {
                ServerLog.Warn($"Treasury: {username} item deposit REJECTED - {claimWhy} (possible cheat)");
                KmhRouter.Notify(client, "negative", claimWhy);
                SendSnapshotTo(client);
                return;
            }
            // An approval covered the item and count asked for; stacks worth more than that, or none asked for, are judged here.
            bool asPayloads = req.Payloads != null && req.Payloads.Count > 0;
            long value = asPayloads ? Features.Moderation.KmhEconomyGuard.ValueOfPayloads(req.Payloads)
                                    : Features.Moderation.KmhEconomyGuard.ValueOfItem(req.ItemDefName, req.Qty);
            bool pastApproval = preApproved && value > Features.Moderation.KmhEconomyGuard.ValueOfItem(req.ItemDefName, req.Qty);
            if ((!preApproved || pastApproval)
                && !Features.Moderation.KmhEconomyGuard.AllowsDeposit(username, value, ReservedValue(username), $"x{total} {req.ItemDefName}",
                       pastApproval ? Features.Moderation.KmhEconomyGuard.PastApproval : Features.Moderation.KmhEconomyGuard.NoApproval, out string limitWhy))
            {
                ServerLog.Warn($"Treasury: {username} item deposit REJECTED - over the daily deposit limit {(pastApproval ? "past its approval" : "without an approval")} (possible cheat)");
                KmhRouter.Notify(client, "negative", limitWhy);
                SendSnapshotTo(client);
                return;
            }
            if (durable && TooManyUnconfirmed(username, out string backlog))
            { KmhRouter.Notify(client, "negative", backlog); SendSnapshotTo(client); return; }
            if (!preApproved && !Gate(client, username, isWithdraw: false, isGuild: false, isItem: true, env)) return;

            if (req.Payloads != null && req.Payloads.Count > 0)
            {
                if (durable)
                {
                    string defLabel = req.Payloads.Count > 0 ? (req.Payloads[0]?.DefName ?? "") : "";
                    if (TreasuryStore.BeginPendingDeposit(username, new Dto.PendingDeposit
                            { TxnId = req.TxnId, Kind = Dto.PendingDeposit.KindPayload, Payloads = req.Payloads, Qty = total, ItemDefName = defLabel }))
                    {
                        ServerLog.Info($"Treasury: {username} payload deposit x{total} PENDING (txn {req.TxnId}) - awaiting durable-save confirm");
                        NoteDeposit(username, value);
                    }
                    SendSnapshotTo(client);
                    return;
                }
                int stored = 0;
                foreach (Items.KmhThingPayload p in req.Payloads)
                    if (TreasuryStore.DepositPayload(username, p, note: "")) stored += p.StackCount;
                if (stored > 0)
                {
                    ServerLog.Info($"Treasury: {username} deposited x{stored} item(s) with full state.");
                    NoteDeposit(username, value);
                }
                SendSnapshotTo(client);
                return;
            }

            string itemDefName = req.ItemDefName ?? "";
            int    qty         = req.Qty;
            if (durable)
            {
                if (TreasuryStore.BeginPendingDeposit(username, new Dto.PendingDeposit
                        { TxnId = req.TxnId, Kind = Dto.PendingDeposit.KindItem, ItemDefName = itemDefName, Qty = qty }))
                {
                    ServerLog.Info($"Treasury: {username} deposit x{qty} {itemDefName} PENDING (txn {req.TxnId}) - awaiting durable-save confirm");
                    NoteDeposit(username, value);
                }
                SendSnapshotTo(client);
                return;
            }
            if (string.IsNullOrEmpty(req.TxnId))
                ServerLog.Warn($"Treasury: {username} deposited x{qty} {itemDefName} immediately (old client, no durable txn) - rollback dupe risk.");
            if (TreasuryStore.DepositItem(username, itemDefName, qty, note: ""))
            {
                ServerLog.Info($"Treasury: {username} deposited x{qty} {itemDefName}");
                NoteDeposit(username, value);
                SendSnapshotTo(client);
            }
        }

        internal const int MaxPayloadsPerDeposit = 500;
        // One item's saved state; a modded item with many comps is a few tens of KB.
        internal const int MaxScribeChars = 256 * 1024;

        // Null when the claim is acceptable. Stacks below one are refused rather than clamped, and the sum is wide,
        // so neither a negative stack nor an overflow can shrink it under the cap.
        internal static string ItemClaimRefusal(Dto.TreasuryItemRequest req, int qtyCap, out int total)
        {
            total = 0;
            if (req.Payloads != null && req.Payloads.Count > 0)
            {
                if (req.Payloads.Count > MaxPayloadsPerDeposit) return $"That deposit carries too many stacks (limit {MaxPayloadsPerDeposit}).";
                long sum = 0;
                foreach (Items.KmhThingPayload p in req.Payloads)
                {
                    if (p == null || p.StackCount < 1 || !Util.KmhSafe.IsDefName(p.DefName)
                        || (!string.IsNullOrEmpty(p.StuffDefName) && !Util.KmhSafe.IsDefName(p.StuffDefName)))
                        return "That deposit carries an invalid stack.";
                    if (Items.KmhItemSafety.IsSilverDef(p.DefName)) return Items.KmhItemSafety.SilverAsItemRefusal;
                    if (p.DefName.Length > Util.KmhSafe.MaxDefLength || (p.ScribeXml ?? "").Length > MaxScribeChars)
                        return "That deposit carries a stack too large to store.";
                    if (!Items.KmhItemSafety.BlobMatches(p, out string blobWhy))
                        return $"That deposit's saved item does not match what it names - {blobWhy}.";
                    sum += p.StackCount;
                }
                if (sum > int.MaxValue || (qtyCap > 0 && sum > qtyCap))
                    return $"That item deposit is over the server's per-deposit limit ({qtyCap}).";
                total = (int)sum;
                return null;
            }
            if (string.IsNullOrEmpty(req.ItemDefName) || req.Qty <= 0) return "That deposit names no item.";
            if (!Util.KmhSafe.IsItemKey(req.ItemDefName)) return "That deposit names no item this server can store.";
            if (Items.KmhItemSafety.IsSilverDef(req.ItemDefName)) return Items.KmhItemSafety.SilverAsItemRefusal;
            if (qtyCap > 0 && req.Qty > qtyCap) return $"That item deposit is over the server's per-deposit limit ({qtyCap}).";
            // A complex item sent as a bare count would be minted with default state, so it is refused, never held.
            if (!Items.KmhItemSafety.IsCompactDepositSafe(req.ItemDefName.Split('|')[0], out string unsafeReason))
                return $"That item has to be deposited with its full state ({unsafeReason}).";
            total = req.Qty;
            return null;
        }

        private static void OnWithdrawItem(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            Dto.TreasuryItemRequest req = env?.DataAs<Dto.TreasuryItemRequest>();
            if (req == null) return;
            if (!Gate(client, username, isWithdraw: true, isGuild: false, isItem: true, env)) return;
            // Extension veto, asked before the debit. A fingerprint names the item taken; item_def_name is only a label.
            string vetoDef = string.IsNullOrEmpty(req.Fingerprint) ? req.ItemDefName
                           : TreasuryStore.PayloadDefOf(username, req.Fingerprint) ?? req.ItemDefName;
            KMH.Sdk.Server.Hooks.KmhHookVerdict wv = Extensibility.KmhHooks.Instance.CheckTreasuryWithdraw(
                new KMH.Sdk.Server.Hooks.KmhTreasuryWithdrawContext(username, isItem: true, vetoDef, req.Qty, silverAmount: 0));
            if (wv.Denied) { KmhRouter.Notify(client, "negative", wv.Reason); return; }

            var op = new Security.KmhOpClaim("treasury.withdraw_item", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); return; }

            // A fungible stack grants one representative blob whatever its count, so the cap can match the deposit cap.
            if (!string.IsNullOrEmpty(req.Fingerprint))
            {
                int cap = Features.Economy.EconomyConfig.Current.Treasury.MaxItemDepositQtyPerTx;
                int qty = cap > 0 ? System.Math.Min(req.Qty, cap) : req.Qty;
                if (qty <= 0) return;
                // The instances are only known once taken, so the id comes first and the vault keeps a pending take
                // until the row naming them is written - the same order a full-state listing uses.
                Transactions.KmhTransaction ptx = Transactions.KmhTransactionRepository.PrepareTake(
                    username, Transactions.KmhTxType.Withdrawal, "withdraw full-state item(s)", 0);
                System.Collections.Generic.List<Items.KmhThingPayload> granted = ptx == null ? null
                    : TreasuryStore.WithdrawPayloadsForTxn(username, req.Fingerprint, qty, ptx.TakeMarker, note: "",
                                                          refundMarker: ptx.RefundMarker);
                if (granted != null && granted.Count > 0)
                {
                    int taken = 0; foreach (var g in granted) taken += g.StackCount;
                    if (!Transactions.KmhTransactionRepository.CommitTake(ptx, granted))
                    {
                        TreasuryStore.ReturnPendingTake(username, ptx.TakeMarker, ptx.RefundMarker, granted,
                                                        "withdrawal could not be recorded");
                        KmhRouter.Notify(client, "negative", "The server couldn't record that withdrawal - your items stayed in the vault.");
                        SendSnapshotTo(client);
                        return;
                    }
                    if (!TreasuryStore.ClearPendingTake(username, ptx.TakeMarker))
                    {
                        // The row is written, so compensation returns the instances once, on its own refund marker.
                        Transactions.KmhTransactionRepository.Compensate(ptx);
                        SendSnapshotTo(client);
                        return;
                    }
                    ServerLog.Info($"Treasury: {username} withdrew x{taken} full-state item(s).");
                    Features.Economy.EconomyAccess.RecordAction(username, true);
                    Delivery.OutboundDelivery d = Delivery.DeliveryStore.OweForWithdrawal(ptx, 0, null, granted);
                    if (d != null) Delivery.DeliveryHandler.Send(client, d);
                    SendSnapshotTo(client);
                }
                else
                {
                    op.Release();
                    // Almost always a row the player drained a moment ago, so say that rather than implying a mismatch.
                    KmhRouter.Notify(client, "negative", "Nothing left to withdraw from that row - your vault view has been refreshed.");
                    SendSnapshotTo(client);
                }
                return;
            }

            string itemDefName = req.ItemDefName ?? "";
            int    legacyQty   = req.Qty;
            if (string.IsNullOrEmpty(itemDefName) || legacyQty <= 0) { op.Release(); return; }
            var legs = new System.Collections.Generic.Dictionary<string, int> { { itemDefName, legacyQty } };
            Transactions.KmhTransaction itx = Transactions.KmhTransactionRepository.OpenWithdrawal(
                username, $"withdraw x{legacyQty} {itemDefName}", 0, legs);
            if (itx == null)
            {
                op.Release();
                KmhRouter.Notify(client, "negative", "The server couldn't record that withdrawal - nothing was taken.");
                SendSnapshotTo(client);
                return;
            }
            if (TreasuryStore.WithdrawItemForTxn(username, itemDefName, legacyQty, itx.TakeMarker, note: ""))
            {
                ServerLog.Info($"Treasury: {username} withdrew x{legacyQty} {itemDefName}");
                Features.Economy.EconomyAccess.RecordAction(username, true);
                Delivery.OutboundDelivery d = Delivery.DeliveryStore.OweForWithdrawal(itx, 0, legs, null);
                if (d != null) Delivery.DeliveryHandler.Send(client, d);
                SendSnapshotTo(client);
            }
            else
            {
                Transactions.KmhTransactionRepository.Abort(itx);
                op.Release();
                ServerLog.Verbose($"Treasury withdraw_item rejected (insufficient) for {username}");
                KmhRouter.Notify(client, "negative", "Item withdraw failed - not enough in the vault, or no permission.");
                SendSnapshotTo(client);
            }
        }

        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            TreasurySnapshot snapshot = TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
