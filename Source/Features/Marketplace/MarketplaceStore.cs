using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Marketplace.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Marketplace
{
    // Everything moves through treasuries: posting escrows items out of the poster's treasury, buying moves silver
    // buyer->seller and items->buyer, cancel/expire returns the remainder. Persisted to marketplace.json
    internal static class MarketplaceStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, MarketplaceListing> _byId
            = new Dictionary<long, MarketplaceListing>();
        private static long _nextId = 1;

        // Lifetime aggregates - shown in the dialog header.
        private static long _houseSilverPool         = 0;
        private static long _lifetimeTradesCompleted = 0;
        private static long _lifetimeSilverTraded    = 0;
        private static bool _housePoolSeeded         = false; // one-time new-server prime guard (see SeedHousePoolOnce)

        // Build a per-caller snapshot. Filters out guild-only listings that the caller isn't entitled to see - see
        // Guilds.GuildVisibility for
        // the full rules (poster's own guild + allied guilds get visibility;
        // others don't).
        //
        // Special case: caller is ALWAYS shown their own listings regardless of visibility flag (you can manage
        // what you posted even if you somehow leave the guild that scoped it)
        public static MarketplaceSnapshot BuildSnapshot(string callerUsername)
        {
            MarketplaceSnapshot s = new MarketplaceSnapshot();
            // Resolve once outside the per-listing loop - GuildStore lookup is O(1) but still cheaper than M
            // repeats for M listings
            string callerGuild = string.IsNullOrEmpty(callerUsername)
                ? null
                : Guilds.GuildStore.CurrentGuildOf(callerUsername);
            lock (_lock)
            {
                s.HouseSilverPool         = _houseSilverPool;
                s.LifetimeTradesCompleted = _lifetimeTradesCompleted;
                s.LifetimeSilverTraded    = _lifetimeSilverTraded;

                s.Listings = new List<MarketplaceListing>(_byId.Count);
                foreach (MarketplaceListing l in _byId.Values)
                {
                    bool isMine = !string.IsNullOrEmpty(callerUsername) &&
                                  string.Equals(l.SellerUsername, callerUsername, System.StringComparison.OrdinalIgnoreCase);
                    if (!isMine &&
                        !Guilds.GuildVisibility.IsVisibleTo(l.SellerTreasuryKey, l.Visibility, callerGuild, prefetched: true))
                        continue;

                    // Shallow copy so concurrent mutation can't corrupt the serializer's view mid-send
                    s.Listings.Add(new MarketplaceListing
                    {
                        Id                = l.Id,
                        SellerUsername    = l.SellerUsername,
                        SellerTreasuryKey = l.SellerTreasuryKey,
                        ItemDefName       = l.ItemDefName,
                        RemainingQty      = l.RemainingQty,
                        OriginalQty       = l.OriginalQty,
                        UnitPriceSilver   = l.UnitPriceSilver,
                        ListedUtcTicks    = l.ListedUtcTicks,
                        ExpiresUtcTicks   = l.ExpiresUtcTicks,
                        IsAutoListing     = l.IsAutoListing,
                        QualityIndex      = l.QualityIndex,
                        StuffDefName      = l.StuffDefName,
                        Visibility        = l.Visibility,
                    });
                }
            }
            return s;
        }

        // SDK / convenience entry - returns the new listing's id, or 0 on any failure. Use the out-reason overload
        // when you want to tell the user WHY a post was rejected (price floor, listing cap, treasury short)
        public static long Post(
            string sellerUsername, string itemDefName, int qty, int unitPriceSilver,
            string visibility, int expiresInHours = 0, string stuffDefName = "", int qualityIndex = 0)
            => Post(sellerUsername, itemDefName, qty, unitPriceSilver, visibility, expiresInHours, out _, stuffDefName, qualityIndex);

        // Post a listing. Enforces the EconomyConfig knobs (min/max unit price, per-user open-listing cap, listing
        // lifetime). Escrows the items from the seller's treasury. Returns the new listing id, or 0 with a
        // human-readable `reason`.
        public static long Post(
            string sellerUsername,
            string itemDefName,
            int    qty,
            int    unitPriceSilver,
            string visibility,
            int    expiresInHours,
            out string reason,
            string stuffDefName = "",
            int    qualityIndex = 0)
        {
            reason = null;
            if (string.IsNullOrEmpty(sellerUsername)) { reason = "No seller.";        return 0; }
            if (string.IsNullOrEmpty(itemDefName))    { reason = "No item.";          return 0; }
            if (qty <= 0)                             { reason = "Quantity must be > 0."; return 0; }
            qualityIndex = Util.ItemKey.Clamp(qualityIndex);
            stuffDefName = stuffDefName ?? "";

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            if (unitPriceSilver < cfg.MarketplaceMinUnitPrice)
            { reason = $"Minimum unit price is {Util.SilverFmt.Format(cfg.MarketplaceMinUnitPrice)}."; return 0; }
            if (unitPriceSilver > cfg.MarketplaceMaxUnitPrice)
            { reason = $"Maximum unit price is {Util.SilverFmt.Format(cfg.MarketplaceMaxUnitPrice)}."; return 0; }

            // Cap the listing's total value to int range. Buy computes cost as int (UnitPrice * qty); without this a
            // large-but-legal listing (high qty * high unit price) would overflow to a negative cost - i.e. pay the
            // buyer. Every partial buy is <= this total, so guarding here covers Buy too. (Mirrors WantStore.Post.)
            if ((long)unitPriceSilver * qty > int.MaxValue)
            { reason = "That listing's total value is too large - lower the quantity or price."; return 0; }

            // Listing lifetime: the client may request a shorter window, but the config lifetime is the ceiling. 0
            // (unspecified) -> config default
            int lifetimeHours = expiresInHours > 0
                ? Math.Min(expiresInHours, cfg.MarketplaceListingLifetimeHours)
                : cfg.MarketplaceListingLifetimeHours;

            // Per-user open-listing cap. A single client's packets are processed serially (one listener thread per
            // client), so a seller can't race their own cap; counting under the lock is sufficient
            lock (_lock)
            {
                int open = 0;
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, sellerUsername, StringComparison.OrdinalIgnoreCase)) open++;
                if (open >= cfg.MarketplaceMaxOpenListingsPerUser)
                { reason = $"You already have the max {cfg.MarketplaceMaxOpenListingsPerUser} open listings."; return 0; }
            }

            // Escrow the items from the seller's treasury. Cross-feature call - TreasuryStore.WithdrawItem returns
            // false if the treasury doesn't have the qty available
            string escrowKey = Util.ItemKey.Compose(itemDefName, stuffDefName, qualityIndex);
            if (!Treasury.TreasuryStore.WithdrawItem(sellerUsername, escrowKey, qty, note: "marketplace post escrow"))
            {
                reason = "Your treasury doesn't have that many to list.";
                return 0;
            }

            long assignedId;
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                assignedId = _nextId++;
                _byId[assignedId] = new MarketplaceListing
                {
                    Id                = assignedId,
                    SellerUsername    = sellerUsername,
                    SellerTreasuryKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(sellerUsername),
                    ItemDefName       = itemDefName,
                    RemainingQty      = qty,
                    OriginalQty       = qty,
                    UnitPriceSilver   = unitPriceSilver,
                    ListedUtcTicks    = now,
                    ExpiresUtcTicks   = now + TimeSpan.FromHours(lifetimeHours).Ticks,
                    IsAutoListing     = false,
                    QualityIndex      = qualityIndex,
                    StuffDefName      = stuffDefName,
                    Visibility        = string.IsNullOrEmpty(visibility) ? "public" : visibility,
                };
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseMarketplacePost(new KMH.Sdk.Server.Events.MarketplacePostEvent { ListingId = assignedId, SellerUsername = sellerUsername, ItemDefName = itemDefName, Qty = qty, UnitPriceSilver = unitPriceSilver, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            reason = $"Listed {qty}x at {Util.SilverFmt.Format(unitPriceSilver)} each.";
            return assignedId;
        }

        // Server-initiated expiry. No caller validation (the sweeper has no caller). Removes the listing + refunds
        // remaining stock to the seller's treasury. Returns the seller's username so the sweeper can push them a
        // treasury update if they're online
        public static bool ExpireListing(long listingId, out string sellerUsername)
        {
            sellerUsername = null;
            MarketplaceListing listing;
            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                _byId.Remove(listingId);
            }
            sellerUsername = listing.SellerUsername;
            if (listing.RemainingQty > 0 && !string.IsNullOrEmpty(sellerUsername))
            {
                Treasury.TreasuryStore.DepositItem(sellerUsername, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), listing.RemainingQty,
                    note: $"marketplace listing #{listingId} expired");
            }
            SaveToDisk();
            return true;
        }

        // Returns ids of every listing whose ExpiresUtcTicks > 0 and <= now. Used by the periodic sweeper. Doesn't
        // mutate - caller iterates + calls ExpireListing per id (each is brief; no need to hold the store lock
        // across the sweep)
        public static List<long> CollectExpiredIds(long nowTicks)
        {
            List<long> expired = new List<long>();
            lock (_lock)
            {
                foreach (MarketplaceListing l in _byId.Values)
                {
                    if (l.ExpiresUtcTicks > 0 && nowTicks >= l.ExpiresUtcTicks)
                        expired.Add(l.Id);
                }
            }
            return expired;
        }

        // Cancel: returns true on success. Server validates caller is the seller; returns remaining items to
        // seller's treasury
        public static bool Cancel(string callerUsername, long listingId)
        {
            if (string.IsNullOrEmpty(callerUsername)) return false;

            MarketplaceListing listing;
            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                if (!string.Equals(listing.SellerUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                _byId.Remove(listingId);
            }

            // Refund the remaining stock outside the lock (treasury has its own lock).
            if (listing.RemainingQty > 0)
            {
                Treasury.TreasuryStore.DepositItem(callerUsername, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), listing.RemainingQty,
                    note: $"marketplace cancel listing #{listingId}");
            }
            SaveToDisk();
            return true;
        }

        // Reserves the qty under the lock BEFORE any treasury move so two
        // buyers can't both pass the availability check on the same units;
        // rolls the reservation back if the buyer's silver withdrawal fails. sellerUsername is set so the handler
        // can push the seller a snapshot
        public static bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername)
        {
            sellerUsername = null;
            if (string.IsNullOrEmpty(buyerUsername)) return false;
            if (qty <= 0) return false;

            MarketplaceListing listing;
            int cost;
            int qtyToSell;
            bool removedOnReserve;

            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                if (string.Equals(listing.SellerUsername, buyerUsername, StringComparison.OrdinalIgnoreCase))
                    return false; // can't buy your own listing
                qtyToSell = Math.Min(qty, listing.RemainingQty);
                if (qtyToSell <= 0) return false;
                long costL = (long)listing.UnitPriceSilver * qtyToSell;
                if (costL > int.MaxValue) return false; // legacy/tampered listing; Post now caps total to int range
                cost = (int)costL;

                // Reserve the units now, atomically with the availability check.
                listing.RemainingQty -= qtyToSell;
                removedOnReserve = listing.RemainingQty <= 0;
                if (removedOnReserve) _byId.Remove(listingId);
            }
            sellerUsername = listing.SellerUsername;

            // Pull silver from buyer's treasury (outside _lock - TreasuryStore has its own lock). The units are
            // already reserved above
            if (!Treasury.TreasuryStore.WithdrawSilver(buyerUsername, cost, note: $"marketplace buy listing #{listingId}"))
            {
                // Roll the reservation back. Restore the qty on the captured listing object; if our reservation
                // removed it, re-add it (same object, so any other field stays intact). A concurrent Cancel that
                // ran while it was out of the map simply saw "not found" and bailed, so re-adding here can't
                // double-anything
                lock (_lock)
                {
                    listing.RemainingQty += qtyToSell;
                    if (removedOnReserve && !_byId.ContainsKey(listingId))
                        _byId[listingId] = listing;
                }
                sellerUsername = null;
                return false; // insufficient silver
            }

            // --- house tax + guild taxes ---
            // Buyer always pays full `cost`. The seller's payout is reduced by:
            //   1. The marketplace house tax (config %, minus the seller guild's
            //      MarketplaceTaxReduction perk), which accrues to the house pool.
            //   2. The seller guild's own sale tax, which accrues to the guild vault.
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            int basePct   = cfg.MarketplaceTaxPercent;
            int reduction = Guilds.GuildStore.GetMarketplaceTaxReductionPoints(
                                Guilds.GuildStore.CurrentGuildOf(listing.SellerUsername));
            int taxPercent = Math.Max(0, basePct - reduction);
            // World event: a tax holiday waives the house tax entirely.
            if (World.WorldStore.IsTaxHoliday()) taxPercent = 0;
            else if (cfg.DynamicDemandPricingEnabled)
            {
                // Supply/demand drift: in-demand items get a tax rebate (seller keeps more), gluts a surcharge (more
                // flows to the house pool). Closed loop - the buyer's cost is unchanged, only the seller/house split.
                long demand = WantBoard.WantStore.OpenDemandQty(listing.ItemDefName);
                long supply = OpenSupplyQty(listing.ItemDefName);
                long denom  = demand + supply;
                if (denom > 0)
                {
                    double f = (demand - supply) / (double)denom; // +1 = pure demand .. -1 = pure glut
                    taxPercent = Math.Max(0, Math.Min(90, taxPercent - (int)Math.Round(f * cfg.DemandTaxSwingPercent)));
                }
            }
            int houseTax   = (int)Math.Round(cost * (taxPercent / 100.0));
            if (houseTax < 0) houseTax = 0;
            int sellerNet  = cost - houseTax;
            if (sellerNet < 0) sellerNet = 0;

            // World event: market boom/crash (server-wide) + any resource-shortage demand spike on this item scale
            // the seller's payout up or down.
            double payoutMult = World.WorldStore.MarketPayoutMultiplierFor(listing.ItemDefName);
            if (payoutMult != 1.0) sellerNet = (int)Math.Round(sellerNet * payoutMult);
            if (sellerNet < 0) sellerNet = 0;

            // Guild sale tax skims from the seller's net into their guild vault.
            sellerNet = Guilds.GuildStore.ApplyGuildMarketplaceSaleTax(listing.SellerUsername, sellerNet);

            // Commit: credit seller net silver, give items to buyer.
            Treasury.TreasuryStore.DepositSilver(listing.SellerUsername, sellerNet,
                note: $"marketplace sale listing #{listingId} to {buyerUsername}");
            Treasury.TreasuryStore.DepositItem(buyerUsername, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), qtyToSell,
                note: $"marketplace buy listing #{listingId} from {listing.SellerUsername}");

            lock (_lock)
            {
                _houseSilverPool         += houseTax;
                _lifetimeTradesCompleted += 1;
                _lifetimeSilverTraded    += cost;
            }

            // Cross-feature: bump SalesEarned + trade detail (seller) and spend + items (buyer) on the leaderboard.
            PlayerStats.PlayerStatsStore.AddSalesEarned(listing.SellerUsername, sellerNet);
            PlayerStats.PlayerStatsStore.RecordSale(listing.SellerUsername, qtyToSell, cost);
            PlayerStats.PlayerStatsStore.RecordPurchase(buyerUsername, qtyToSell, cost);
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseMarketplaceBuy(new KMH.Sdk.Server.Events.MarketplaceBuyEvent { ListingId = listingId, BuyerUsername = buyerUsername, SellerUsername = sellerUsername, ItemDefName = listing.ItemDefName, QtyBought = qtyToSell, TotalSilverPaid = cost });
            return true;
        }

        /// <summary>Admin sink - drain the house silver pool to a user's treasury. Returns the amount drained.</summary>
        public static long DrainHousePool(string toUsername)
        {
            if (string.IsNullOrEmpty(toUsername)) return 0;
            long drained;
            lock (_lock)
            {
                if (_houseSilverPool <= 0) return 0;
                drained = _houseSilverPool;
                _houseSilverPool = 0;
            }
            // Deposit in int-sized chunks (treasury silver is int-based).
            long remaining = drained;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, int.MaxValue);
                Treasury.TreasuryStore.DepositSilver(toUsername, chunk, note: "marketplace house-pool drain");
                remaining -= chunk;
            }
            Persistence.TransactionLedger.RecordHousePool(credit: false, amount: drained, note: $"house-pool drain to {toUsername}");
            SaveToDisk();
            return drained;
        }

        /// <summary>Current house silver pool balance (read-only).</summary>
        public static long HousePoolBalance()
        {
            lock (_lock) { return _houseSilverPool; }
        }

        /// <summary>Total open listing quantity for an item - the live supply side of the demand/price drift.</summary>
        public static long OpenSupplyQty(string itemDefName)
        {
            if (string.IsNullOrEmpty(itemDefName)) return 0;
            long total = 0;
            lock (_lock)
                foreach (MarketplaceListing l in _byId.Values)
                    if (l != null && string.Equals(l.ItemDefName, itemDefName, StringComparison.OrdinalIgnoreCase))
                        total += Math.Max(0, l.RemainingQty);
            return total;
        }

        /// <summary>
        /// Reserve <paramref name="amount"/> from the house pool atomically - true only if the pool covered it.
        /// Used to fund World Engine quest rewards from tax revenue rather than minting them.
        /// </summary>
        public static bool TryDebitHousePool(long amount)
        {
            if (amount <= 0) return true;
            lock (_lock)
            {
                if (_houseSilverPool < amount) return false;
                _houseSilverPool -= amount;
            }
            SaveToDisk();
            return true;
        }

        /// <summary>Return silver to the house pool (e.g. refunding an unclaimed quest reward).</summary>
        public static void CreditHousePool(long amount)
        {
            if (amount <= 0) return;
            lock (_lock) { _houseSilverPool += amount; }
            Persistence.TransactionLedger.RecordHousePool(credit: true, amount: amount, note: "house-pool credit (tax/refund)");
            SaveToDisk();
        }

        /// <summary>
        /// Prime the house pool exactly once on a brand-new server so the first global quests can pay before any
        /// tax revenue accrues. The seeded flag persists, so a restart (or a drained pool) never re-triggers it -
        /// this is the only injection of new money; everything after is the closed tax loop.
        /// </summary>
        public static void SeedHousePoolOnce(long amount)
        {
            bool seeded = false;
            lock (_lock)
            {
                if (_housePoolSeeded) return;
                _housePoolSeeded = true;
                if (amount > 0) { _houseSilverPool += amount; seeded = true; }
            }
            SaveToDisk(); // persist the flag even when amount is 0, so it stays a true one-shot
            if (seeded) Diagnostics.ServerLog.Info($"Marketplace: seeded house pool with {amount} silver (one-time, new server).");
        }

        // --- persistence ---

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.MarketplaceFile, out PersistedState state) && state != null)
            {
                lock (_lock)
                {
                    _byId.Clear();
                    if (state.Listings != null)
                    {
                        foreach (MarketplaceListing l in state.Listings)
                        {
                            if (l == null || l.Id <= 0) continue;
                            _byId[l.Id] = l;
                        }
                    }
                    _nextId                  = Math.Max(state.NextId, 1);
                    _houseSilverPool         = state.HouseSilverPool;
                    _housePoolSeeded         = state.HousePoolSeeded;
                    _lifetimeTradesCompleted = state.LifetimeTradesCompleted;
                    _lifetimeSilverTraded    = state.LifetimeSilverTraded;
                }
                Diagnostics.ServerLog.Info($"Marketplace: loaded {state.Listings?.Count ?? 0} listing(s) from disk");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Listings                = new List<MarketplaceListing>(_byId.Values);
                state.NextId                  = _nextId;
                state.HouseSilverPool         = _houseSilverPool;
                state.HousePoolSeeded         = _housePoolSeeded;
                state.LifetimeTradesCompleted = _lifetimeTradesCompleted;
                state.LifetimeSilverTraded    = _lifetimeSilverTraded;
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
            }
            JsonFileStore.Save(KmhDataPaths.MarketplaceFile, state, seq);
        }

        private class PersistedState
        {
            public List<MarketplaceListing> Listings              { get; set; } = new List<MarketplaceListing>();
            public long                     NextId                { get; set; } = 1;
            public long                     HouseSilverPool       { get; set; } = 0;
            public bool                     HousePoolSeeded       { get; set; } = false;
            public long                     LifetimeTradesCompleted { get; set; } = 0;
            public long                     LifetimeSilverTraded  { get; set; } = 0;
        }
    }
}
