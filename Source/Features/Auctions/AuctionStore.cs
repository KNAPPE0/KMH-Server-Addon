using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Auctions.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.Auctions
{
    // Auctions escrow seller items and bidder silver through the server ledger, refunding/settling authoritatively.
    internal static class AuctionStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, AuctionDto> _byId = new Dictionary<long, AuctionDto>();
        private static long _nextId = 1;

        // -- persistence --

        private sealed class PersistedState
        {
            public List<AuctionDto> Auctions { get; set; } = new List<AuctionDto>();
            public long             NextId   { get; set; } = 1;
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.AuctionsFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _byId.Clear();
                if (s.Auctions != null) foreach (AuctionDto a in s.Auctions) if (a != null) _byId[a.Id] = a;
                _nextId = Math.Max(1, s.NextId);
            }
            Diagnostics.ServerLog.Info($"Auctions: loaded {s.Auctions?.Count ?? 0} open auction(s)");
        }

        public static void SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock) { s.Auctions.AddRange(_byId.Values); s.NextId = _nextId; seq = JsonFileStore.NextSequence(); }
            JsonFileStore.Save(KmhDataPaths.AuctionsFile, s, seq);
        }

        // -- snapshot (guild visibility, caller always sees their own) --

        public static AuctionSnapshot BuildSnapshot(string caller)
        {
            string callerGuild = string.IsNullOrEmpty(caller) ? null : Guilds.GuildStore.CurrentGuildOf(caller);
            AuctionSnapshot s = new AuctionSnapshot();
            lock (_lock)
            {
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

        // -- post --

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
            int hours    = durationHours > 0 ? Math.Min(durationHours, cfg.AuctionMaxDurationHours) : cfg.AuctionDefaultDurationHours;

            lock (_lock)
            {
                int open = 0;
                foreach (AuctionDto a in _byId.Values) if (Eq(a.SellerUsername, seller)) open++;
                if (open >= cfg.AuctionMaxOpenPerUser)
                    return (0, $"You already have the max {cfg.AuctionMaxOpenPerUser} open auctions.");
            }

            // Escrow the item from the seller's treasury (outside the store lock - treasury has its own).
            string key = Util.ItemKey.Compose(itemDef, stuff, quality);
            if (!Treasury.TreasuryStore.WithdrawItem(seller, key, qty, note: "auction post escrow"))
                return (0, "Your treasury doesn't have that many to auction.");

            long id, now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                id = _nextId++;
                _byId[id] = new AuctionDto
                {
                    Id                = id,
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
                    Visibility        = string.IsNullOrEmpty(visibility) ? "public" : visibility,
                };
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseAuctionPosted(new KMH.Sdk.Server.Events.AuctionPostedEvent
            { AuctionId = id, SellerUsername = seller, ItemDefName = itemDef, Qty = qty, StartingBid = startingBid, BuyoutSilver = buyout, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            return (id, $"Auction posted: {qty}x, starts at {Util.SilverFmt.Format(startingBid)}.");
        }

        // -- bid --

        public sealed class BidResult
        {
            public bool         Ok;
            public string       Reason = "";
            public AuctionDto   Auction;      // current state after a successful bid
            public bool         Settled;      // buyout hit -> the auction settled immediately
            public string       OutbidUser;   // previous high bidder we refunded (push them a treasury snapshot)
            public SettleOutcome Settlement;  // set when the bid triggered a buyout settle
        }

        // Escrows the new bid up front, then double-checks under the lock (someone may have outbid us in the gap) and
        // refunds if we lost the race - so the bidder's silver is never lost. The previous high bidder is refunded.
        public static BidResult Bid(string bidder, long auctionId, long amount)
        {
            BidResult r = new BidResult();
            if (string.IsNullOrEmpty(bidder) || amount <= 0) { r.Reason = "Bad bid."; return r; }
            amount = Math.Min(amount, int.MaxValue);

            long now = DateTime.UtcNow.Ticks;

            // Cheap pre-check so an obviously-bad bid never touches the treasury.
            lock (_lock)
            {
                if (!_byId.TryGetValue(auctionId, out AuctionDto pa)) { r.Reason = "Auction not found."; return r; }
                if (pa.EndsUtcTicks <= now)               { r.Reason = "This auction has ended.";            return r; }
                if (Eq(pa.SellerUsername, bidder))        { r.Reason = "You can't bid on your own auction."; return r; }
                if (pa.BuyoutSilver > 0 && amount > pa.BuyoutSilver) amount = pa.BuyoutSilver; // never overpay a buyout
                long need = pa.CurrentBid > 0 ? pa.CurrentBid + pa.MinIncrement : pa.StartingBid;
                if (amount < need) { r.Reason = $"Bid must be at least {Util.SilverFmt.Format(need)}."; return r; }
            }

            // Escrow the bid from the bidder's treasury.
            if (!Treasury.TreasuryStore.WithdrawSilver(bidder, (int)amount, note: $"auction #{auctionId} bid"))
            { r.Reason = "Your treasury doesn't have that much silver."; return r; }

            string refundUser = null; long refundAmt = 0;
            lock (_lock)
            {
                now = DateTime.UtcNow.Ticks;
                if (_byId.TryGetValue(auctionId, out AuctionDto a)
                    && a.EndsUtcTicks > now
                    && !Eq(a.SellerUsername, bidder)
                    && amount >= (a.CurrentBid > 0 ? a.CurrentBid + a.MinIncrement : a.StartingBid))
                {
                    refundUser   = a.HighBidder; refundAmt = a.CurrentBid;  // previous high bidder is refunded
                    a.HighBidder = bidder; a.CurrentBid = amount; a.BidCount++;

                    // Anti-snipe: a late bid extends the close so others can respond.
                    long snipe = TimeSpan.FromMinutes(Economy.EconomyConfig.Current.AuctionAntiSnipeMinutes).Ticks;
                    if (a.EndsUtcTicks - now < snipe) a.EndsUtcTicks = now + snipe;

                    if (a.BuyoutSilver > 0 && a.CurrentBid >= a.BuyoutSilver) r.Settled = true;
                    r.Auction = Clone(a);
                    r.Ok = true;
                }
            }

            if (!r.Ok)
            {
                // Lost the race (outbid / ended) - return our escrow, reject.
                Treasury.TreasuryStore.DepositSilver(bidder, (int)amount, note: $"auction #{auctionId} bid returned");
                r.Reason = "Outbid or the auction ended - your silver was returned.";
                return r;
            }

            if (!string.IsNullOrEmpty(refundUser) && refundAmt > 0)
            {
                DepositLong(refundUser, refundAmt, $"auction #{auctionId} outbid refund");
                r.OutbidUser = refundUser;
            }

            Extensibility.KmhEventBus.Instance.RaiseAuctionBid(new KMH.Sdk.Server.Events.AuctionBidEvent
            { AuctionId = auctionId, BidderUsername = bidder, Amount = amount, OutbidUsername = refundUser ?? "" });

            if (r.Settled) r.Settlement = SettleNow(auctionId);
            SaveToDisk();
            return r;
        }

        // -- cancel (seller, only before any bid) --

        public static (bool ok, string reason) Cancel(string seller, long auctionId)
        {
            AuctionDto a;
            lock (_lock)
            {
                if (!_byId.TryGetValue(auctionId, out a)) return (false, "Auction not found.");
                if (!Eq(a.SellerUsername, seller))        return (false, "That isn't your auction.");
                if (!string.IsNullOrEmpty(a.HighBidder))  return (false, "Can't cancel - it already has a bid.");
                _byId.Remove(auctionId);
            }
            Treasury.TreasuryStore.DepositItem(seller, Util.ItemKey.Compose(a.ItemDefName, a.StuffDefName, a.QualityIndex), a.Qty,
                note: $"auction #{auctionId} cancelled");
            SaveToDisk();
            return (true, "Auction cancelled - item returned to your treasury.");
        }

        // -- admin recovery --

        // Every open auction regardless of visibility - for `kmh inspect`/`kmh cancel`, never sent to a normal client.
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
            public string Bidder      = "";   // refunded high bidder, if any
            public long   RefundedBid;
            public string ItemDefName = "";
            public int    Qty;
            public HashSet<string> Affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Admin force-void of a stuck/disputed auction: full undo - the item goes back to the seller and the current
        // high bid (escrowed silver) is refunded to the bidder. No sale, no tax. Returns the affected users so the
        // caller can push treasuries + notices.
        public static VoidOutcome AdminVoid(long auctionId)
        {
            VoidOutcome o = new VoidOutcome();
            AuctionDto a;
            lock (_lock) { if (!_byId.TryGetValue(auctionId, out a)) return o; _byId.Remove(auctionId); }

            o.Done = true; o.Seller = a.SellerUsername; o.ItemDefName = a.ItemDefName; o.Qty = a.Qty;
            string key = Util.ItemKey.Compose(a.ItemDefName, a.StuffDefName, a.QualityIndex);
            Treasury.TreasuryStore.DepositItem(a.SellerUsername, key, a.Qty, note: $"auction #{a.Id} voided by admin");
            o.Affected.Add(a.SellerUsername);
            if (!string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0)
            {
                DepositLong(a.HighBidder, a.CurrentBid, $"auction #{a.Id} voided - bid refund");
                o.Bidder = a.HighBidder; o.RefundedBid = a.CurrentBid; o.Affected.Add(a.HighBidder);
            }
            SaveToDisk();
            Diagnostics.ServerLog.Info($"Auction #{a.Id} voided by admin - {a.Qty}x {a.ItemDefName} returned to {a.SellerUsername}" +
                                       (o.RefundedBid > 0 ? $", {o.RefundedBid} refunded to {o.Bidder}" : ""));
            return o;
        }

        // -- settle (sweeper on end, or buyout) --

        public static List<long> CollectEndedIds(long now)
        {
            List<long> ended = new List<long>();
            lock (_lock)
                foreach (AuctionDto a in _byId.Values)
                    if (a.EndsUtcTicks > 0 && now >= a.EndsUtcTicks) ended.Add(a.Id);
            return ended;
        }

        // Outcome of settling an auction - enough for the caller to push treasuries + send win/sold/no-bid notices.
        public sealed class SettleOutcome
        {
            public bool   Done;          // an auction with this id existed and was settled
            public bool   Sold;          // had a winning bid
            public string Winner      = "";
            public string Seller      = "";
            public string ItemDefName = "";
            public int    Qty;
            public long   FinalBid;
            public long   SellerNet;     // what the seller received (bid - house tax)
            public HashSet<string> Affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Distributes a finished auction (winner gets the item, seller the bid - house tax; no bids -> item returns).
        public static SettleOutcome SettleNow(long auctionId)
        {
            SettleOutcome o = new SettleOutcome();
            AuctionDto a;
            lock (_lock) { if (!_byId.TryGetValue(auctionId, out a)) return o; _byId.Remove(auctionId); }

            o.Done = true; o.Seller = a.SellerUsername; o.ItemDefName = a.ItemDefName; o.Qty = a.Qty;
            string key = Util.ItemKey.Compose(a.ItemDefName, a.StuffDefName, a.QualityIndex);
            if (!string.IsNullOrEmpty(a.HighBidder) && a.CurrentBid > 0)
            {
                Treasury.TreasuryStore.DepositItem(a.HighBidder, key, a.Qty, note: $"auction #{a.Id} won");
                long tax = HouseTax(a.SellerUsername, a.CurrentBid);
                long net = Math.Max(0, a.CurrentBid - tax);
                DepositLong(a.SellerUsername, net, $"auction #{a.Id} sold");
                if (tax > 0) Marketplace.MarketplaceStore.CreditHousePool(tax);
                o.Sold = true; o.Winner = a.HighBidder; o.FinalBid = a.CurrentBid; o.SellerNet = net;
                o.Affected.Add(a.HighBidder); o.Affected.Add(a.SellerUsername);
                Diagnostics.ServerLog.Info($"Auction #{a.Id} '{a.ItemDefName} x{a.Qty}' won by {a.HighBidder} for {a.CurrentBid} (tax {tax})");
            }
            else
            {
                Treasury.TreasuryStore.DepositItem(a.SellerUsername, key, a.Qty, note: $"auction #{a.Id} unsold");
                o.Affected.Add(a.SellerUsername);
                Diagnostics.ServerLog.Info($"Auction #{a.Id} '{a.ItemDefName} x{a.Qty}' ended with no bids - returned to {a.SellerUsername}");
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseAuctionSettled(new KMH.Sdk.Server.Events.AuctionSettledEvent
            { AuctionId = a.Id, Sold = o.Sold, WinnerUsername = o.Winner, SellerUsername = o.Seller, ItemDefName = o.ItemDefName, Qty = o.Qty, FinalBid = o.FinalBid, SellerNet = o.SellerNet });
            return o;
        }

        // -- helpers --

        // House tax on the winning bid: marketplace tax %, reduced by the seller guild's perk, waived during a tax holiday.
        private static long HouseTax(string seller, long amount)
        {
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            if (World.WorldStore.IsTaxHoliday()) return 0;
            int reduction = Guilds.GuildStore.GetMarketplaceTaxReductionPoints(Guilds.GuildStore.CurrentGuildOf(seller));
            int pct = Math.Max(0, cfg.MarketplaceTaxPercent - reduction);
            return (long)Math.Round(amount * (pct / 100.0));
        }

        private static void DepositLong(string user, long amount, string note)
        {
            long rem = amount;
            while (rem > 0) { int chunk = (int)Math.Min(rem, int.MaxValue); Treasury.TreasuryStore.DepositSilver(user, chunk, note); rem -= chunk; }
        }

        private static AuctionDto Clone(AuctionDto a) => new AuctionDto
        {
            Id = a.Id, SellerUsername = a.SellerUsername, SellerTreasuryKey = a.SellerTreasuryKey,
            ItemDefName = a.ItemDefName, StuffDefName = a.StuffDefName, QualityIndex = a.QualityIndex, Qty = a.Qty,
            StartingBid = a.StartingBid, MinIncrement = a.MinIncrement, BuyoutSilver = a.BuyoutSilver,
            CurrentBid = a.CurrentBid, HighBidder = a.HighBidder, BidCount = a.BidCount,
            ListedUtcTicks = a.ListedUtcTicks, EndsUtcTicks = a.EndsUtcTicks, Visibility = a.Visibility,
        };
    }
}
