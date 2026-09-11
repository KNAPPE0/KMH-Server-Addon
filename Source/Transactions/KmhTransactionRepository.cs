using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Transactions
{
    // Recorded here BEFORE value crosses ownership, so a crash leaves a durable record the startup pass can reconcile.
    internal static class KmhTransactionRepository
    {
        private sealed class State
        {
            public List<KmhTransaction> Transactions { get; set; } = new List<KmhTransaction>();
            public Dictionary<string, long> SpentKeys { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        }

        private static readonly object _lock = new object();
        private static readonly KmhTransactionIndex _index = new KmhTransactionIndex();

        // Finite on purpose: the durable audit trail is the append-only ledger, not this file.
        private const int MaxFinishedKept = 500;

        public static void LoadFromDisk()
        {
            lock (_lock)
            {
                State s = JsonFileStore.TryLoad(KmhDataPaths.TransactionLedgerFile, out State loaded) && loaded?.Transactions != null
                    ? loaded : new State();
                // Pruned before the index sees it; dropped rows' keys carry over separately so those operations stay un-repeatable.
                long now = DateTime.UtcNow.Ticks;
                var carried = new Dictionary<string, long>(s.SpentKeys ?? new Dictionary<string, long>(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (KmhTransaction dropping in KmhTxPrune.SelectForPrune(s.Transactions, MaxFinishedKept))
                    if (!string.IsNullOrEmpty(dropping.RequestKey)) carried[dropping.RequestKey] = now;
                int dropped = KmhTxPrune.PruneFinished(s.Transactions, MaxFinishedKept);
                if (dropped > 0)
                    Diagnostics.ServerLog.Info($"Transactions: pruned {dropped} settled transaction(s) beyond the newest {MaxFinishedKept}.");
                _index.Load(s.Transactions, carried);
            }
        }

        public static bool IsDuplicate(string requestKey) { lock (_lock) return _index.IsDuplicate(requestKey); }

        // False also when the row could not be written down - a caller must never treat an unrecoverable intent as recorded.
        public static bool Add(KmhTransaction tx)
        {
            lock (_lock)
            {
                if (!_index.Add(tx)) return false;
                if (Save()) return true;
                _index.Remove(tx.Id);
                return false;
            }
        }

        public static bool Update(KmhTransaction tx)
        {
            if (tx == null) return false;
            if (IsStale(tx)) return false;
            lock (_lock) return _index.Get(tx.Id) != null && Save();
        }

        // A reset destroyed what this row was authorised against, so advancing it moves value out of state that no longer holds it.
        internal static bool IsStale(KmhTransaction tx)
            => tx != null && !string.IsNullOrEmpty(tx.Player)
            && tx.TargetGeneration < Features.Economy.KmhEconomyReset.GenerationOf(tx.Player);

        public static KmhTransaction Get(string id)            { lock (_lock) return _index.Get(id); }
        public static List<KmhTransaction> ForPlayer(string p) { lock (_lock) return _index.ForPlayer(p); }
        public static List<KmhTransaction> Pending()           { lock (_lock) return _index.Pending(); }
        public static List<KmhTransaction> All()               { lock (_lock) return new List<KmhTransaction>(_index.All); }
        public static int Count                                { get { lock (_lock) return _index.Count; } }

        // Moves only the states that are safe to move and reports the rest, rather than guessing at escrowed value.
        public static void RecoverOnBoot()
        {
            List<KmhTransaction> pending = Pending();
            if (pending.Count == 0) return;
            int rejected = 0, refunded = 0, needsRefund = 0, needsReconcile = 0;
            foreach (KmhTransaction t in pending)
            {
                switch (KmhTransactionRecovery.DecideForStuck(t.State))
                {
                    case KmhTxRecoveryAction.Reject:    t.Advance(KmhTxState.Rejected); rejected++; break;
                    case KmhTxRecoveryAction.Refund:    if (Compensate(t)) refunded++; else needsRefund++; break;
                    case KmhTxRecoveryAction.Reconcile: needsReconcile++; break;
                }
            }
            lock (_lock) Save();
            ServerLog.Warn($"Transaction recovery: {pending.Count} non-terminal transaction(s) from a previous run - " +
                           $"{rejected} rejected (nothing reserved), {refunded} refunded, {needsRefund} awaiting refund, " +
                           $"{needsReconcile} awaiting reconcile.");

            // A Delivered row closes itself when the client confirms; re-issuing it would duplicate value, so only a refund needs a human.
            if (needsRefund > 0)
                ServerLog.Error($"Transaction recovery: {needsRefund} transaction(s) hold value that could not be "
                    + "refunded automatically - see 'kmh recover list'. Ids: " + string.Join(", ", StuckIds(pending)));
            else if (needsReconcile > 0)
                ServerLog.Warn($"Transaction recovery: {needsReconcile} delivery(ies) are waiting for the player to "
                    + "confirm receipt; they re-send on the next join. 'kmh recover list' shows them.");
        }

        // Deduplicated on the transaction id, so crashing anywhere in here resumes rather than pays twice; false means a human must settle it.
        internal static bool Compensate(KmhTransaction t)
        {
            if (t == null || string.IsNullOrEmpty(t.Player)) return false;
            // Refunding across a reset would resurrect value the reset deliberately destroyed.
            if (IsStale(t)) { Abort(t); return true; }
            if (!t.HasEscrow && !t.HasCounterEscrow) { t.Advance(KmhTxState.Refunded); return true; }

            if (t.State != KmhTxState.Compensating)
            {
                if (!t.Advance(KmhTxState.Compensating)) return false;
                lock (_lock) { if (!Save()) return false; }
            }

            // The ledger records intent first, so it can name silver a crash prevented from being taken - only the vault knows the debit happened.
            bool owedToPlayer = !t.HasEscrow
                || Features.Treasury.TreasuryStore.KnowsTxnMarker(t.Player, t.TakeMarker);
            if (t.HasEscrow && owedToPlayer
                && !Features.Treasury.TreasuryStore.DepositEscrowOnce(
                        t.Player, t.RefundMarker, t.EscrowSilver, t.EscrowItems, t.EscrowPayloads,
                        $"transaction {t.Id} refunded"))
                return false;

            if (t.HasCounterEscrow && !string.IsNullOrEmpty(t.CounterPlayer)
                && !Features.Treasury.TreasuryStore.DepositEscrowOnce(
                        t.CounterPlayer, t.CounterMarker, t.CounterSilver, t.CounterItems, t.CounterPayloads,
                        $"transaction {t.Id} returned"))
                return false;

            t.Advance(KmhTxState.Refunded);
            lock (_lock) return Save();
        }

        // Held at Delivered: the economy is settled, but the colony has not confirmed holding the value.
        internal static KmhTransaction OpenDelivery(string player, KmhTxType type, string summary, long silver,
                                                     IDictionary<string, int> items,
                                                     IEnumerable<Items.KmhThingPayload> payloads)
        {
            KmhTransaction t = KmhTransaction.Create(player, "personal_treasury", type, summary ?? "");
            t.Destination  = "colony";
            t.EscrowSilver = silver;
            if (items != null) foreach (KeyValuePair<string, int> kv in items) if (kv.Value > 0) t.EscrowItems[kv.Key] = kv.Value;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) t.EscrowPayloads.Add(p);
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            t.Advance(KmhTxState.Approved);
            return Add(t) ? t : null;
        }

        // Rides the primary leg, so compensation pays only once the vault confirms the take - a crash before the debit must not refund goods still sitting there.
        internal static KmhTransaction OpenTake(string player, KmhTxType type, string summary, long targetId,
                                                long silver, IDictionary<string, int> items,
                                                IEnumerable<Items.KmhThingPayload> payloads)
        {
            if (string.IsNullOrEmpty(player)) return null;
            KmhTransaction t = KmhTransaction.Create(player, "personal_treasury", type, summary ?? "");
            t.Destination  = "marketplace";
            t.TargetId     = targetId;
            t.EscrowSilver = silver < 0 ? 0 : silver;
            if (items != null) foreach (KeyValuePair<string, int> kv in items) if (kv.Value > 0) t.EscrowItems[kv.Key] = kv.Value;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) t.EscrowPayloads.Add(p);
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            return Add(t) ? t : null;
        }

        // The id without the row: a payload take happens before the instances are known, so the marker is stamped first and the row written after.
        internal static KmhTransaction PrepareTake(string player, KmhTxType type, string summary, long targetId)
        {
            if (string.IsNullOrEmpty(player)) return null;
            KmhTransaction t = KmhTransaction.Create(player, "personal_treasury", type, summary ?? "");
            t.Destination = "marketplace";
            t.TargetId    = targetId;
            t.Advance(KmhTxState.Validating);
            return t;
        }

        // False means nothing durable names the value, so the caller must hand it straight back.
        internal static bool CommitTake(KmhTransaction t, IEnumerable<Items.KmhThingPayload> payloads)
        {
            if (t == null) return false;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) t.EscrowPayloads.Add(p);
            t.Advance(KmhTxState.Reserved);
            return Add(t);
        }

        // Rides the counterparty leg because a non-vault authority offers no vault debit, which is what the primary leg is gated on.
        internal static KmhTransaction OpenReturn(string toPlayer, KmhTxType type, string summary, long targetId,
                                                  long silver, IDictionary<string, int> items,
                                                  IEnumerable<Items.KmhThingPayload> payloads)
        {
            if (string.IsNullOrEmpty(toPlayer)) return null;
            KmhTransaction t = KmhTransaction.Create(toPlayer, "marketplace", type, summary ?? "");
            t.Destination   = "personal_treasury";
            t.TargetId      = targetId;
            t.CounterPlayer = toPlayer;
            t.CounterSilver = silver < 0 ? 0 : silver;
            if (items != null) foreach (KeyValuePair<string, int> kv in items) if (kv.Value > 0) t.CounterItems[kv.Key] = kv.Value;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) t.CounterPayloads.Add(p);
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            return Add(t) ? t : null;
        }

        // A reset destroys the value these rows point at on purpose, so they are closed rather than compensated.
        public static int AbortAllFor(string player)
        {
            if (string.IsNullOrEmpty(player)) return 0;
            int closed = 0;
            foreach (KmhTransaction t in Pending())
            {
                if (!NamesPlayer(t, player)) continue;
                Abort(t);
                closed++;
            }
            return closed;
        }

        public static int PendingCountFor(string player)
        {
            if (string.IsNullOrEmpty(player)) return 0;
            int n = 0;
            foreach (KmhTransaction t in Pending()) if (NamesPlayer(t, player)) n++;
            return n;
        }

        private static bool NamesPlayer(KmhTransaction t, string player)
            => t != null && (string.Equals(t.Player, player, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(t.CounterPlayer, player, StringComparison.OrdinalIgnoreCase));

        // The legs are emptied as well as closed: a terminal row that still names goods is one bad iteration from handing them out on top of the authority that kept them.
        internal static void Abort(KmhTransaction t)
        {
            if (t == null || KmhTxStateMachine.IsTerminal(t.State)) return;
            t.EscrowSilver = 0; t.EscrowItems.Clear(); t.EscrowPayloads.Clear();
            t.CounterSilver = 0; t.CounterItems.Clear(); t.CounterPayloads.Clear();
            if (!t.Advance(KmhTxState.Rejected)) { t.Advance(KmhTxState.Failed); t.Advance(KmhTxState.Refunded); }
            Update(t);
        }

        // The legs are emptied with the close: a settled row that still names the goods would hand them out a second time.
        internal static void Settle(KmhTransaction t)
        {
            if (t == null || KmhTxStateMachine.IsTerminal(t.State)) return;
            if (t.State == KmhTxState.Reserved) t.Advance(KmhTxState.Approved);
            t.Advance(KmhTxState.Delivered);
            t.Advance(KmhTxState.Confirmed);
            t.EscrowSilver = 0; t.EscrowItems.Clear(); t.EscrowPayloads.Clear();
            t.CounterSilver = 0; t.CounterItems.Clear(); t.CounterPayloads.Clear();
            Update(t);
        }

        // The client says it holds the delivery, so the operation behind it is finally over.
        internal static void ConfirmDelivered(string deliveryId)
        {
            if (string.IsNullOrEmpty(deliveryId)) return;
            KmhTransaction t = null;
            lock (_lock)
                foreach (KmhTransaction x in _index.All)
                    if (string.Equals(x.DeliveryId, deliveryId, StringComparison.Ordinal)) { t = x; break; }
            if (t == null || KmhTxStateMachine.IsTerminal(t.State)) return;
            if (t.State != KmhTxState.Delivered) t.Advance(KmhTxState.Delivered);
            t.Advance(KmhTxState.Confirmed);
            lock (_lock) Save();
        }

        // Capped so a mass-stuck run can't turn one line into a wall of ids.
        private static List<string> StuckIds(List<KmhTransaction> pending)
        {
            var ids = new List<string>();
            foreach (KmhTransaction t in pending)
            {
                KmhTxRecoveryAction a = KmhTransactionRecovery.DecideForStuck(t.State);
                if (a != KmhTxRecoveryAction.Refund && a != KmhTxRecoveryAction.Reconcile) continue;
                if (ids.Count == 10) { ids.Add("..."); break; }
                ids.Add($"{t.Id} ({t.State})");
            }
            return ids;
        }

        // Boot materializes the file so the next integrity scan reads an empty ledger instead of reporting it missing.
        public static bool SaveToDisk() { lock (_lock) return Save(); }

        // False means the ledger is memory-only, and an escrow whose intent was never written down cannot be recovered after a restart.
        private static bool Save()
        {
            try
            {
                var state = new State();
                state.Transactions.AddRange(_index.All);
                foreach (KeyValuePair<string, long> kv in _index.SpentKeys) state.SpentKeys[kv.Key] = kv.Value;
                // Prunes the copy, not the live index: dropping rows from under a caller mid-session would surprise it.
                long now = DateTime.UtcNow.Ticks;
                foreach (KmhTransaction dropping in KmhTxPrune.SelectForPrune(state.Transactions, MaxFinishedKept))
                    if (!string.IsNullOrEmpty(dropping.RequestKey)) state.SpentKeys[dropping.RequestKey] = now;
                KmhTxPrune.PruneFinished(state.Transactions, MaxFinishedKept);
                return JsonFileStore.Save(KmhDataPaths.TransactionLedgerFile, state);
            }
            catch (Exception ex) { ServerLog.Warn($"Transaction ledger: save failed ({ex.Message})."); return false; }
        }
    }
}
