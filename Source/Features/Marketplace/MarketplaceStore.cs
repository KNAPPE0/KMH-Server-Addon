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

        // Per-caller snapshot; hides guild-only listings the caller can't see (GuildVisibility rules), but ALWAYS
        // includes the caller's own listings so they can manage what they posted even after leaving that guild.
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
                s.ServerTaxPercent        = Economy.EconomyConfig.Current.MarketplaceTaxPercent;

                s.Listings = new List<MarketplaceListing>(_byId.Count);
                foreach (MarketplaceListing l in _byId.Values)
                {
                    bool isMine = !string.IsNullOrEmpty(callerUsername) &&
                                  string.Equals(l.SellerUsername, callerUsername, System.StringComparison.OrdinalIgnoreCase);
                    if (!isMine &&
                        !Guilds.GuildVisibility.IsVisibleTo(l.SellerTreasuryKey, l.Visibility, callerGuild, prefetched: true))
                        continue;

                    // Shallow copy so concurrent mutation can't corrupt the serializer's view mid-send. EscrowPayloads
                    // (the deep blobs) are NEVER sent - only the display note/fingerprint travel.
                    s.Listings.Add(new MarketplaceListing
                    {
                        Id                = l.Id,
                        SellerUsername    = l.SellerUsername,
                        SellerTreasuryKey = l.SellerTreasuryKey,
                        ItemDefName       = l.ItemDefName,
                        RemainingQty      = l.RemainingQty,
                        OriginalQty       = l.OriginalQty,
                        UnitPriceSilver   = l.UnitPriceSilver,
                        UnitPriceMilli    = l.UnitPriceMilli,
                        ListedUtcTicks    = l.ListedUtcTicks,
                        ExpiresUtcTicks   = l.ExpiresUtcTicks,
                        IsAutoListing     = l.IsAutoListing,
                        QualityIndex      = l.QualityIndex,
                        StuffDefName      = l.StuffDefName,
                        Visibility        = l.Visibility,
                        StateFingerprint  = l.StateFingerprint,
                        StateNote         = l.StateNote,
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
            int    qualityIndex = 0,
            int    unitPriceMilli = -1)   // canonical price in milli-silver; -1 => derive from unitPriceSilver
        {
            reason = null;
            if (string.IsNullOrEmpty(sellerUsername)) { reason = "No seller.";        return 0; }
            if (string.IsNullOrEmpty(itemDefName))    { reason = "No item.";          return 0; }
            if (qty <= 0)                             { reason = "Quantity must be > 0."; return 0; }
            qualityIndex = Util.ItemKey.Clamp(qualityIndex);
            stuffDefName = stuffDefName ?? "";

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            // Canonical price is milli-silver. Old callers (sites/discord/sdk) pass whole silver -> *1000.
            int milli = unitPriceMilli > 0 ? unitPriceMilli : (int)System.Math.Min(int.MaxValue, (long)System.Math.Max(0, unitPriceSilver) * 1000);
            if (milli < cfg.MarketplaceMinUnitPriceMilli)
            { reason = $"Minimum unit price is {(cfg.MarketplaceMinUnitPriceMilli / 1000.0):0.###} silver."; return 0; }
            if (milli > cfg.MarketplaceMaxUnitPriceMilli)
            { reason = $"Maximum unit price is {(cfg.MarketplaceMaxUnitPriceMilli / 1000.0):0.###} silver."; return 0; }
            int priceSilver = (int)System.Math.Round(milli / 1000.0);   // rounded display / old clients
            if (!CheckUnderpricing(itemDefName, sellerUsername, milli, out reason)) return 0;

            // Cap the listing's total value to int range. Buy computes cost = round(milli*qty/1000); guard the biggest
            // possible total (whole listing) so it can't overflow to a negative cost. Every partial buy is <= this.
            if ((long)milli * qty / 1000 > int.MaxValue)
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
                    UnitPriceSilver   = priceSilver,
                    UnitPriceMilli    = milli,
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

        // Post a complex item as a state-preserving listing. Escrows the exact payloads out of the seller's treasury
        // (escrowed as payloads), so the listed item keeps its HP/taint/quality/comp state. Returns
        // the new listing id, or 0 with a reason.
        public static long PostPayload(string sellerUsername, string fingerprint, int qty, int unitPriceSilver,
            string visibility, int expiresInHours, out string reason, int unitPriceMilli = -1)
        {
            reason = null;
            if (string.IsNullOrEmpty(sellerUsername)) { reason = "No seller.";        return 0; }
            if (string.IsNullOrEmpty(fingerprint))    { reason = "No item.";          return 0; }
            if (qty <= 0)                             { reason = "Quantity must be > 0."; return 0; }

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            int milli = unitPriceMilli > 0 ? unitPriceMilli : (int)System.Math.Min(int.MaxValue, (long)System.Math.Max(0, unitPriceSilver) * 1000);
            if (milli < cfg.MarketplaceMinUnitPriceMilli)
            { reason = $"Minimum unit price is {(cfg.MarketplaceMinUnitPriceMilli / 1000.0):0.###} silver."; return 0; }
            if (milli > cfg.MarketplaceMaxUnitPriceMilli)
            { reason = $"Maximum unit price is {(cfg.MarketplaceMaxUnitPriceMilli / 1000.0):0.###} silver."; return 0; }
            int priceSilver = (int)System.Math.Round(milli / 1000.0);
            if ((long)milli * qty / 1000 > int.MaxValue)
            { reason = "That listing's total value is too large - lower the quantity or price."; return 0; }

            int lifetimeHours = expiresInHours > 0
                ? Math.Min(expiresInHours, cfg.MarketplaceListingLifetimeHours)
                : cfg.MarketplaceListingLifetimeHours;

            lock (_lock)
            {
                int open = 0;
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, sellerUsername, StringComparison.OrdinalIgnoreCase)) open++;
                if (open >= cfg.MarketplaceMaxOpenListingsPerUser)
                { reason = $"You already have the max {cfg.MarketplaceMaxOpenListingsPerUser} open listings."; return 0; }
            }

            List<Items.KmhThingPayload> escrow = Treasury.TreasuryStore.WithdrawPayloads(sellerUsername, fingerprint, qty, note: "marketplace post escrow");
            if (escrow == null || escrow.Count == 0) { reason = "Your treasury doesn't have that item to list."; return 0; }
            int totalUnits = 0; foreach (Items.KmhThingPayload p in escrow) totalUnits += p.StackCount;
            Items.KmhThingPayload meta = escrow[0];

            // Underpricing check needs the item's def (only known after the escrow pop). If a strict server blocks it,
            // push the escrow back so nothing is lost.
            if (!CheckUnderpricing(meta.DefName, sellerUsername, milli, out reason))
            {
                foreach (Items.KmhThingPayload p in escrow) Treasury.TreasuryStore.DepositPayload(sellerUsername, p, note: "marketplace post rejected - refund");
                return 0;
            }

            long assignedId, now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                assignedId = _nextId++;
                _byId[assignedId] = new MarketplaceListing
                {
                    Id                = assignedId,
                    SellerUsername    = sellerUsername,
                    SellerTreasuryKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(sellerUsername),
                    ItemDefName       = meta.DefName,
                    StuffDefName      = meta.StuffDefName,
                    QualityIndex      = meta.Quality,
                    RemainingQty      = totalUnits,
                    OriginalQty       = totalUnits,
                    UnitPriceSilver   = priceSilver,
                    UnitPriceMilli    = milli,
                    ListedUtcTicks    = now,
                    ExpiresUtcTicks   = now + TimeSpan.FromHours(lifetimeHours).Ticks,
                    IsAutoListing     = false,
                    Visibility        = string.IsNullOrEmpty(visibility) ? "public" : visibility,
                    EscrowPayloads    = escrow,
                    StateFingerprint  = fingerprint,
                    StateNote         = Items.KmhItemSafety.DescribeStateForLedger(meta),
                };
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseMarketplacePost(new KMH.Sdk.Server.Events.MarketplacePostEvent { ListingId = assignedId, SellerUsername = sellerUsername, ItemDefName = meta.DefName, Qty = totalUnits, UnitPriceSilver = unitPriceSilver, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            reason = $"Listed {totalUnits}x {meta.DisplayLabel} at {Util.SilverFmt.Format(unitPriceSilver)} each (full state kept).";
            return assignedId;
        }

        // Pop up to `qty` escrow payload-units off a listing (under _lock). Blob instances are atomic; metadata splits.
        private static List<Items.KmhThingPayload> PopPayloadsLocked(MarketplaceListing l, int qty)
        {
            List<Items.KmhThingPayload> outp = new List<Items.KmhThingPayload>();
            if (l.EscrowPayloads == null) return outp;
            int rem = qty;
            foreach (Items.KmhThingPayload e in new List<Items.KmhThingPayload>(l.EscrowPayloads))
            {
                if (rem <= 0) break;
                if (e.StackCount <= rem) { outp.Add(e); l.EscrowPayloads.Remove(e); rem -= e.StackCount; }
                else if (string.IsNullOrEmpty(e.ScribeXml) || e.Mergeable) { outp.Add(ClonePayload(e, rem)); e.StackCount -= rem; rem = 0; }
            }
            return outp;
        }

        private static Items.KmhThingPayload ClonePayload(Items.KmhThingPayload p, int stackCount) => new Items.KmhThingPayload
        {
            SchemaVersion = p.SchemaVersion, DefName = p.DefName, StuffDefName = p.StuffDefName, StackCount = stackCount,
            HitPoints = p.HitPoints, MaxHitPoints = p.MaxHitPoints, Quality = p.Quality, Tainted = p.Tainted,
            ScribeXml = p.ScribeXml, Fidelity = p.Fidelity, DisplayLabel = p.DisplayLabel, MarketValue = p.MarketValue,
            Fingerprint = p.Fingerprint, Legacy = p.Legacy, Warnings = new List<string>(p.Warnings ?? new List<string>()),
            Mergeable = p.Mergeable, RotProgressTicks = p.RotProgressTicks,
        };

        // Return every escrowed payload on a listing to a user's treasury (cancel/expire refund).
        // Optional underpricing guard: compare a listing's unit price to the item's trusted server-side value. Off by
        // default (returns true). Warns below WarnBelowTrustedValuePercent; blocks below MinPercentOfTrustedValue when
        // BlockSuspiciousUnderpricedListings is on. Unknown value (0) -> can't judge -> allow.
        private static bool CheckUnderpricing(string itemDefName, string seller, int unitPriceMilli, out string reason)
        {
            reason = null;
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            if (cfg.MarketplaceWarnBelowTrustedValuePercent <= 0 &&
                !(cfg.MarketplaceBlockSuspiciousUnderpricedListings && cfg.MarketplaceMinPercentOfTrustedValue > 0))
                return true;
            Util.ItemKey.Split(itemDefName ?? "", out string pureDef, out _, out _);
            long trusted = Items.KmhItemSafety.GetTrustedMarketValue(pureDef);
            if (trusted <= 0) return true;
            double price = unitPriceMilli / 1000.0;                 // fractional silver, so sub-silver ratios are accurate
            double ratio = price / trusted;
            if (cfg.MarketplaceBlockSuspiciousUnderpricedListings && cfg.MarketplaceMinPercentOfTrustedValue > 0 &&
                ratio < cfg.MarketplaceMinPercentOfTrustedValue)
            {
                reason = $"That price ({price:0.###} silver) is only {ratio:P1} of the item's value ({Util.SilverFmt.Format(trusted)}); this server requires at least {cfg.MarketplaceMinPercentOfTrustedValue:P0} to prevent near-free transfers.";
                return false;
            }
            if (cfg.MarketplaceWarnBelowTrustedValuePercent > 0 && ratio < cfg.MarketplaceWarnBelowTrustedValuePercent)
                Diagnostics.ServerLog.Warn($"Marketplace: {seller} listed {pureDef} at {price:0.###}s = {ratio:P1} of trusted value {trusted}s (underpriced - audit flag).");
            return true;
        }

        private static void RefundPayloadsTo(string username, MarketplaceListing l, string note)
        {
            if (l.EscrowPayloads == null) return;
            // Recovery-safe: a deposit that can't land (invalid/deleted seller) is HELD, never dropped.
            foreach (Items.KmhThingPayload p in l.EscrowPayloads)
                Items.KmhPayloadEscrow.Deliver(username, p, note, note);
            l.EscrowPayloads = null;
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
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                    RefundPayloadsTo(sellerUsername, listing, $"marketplace listing #{listingId} expired");
                else
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
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                    RefundPayloadsTo(callerUsername, listing, $"marketplace cancel listing #{listingId}");
                else
                    Treasury.TreasuryStore.DepositItem(callerUsername, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), listing.RemainingQty,
                        note: $"marketplace cancel listing #{listingId}");
            }
            SaveToDisk();
            return true;
        }

        // Admin cancel: like Cancel but bypasses the owner check and refunds the real seller (recovery-safe). Returns
        // the affected seller so the caller can push a fresh snapshot.
        public static bool AdminCancelListing(long listingId, out string seller, out int refundedQty)
        {
            seller = null; refundedQty = 0;
            MarketplaceListing listing;
            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                _byId.Remove(listingId);
            }
            seller = listing.SellerUsername;
            refundedQty = Math.Max(0, listing.RemainingQty);
            if (refundedQty > 0)
            {
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                    RefundPayloadsTo(seller, listing, $"admin cancel listing #{listingId}");
                else if (!Treasury.TreasuryStore.DepositItem(seller, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), refundedQty, note: $"admin cancel listing #{listingId}"))
                    // Seller invalid (corrupt legacy listing) - hold in recovery, never drop.
                    Features.Recovery.RecoveryStore.HoldItem(seller, Items.KmhItemSafety.MarkLegacyPartial(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex, refundedQty, listing.ItemDefName),
                        $"admin cancel listing #{listingId}", "seller invalid on refund");
            }
            SaveToDisk();
            return true;
        }

        // Admin purge a seller's listings, refunding escrow (recovery-safe). dryRun -> counts only, no change.
        public static (int listings, int items) AdminPurgeSeller(string user, bool dryRun)
        {
            if (string.IsNullOrEmpty(user)) return (0, 0);
            List<MarketplaceListing> mine = new List<MarketplaceListing>();
            lock (_lock)
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, user, StringComparison.OrdinalIgnoreCase)) mine.Add(l);
            int items = 0; foreach (MarketplaceListing l in mine) items += Math.Max(0, l.RemainingQty);
            if (dryRun) return (mine.Count, items);
            foreach (MarketplaceListing l in mine) AdminCancelListing(l.Id, out _, out _);
            return (mine.Count, items);
        }

        // Reserve the qty under the lock BEFORE any treasury move so two buyers can't both pass the availability
        // check on the same units; roll the reservation back if the buyer's silver withdrawal fails.
        public static bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername)
        {
            sellerUsername = null;
            if (string.IsNullOrEmpty(buyerUsername)) return false;
            if (qty <= 0) return false;

            MarketplaceListing listing;
            int cost;
            int qtyToSell;
            bool removedOnReserve;
            List<Items.KmhThingPayload> soldPayloads = null;   // reserved-payloads for a payload listing
            string buyerGuild = Guilds.GuildStore.CurrentGuildOf(buyerUsername); // resolve before the lock

            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                if (string.Equals(listing.SellerUsername, buyerUsername, StringComparison.OrdinalIgnoreCase))
                    return false; // can't buy your own listing
                // Enforce guild-only visibility on the action too - the snapshot hides it, but a crafted client could
                // target a hidden listing by (sequential) id.
                if (!Guilds.GuildVisibility.IsVisibleTo(listing.SellerTreasuryKey, listing.Visibility, buyerGuild, prefetched: true))
                    return false;
                qtyToSell = Math.Min(qty, listing.RemainingQty);
                if (qtyToSell <= 0) return false;
                // Cost from the canonical milli price, rounded like RimWorld (1000 milli = 1 silver). Old listings
                // with no milli fall back to whole silver * 1000. Floor at 1 so a tiny sub-silver buy can't round to a
                // free transfer.
                long unitMilli = listing.UnitPriceMilli > 0 ? listing.UnitPriceMilli : (long)listing.UnitPriceSilver * 1000;
                long costL = Math.Max(1, (long)Math.Round(unitMilli * (double)qtyToSell / 1000.0));
                if (costL > int.MaxValue) return false; // legacy/tampered listing; Post now caps total to int range
                cost = (int)costL;

                // Reserve the units now, atomically with the availability check. For a payload listing, pop the exact
                // escrow payloads here too so a rollback can put them back.
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                {
                    soldPayloads = PopPayloadsLocked(listing, qtyToSell);
                    qtyToSell = 0; foreach (Items.KmhThingPayload sp in soldPayloads) qtyToSell += sp.StackCount;
                    if (qtyToSell <= 0) return false;
                    cost = (int)Math.Max(1, Math.Round(unitMilli * (double)qtyToSell / 1000.0));
                }
                listing.RemainingQty -= qtyToSell;
                removedOnReserve = listing.RemainingQty <= 0 && (listing.EscrowPayloads == null || listing.EscrowPayloads.Count == 0);
                if (removedOnReserve) _byId.Remove(listingId);
            }
            sellerUsername = listing.SellerUsername;

            // Pull silver from buyer's treasury (outside _lock - TreasuryStore has its own lock). The units are
            // already reserved above
            if (!Treasury.TreasuryStore.WithdrawSilver(buyerUsername, cost, note: $"marketplace buy listing #{listingId}"))
            {
                // Roll the reservation back: restore the qty on the captured listing (re-add if our reservation removed it -
                // same object, fields intact). A concurrent Cancel saw "not found" and bailed, so this can't double anything.
                lock (_lock)
                {
                    listing.RemainingQty += qtyToSell;
                    if (soldPayloads != null)   // put the reserved escrow payloads back
                    {
                        listing.EscrowPayloads = listing.EscrowPayloads ?? new List<Items.KmhThingPayload>();
                        listing.EscrowPayloads.AddRange(soldPayloads);
                    }
                    if (removedOnReserve && !_byId.ContainsKey(listingId))
                        _byId[listingId] = listing;
                }
                sellerUsername = null;
                return false; // insufficient silver
            }

            // One authoritative breakdown: buyer charge == seller payout + server tax + guild tax (world boom/crash
            // legs are funded by / returned to the house pool, never minted or vanished).
            Economy.SaleSplit split = Economy.SaleSplit.Compute(listing.SellerUsername, listing.ItemDefName, cost,
                                                                demandDrift: true, worldPayoutEvents: true);
            split.Settle(listing.SellerUsername, $"marketplace sale listing #{listingId}");
            int sellerNet = (int)Math.Min(int.MaxValue, split.SellerPayout);

            // Commit: credit seller net silver, give items to buyer. Payload listings deliver the exact captured
            // items into the buyer's treasury (state preserved); compact listings use the def+count path.
            Treasury.TreasuryStore.DepositSilver(listing.SellerUsername, sellerNet,
                note: $"marketplace sale listing #{listingId} to {buyerUsername}");
            if (soldPayloads != null)
                foreach (Items.KmhThingPayload sp in soldPayloads)
                    Treasury.TreasuryStore.DepositPayload(buyerUsername, sp, note: $"marketplace buy listing #{listingId} from {listing.SellerUsername}");
            else
                Treasury.TreasuryStore.DepositItem(buyerUsername, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), qtyToSell,
                    note: $"marketplace buy listing #{listingId} from {listing.SellerUsername}");

            lock (_lock)
            {
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
        public static void CreditHousePool(long amount, string note = "house-pool credit (tax/refund)")
        {
            if (amount <= 0) return;
            lock (_lock) { _houseSilverPool += amount; }
            Persistence.TransactionLedger.RecordHousePool(credit: true, amount: amount, note: note);
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

        // Save-reset: a seller's open listings escrow items OUTSIDE the treasury, so a save-reset must drop them too or
        // they shelter value. Purge burns them (the reset burns the treasury alongside). HasSellerListings gates the
        // reset's "nothing to clear" early-out.
        public static bool HasSellerListings(string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock)
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, user, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static int PurgeSeller(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            List<long> ids = new List<long>();
            lock (_lock)
            {
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, user, StringComparison.OrdinalIgnoreCase)) ids.Add(l.Id);
                foreach (long id in ids) _byId.Remove(id);
            }
            if (ids.Count > 0) SaveToDisk();
            return ids.Count;
        }

        // Season reset: clear all listings, the house pool, and lifetime tallies (re-seeds on next boot).
        public static void ClearForNewSeason()
        {
            lock (_lock)
            {
                _byId.Clear();
                _nextId = 1;
                _houseSilverPool = 0;
                _lifetimeTradesCompleted = 0;
                _lifetimeSilverTraded = 0;
                _housePoolSeeded = false;
            }
            SaveToDisk();
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
