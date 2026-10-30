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
        internal const int LivePruneSlack = 250;
        internal static readonly TimeSpan LivePruneMinAge = TimeSpan.FromHours(1);

        public static void LoadFromDisk()
        {
            lock (_lock)
            {
                State s = JsonFileStore.TryLoad(KmhDataPaths.TransactionLedgerFile, out State loaded) && loaded?.Transactions != null
                    ? loaded : new State();
                // Pruned before the index sees it; dropped rows' keys carry over so those operations stay un-repeatable.
                long now = DateTime.UtcNow.Ticks;
                var carried = new Dictionary<string, long>(s.SpentKeys ?? new Dictionary<string, long>(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (KmhTransaction dropping in KmhTxPrune.SelectForPrune(s.Transactions, MaxFinishedKept))
                    if (!string.IsNullOrEmpty(dropping.RequestKey)) carried[dropping.RequestKey] = now;
                int dropped = KmhTxPrune.PruneFinished(s.Transactions, MaxFinishedKept);
                if (dropped > 0)
                    Diagnostics.ServerLog.Info($"Transactions: pruned {dropped} settled transaction(s) beyond the newest {MaxFinishedKept}.");
                _index.Load(s.Transactions, carried);
                _index.EvictStaleKeys(now);
            }
        }

        public static bool IsDuplicate(string requestKey) { lock (_lock) return _index.IsDuplicate(requestKey); }

        // False also when the row could not be written down: an unrecoverable intent must never count as recorded.
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

        // Degrades here because callers discard the answer: a row advanced in memory but not on disk reads as its OLD
        // state next boot, which recovery refunds. Only a failed WRITE degrades; a stale or unknown row is a refusal.
        public static bool Update(KmhTransaction tx)
        {
            if (tx == null) return false;
            if (IsStale(tx)) return false;
            lock (_lock)
            {
                if (_index.Get(tx.Id) == null) return false;
                if (Save()) return true;
            }
            Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(true);
            ServerLog.Error($"Transaction {tx.Id}: state {tx.State} could not be written. Value moves are "
                          + "refused until persistence recovers; 'kmh recover list' shows the row.");
            return false;
        }

        // A reset destroyed what this row was authorised against; advancing it moves value out of state that no longer has it.
        internal static bool IsStale(KmhTransaction tx)
            => tx != null && !string.IsNullOrEmpty(tx.Player)
            && tx.TargetGeneration < Features.Economy.KmhEconomyReset.GenerationOf(tx.Player);

        public static KmhTransaction Get(string id)            { lock (_lock) return _index.Get(id); }
        public static List<KmhTransaction> ForPlayer(string p) { lock (_lock) return _index.ForPlayer(p); }
        public static List<KmhTransaction> Pending()           { lock (_lock) return _index.Pending(); }
        public static List<KmhTransaction> All()               { lock (_lock) return new List<KmhTransaction>(_index.All); }
        public static int Count                                { get { lock (_lock) return _index.Count; } }

        // Moves only the states that are safe to move and reports the rest, rather than guessing at escrowed value.
        public static void RecoverOnBoot() => RecoverPending(null);

        // The boot pass over one player's rows only, so a test never settles or refunds anybody else's live operation.
        internal static void RecoverPendingForTest(string player)
            => RecoverPending(t => string.Equals(t.Player, player, StringComparison.OrdinalIgnoreCase));

        private static void RecoverPending(Func<KmhTransaction, bool> only)
        {
            List<KmhTransaction> pending = Pending();
            if (only != null) pending = pending.FindAll(t => only(t));
            if (pending.Count == 0) return;
            int rejected = 0, refunded = 0, needsRefund = 0, needsReconcile = 0, storeOwned = 0, stale = 0;
            foreach (KmhTransaction t in pending)
            {
                // From before a reset that destroyed what it moves (a season wipe, or this player's own): finishing it
                // would pay old value into the new vaults, so it closes unpaid. A guild's goods are not a player's to lose.
                if (IsStale(t) && !IsGuildTake(t)) { Abort(t); stale++; continue; }
                switch (FinishOrUnwind(t))
                {
                    case Outcome.StoreOwned:     storeOwned++; break;
                    case Outcome.NeedsRefund:    needsRefund++; break;
                    case Outcome.NeedsReconcile: needsReconcile++; break;
                    case Outcome.Rejected:       rejected++; break;
                    case Outcome.Refunded:       refunded++; break;
                }
            }
            lock (_lock) Save();
            if (stale > 0) ServerLog.Warn($"Transaction recovery: {stale} transaction(s) from before a reset closed without paying out.");
            ServerLog.Warn($"Transaction recovery: {pending.Count} non-terminal transaction(s) from a previous run - " +
                           $"{rejected} rejected (nothing reserved), {refunded} refunded, {needsRefund} awaiting refund, " +
                           $"{needsReconcile} awaiting reconcile, {storeOwned} closed on what a store still holds.");

            // A Delivered row closes when the client confirms (re-issuing it duplicates value), so only a refund needs a human.
            if (needsRefund > 0)
                ServerLog.Error($"Transaction recovery: {needsRefund} transaction(s) hold value that could not be "
                    + "refunded automatically - see 'kmh recover list'. Ids: " + string.Join(", ", StuckIds(pending)));
            else if (needsReconcile > 0)
                ServerLog.Warn($"Transaction recovery: {needsReconcile} delivery(ies) are waiting for the player to "
                    + "confirm receipt; they re-send on the next join. 'kmh recover list' shows them.");
        }

        private enum Outcome { StoreOwned, NeedsRefund, NeedsReconcile, Rejected, Refunded, Waiting }

        // Ends a stuck row the way the stores' evidence says it ended, for every party it names.
        private static Outcome FinishOrUnwind(KmhTransaction t)
        {
            // A store still holding the value this row names proves how the move ended; paying the row too pays twice.
            // A world delivery the quest recorded keeps what it credited; one it never recorded is refunded below.
            if (t.TargetKind == TargetWorldDelivery && Features.World.WorldStore.RecordedDelivery(t.Id, out int credited))
                return Features.World.WorldEngine.FinishDeliveryRow(t, credited) ? Outcome.StoreOwned : Outcome.NeedsRefund;
            // A want fill knows from the want store whether it landed: finish it if so, hand the goods back if not.
            if (t.TargetKind == TargetWantFill)
            {
                bool done = Features.WantBoard.WantStore.RecordedFill(t.Id)
                    ? Features.WantBoard.WantStore.CompleteFill(t)
                    : Features.WantBoard.WantStore.UndoFill(t);
                return done ? Outcome.StoreOwned : Outcome.NeedsRefund;
            }
            // A sale past its Delivered write names every leg it still owes, so it is finished, never refunded.
            if (t.TargetKind == TargetMarketBuy && t.State == KmhTxState.Delivered)
                return Features.Marketplace.MarketplaceStore.CompletePurchase(t) ? Outcome.StoreOwned : Outcome.NeedsRefund;
            switch (StoreEvidence(t))
            {
                case StoreFinding.OwnsGoods:  PayHouseFee(t); Settle(t); return Outcome.StoreOwned;
                case StoreFinding.StillHolds: Abort(t); return Outcome.StoreOwned;
                case StoreFinding.Delivered:  AdoptDelivery(t); return Outcome.NeedsReconcile;
            }
            switch (KmhTransactionRecovery.DecideForStuck(t.State))
            {
                case KmhTxRecoveryAction.Reject:    t.Advance(KmhTxState.Rejected); return Outcome.Rejected;
                case KmhTxRecoveryAction.Refund:    return Compensate(t) ? Outcome.Refunded : Outcome.NeedsRefund;
                case KmhTxRecoveryAction.Reconcile: return Outcome.NeedsReconcile;
            }
            return Outcome.Waiting;
        }

        internal enum StoreFinding { None, OwnsGoods, StillHolds, Delivered }

        internal static string HouseFeeKeyFor(string txId) => txId + ":house";

        // A message that holds its attachment collected the fee with it; a crash before the pool's save left it unpaid.
        // On the row's own key, so the live path and recovery credit it once between them.
        internal static void PayHouseFee(KmhTransaction t)
        {
            if (t == null || t.HouseTax <= 0 || t.TargetKind != TargetMailSend) return;
            if (!Features.Marketplace.MarketplaceStore.TryCreditHousePoolOnce(HouseFeeKeyFor(t.Id), t.HouseTax, $"mail attachment fee from {t.Player}"))
                Features.Treasury.TreasuryStore.OweHouseFee(HouseFeeKeyFor(t.Id), t.HouseTax, t.Player, $"mail attachment fee from {t.Player}");
        }

        // Post rows: a listing or auction saved holding the goods owns them, so the row is closed, not refunded.
        // Return rows: a source still on disk never durably left, so its own next pass settles it exactly once.
        // Want rows are left alone: a want legitimately survives a partial fill.
        internal static StoreFinding StoreEvidence(KmhTransaction t)
        {
            if (t == null) return StoreFinding.None;
            // A withdrawal whose delivery was written is being handed over; refunding it too pays it twice.
            if (t.Type == KmhTxType.Withdrawal && t.State != KmhTxState.Delivered
                && Features.Delivery.DeliveryStore.IdForSource(t.Id) != null)
                return StoreFinding.Delivered;
            if (t.Type == KmhTxType.Listing && t.HasEscrow && !string.IsNullOrEmpty(t.Id)
                && (Features.Marketplace.MarketplaceStore.HoldsPost(t.Id) || Features.Auctions.AuctionStore.HoldsPost(t.Id)))
                return StoreFinding.OwnsGoods;
            if (t.TargetKind == TargetWantPost && Features.WantBoard.WantStore.HoldsPost(t.Id)) return StoreFinding.OwnsGoods;
            if (t.TargetKind == TargetQuestPost && Features.Quests.QuestStore.HoldsPost(t.Id)) return StoreFinding.OwnsGoods;
            if (t.TargetKind == TargetMailSend && Features.Mail.MailStore.HoldsSend(t.Id)) return StoreFinding.OwnsGoods;
            // A site change or a road project saved naming its row is what the charge bought; one never saved is refunded.
            if (t.TargetKind == TargetSitePurchase && Features.Sites.SiteStore.HoldsPurchase(t.Id)) return StoreFinding.OwnsGoods;
            if (t.TargetKind == TargetRoadStart && Features.Roadworks.RoadworksStore.HoldsStart(t.Id)) return StoreFinding.OwnsGoods;
            if (t.TargetId <= 0) return StoreFinding.None;
            switch (t.TargetKind)
            {
                case TargetListing:    return Features.Marketplace.MarketplaceStore.Exists(t.TargetId) ? StoreFinding.StillHolds : StoreFinding.None;
                case TargetAuction:    return Features.Auctions.AuctionStore.Exists(t.TargetId) ? StoreFinding.StillHolds : StoreFinding.None;
                // The displaced bidder still leading means the new bid never landed, so they are owed nothing.
                case TargetAuctionBid: return Features.Auctions.AuctionStore.HighBidderIs(t.TargetId, t.CounterPlayer)
                                            ? StoreFinding.StillHolds : StoreFinding.None;
                // A bid the auction saved as its lead is the auction's silver now; one it never saved is refunded.
                case TargetAuctionLead: return Features.Auctions.AuctionStore.LeadTxIs(t.TargetId, t.Id)
                                            ? StoreFinding.OwnsGoods : StoreFinding.None;
            }
            return StoreFinding.None;
        }

        internal const string TargetListing = "listing", TargetAuction = "auction", TargetAuctionBid = "auction-bid";
        internal const string TargetWantPost = "want-post", TargetWantFill = "want-fill", TargetAuctionLead = "auction-lead";
        internal const string TargetQuestPost = "quest-post", TargetMailSend = "mail-send", TargetWorldDelivery = "world-delivery";
        internal const string TargetMarketBuy = "market-buy";
        internal const string TargetSitePurchase = "site-purchase", TargetRoadStart = "road-start";

        private static void AdoptDelivery(KmhTransaction t)
        {
            t.DeliveryId = Features.Delivery.DeliveryStore.IdForSource(t.Id) ?? t.DeliveryId;
            // The gross the row escrowed, less what the delivery carries, is the fee the crash may have cut short.
            long fee = t.EscrowSilver - Features.Delivery.DeliveryStore.SilverForSource(t.Id);
            if (fee > 0)
                Features.Marketplace.MarketplaceStore.TryCreditHousePoolOnce(Features.Delivery.DeliveryStore.FeeKeyFor(t.Id), fee,
                                                                             $"treasury withdraw fee ({t.Player}, recovered)");
            if (t.State == KmhTxState.Reserved) t.Advance(KmhTxState.Approved);
            t.Advance(KmhTxState.Delivered);
            Update(t);
        }

        // Row before debit, and the debit carries the take marker, so a crash between any two steps is readable by recovery.
        internal static KmhTransaction OpenWithdrawal(string player, string summary, long silver, IDictionary<string, int> items)
            => OpenTake(player, KmhTxType.Withdrawal, summary, 0, silver, items, null, destination: "colony");

        internal const string GuildTreasurySource = "guild_treasury";

        // The take is stamped in the guild's vault, so recovery returns the goods there, never to the member's own.
        internal static KmhTransaction OpenGuildWithdrawal(string player, string guild, string identity, string summary,
                                                           IDictionary<string, int> items,
                                                           IEnumerable<Items.KmhThingPayload> payloads)
        {
            if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(guild) || string.IsNullOrEmpty(identity)) return null;
            KmhTransaction t = KmhTransaction.Create(player, GuildTreasurySource, KmhTxType.Withdrawal, summary ?? "");
            t.Destination   = "colony";
            t.Guild         = guild;
            t.GuildIdentity = identity;
            FillLeg(t.EscrowItems, t.EscrowPayloads, items, payloads);
            if (!t.HasEscrow) return null;
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            return Add(t) ? t : null;
        }

        internal static bool IsGuildTake(KmhTransaction t)
            => t != null && t.Source == GuildTreasurySource && !string.IsNullOrEmpty(t.Guild);

        // To the guild while it lives, else to the member who took them: the disband gave the rest to its last member.
        private static bool ReturnGuildTake(KmhTransaction t)
        {
            string note = $"transaction {t.Id} returned to the guild vault";
            if (Features.Guilds.GuildIdentity.IsLive(t.Guild, t.GuildIdentity))
                return Features.Treasury.TreasuryStore.ReturnGuildGoodsOnce(t.Guild, t.GuildIdentity, t.RefundMarker,
                                                                           t.EscrowItems, t.EscrowPayloads, note);
            return Features.Treasury.TreasuryStore.DepositEscrowOnce(t.Player, t.RefundMarker, t.EscrowSilver, t.EscrowItems,
                t.EscrowPayloads, $"transaction {t.Id} - guild '{t.Guild}' is gone, so its goods went to {t.Player}");
        }

        // Deduplicated on the transaction id, so a crash in here resumes rather than pays twice. False: a human settles it.
        internal static bool Compensate(KmhTransaction t)
        {
            if (t == null || string.IsNullOrEmpty(t.Player)) return false;
            bool guildTake = IsGuildTake(t);
            // Refunding across a reset resurrects destroyed value - but a player's reset never destroys a guild's goods.
            if (IsStale(t) && !guildTake) { Abort(t); return true; }
            if (!t.HasEscrow && !t.HasCounterEscrow) { t.Advance(KmhTxState.Refunded); return true; }

            if (t.State != KmhTxState.Compensating)
            {
                if (!t.Advance(KmhTxState.Compensating)) return false;
                lock (_lock) { if (!Save()) return false; }
            }

            // Intent is recorded first, so only the vault (the guild's, for a guild take) knows the debit happened.
            bool owedToPlayer = !t.HasEscrow
                || (guildTake ? Features.Treasury.TreasuryStore.KnowsGuildTxnMarker(t.Guild, t.TakeMarker)
                              : Features.Treasury.TreasuryStore.KnowsTxnMarker(t.Player, t.TakeMarker));
            if (t.HasEscrow && owedToPlayer
                && !(guildTake ? ReturnGuildTake(t)
                               : Features.Treasury.TreasuryStore.DepositEscrowOnce(
                                    t.Player, t.RefundMarker, t.EscrowSilver, t.EscrowItems, t.EscrowPayloads,
                                    $"transaction {t.Id} refunded")))
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
            FillLeg(t.EscrowItems, t.EscrowPayloads, items, payloads);
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            t.Advance(KmhTxState.Approved);
            return Add(t) ? t : null;
        }

        // On the primary leg, so compensation pays only once the vault confirms the take; a pre-debit crash refunds nothing.
        internal static KmhTransaction OpenTake(string player, KmhTxType type, string summary, long targetId,
                                                long silver, IDictionary<string, int> items,
                                                IEnumerable<Items.KmhThingPayload> payloads, string destination = "marketplace",
                                                string targetKind = "", long houseFee = 0)
        {
            if (string.IsNullOrEmpty(player)) return null;
            KmhTransaction t = KmhTransaction.Create(player, "personal_treasury", type, summary ?? "");
            t.Destination  = destination ?? "marketplace";
            t.TargetId     = targetId;
            t.TargetKind   = targetKind ?? "";
            // The part of the escrow owed to the house pool once the take lands, named on the row so recovery can pay it.
            t.HouseTax     = houseFee < 0 ? 0 : houseFee;
            t.EscrowSilver = silver < 0 ? 0 : silver;
            FillLeg(t.EscrowItems, t.EscrowPayloads, items, payloads);
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            return Add(t) ? t : null;
        }

        // Id without the row: a payload take precedes knowing the instances, so the marker is stamped first, the row after.
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

        // Rides the counterparty leg: the primary leg is gated on a vault debit, which a non-vault authority never makes.
        internal static KmhTransaction OpenReturn(string toPlayer, KmhTxType type, string summary, long targetId,
                                                  long silver, IDictionary<string, int> items,
                                                  IEnumerable<Items.KmhThingPayload> payloads, string targetKind = "")
        {
            if (string.IsNullOrEmpty(toPlayer)) return null;
            KmhTransaction t = KmhTransaction.Create(toPlayer, "marketplace", type, summary ?? "");
            t.Destination   = "personal_treasury";
            t.TargetId      = targetId;
            t.TargetKind    = targetKind ?? "";
            t.CounterPlayer = toPlayer;
            t.CounterSilver = silver < 0 ? 0 : silver;
            FillLeg(t.CounterItems, t.CounterPayloads, items, payloads);
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            return Add(t) ? t : null;
        }

        private static void FillLeg(Dictionary<string, int> legItems, List<Items.KmhThingPayload> legPayloads,
                                    IDictionary<string, int> items, IEnumerable<Items.KmhThingPayload> payloads)
        {
            if (items != null) foreach (KeyValuePair<string, int> kv in items) if (kv.Value > 0) legItems[kv.Key] = kv.Value;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) legPayloads.Add(p);
        }

        // A reset destroys the value these rows point at on purpose, so they are closed rather than compensated. A trade
        // with someone else ends as a restart would end it, so their side is made whole; the reset player's side lands
        // in stores the reset empties next. One that cannot end yet stays open, and the reset waits for it.
        public static int AbortAllFor(string player)
        {
            if (string.IsNullOrEmpty(player)) return 0;
            int closed = 0;
            foreach (KmhTransaction t in Pending())
            {
                if (!NamesPlayer(t, player)) continue;
                if (!HasOtherParty(t)) Abort(t);
                else FinishOrUnwind(t);
                if (!KmhTxStateMachine.IsTerminal(t.State))
                {
                    ServerLog.Error($"Reset of '{player}': transaction {t.Id} with {(Eq(t.Player, player) ? t.CounterPlayer : t.Player)} "
                                  + "could not be finished or returned yet - the reset waits for it.");
                    continue;
                }
                closed++;
            }
            lock (_lock) Save();
            return closed;
        }

        private static bool HasOtherParty(KmhTransaction t)
            => !string.IsNullOrEmpty(t.CounterPlayer) && !Eq(t.Player, t.CounterPlayer);

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

        // Legs emptied as well as closed: a terminal row still naming goods is one bad iteration from paying them twice.
        internal static void Abort(KmhTransaction t)
        {
            if (t == null || KmhTxStateMachine.IsTerminal(t.State)) return;
            ClearLegs(t);
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
            ClearLegs(t);
            Update(t);
        }

        private static void ClearLegs(KmhTransaction t)
        {
            t.EscrowSilver = 0; t.EscrowItems.Clear(); t.EscrowPayloads.Clear();
            t.CounterSilver = 0; t.CounterItems.Clear(); t.CounterPayloads.Clear();
        }

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
            // Delivered and confirmed, so the legs describe nothing still owed; kept, they hold every withdrawn blob.
            ClearLegs(t);
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

        // A change landing mid-copy throws rather than tearing the copy; a later attempt sees the row whole.
        private static List<KmhTransaction> SnapshotRows()
        {
            for (int attempt = 1; ; attempt++)
            {
                try { BeforeSnapshotForTest?.Invoke(); return KmhDeepCopy.OfList(_index.All); }
                catch (InvalidOperationException) when (attempt < 5) { System.Threading.Thread.Yield(); }
            }
        }

        internal static Action BeforeSnapshotForTest;

        // False means the ledger is memory-only: an escrow whose intent was never written down is unrecoverable after a restart.
        private static bool Save()
        {
            try
            {
                // The live index is trimmed now and then, with slack, so a save does not pay for the scan every time.
                if (_index.Count > MaxFinishedKept + LivePruneSlack)
                    _index.PruneFinished(MaxFinishedKept, LivePruneMinAge, DateTime.UtcNow.Ticks);
                var state = new State();
                // Copies: callers change rows outside this lock, and a "collection modified" failure freezes value moves.
                state.Transactions.AddRange(SnapshotRows());
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
