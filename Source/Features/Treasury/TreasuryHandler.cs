using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Treasury.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Treasury
{
    // Server-side handler for kmh.treasury.* (counterpart to the client's TreasuryHandler). Flow: request -> send the
    // caller's vault snapshot; deposit/withdraw silver|item -> adjust the ledger (if sufficient), log the tx, broadcast.
    // Deposits are per-tx capped (EconomyConfig) as an anti-mint guard on the client-trusted amount.
    internal static class TreasuryHandler
    {
        // Grace before a full reconcile will revert an unlisted pending deposit (guards a just-made-but-not-yet-saved
        // deposit from being wrongly reverted on the reconnect that races it).
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

        // Preflight: approve a deposit (read-only cap/access/cooldown/vault/safety) BEFORE the client removes goods, then
        // mint a short-lived token it echoes on the real deposit (which skips re-checking). A rejection removes nothing.
        private struct PreApproval { public string User, Kind, ItemDefName; public long Amount; public int Qty; public long ExpiryTicks; }
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
                    ExpiryTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(PreflightTtlSeconds).Ticks };
                ReapPreflight();
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryDepositApproval, new { req_id = reqId, ok = true, token, ttl = PreflightTtlSeconds });
            }
            // needs_payload marks the compact-safety rejection so the client retries via the payload path, not gives up.
            else KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryDepositApproval, new { req_id = reqId, ok = false, reason = reason ?? "Deposit not allowed right now.", needs_payload = needsPayload });
        }

        // Read-only version of the deposit rejectable rules (cap, access, cooldown, vault cap, compact-item safety).
        private static bool CheckDepositAllowed(string username, bool isItem, long silver, string itemDef, int qty, bool isPayload, KmhEnvelope env, out string reason, out bool needsPayload)
        {
            reason = null;
            needsPayload = false;
            var cfg = Features.Economy.EconomyConfig.Current;
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

        // Consume a preflight token that exactly matches this deposit (user + kind + amount/def/qty) and is unexpired.
        private static bool TryConsumePreflight(string username, string kind, long amount, string itemDef, int qty, string token)
        {
            if (string.IsNullOrEmpty(token) || !_preflight.TryRemove(token, out PreApproval a)) return false;
            if (a.ExpiryTicks < DateTime.UtcNow.Ticks) return false;
            return string.Equals(a.User, username, StringComparison.OrdinalIgnoreCase) && a.Kind == kind
                && a.Amount == amount && a.Qty == qty
                && string.Equals(a.ItemDefName ?? "", itemDef ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static void ReapPreflight()
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (var kv in _preflight)
                if (kv.Value.ExpiryTicks < now) _preflight.TryRemove(kv.Key, out _);
        }

        // Client reports these deposit txns are now durably saved locally -> commit them (make spendable).
        private static void OnDepositConfirm(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            System.Collections.Generic.List<string> ids = env?.GetStringList("txn_ids");
            int committed = TreasuryStore.ConfirmDeposits(username, ids);
            if (committed > 0)
            {
                ServerLog.Info($"Treasury: {username} confirmed {committed} durable deposit(s) - now spendable.");
                SendSnapshotTo(client);
            }
        }

        // Full reconcile on (re)connect: the client's complete set of durably-saved deposit txns. Commit any pending
        // in the set; revert stale pendings the client no longer has (a local rollback).
        private static void OnDepositReconcile(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            System.Collections.Generic.List<string> ids = env?.GetStringList("committed") ?? new System.Collections.Generic.List<string>();
            (int committed, int reverted) = TreasuryStore.ReconcileDeposits(username, ids, ReconcileGraceSeconds);
            if (committed > 0 || reverted > 0)
            {
                ServerLog.Info($"Treasury: reconciled {username} deposits - {committed} committed, {reverted} reverted (local rollback).");
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
            // Preflight token (client got approval BEFORE removing goods) skips re-checking the rejectable rules; without
            // one, the removal->Recovery-hold safety net below still guarantees no loss.
            bool preApproved = TryConsumePreflight(username, "silver", amount, "", 0, env?.GetString("deposit_token") ?? "");
            long cap = Features.Economy.EconomyConfig.Current.MaxSilverDepositPerTx;
            if (!preApproved && cap > 0 && amount > cap)
            {
                // Over the per-deposit limit - reject + warn (a cheat signal). NO grant-back: a modified client may
                // not have removed the silver, so returning it could mint. The default cap is high enough that a
                // legitimate single deposit never trips this.
                ServerLog.Warn($"Treasury: {username} deposit_silver {amount}s REJECTED - over cap {cap}s (possible cheat)");
                KmhRouter.Notify(client, "negative", $"That deposit is over the server's per-deposit limit ({Util.SilverFmt.Format(cap)}).");
                SendSnapshotTo(client);
                return;
            }
            // Access gate + cooldown + fee + cap. The client already removed the silver, so a policy rejection parks
            // it in Recovery instead of dropping it (no honest-player loss).
            if (!preApproved && !Gate(client, username, isWithdraw: false, isGuild: false, isItem: false, env))
            { HoldRejectedDeposit(client, username, amount, null, "deposit blocked (cooldown or access mode)"); return; }
            int fee = Features.Economy.EconomyAccess.DepositFeeOn(amount);
            int net = amount - fee;
            if (!preApproved && Features.Economy.EconomyAccess.WouldExceedCap(false, TreasuryStore.GetPersonalSilver(username), net, out string capReason))
            { HoldRejectedDeposit(client, username, amount, null, capReason); KmhRouter.Notify(client, "negative", capReason); SendSnapshotTo(client); return; }

            // Durable path: new clients send a txn id -> hold PENDING until the save confirms (fixes the rollback dupe).
            // The fee rides on the pending record and credits the house pool only when it COMMITS, never if it reverts.
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
                if (fee > 0) Marketplace.MarketplaceStore.CreditHousePool(fee, $"treasury deposit fee ({username})");
                ServerLog.Info($"Treasury: {username} deposited {net}s (fee {fee}s -> house pool)");
                Features.Economy.EconomyAccess.RecordAction(username, false);
                Features.Economy.EconomyAccess.Audit(username, false, false, amount);
                SendSnapshotTo(client);
            }
        }

        // Centralized gate: access mode + cooldown. Notifies + refreshes the client's snapshot on denial. Pending-
        // deposit safety is separate and always applies.
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

        // A rejected deposit (client already removed the goods) is parked in Recovery instead of dropped so an honest
        // player never loses it. Mint-safe: value stays server-side, so a fake deposit just fills the queue for nothing.
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
            if (TreasuryStore.WithdrawSilver(username, amount, note: ""))
            {
                // Withdraw fee: debited gross from the vault, net delivered, the difference credited to the house pool.
                int fee = Features.Economy.EconomyAccess.WithdrawFeeOn(amount);
                int net = amount - fee;
                if (fee > 0) Marketplace.MarketplaceStore.CreditHousePool(fee, $"treasury withdraw fee ({username})");
                ServerLog.Info($"Treasury: {username} withdrew {amount}s (fee {fee}s -> house pool, delivered {net}s)");
                Features.Economy.EconomyAccess.RecordAction(username, true);
                Features.Economy.EconomyAccess.Audit(username, true, false, amount);
                // Grant first so the client materializes the silver into the colony only after the server has
                // actually debited it
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant, new { kind = "silver", amount = net });
                SendSnapshotTo(client);
            }
            else
            {
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
            // Preflight token skips re-checking; without one the removal->Recovery-hold net below still prevents loss.
            bool preApproved = TryConsumePreflight(username, "item", 0, req.ItemDefName ?? "", req.Qty, env?.GetString("deposit_token") ?? "");
            // Client already removed the items; a policy rejection parks them in Recovery instead of dropping them.
            if (!preApproved && !Gate(client, username, isWithdraw: false, isGuild: false, isItem: true, env))
            { HoldRejectedDeposit(client, username, 0, req, "item deposit blocked (cooldown or access mode)"); return; }
            int  qtyCap  = Features.Economy.EconomyConfig.Current.MaxItemDepositQtyPerTx;
            bool durable = Features.Economy.EconomyConfig.Current.RequireDurableLocalSaveForDeposits && !string.IsNullOrEmpty(req.TxnId);

            // Payload path: state-preserving complex items.
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

            // Legacy/simple path: def|stuff|quality key + count.
            string itemDefName = req.ItemDefName ?? "";
            int    qty         = req.Qty;
            if (string.IsNullOrEmpty(itemDefName) || qty <= 0) return;
            // Backstop: block a modified client smuggling a complex/unsafe item through the compact path; reject to
            // Recovery (honest deposits never lost). Item safety is ALWAYS enforced - a preflight token never waives it.
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

            // Payload path: withdraw by state fingerprint, materialize the exact captured items. A fungible stack
            // grants ONE representative blob regardless of count (frame-safe), so cap at the item deposit cap for
            // symmetric movement rather than the old fixed 10 that truncated large food/resource stacks.
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
                    KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant, new { kind = "item_payloads", payloads = granted });
                    SendSnapshotTo(client);
                }
                else
                {
                    KmhRouter.Notify(client, "negative", "Item withdraw failed - nothing matched in the vault.");
                    SendSnapshotTo(client);
                }
                return;
            }

            // Legacy/simple path.
            string itemDefName = req.ItemDefName ?? "";
            int    legacyQty   = req.Qty;
            if (string.IsNullOrEmpty(itemDefName) || legacyQty <= 0) return;
            if (TreasuryStore.WithdrawItem(username, itemDefName, legacyQty, note: ""))
            {
                ServerLog.Info($"Treasury: {username} withdrew x{legacyQty} {itemDefName}");
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant, new { kind = "item", def_name = itemDefName, amount = legacyQty });
                SendSnapshotTo(client);
            }
            else
            {
                ServerLog.Verbose($"Treasury withdraw_item rejected (insufficient) for {username}");
                KmhRouter.Notify(client, "negative", "Item withdraw failed - not enough in the vault, or no permission.");
                SendSnapshotTo(client);
            }
        }

        // Send the caller-scoped snapshot to a single client. Caller-scoped because the snapshot's CanDeposit /
        // CanWithdraw flags depend on who's asking
        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            TreasurySnapshot snapshot = TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
