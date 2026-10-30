using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Auctions.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.Auctions
{
    // Both the seller's item and every bid live outside the treasury until the auction settles.
    internal static class AuctionStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, AuctionDto> _byId = new Dictionary<long, AuctionDto>();
        private static long _nextId = 1;

        private sealed class PersistedState
        {
            public List<AuctionDto> Auctions { get; set; } = new List<AuctionDto>();
            public long             NextId   { get; set; } = 1;
            public Dictionary<long, string> LeadTx { get; set; } = new Dictionary<long, string>();
            public List<PendingRefund> PendingRefunds { get; set; } = new List<PendingRefund>();
        }

        // Written in the same save that removes a purged auction, so a crash-resumed reset cannot refund one bid twice.
        internal sealed class PendingRefund
        {
            [Newtonsoft.Json.JsonProperty("marker")] public string Marker { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("bidder")] public string Bidder { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("silver")] public long   Silver { get; set; }
            [Newtonsoft.Json.JsonProperty("note")]   public string Note   { get; set; } = "";
        }
        private static readonly Dictionary<string, PendingRefund> _pendingRefunds = new Dictionary<string, PendingRefund>(StringComparer.Ordinal);

        // Server-side only: the ledger row whose silver the standing bid is, so recovery can see a bid that landed.
        private static readonly Dictionary<long, string> _leadTx = new Dictionary<long, string>();

        internal static bool LeadTxIs(long auctionId, string txId)
        {
            if (string.IsNullOrEmpty(txId)) return false;
            lock (_lock)
                return _byId.ContainsKey(auctionId) && _leadTx.TryGetValue(auctionId, out string t) && t == txId;
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.AuctionsFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _byId.Clear();
                if (s.Auctions != null) foreach (AuctionDto a in s.Auctions) if (a != null) _byId[a.Id] = a;
                _leadTx.Clear();
                if (s.LeadTx != null) foreach (KeyValuePair<long, string> kv in s.LeadTx) _leadTx[kv.Key] = kv.Value;
                _nextId = Math.Max(1, s.NextId);
                _pendingRefunds.Clear();
                if (s.PendingRefunds != null)
                    foreach (PendingRefund p in s.PendingRefunds)
                        if (p != null && !string.IsNullOrEmpty(p.Marker)) _pendingRefunds[p.Marker] = p;
            }
            Diagnostics.ServerLog.Diag($"Auctions: loaded {s.Auctions?.Count ?? 0} open auction(s)");
        }

        // Auction escrow sits outside the treasury, so a save-reset that skipped this user would let it shelter value.
        public static bool HasUserActivity(string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock)
                foreach (AuctionDto a in _byId.Values)
                    if (Eq(a.SellerUsername, user) || Eq(a.HighBidder, user)) return true;
            return false;
        }

        // A high bidder on this user's auction is innocent and refunded; the resetting user's own escrow burns.
        public static (int removed, int retracted) PurgeUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return (0, 0);
            List<long> remove = new List<long>();
            int retracted = 0;
            lock (_lock)
            {
                foreach (AuctionDto a in _byId.Values)
                {
                    if (Eq(a.SellerUsername, user))
                    {
                        if (!string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0 && !Eq(a.HighBidder, user))
                        {
                            string marker = $"auc:{a.Id}:void";
                            _pendingRefunds[marker] = new PendingRefund
                            {
                                Marker = marker, Bidder = a.HighBidder, Silver = a.CurrentBid, Note = "auction voided (seller reset)",
                            };
                        }
                        remove.Add(a.Id);
                    }
                    else if (Eq(a.HighBidder, user) && a.CurrentBid > 0)
                    {
                        a.CurrentBid = 0; a.HighBidder = ""; retracted++;
                    }
                }
                foreach (long id in remove) _byId.Remove(id);
            }
            AtPurgePhaseForTest?.Invoke("purged");
            // Unconditional, so a retry after a refused save writes down what the first attempt left in memory. Refunds
            // only follow a purge that is durable: one paid for a purge that never landed would be paid again by the retry.
            if (SaveToDisk())
            {
                AtPurgePhaseForTest?.Invoke("saved");
                SettlePendingRefunds();
            }
            return (remove.Count, retracted);
        }

        internal static Action<string> AtPurgePhaseForTest;

        // On each refund's own marker, so a boot resuming one already credited pays nothing; a refused credit is kept for it.
        public static void SettlePendingRefunds()
        {
            List<PendingRefund> owed;
            lock (_lock) owed = new List<PendingRefund>(_pendingRefunds.Values);
            if (owed.Count == 0) return;
            bool changed = false;
            foreach (PendingRefund p in owed)
            {
                if (p == null || string.IsNullOrEmpty(p.Marker)) continue;
                // Owed to a save that is being destroyed: dropped with it, never paid into the new one.
                if (Economy.KmhEconomyReset.Resetting(p.Bidder)) { lock (_lock) _pendingRefunds.Remove(p.Marker); changed = true; continue; }
                if (!Treasury.TreasuryStore.DepositEscrowOnce(p.Bidder, p.Marker, p.Silver, null, null, p.Note ?? "auction refund"))
                {
                    Diagnostics.ServerLog.Error($"Auctions: {p.Silver} silver for '{p.Bidder}' could not be refunded ({p.Note}) - held as {p.Marker} and retried at the next boot.");
                    continue;
                }
                AtPurgePhaseForTest?.Invoke("refund-credited");
                lock (_lock) _pendingRefunds.Remove(p.Marker);
                changed = true;
            }
            if (changed) SaveToDisk();
        }

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropPendingRefundsFor(string bidder)
        {
            if (string.IsNullOrEmpty(bidder)) return 0;
            int dropped = 0;
            lock (_lock)
                foreach (PendingRefund p in new List<PendingRefund>(_pendingRefunds.Values))
                    if (p != null && Eq(p.Bidder, bidder) && _pendingRefunds.Remove(p.Marker)) dropped++;
            return dropped;
        }

        internal static int PendingRefundCountForTest { get { lock (_lock) return _pendingRefunds.Count; } }

        public static void ClearForNewSeason()
        {
            // Refunds owed to the old season go with it: the vaults they pay into are wiped, and ids restart at 1, so an
            // old auction #N refund marker would make the new auction #N's refund to the same bidder a no-op.
            lock (_lock) { _byId.Clear(); _pendingRefunds.Clear(); _leadTx.Clear(); _nextId = 1; }
            SaveToDisk();
        }

        internal static void PlantPendingRefundForTest(string bidder, long silver)
        {
            string marker = "selftest:" + Guid.NewGuid().ToString("N");
            lock (_lock) _pendingRefunds[marker] = new PendingRefund { Marker = marker, Bidder = bidder, Silver = silver, Note = "selftest" };
        }

        // False means in-memory only; escrow lives here, not in a vault, so an unwritten auction loses both sides' value.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock)
            {
                s.Auctions.AddRange(KmhDeepCopy.OfList(_byId.Values)); s.NextId = _nextId;
                foreach (KeyValuePair<long, string> kv in _leadTx) if (_byId.ContainsKey(kv.Key)) s.LeadTx[kv.Key] = kv.Value;
                s.PendingRefunds.AddRange(KmhDeepCopy.OfList(_pendingRefunds.Values));
                seq = JsonFileStore.NextSequence();
            }
            if (!JsonFileStore.Save(KmhDataPaths.AuctionsFile, s, seq)) return false;
            Maintenance.KmhInvalidation.AuctionsCommitted();
            return true;
        }

        // Derived per broadcast rather than stored, and it decides who may not share a serialized payload.
        public static HashSet<string> SellersOfNonPublic()
        {
            HashSet<string> sellers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                foreach (AuctionDto a in _byId.Values)
                    if (a != null && !string.IsNullOrEmpty(a.SellerUsername)
                        && !string.Equals(a.Visibility, Guilds.GuildVisibility.Public, StringComparison.OrdinalIgnoreCase))
                        sellers.Add(a.SellerUsername);
            return sellers;
        }

        public static AuctionSnapshot BuildSnapshot(string caller)
        {
            string callerGuild = string.IsNullOrEmpty(caller) ? null : Guilds.GuildStore.CurrentGuildOf(caller);
            AuctionSnapshot s = new AuctionSnapshot();
            lock (_lock)
            {
                s.Revision = Util.KmhSnapshotRevision.Next();
                foreach (AuctionDto a in _byId.Values)
                {
                    bool mine = !string.IsNullOrEmpty(caller) && Eq(a.SellerUsername, caller);
                    if (!mine && !Guilds.GuildVisibility.IsVisibleTo(a.SellerTreasuryKey, a.Visibility, callerGuild, prefetched: true))
                        continue;
                    s.Auctions.Add(Clone(a));   // shallow copy so a mid-send mutation can't corrupt the serializer
                }
            }
            return s;
        }

        public static (long id, string reason) Post(string seller, string itemDef, string stuff, int quality, int qty,
            long startingBid, long minIncrement, long buyout, int durationHours, string visibility)
        {
            if (string.IsNullOrEmpty(seller)) return (0, "No seller.");
            if (string.IsNullOrEmpty(itemDef)) return (0, "No item.");
            if (qty <= 0) return (0, "Quantity must be > 0.");
            quality = Util.ItemKey.Clamp(quality);
            stuff   = stuff ?? "";

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            startingBid  = Clamp(startingBid, 1, int.MaxValue);
            minIncrement = Clamp(minIncrement, 1, int.MaxValue);
            buyout       = buyout <= 0 ? 0 : Clamp(buyout, startingBid, int.MaxValue);
            int hours    = durationHours > 0 ? Math.Min(durationHours, cfg.Auctions.MaxDurationHours) : cfg.Auctions.DefaultDurationHours;

            lock (_lock)
            {
                if (OpenAuctionsLocked(seller) >= cfg.Auctions.MaxOpenPerUser)
                    return (0, $"You already have the max {cfg.Auctions.MaxOpenPerUser} open auctions.");
            }

            // Asked before escrow, so a denial leaves nothing to undo.
            KMH.Sdk.Server.Hooks.KmhHookVerdict verdict = Extensibility.KmhHooks.Instance.CheckAuctionListing(
                new KMH.Sdk.Server.Hooks.KmhAuctionListingContext(seller, itemDef, qty, startingBid));
            if (verdict.Denied) return (0, verdict.Reason);

            // Escrowed outside the store lock, because TreasuryStore holds its own and the two must never nest.
            string key = Util.ItemKey.Compose(itemDef, stuff, quality);

            // Recorded before the debit: until the auction reaches disk, nothing else says the goods exist.
            Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.OpenTake(
                seller, Transactions.KmhTxType.Listing, $"auction post {qty}x", 0, 0,
                new Dictionary<string, int> { { key, qty } }, null);
            if (post == null) return (0, "The server couldn't record that auction - nothing was taken.");

            if (!Treasury.TreasuryStore.WithdrawItemForTxn(seller, key, qty, post.TakeMarker, note: "auction post escrow"))
            {
                Transactions.KmhTransactionRepository.Abort(post);
                return (0, "Your treasury doesn't have that many to auction.");
            }

            long id = 0, now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                // Re-counted because the escrow released the lock, so a concurrent post may have taken the last slot.
                overCap = OpenAuctionsLocked(seller) >= cfg.Auctions.MaxOpenPerUser;
                if (!overCap)
                {
                    id = _nextId++;
                    _byId[id] = new AuctionDto
                    {
                        Id                = id,
                        PostTxId          = post.Id,
                        SellerUsername    = seller,
                        SellerTreasuryKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(seller),
                        ItemDefName       = itemDef,
                        StuffDefName      = stuff,
                        QualityIndex      = quality,
                        Qty               = qty,
                        StartingBid       = startingBid,
                        MinIncrement      = minIncrement,
                        BuyoutSilver      = buyout,
                        CurrentBid        = 0,
                        HighBidder        = "",
                        BidCount          = 0,
                        ListedUtcTicks    = now,
                        EndsUtcTicks      = now + TimeSpan.FromHours(hours).Ticks,
                        Visibility        = Guilds.GuildVisibility.Normalize(visibility),
                    };
                }
            }
            if (overCap)
            {
                Marketplace.MarketplaceStore.RefundPostOnMarker(post, seller, key, qty, "auction post refund (open-auction limit reached)");
                Transactions.KmhTransactionRepository.Settle(post);
                return (0, $"You already have the max {cfg.Auctions.MaxOpenPerUser} open auctions - your items were returned.");
            }
            // The escrow has already left the seller's treasury, so an unwritten auction has to hand it back.
            if (!SaveToDisk())
            {
                lock (_lock) _byId.Remove(id);
                Marketplace.MarketplaceStore.RefundPostOnMarker(post, seller, key, qty, "auction post could not be saved");
                Transactions.KmhTransactionRepository.Settle(post);
                return (0, "The server couldn't save that auction - your items were returned. Try again shortly.");
            }
            // The auction owns the goods now, so the ledger releases its claim.
            Transactions.KmhTransactionRepository.Settle(post);
            Extensibility.KmhEventBus.Instance.RaiseAuctionPosted(new KMH.Sdk.Server.Events.AuctionPostedEvent
            { AuctionId = id, SellerUsername = seller, ItemDefName = itemDef, Qty = qty, StartingBid = startingBid, BuyoutSilver = buyout, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            return (id, $"Auction posted: {qty}x, starts at {Util.SilverFmt.Format(startingBid)}.");
        }

        public static (long id, string reason) PostPayload(string seller, string fingerprint, int qty,
            long startingBid, long minIncrement, long buyout, int durationHours, string visibility)
        {
            if (string.IsNullOrEmpty(seller)) return (0, "No seller.");
            if (string.IsNullOrEmpty(fingerprint)) return (0, "No item.");
            if (qty <= 0) return (0, "Quantity must be > 0.");

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            startingBid  = Clamp(startingBid, 1, int.MaxValue);
            minIncrement = Clamp(minIncrement, 1, int.MaxValue);
            buyout       = buyout <= 0 ? 0 : Clamp(buyout, startingBid, int.MaxValue);
            int hours    = durationHours > 0 ? Math.Min(durationHours, cfg.Auctions.MaxDurationHours) : cfg.Auctions.DefaultDurationHours;

            lock (_lock)
            {
                if (OpenAuctionsLocked(seller) >= cfg.Auctions.MaxOpenPerUser)
                    return (0, $"You already have the max {cfg.Auctions.MaxOpenPerUser} open auctions.");
            }

            // Id first so the vault can stamp its marker; the row stores the exact instances, as a rebuilt copy loses state.
            Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                seller, Transactions.KmhTxType.Listing, "auction post (full state)", 0);
            if (post == null) return (0, "No seller.");

            List<Items.KmhThingPayload> escrow = Treasury.TreasuryStore.WithdrawPayloadsForTxn(
                seller, fingerprint, qty, post.TakeMarker, note: "auction post escrow",
                refundMarker: post.RefundMarker);
            if (escrow == null || escrow.Count == 0) return (0, "Your treasury doesn't have that item to auction.");
            int totalUnits = Items.KmhPayloadEscrow.TotalUnits(escrow);
            Items.KmhThingPayload meta = escrow[0];

            if (!Transactions.KmhTransactionRepository.CommitTake(post, escrow))
            {
                Treasury.TreasuryStore.ReturnPendingTake(seller, post.TakeMarker, post.RefundMarker, escrow,
                                                        "auction post could not be recorded");
                return (0, "The server couldn't record that auction - your items were returned.");
            }
            // False only for a missing owner or marker: a refused save keeps the clear in memory for the save retry.
            if (!Treasury.TreasuryStore.ClearPendingTake(seller, post.TakeMarker))
            {
                Treasury.TreasuryStore.DepositEscrowOnce(seller, post.RefundMarker, 0, null, escrow,
                                                        "auction post could not be recorded");
                return (0, "The server couldn't record that auction - your items were returned.");
            }
            // The same veto the compact path asks, once the def is known.
            KMH.Sdk.Server.Hooks.KmhHookVerdict vetoed = Extensibility.KmhHooks.Instance.CheckAuctionListing(
                new KMH.Sdk.Server.Hooks.KmhAuctionListingContext(seller, meta.DefName, totalUnits, startingBid));
            if (vetoed.Denied)
            {
                Treasury.TreasuryStore.DepositEscrowOnce(seller, post.RefundMarker, 0, null, escrow, "auction post vetoed - refund");
                Transactions.KmhTransactionRepository.Settle(post);
                return (0, vetoed.Reason);
            }

            long id = 0, now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                // Re-counted because the escrow released the lock, so a concurrent post may have taken the last slot.
                overCap = OpenAuctionsLocked(seller) >= cfg.Auctions.MaxOpenPerUser;
                if (!overCap)
                {
                    id = _nextId++;
                    _byId[id] = new AuctionDto
                    {
                        Id = id, PostTxId = post.Id, SellerUsername = seller, SellerTreasuryKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(seller),
                        ItemDefName = meta.DefName, StuffDefName = meta.StuffDefName, QualityIndex = meta.Quality, Qty = totalUnits,
                        StartingBid = startingBid, MinIncrement = minIncrement, BuyoutSilver = buyout,
                        CurrentBid = 0, HighBidder = "", BidCount = 0, ListedUtcTicks = now,
                        EndsUtcTicks = now + TimeSpan.FromHours(hours).Ticks,
                        Visibility = Guilds.GuildVisibility.Normalize(visibility),
                        EscrowPayloads = escrow, StateFingerprint = fingerprint, StateNote = Items.KmhPayloadEscrow.StateNote(meta),
                    };
                }
            }
            // Returned on the row's own refund marker, the key recovery uses, so a crash before Settle cannot pay twice.
            if (overCap)
            {
                Treasury.TreasuryStore.DepositEscrowOnce(seller, post.RefundMarker, 0, null, escrow,
                                                        "auction post refund (open-auction limit reached)");
                Transactions.KmhTransactionRepository.Settle(post);
                return (0, $"You already have the max {cfg.Auctions.MaxOpenPerUser} open auctions - your items were returned.");
            }
            // These carry their own state, so an unwritten auction has to return the exact payloads it escrowed.
            if (!SaveToDisk())
            {
                lock (_lock) _byId.Remove(id);
                Treasury.TreasuryStore.DepositEscrowOnce(seller, post.RefundMarker, 0, null, escrow,
                                                        "auction post could not be saved");
                Transactions.KmhTransactionRepository.Settle(post);
                return (0, "The server couldn't save that auction - your items were returned. Try again shortly.");
            }
            // The auction owns the payloads now, so the ledger releases its claim.
            Transactions.KmhTransactionRepository.Settle(post);
            Extensibility.KmhEventBus.Instance.RaiseAuctionPosted(new KMH.Sdk.Server.Events.AuctionPostedEvent
            { AuctionId = id, SellerUsername = seller, ItemDefName = meta.DefName, Qty = totalUnits, StartingBid = startingBid, BuyoutSilver = buyout, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            return (id, $"Auction posted: {totalUnits}x {meta.DisplayLabel}, starts at {Util.SilverFmt.Format(startingBid)} (full state kept).");
        }

        // On the row's own marker: delivery precedes the row's Settle, so a crash between must not let recovery pay twice.
        private static void DeliverAuctionItem(Transactions.KmhTransaction tx, string username, AuctionDto a, string note)
        {
            if (tx != null && Treasury.TreasuryStore.DepositEscrowOnce(username, tx.CounterMarker, 0,
                                                                      tx.CounterItems, tx.CounterPayloads, note))
            {
                a.EscrowPayloads = null;
                return;
            }

            // No row to key on, or the vault refused it - park rather than drop.
            if (a.EscrowPayloads != null && a.EscrowPayloads.Count > 0)
            {
                Items.KmhPayloadEscrow.RefundTo(username, a.EscrowPayloads, note);
                a.EscrowPayloads = null;
            }
            else
                Items.KmhPayloadEscrow.DeliverCompact(username, Util.ItemKey.Compose(a.ItemDefName, a.StuffDefName, a.QualityIndex), a.Qty, note, "auction delivery failed");
        }

        // Same rule for silver: the marker is what makes a replayed payout a no-op.
        private static bool DepositSilverOnMarker(Transactions.KmhTransaction tx, string user, long amount, string note)
        {
            if (tx != null && amount > 0
                && Treasury.TreasuryStore.DepositEscrowOnce(user, tx.CounterMarker, amount, null, null, note))
                return true;
            return DepositLong(user, amount, note);
        }

        public sealed class BidResult
        {
            public bool         Ok;
            public string       Reason = "";
            public AuctionDto   Auction;
            public bool         Settled;
            public string       OutbidUser;
            public SettleOutcome Settlement;
        }

        // The bid is escrowed before the lock, so losing the race afterwards must refund rather than drop it.
        public static BidResult Bid(string bidder, long auctionId, long amount)
        {
            BidResult r = new BidResult();
            if (string.IsNullOrEmpty(bidder) || amount <= 0) { r.Reason = "Bad bid."; return r; }
            amount = Math.Min(amount, int.MaxValue);

            long now = DateTime.UtcNow.Ticks;
            string bidderGuild = Guilds.GuildStore.CurrentGuildOf(bidder); // resolve before the lock

            // Checked before the treasury is touched, so an obviously-bad bid never escrows anything.
            lock (_lock)
            {
                if (!_byId.TryGetValue(auctionId, out AuctionDto pa)) { r.Reason = "Auction not found."; return r; }
                // A malformed row has nobody to pay, so escrowing a bid against it would only strand the silver.
                if (string.IsNullOrEmpty(pa.SellerUsername)) { r.Reason = "That auction is unavailable."; return r; }
                // A frozen seller's lot may be what they minted; staff decide before anyone pays for it.
                if (Moderation.KmhEconomyGuard.IsFrozen(pa.SellerUsername)) { r.Reason = "That auction is paused by staff."; return r; }
                if (pa.EndsUtcTicks <= now)               { r.Reason = "This auction has ended.";            return r; }
                if (Eq(pa.SellerUsername, bidder))        { r.Reason = "You can't bid on your own auction."; return r; }
                // Checked on the action too, or a crafted client could bid on an id it was never shown.
                if (!Guilds.GuildVisibility.IsVisibleTo(pa.SellerTreasuryKey, pa.Visibility, bidderGuild, prefetched: true))
                { r.Reason = "You can't bid on that auction."; return r; }
                if (pa.BuyoutSilver > 0 && amount > pa.BuyoutSilver) amount = pa.BuyoutSilver;
                long need = pa.CurrentBid > 0 ? pa.CurrentBid + pa.MinIncrement : pa.StartingBid;
                if (amount < need) { r.Reason = $"Bid must be at least {Util.SilverFmt.Format(need)}."; return r; }
            }

            // Named before the debit, which carries the row's marker: until the auction saves this bid as the lead,
            // recovery refunds it, and once it has, the auction owns the silver.
            Transactions.KmhTransaction bidTx = Transactions.KmhTransactionRepository.OpenTake(
                bidder, Transactions.KmhTxType.AuctionBid, $"auction #{auctionId} bid", auctionId, amount, null, null,
                destination: "auction", targetKind: Transactions.KmhTransactionRepository.TargetAuctionLead);
            if (bidTx == null) { r.Reason = "The server couldn't record that bid - nothing was taken."; return r; }
            if (!Treasury.TreasuryStore.WithdrawSilverForTxn(bidder, (int)amount, bidTx.TakeMarker, note: $"auction #{auctionId} bid"))
            {
                Transactions.KmhTransactionRepository.Abort(bidTx);
                r.Reason = "Your treasury doesn't have that much silver.";
                return r;
            }
            AtValuePhaseForTest?.Invoke("bid-debited");

            // Captured before the auction stops naming the displaced bidder and the disk loses track of that silver.
            string outgoingUser = null; long outgoingAmt = 0;
            lock (_lock)
                if (_byId.TryGetValue(auctionId, out AuctionDto peek) && !string.IsNullOrEmpty(peek.HighBidder) && peek.CurrentBid > 0)
                { outgoingUser = peek.HighBidder; outgoingAmt = peek.CurrentBid; }

            Transactions.KmhTransaction outbid = null;
            if (!string.IsNullOrEmpty(outgoingUser))
            {
                outbid = Transactions.KmhTransactionRepository.OpenReturn(
                    outgoingUser, Transactions.KmhTxType.Refund, $"auction #{auctionId} outbid", auctionId,
                    outgoingAmt, null, null, Transactions.KmhTransactionRepository.TargetAuctionBid);
                if (outbid == null)
                {
                    ReturnBid(bidTx);
                    r.Reason = "The server couldn't record that bid - your silver was returned.";
                    return r;
                }
            }
            AtValuePhaseForTest?.Invoke("outbid-recorded");

            string refundUser = null; long refundAmt = 0;
            lock (_lock)
            {
                now = DateTime.UtcNow.Ticks;
                // The lead must still be the one the outbid row names, or that row would refund the wrong player.
                if (_byId.TryGetValue(auctionId, out AuctionDto a)
                    && a.EndsUtcTicks > now
                    && !Eq(a.SellerUsername, bidder)
                    && amount >= (a.CurrentBid > 0 ? a.CurrentBid + a.MinIncrement : a.StartingBid)
                    && LeadMatches(a, outgoingUser, outgoingAmt))
                {
                    string wasHigh = a.HighBidder; long wasBid = a.CurrentBid;
                    int wasCount = a.BidCount; long wasEnds = a.EndsUtcTicks;
                    _leadTx.TryGetValue(auctionId, out string wasLeadTx);

                    refundUser   = a.HighBidder; refundAmt = a.CurrentBid;
                    a.HighBidder = bidder; a.CurrentBid = amount; a.BidCount++;
                    _leadTx[auctionId] = bidTx.Id;

                    // A late bid extends the close, so sniping cannot win an auction nobody could answer.
                    long snipe = TimeSpan.FromMinutes(Economy.EconomyConfig.Current.Auctions.AntiSnipeMinutes).Ticks;
                    if (a.EndsUtcTicks - now < snipe) a.EndsUtcTicks = now + snipe;

                    // Durable before the refund, or a restart shows the previous bidder still leading a bid already repaid.
                    if (!SaveToDisk())
                    {
                        a.HighBidder = wasHigh; a.CurrentBid = wasBid;
                        a.BidCount = wasCount;  a.EndsUtcTicks = wasEnds;
                        if (wasLeadTx != null) _leadTx[auctionId] = wasLeadTx; else _leadTx.Remove(auctionId);
                        refundUser = null; refundAmt = 0;
                    }
                    else
                    {
                        if (a.BuyoutSilver > 0 && a.CurrentBid >= a.BuyoutSilver) r.Settled = true;
                        r.Auction = Clone(a);
                        r.Ok = true;
                    }
                }
            }

            if (!r.Ok)
            {
                // Nothing was displaced, so the recorded return must not stay owed on top of the standing bid.
                Transactions.KmhTransactionRepository.Abort(outbid);
                ReturnBid(bidTx);
                r.Reason = "Outbid or the auction ended - your silver was returned.";
                return r;
            }
            AtValuePhaseForTest?.Invoke("bid-saved");
            Transactions.KmhTransactionRepository.Settle(bidTx);

            if (!string.IsNullOrEmpty(refundUser) && refundAmt > 0)
            {
                DepositSilverOnMarker(outbid, refundUser, refundAmt, $"auction #{auctionId} outbid refund");
                r.OutbidUser = refundUser;
            }
            AtValuePhaseForTest?.Invoke("outbid-refunded");
            Transactions.KmhTransactionRepository.Settle(outbid);

            Extensibility.KmhEventBus.Instance.RaiseAuctionBid(new KMH.Sdk.Server.Events.AuctionBidEvent
            { AuctionId = auctionId, BidderUsername = bidder, Amount = amount, OutbidUsername = refundUser ?? "" });

            if (r.Settled) r.Settlement = SettleNow(auctionId);
            return r;
        }

        public static (bool ok, string reason) Cancel(string seller, long auctionId)
        {
            AuctionDto a;
            lock (_lock)
            {
                if (!_byId.TryGetValue(auctionId, out a)) return (false, "Auction not found.");
                if (!Eq(a.SellerUsername, seller))        return (false, "That isn't your auction.");
                if (!string.IsNullOrEmpty(a.HighBidder))  return (false, "Can't cancel - it already has a bid.");
            }

            // Written down before the auction stops owning the goods, so a crash in between still names them.
            Transactions.KmhTransaction tx = Transactions.KmhTransactionRepository.OpenReturn(
                seller, Transactions.KmhTxType.Listing, $"auction #{auctionId} cancel", auctionId, 0,
                GoodsLegOf(a), a.EscrowPayloads, Transactions.KmhTransactionRepository.TargetAuction);
            if (tx == null) return (false, "The server couldn't record that cancellation - nothing changed.");
            AtValuePhaseForTest?.Invoke("cancel-recorded");

            lock (_lock)
            {
                // Re-checked because the record released the lock: a bid or a settlement may have landed since.
                if (!_byId.TryGetValue(auctionId, out AuctionDto now) || !ReferenceEquals(now, a)
                    || !string.IsNullOrEmpty(a.HighBidder))
                {
                    Transactions.KmhTransactionRepository.Abort(tx);
                    return (false, "Auction not found.");
                }
                _byId.Remove(auctionId);
                // Durable before the item goes back, or the restart re-lists goods the seller already holds.
                if (!SaveToDisk())
                {
                    _byId[auctionId] = a;
                    Transactions.KmhTransactionRepository.Abort(tx);
                    return (false, "The server couldn't save that - nothing changed. Try again shortly.");
                }
            }
            DeliverAuctionItem(tx, seller, a, $"auction #{auctionId} cancelled");
            AtValuePhaseForTest?.Invoke("cancel-returned");
            Transactions.KmhTransactionRepository.Settle(tx);
            return (true, "Auction cancelled - item returned to your treasury.");
        }

        // Ignores visibility, so this must never reach a normal client.
        public static List<AuctionDto> AllForAdmin()
        {
            lock (_lock)
            {
                List<AuctionDto> all = new List<AuctionDto>(_byId.Count);
                foreach (AuctionDto a in _byId.Values) all.Add(Clone(a));
                return all;
            }
        }

        public sealed class VoidOutcome
        {
            public bool   Done;
            public string Seller      = "";
            public string Bidder      = "";
            public long   RefundedBid;
            public string ItemDefName = "";
            public int    Qty;
            public HashSet<string> Affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // A full undo rather than a sale, so no tax is taken and both sides get their escrow back.
        public static VoidOutcome AdminVoid(long auctionId)
        {
            // A bid landing mid-void changes who is owed; the attempt that saw it backs out and the next one reads it.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                VoidOutcome o = TryAdminVoid(auctionId, out bool bidMoved);
                if (!bidMoved) return o;
            }
            return new VoidOutcome();
        }

        internal static Action DuringVoidForTest;

        private static VoidOutcome TryAdminVoid(long auctionId, out bool bidMoved)
        {
            bidMoved = false;
            VoidOutcome o = new VoidOutcome();
            AuctionDto a;
            string bidder;
            long bid;
            lock (_lock)
            {
                if (!_byId.TryGetValue(auctionId, out a)) return o;
                bidder = a.HighBidder; bid = a.CurrentBid;
            }

            // Two owners get value back, so each leg is its own record and one unwritten leg cannot sink the other.
            bool refundBid = !string.IsNullOrEmpty(bidder) && bid > 0;
            Transactions.KmhTransaction txGoods = string.IsNullOrEmpty(a.SellerUsername) ? null
                : Transactions.KmhTransactionRepository.OpenReturn(a.SellerUsername, Transactions.KmhTxType.Listing,
                    $"auction #{auctionId} voided - goods", auctionId, 0, GoodsLegOf(a), a.EscrowPayloads,
                    Transactions.KmhTransactionRepository.TargetAuction);
            if (!string.IsNullOrEmpty(a.SellerUsername) && txGoods == null) return o;
            Transactions.KmhTransaction txBid = !refundBid ? null
                : Transactions.KmhTransactionRepository.OpenReturn(bidder, Transactions.KmhTxType.AuctionBid,
                    $"auction #{auctionId} voided - bid refund", auctionId, bid, null, null,
                    Transactions.KmhTransactionRepository.TargetAuction);
            if (refundBid && txBid == null) { Transactions.KmhTransactionRepository.Abort(txGoods); return o; }
            DuringVoidForTest?.Invoke();

            lock (_lock)
            {
                bool gone = !_byId.TryGetValue(auctionId, out AuctionDto now) || !ReferenceEquals(now, a);
                if (gone || !string.Equals(a.HighBidder, bidder, StringComparison.Ordinal) || a.CurrentBid != bid)
                {
                    Transactions.KmhTransactionRepository.Abort(txGoods);
                    Transactions.KmhTransactionRepository.Abort(txBid);
                    bidMoved = !gone;
                    return o;
                }
                _byId.Remove(auctionId);
                // Both escrows go back irreversibly below, so the void has to be on disk before either moves.
                if (!SaveToDisk())
                {
                    _byId[auctionId] = a;
                    Transactions.KmhTransactionRepository.Abort(txGoods);
                    Transactions.KmhTransactionRepository.Abort(txBid);
                    return o;
                }
            }

            o.Done = true; o.Seller = a.SellerUsername; o.ItemDefName = a.ItemDefName; o.Qty = a.Qty;
            DeliverAuctionItem(txGoods, a.SellerUsername, a, $"auction #{a.Id} voided by admin");
            Transactions.KmhTransactionRepository.Settle(txGoods);
            o.Affected.Add(a.SellerUsername);
            if (refundBid)
            {
                DepositSilverOnMarker(txBid, bidder, bid, $"auction #{a.Id} voided - bid refund");
                o.Bidder = bidder; o.RefundedBid = bid; o.Affected.Add(bidder);
            }
            Transactions.KmhTransactionRepository.Settle(txBid);
            SaveToDisk();
            Diagnostics.ServerLog.Info($"Auction #{a.Id} voided by admin - {a.Qty}x {a.ItemDefName} returned to {a.SellerUsername}" +
                                       (o.RefundedBid > 0 ? $", {o.RefundedBid} refunded to {o.Bidder}" : ""));
            return o;
        }

        // A seller's escrowed lot plus a high bidder's held silver: both are still that player's value.
        public static long EscrowValueFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long v = 0;
            lock (_lock)
                foreach (AuctionDto a in _byId.Values)
                {
                    if (a == null) continue;
                    if (string.Equals(a.SellerUsername, username, StringComparison.OrdinalIgnoreCase))
                    {
                        long item = 0;
                        if (a.EscrowPayloads != null)
                            foreach (Items.KmhThingPayload p in a.EscrowPayloads)
                                item += Items.KmhItemSafety.GetTrustedMarketValue(p?.DefName ?? "") * Math.Max(1, p?.StackCount ?? 1);
                        if (item <= 0 && a.Qty > 0)
                            item = Items.KmhItemSafety.GetTrustedMarketValue((a.ItemDefName ?? "").Split('|')[0]) * a.Qty;
                        v += item;
                    }
                    if (a.CurrentBid > 0 && string.Equals(a.HighBidder, username, StringComparison.OrdinalIgnoreCase))
                        v += a.CurrentBid;
                }
            return v;
        }

        public static List<long> CollectEndedIds(long now)
        {
            List<long> ended = new List<long>();
            lock (_lock)
                foreach (AuctionDto a in _byId.Values)
                    if (a.EndsUtcTicks > 0 && now >= a.EndsUtcTicks) ended.Add(a.Id);
            return ended;
        }

        public sealed class SettleOutcome
        {
            public bool   Done;
            public bool   Sold;
            public string Winner      = "";
            public string Seller      = "";
            public string ItemDefName = "";
            public int    Qty;
            public long   FinalBid;
            public long   SellerNet;
            public HashSet<string> Affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal static Action BeforeSettleCommitForTest;

        // Fired between a delivery and the Settle that closes its row - the only window a crash can land in.
        internal static Action<string> AtValuePhaseForTest;

        public static SettleOutcome SettleNow(long auctionId)
        {
            SettleOutcome o = new SettleOutcome();
            AuctionDto a;
            lock (_lock) { if (!_byId.TryGetValue(auctionId, out a)) return o; }

            // Separate rows because the legs complete separately: one can reach its vault while the other cannot.
            bool sold = !string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0 && !string.IsNullOrEmpty(a.SellerUsername);
            string goodsTo = sold ? a.HighBidder : a.SellerUsername;
            Transactions.KmhTransaction txGoods = string.IsNullOrEmpty(goodsTo) ? null
                : Transactions.KmhTransactionRepository.OpenReturn(goodsTo, Transactions.KmhTxType.Sale,
                    $"auction #{auctionId} goods", auctionId, 0, GoodsLegOf(a), a.EscrowPayloads,
                    Transactions.KmhTransactionRepository.TargetAuction);
            if (goodsTo != null && txGoods == null)
            {
                Diagnostics.ServerLog.Warn($"Auction #{auctionId}: settlement deferred - what it owes could not be written down.");
                return o;
            }

            // Computed exactly once: a boom debits the house pool, so a second call takes the boost twice.
            Economy.SaleSplit split = sold
                ? Economy.SaleSplit.Compute(a.SellerUsername, a.ItemDefName, a.CurrentBid,
                                            demandDrift: false, worldPayoutEvents: false)
                : null;
            // Taken before the payout is written down, so the recorded figure is the one that will actually be paid.
            split?.CommitBoost($"auction #{auctionId} world-event sale boost");
            long plannedPayout = split?.SellerPayout ?? 0;
            Transactions.KmhTransaction txPayout = plannedPayout <= 0 ? null
                : Transactions.KmhTransactionRepository.OpenReturn(a.SellerUsername, Transactions.KmhTxType.Sale,
                    $"auction #{auctionId} payout", auctionId, plannedPayout, null, null,
                    Transactions.KmhTransactionRepository.TargetAuction);
            if (plannedPayout > 0 && txPayout == null)
            {
                UnwindSplitBoost(split, $"auction #{auctionId} settlement deferred");
                Transactions.KmhTransactionRepository.Abort(txGoods);
                Diagnostics.ServerLog.Warn($"Auction #{auctionId}: settlement deferred - the payout could not be written down.");
                return o;
            }

            // Test seam: the escrow rows open outside the lock, so a second settlement worker can take the auction here.
            BeforeSettleCommitForTest?.Invoke();
            AtValuePhaseForTest?.Invoke("settle-recorded");

            lock (_lock)
            {
                if (!_byId.ContainsKey(auctionId))
                {
                    UnwindSplitBoost(split, $"auction #{auctionId} already settled");
                    Transactions.KmhTransactionRepository.Abort(txGoods);
                    Transactions.KmhTransactionRepository.Abort(txPayout);
                    return o;
                }
                _byId.Remove(auctionId);
                // Irreversible, so the auction leaves disk first or the sweeper settles it again after a restart.
                if (!SaveToDisk())
                {
                    _byId[auctionId] = a;
                    UnwindSplitBoost(split, $"auction #{auctionId} settlement rolled back");
                    Transactions.KmhTransactionRepository.Abort(txGoods);
                    Transactions.KmhTransactionRepository.Abort(txPayout);
                    return o;
                }
            }

            o.Done = true; o.Seller = a.SellerUsername; o.ItemDefName = a.ItemDefName; o.Qty = a.Qty;

            // Posting refuses an empty seller, so this row is malformed and has nobody to pay the sale to.
            if (string.IsNullOrEmpty(a.SellerUsername))
            {
                if (!string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0)
                {
                    DepositLong(a.HighBidder, a.CurrentBid, $"auction #{a.Id} has no seller - bid refunded");
                    o.Affected.Add(a.HighBidder);
                }
                DeliverAuctionItem(txGoods, a.SellerUsername, a, $"auction #{a.Id} has no seller");
                Transactions.KmhTransactionRepository.Settle(txGoods);
                Transactions.KmhTransactionRepository.Settle(txPayout);
                SaveToDisk();
                Diagnostics.ServerLog.Error($"Auction #{a.Id} has no seller - refused to settle. Refunded {a.CurrentBid} " +
                                            $"to '{a.HighBidder}' and held the item in recovery; the row was malformed.");
                return o;
            }

            if (!string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0)
            {
                // Before either delivery: the payout row replays after a crash, but the house and guild taxes would not.
                split.Settle(a.SellerUsername, $"auction #{a.Id} sold", txPayout?.Id ?? txGoods?.Id);
                DeliverAuctionItem(txGoods, a.HighBidder, a, $"auction #{a.Id} won");
                AtValuePhaseForTest?.Invoke("goods-delivered");
                Transactions.KmhTransactionRepository.Settle(txGoods);
                DepositSilverOnMarker(txPayout, a.SellerUsername, split.SellerPayout, $"auction #{a.Id} sold");
                AtValuePhaseForTest?.Invoke("payout-paid");
                Transactions.KmhTransactionRepository.Settle(txPayout);
                o.Sold = true; o.Winner = a.HighBidder; o.FinalBid = a.CurrentBid; o.SellerNet = split.SellerPayout;
                o.Affected.Add(a.HighBidder); o.Affected.Add(a.SellerUsername);
                Diagnostics.ServerLog.Info($"Auction #{a.Id} '{a.ItemDefName} x{a.Qty}' won by {a.HighBidder} for {a.CurrentBid} " +
                                           $"(tax {split.ServerTax}, guild {split.GuildTax})");
            }
            else
            {
                DeliverAuctionItem(txGoods, a.SellerUsername, a, $"auction #{a.Id} unsold");
                AtValuePhaseForTest?.Invoke("unsold-returned");
                Transactions.KmhTransactionRepository.Settle(txGoods);
                Transactions.KmhTransactionRepository.Settle(txPayout);
                o.Affected.Add(a.SellerUsername);
                Diagnostics.ServerLog.Info($"Auction #{a.Id} '{a.ItemDefName} x{a.Qty}' ended with no bids - returned to {a.SellerUsername}");
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseAuctionSettled(new KMH.Sdk.Server.Events.AuctionSettledEvent
            { AuctionId = a.Id, Sold = o.Sold, WinnerUsername = o.Winner, SellerUsername = o.Seller, ItemDefName = o.ItemDefName, Qty = o.Qty, FinalBid = o.FinalBid, SellerNet = o.SellerNet });
            return o;
        }

        // Computing a split already debited the boom boost, so a settlement that then does not happen must put it back.
        private static void UnwindSplitBoost(Economy.SaleSplit split, string note)
        {
            if (split != null && split.PoolBoost > 0)
                Marketplace.MarketplaceStore.ReturnToHousePool(split.PoolBoost, note);
        }

        // Null for a payload auction: those goods ride the payload leg instead of a compact key.
        private static Dictionary<string, int> GoodsLegOf(AuctionDto a)
            => (a.EscrowPayloads != null && a.EscrowPayloads.Count > 0) || a.Qty <= 0 ? null
             : new Dictionary<string, int> { { Util.ItemKey.Compose(a.ItemDefName, a.StuffDefName, a.QualityIndex), a.Qty } };

        private static int OpenAuctionsLocked(string seller)
        {
            int open = 0;
            foreach (AuctionDto a in _byId.Values) if (Eq(a.SellerUsername, seller)) open++;
            return open;
        }

        // The same test that captured the displaced lead above, so the two can only disagree if the lead moved.
        private static bool LeadMatches(AuctionDto a, string user, long amt)
        {
            bool has = !string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0;
            return has ? string.Equals(a.HighBidder, user, StringComparison.Ordinal) && a.CurrentBid == amt
                       : user == null && amt == 0;
        }

        // On the bid row's refund marker, so this and boot recovery return the silver once between them.
        private static void ReturnBid(Transactions.KmhTransaction bidTx)
        {
            if (!Transactions.KmhTransactionRepository.Compensate(bidTx))
                Diagnostics.ServerLog.Error($"Auctions: bid {bidTx?.Id} could not be returned yet - the row stays open and the next boot returns it.");
        }

        private static bool DepositLong(string user, long amount, string note)
        {
            if (Items.KmhPayloadEscrow.DeliverSilver(user, amount, note, "auction silver could not be credited")) return true;
            Diagnostics.ServerLog.Error($"Auctions: {amount} silver for '{user}' could not be credited ({note}) - held in recovery.");
            return false;
        }

        // Cloned whole and then stripped, so a new field reaches the wire by decision rather than by omission.
        private static AuctionDto Clone(AuctionDto a)
        {
            AuctionDto c = a.ShallowClone();
            c.EscrowPayloads = null;
            c.PostTxId = null;
            return c;
        }

        internal static AuctionDto CopyForTest(AuctionDto a) => Clone(a);

        internal static bool Exists(long auctionId) { lock (_lock) return _byId.ContainsKey(auctionId); }

        internal static bool HighBidderIs(long auctionId, string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock)
                return _byId.TryGetValue(auctionId, out AuctionDto a)
                    && string.Equals(a.HighBidder, user, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HoldsPost(string postTxId)
        {
            if (string.IsNullOrEmpty(postTxId)) return false;
            lock (_lock)
                foreach (AuctionDto a in _byId.Values)
                    if (string.Equals(a?.PostTxId, postTxId, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
