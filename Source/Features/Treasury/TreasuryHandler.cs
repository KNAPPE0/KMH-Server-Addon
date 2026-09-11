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
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryRequest,          OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositSilver,    OnDepositSilver);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryWithdrawSilver,   OnWithdrawSilver);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositItem,      OnDepositItem);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryWithdrawItem,     OnWithdrawItem);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositConfirm,   OnDepositConfirm);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositReconcile, OnDepositReconcile);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositPreflight, OnDepositPreflight);
        }

        // Owner is the exact session, not the username: a token waives the cooldown and the per-deposit cap, so a reconnect must not spend it.
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

            if (CheckDepositAllowed(username, isItem, amount, itemDef, qty, isPayload, env, out string reason, out bool needsPayload))
            {
                string token = System.Guid.NewGuid().ToString("N");
                _preflight[token] = new PreApproval { User = username, Kind = kind, Amount = amount, ItemDefName = itemDef, Qty = qty,
                    ExpiryTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(PreflightTtlSeconds).Ticks, Owner = client };
                ReapPreflight();
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryDepositApproval, new { req_id = reqId, ok = true, token, ttl = PreflightTtlSeconds });
            }
            // needs_payload marks the compact-safety rejection so the client retries via the payload path, not gives up.
            else KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryDepositApproval, new { req_id = reqId, ok = false, reason = reason ?? "Deposit not allowed right now.", needs_payload = needsPayload });
        }

        // Read-only, so a preflight can answer without moving anything.
        private static bool CheckDepositAllowed(string username, bool isItem, long silver, string itemDef, int qty, bool isPayload, KmhEnvelope env, out string reason, out bool needsPayload)
        {
            reason = null;
            needsPayload = false;
            var cfg = Features.Economy.EconomyConfig.Current;

            // Refused before the client removes anything, or parking the goods in recovery duplicates what it still holds.
            if (TooManyUnconfirmed(username, out string backlog)) { reason = backlog; return false; }

            if (!isItem)
            {
                long cap = cfg.MaxSilverDepositPerTx;
                if (cap > 0 && silver > cap) { reason = $"That deposit is over the per-deposit limit ({Util.SilverFmt.Format(cap)})."; return false; }
            }
            else
            {
                int qtyCap = cfg.MaxItemDepositQtyPerTx;
                if (qtyCap > 0 && qty > qtyCap) { reason = $"That item deposit is over the per-deposit limit ({qtyCap})."; return false; }
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
            return true;
        }

        // Test seam: an approval waives the cooldown and the per-deposit cap, so prove who may spend one.
        internal static string IssuePreflightForTest(ServerClient client, string user, string kind, long amount, string itemDef, int qty)
        {
            string token = System.Guid.NewGuid().ToString("N");
            _preflight[token] = new PreApproval { User = user, Kind = kind, Amount = amount, ItemDefName = itemDef, Qty = qty,
                ExpiryTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(PreflightTtlSeconds).Ticks, Owner = client };
            return token;
        }

        internal static bool ConsumePreflightForTest(ServerClient client, string user, string kind, long amount, string itemDef, int qty, string token)
            => TryConsumePreflight(client, user, kind, amount, itemDef, qty, token);

        internal static int OutstandingPreflightForTest => _preflight.Count;
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
            long seen = TreasuryStore.NoteSaveGeneration(username, gen);
            if (!TreasuryStore.SaveGenerationWentBackwards(seen, gen)) return false;
            // A generation counts saves of one file, so a different colony reports a low number without any rollback.
            ServerLog.Error($"Treasury: {username} reported save generation {gen} after trusted generation {seen} on {source} - " +
                            $"possible rollback or a different save. Backwards state was NOT accepted (still trusting {seen}). " +
                            $"Review with 'kmh audit-player {username}'.");
            return true;
        }

        private static void OnDepositConfirm(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            System.Collections.Generic.List<string> ids = env?.GetStringList("txn_ids");
            NoteSaveGeneration(username, env, "confirm");
            int committed = TreasuryStore.ConfirmDeposits(username, ids);
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
            (int committed, int reverted) = TreasuryStore.ReconcileDeposits(username, ids, unsaved, ReconcileGraceSeconds);

            // Gated on an observed rollback, or a client merely omitting an id would cost a player their vault.
            int undone = 0, flagged = 0;
            if (rolledBack)
                (undone, flagged) = TreasuryStore.ReverseRolledBackCommits(username, ids, unsaved, ReconcileGraceSeconds);

            if (committed > 0 || reverted > 0 || undone > 0 || flagged > 0)
            {
                ServerLog.Info($"Treasury: reconciled {username} deposits - {committed} committed, {reverted} reverted (local rollback)" +
                               (undone + flagged > 0 ? $", {undone} committed deposit(s) undone, {flagged} flagged" : "") + ".");
                SendSnapshotTo(client);
            }
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
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
            long cap = Features.Economy.EconomyConfig.Current.MaxSilverDepositPerTx;
            if (!preApproved && cap > 0 && amount > cap)
            {
                // No grant-back: a modified client may never have removed the silver, so returning it could mint.
                ServerLog.Warn($"Treasury: {username} deposit_silver {amount}s REJECTED - over cap {cap}s (possible cheat)");
                KmhRouter.Notify(client, "negative", $"That deposit is over the server's per-deposit limit ({Util.SilverFmt.Format(cap)}).");
                SendSnapshotTo(client);
                return;
            }
            // The client already removed the silver, so a policy rejection parks it in Recovery rather than dropping it.
            if (!preApproved && !Gate(client, username, isWithdraw: false, isGuild: false, isItem: false, env))
            { HoldRejectedDeposit(client, username, amount, null, "deposit blocked (cooldown or access mode)"); return; }
            int fee = Features.Economy.EconomyAccess.DepositFeeOn(amount);
            int net = amount - fee;
            if (!preApproved && Features.Economy.EconomyAccess.WouldExceedCap(false, TreasuryStore.GetPersonalSilver(username), net, out string capReason))
            { HoldRejectedDeposit(client, username, amount, null, capReason); KmhRouter.Notify(client, "negative", capReason); SendSnapshotTo(client); return; }

            // Held pending until the save confirms, and the fee rides along so it only lands if the deposit commits.
            string txnId = env?.GetString("txn_id") ?? "";
            if (Features.Economy.EconomyConfig.Current.RequireDurableLocalSaveForDeposits && !string.IsNullOrEmpty(txnId))
            {
                if (TreasuryStore.BeginPendingDeposit(username, new Dto.PendingDeposit
                        { TxnId = txnId, Kind = Dto.PendingDeposit.KindSilver, Silver = net, Fee = fee }))
                {
                    ServerLog.Info($"Treasury: {username} deposit {net}s PENDING (txn {txnId}, fee {fee}s -> house pool on commit) - awaiting durable-save confirm");
                    Features.Economy.EconomyAccess.RecordAction(username, false);
                    Features.Economy.EconomyAccess.Audit(username, false, false, amount);
                }
                SendSnapshotTo(client);
                return;
            }
            if (string.IsNullOrEmpty(txnId))
                ServerLog.Warn($"Treasury: {username} deposited {amount}s immediately (old client, no durable txn) - rollback dupe risk.");
            if (TreasuryStore.DepositSilver(username, net, note: fee > 0 ? $"deposit (fee {fee}s)" : ""))
            {
                if (fee > 0) Marketplace.MarketplaceStore.CreditFeeToHousePool(username, fee, $"treasury deposit fee ({username})");
                ServerLog.Info($"Treasury: {username} deposited {net}s (fee {fee}s -> house pool)");
                Features.Economy.EconomyAccess.RecordAction(username, false);
                Features.Economy.EconomyAccess.Audit(username, false, false, amount);
                SendSnapshotTo(client);
            }
        }

        // Pending-deposit safety is separate from this gate and applies regardless of what it answers.
        private static bool Gate(ServerClient client, string username, bool isWithdraw, bool isGuild, bool isItem, KmhEnvelope env)
        {
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
            return true;
        }

        internal static bool WouldRefuseForBacklogForTest(string username, out string reason) => TooManyUnconfirmed(username, out reason);

        // Mint-safe: the value stays server-side, so a fabricated deposit only fills the queue and gains nothing.
        private static bool TooManyUnconfirmed(string username, out string reason)
        {
            reason = null;
            int cap = Features.Economy.EconomyConfig.Current.MaxUnconfirmedDepositsPerPlayer;
            if (cap <= 0) return false;
            int open = TreasuryStore.UnconfirmedDepositCount(username);
            if (open < cap) return false;
            reason = $"Deposit paused: {open} earlier deposit(s) are still waiting for your game to save. "
                   + "Save your game to finalize them, then deposit again.";
            return true;
        }

        private static void HoldRejectedDeposit(ServerClient client, string username, int silver, Dto.TreasuryItemRequest req, string reason)
        {
            if (string.IsNullOrEmpty(username)) return;
            if (silver > 0) Features.Recovery.RecoveryStore.HoldSilver(username, silver, "treasury deposit rejected", reason);
            else if (req?.Payloads != null && req.Payloads.Count > 0)
            {
                foreach (Items.KmhThingPayload p in req.Payloads)
                    if (p != null) Features.Recovery.RecoveryStore.HoldItem(username, p, "treasury deposit rejected", reason);
            }
            else if (!string.IsNullOrEmpty(req?.ItemDefName) && req.Qty > 0)
                Features.Recovery.RecoveryStore.HoldItem(username,
                    new Items.KmhThingPayload { DefName = req.ItemDefName.Split('|')[0], StackCount = req.Qty },
                    "treasury deposit rejected", reason);
            KmhRouter.Notify(client, "neutral", "Deposit couldn't complete right now - the value was set aside for recovery (an admin can return it, or try again shortly).");
        }

        private static void OnWithdrawSilver(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            int    amount   = env?.GetInt("amount", 0) ?? 0;
            if (amount <= 0) return;
            if (!Gate(client, username, isWithdraw: true, isGuild: false, isItem: false, env)) return;
            // Extension veto hooks (banking rules); player-initiated only, before the debit -> side-effect-free denial.
            KMH.Sdk.Server.Hooks.KmhHookVerdict wv = Extensibility.KmhHooks.Instance.CheckTreasuryWithdraw(
                new KMH.Sdk.Server.Hooks.KmhTreasuryWithdrawContext(username, isItem: false, itemDefName: "", quantity: 0, silverAmount: amount));
            if (wv.Denied) { KmhRouter.Notify(client, "negative", wv.Reason); return; }

            var op = new Security.KmhOpClaim("treasury.withdraw_silver", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); return; }

            if (TreasuryStore.WithdrawSilver(username, amount, note: ""))
            {
                // Withdraw fee: debited gross from the vault, net delivered, the difference credited to the house pool.
                int fee = Features.Economy.EconomyAccess.WithdrawFeeOn(amount);
                int net = amount - fee;
                if (fee > 0) Marketplace.MarketplaceStore.CreditFeeToHousePool(username, fee, $"treasury withdraw fee ({username})");
                ServerLog.Info($"Treasury: {username} withdrew {amount}s (fee {fee}s -> house pool, delivered {net}s)");
                Features.Economy.EconomyAccess.RecordAction(username, true);
                Features.Economy.EconomyAccess.Audit(username, true, false, amount);
                // Debited and owed durably before sending: the transport cannot be asked afterwards whether it arrived.
                Delivery.OutboundDelivery d = Delivery.DeliveryStore.OweForOperation(
                    username, Transactions.KmhTxType.Withdrawal, $"withdraw {net}s", net, null, null);
                if (d != null) Delivery.DeliveryHandler.Send(client, d);
                else Items.KmhPayloadEscrow.DeliverSilver(username, net, "withdrawal could not be recorded for delivery", "withdrawal could not be returned");
                SendSnapshotTo(client);
            }
            else
            {
                op.Release();
                ServerLog.Verbose($"Treasury withdraw_silver rejected (insufficient) for {username}");
                KmhRouter.Notify(client, "negative", "Withdraw failed - not enough silver, or you've hit your guild withdraw limit.");
                // Push a corrective snapshot so the patch's optimistic UI gets truth.
                SendSnapshotTo(client);
            }
        }

        private static void OnDepositItem(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            Dto.TreasuryItemRequest req = env?.DataAs<Dto.TreasuryItemRequest>();
            if (req == null) return;
            bool preApproved = TryConsumePreflight(client, username, "item", 0, req.ItemDefName ?? "", req.Qty, env?.GetString("deposit_token") ?? "");
            if (!preApproved && !Gate(client, username, isWithdraw: false, isGuild: false, isItem: true, env))
            { HoldRejectedDeposit(client, username, 0, req, "item deposit blocked (cooldown or access mode)"); return; }
            int  qtyCap  = Features.Economy.EconomyConfig.Current.MaxItemDepositQtyPerTx;
            bool durable = Features.Economy.EconomyConfig.Current.RequireDurableLocalSaveForDeposits && !string.IsNullOrEmpty(req.TxnId);

            if (req.Payloads != null && req.Payloads.Count > 0)
            {
                int total = 0; foreach (Items.KmhThingPayload p in req.Payloads) total += p?.StackCount ?? 0;
                if (!preApproved && qtyCap > 0 && total > qtyCap)
                {
                    ServerLog.Warn($"Treasury: {username} payload deposit x{total} REJECTED - over cap {qtyCap} (possible cheat)");
                    KmhRouter.Notify(client, "negative", $"That item deposit is over the server's per-deposit limit ({qtyCap}).");
                    SendSnapshotTo(client);
                    return;
                }
                if (durable)
                {
                    string defLabel = req.Payloads.Count > 0 ? (req.Payloads[0]?.DefName ?? "") : "";
                    if (TreasuryStore.BeginPendingDeposit(username, new Dto.PendingDeposit
                            { TxnId = req.TxnId, Kind = Dto.PendingDeposit.KindPayload, Payloads = req.Payloads, Qty = total, ItemDefName = defLabel }))
                        ServerLog.Info($"Treasury: {username} payload deposit x{total} PENDING (txn {req.TxnId}) - awaiting durable-save confirm");
                    SendSnapshotTo(client);
                    return;
                }
                int stored = 0;
                foreach (Items.KmhThingPayload p in req.Payloads)
                    if (TreasuryStore.DepositPayload(username, p, note: "")) stored += p.StackCount;
                if (stored > 0) ServerLog.Info($"Treasury: {username} deposited x{stored} item(s) with full state.");
                SendSnapshotTo(client);
                return;
            }

            string itemDefName = req.ItemDefName ?? "";
            int    qty         = req.Qty;
            if (string.IsNullOrEmpty(itemDefName) || qty <= 0) return;
            // Item safety is always enforced here, and a preflight token never waives it.
            if (!Items.KmhItemSafety.IsCompactDepositSafe(itemDefName.Split('|')[0], out string unsafeReason))
            {
                ServerLog.Warn($"Treasury: {username} compact deposit {itemDefName} x{qty} REJECTED - {unsafeReason}");
                HoldRejectedDeposit(client, username, 0, req, $"unsafe compact item ({unsafeReason})");
                return;
            }
            if (!preApproved && qtyCap > 0 && qty > qtyCap)
            {
                ServerLog.Warn($"Treasury: {username} deposit_item {itemDefName} x{qty} REJECTED - over cap {qtyCap} (possible cheat)");
                KmhRouter.Notify(client, "negative", $"That item deposit is over the server's per-deposit limit ({qtyCap}).");
                SendSnapshotTo(client);
                return;
            }
            if (durable)
            {
                if (TreasuryStore.BeginPendingDeposit(username, new Dto.PendingDeposit
                        { TxnId = req.TxnId, Kind = Dto.PendingDeposit.KindItem, ItemDefName = itemDefName, Qty = qty }))
                    ServerLog.Info($"Treasury: {username} deposit x{qty} {itemDefName} PENDING (txn {req.TxnId}) - awaiting durable-save confirm");
                SendSnapshotTo(client);
                return;
            }
            if (string.IsNullOrEmpty(req.TxnId))
                ServerLog.Warn($"Treasury: {username} deposited x{qty} {itemDefName} immediately (old client, no durable txn) - rollback dupe risk.");
            if (TreasuryStore.DepositItem(username, itemDefName, qty, note: ""))
            {
                ServerLog.Info($"Treasury: {username} deposited x{qty} {itemDefName}");
                SendSnapshotTo(client);
            }
        }

        private static void OnWithdrawItem(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            Dto.TreasuryItemRequest req = env?.DataAs<Dto.TreasuryItemRequest>();
            if (req == null) return;
            if (!Gate(client, username, isWithdraw: true, isGuild: false, isItem: true, env)) return;
            // Extension veto hooks (banking rules); player-initiated only, before the debit -> side-effect-free denial.
            KMH.Sdk.Server.Hooks.KmhHookVerdict wv = Extensibility.KmhHooks.Instance.CheckTreasuryWithdraw(
                new KMH.Sdk.Server.Hooks.KmhTreasuryWithdrawContext(username, isItem: true, req.ItemDefName, req.Qty, silverAmount: 0));
            if (wv.Denied) { KmhRouter.Notify(client, "negative", wv.Reason); return; }

            var op = new Security.KmhOpClaim("treasury.withdraw_item", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); return; }

            // A fungible stack grants one representative blob whatever its count, so the cap can match the deposit cap.
            if (!string.IsNullOrEmpty(req.Fingerprint))
            {
                int cap = Features.Economy.EconomyConfig.Current.MaxItemDepositQtyPerTx;
                int qty = cap > 0 ? System.Math.Min(req.Qty, cap) : req.Qty;
                if (qty <= 0) return;
                System.Collections.Generic.List<Items.KmhThingPayload> granted =
                    TreasuryStore.WithdrawPayloads(username, req.Fingerprint, qty, note: "");
                if (granted != null && granted.Count > 0)
                {
                    int taken = 0; foreach (var g in granted) taken += g.StackCount;
                    ServerLog.Info($"Treasury: {username} withdrew x{taken} full-state item(s).");
                    Delivery.OutboundDelivery d = Delivery.DeliveryStore.OweForOperation(
                        username, Transactions.KmhTxType.Withdrawal, $"withdraw x{taken} full-state item(s)", 0, null, granted);
                    if (d != null) Delivery.DeliveryHandler.Send(client, d);
                    else foreach (Items.KmhThingPayload g in granted)
                        Items.KmhPayloadEscrow.Deliver(username, g, "withdrawal could not be recorded for delivery", "withdrawal could not be returned");
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
            if (TreasuryStore.WithdrawItem(username, itemDefName, legacyQty, note: ""))
            {
                ServerLog.Info($"Treasury: {username} withdrew x{legacyQty} {itemDefName}");
                Delivery.OutboundDelivery d = Delivery.DeliveryStore.OweForOperation(
                    username, Transactions.KmhTxType.Withdrawal, $"withdraw x{legacyQty} {itemDefName}", 0,
                    new System.Collections.Generic.Dictionary<string, int> { { itemDefName, legacyQty } }, null);
                if (d != null) Delivery.DeliveryHandler.Send(client, d);
                else Items.KmhPayloadEscrow.DeliverCompact(username, itemDefName, legacyQty, "withdrawal could not be recorded for delivery", "withdrawal could not be returned");
                SendSnapshotTo(client);
            }
            else
            {
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
