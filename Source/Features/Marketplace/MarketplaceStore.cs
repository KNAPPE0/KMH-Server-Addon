using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Marketplace.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Marketplace
{
    // Every movement goes through treasuries, so nothing exists outside one while a listing is open.
    internal static class MarketplaceStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, MarketplaceListing> _byId
            = new Dictionary<long, MarketplaceListing>();
        private static long _nextId = 1;

        private static long _houseSilverPool         = 0;
        private static long _lifetimeTradesCompleted = 0;
        private static long _lifetimeSilverTraded    = 0;
        private static bool _housePoolSeeded         = false; // one-time new-server prime guard (see SeedHousePoolOnce)

        // Deliberately coarse and never cached, because being wrong here shows one player another's hidden listing.
        public static HashSet<string> SellersWithGuildOnlyListings()
        {
            HashSet<string> sellers = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                foreach (MarketplaceListing l in _byId.Values)
                    if (l != null && !string.IsNullOrEmpty(l.SellerUsername)
                        && !string.Equals(l.Visibility, Guilds.GuildVisibility.Public, System.StringComparison.OrdinalIgnoreCase))
                        sellers.Add(l.SellerUsername);
            return sellers;
        }

        // A visibility hook is opaque and may answer per caller, so nothing is shared while one is registered.
        internal static string ShareKeyFor(string caller, HashSet<string> guildOnlySellers)
            => ShareKeyForCore(caller, guildOnlySellers, Extensibility.KmhHooks.Instance.HasMarketplaceVisibility);

        // Pure, because registering a hook on the singleton is append-only and would leak across tests.
        internal static string ShareKeyForCore(string caller, HashSet<string> guildOnlySellers, bool visibilityHooks)
        {
            if (visibilityHooks) return null;
            return Guilds.GuildVisibility.SnapshotShareKey(caller, guildOnlySellers);
        }

        // Clones then drops escrow, so a field added later still reaches clients.
        private static MarketplaceListing CopyForWire(MarketplaceListing l)
        {
            MarketplaceListing c = l.ShallowClone();
            c.EscrowPayloads = null;
            return c;
        }

        internal static MarketplaceListing CopyForTest(MarketplaceListing l) => CopyForWire(l);

        public static MarketplaceSnapshot BuildSnapshot(string callerUsername)
        {
            MarketplaceSnapshot s = new MarketplaceSnapshot();
            string callerGuild = string.IsNullOrEmpty(callerUsername)
                ? null
                : Guilds.GuildStore.CurrentGuildOf(callerUsername);
            lock (_lock)
            {
                s.Revision                = Util.KmhSnapshotRevision.Next();
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

                    s.Listings.Add(CopyForWire(l));
                }
            }
            // Outside the lock: a hook is third-party code that may call back into KMH. A seller always sees their own.
            if (Extensibility.KmhHooks.Instance.HasMarketplaceVisibility && s.Listings.Count > 0)
            {
                var kept = new List<MarketplaceListing>(s.Listings.Count);
                foreach (MarketplaceListing l in s.Listings)
                {
                    bool isMine = !string.IsNullOrEmpty(callerUsername) &&
                                  string.Equals(l.SellerUsername, callerUsername, System.StringComparison.OrdinalIgnoreCase);
                    if (isMine || Extensibility.KmhHooks.Instance.CheckMarketplaceVisible(
                                      callerUsername, l.Id, l.SellerUsername, l.ItemDefName))
                        kept.Add(l);
                }
                s.Listings = kept;
            }
            return s;
        }

        // 0 on any failure; the out-reason overload says why.
        public static long Post(
            string sellerUsername, string itemDefName, int qty, int unitPriceSilver,
            string visibility, int expiresInHours = 0, string stuffDefName = "", int qualityIndex = 0)
            => Post(sellerUsername, itemDefName, qty, unitPriceSilver, visibility, expiresInHours, out _, stuffDefName, qualityIndex);

        // Escrows the items out of the seller's treasury; 0 with a reason on refusal.
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
            // Canonical price is milli-silver; callers passing whole silver are converted here.
            int milli = unitPriceMilli > 0 ? unitPriceMilli : (int)System.Math.Min(int.MaxValue, (long)System.Math.Max(0, unitPriceSilver) * 1000);
            if (milli < cfg.MarketplaceMinUnitPriceMilli)
            { reason = $"Minimum unit price is {cfg.MarketplaceMinUnitPrice:0.###} silver."; return 0; }
            if (milli > cfg.MarketplaceMaxUnitPriceMilli)
            { reason = $"Maximum unit price is {cfg.MarketplaceMaxUnitPrice:0.###} silver."; return 0; }
            int priceSilver = (int)System.Math.Round(milli / 1000.0);   // rounded display / old clients
            if (!CheckUnderpricing(itemDefName, sellerUsername, milli, out reason)) return 0;

            // Guards the whole-listing total, so no partial buy can overflow to a negative cost.
            if ((long)milli * qty / 1000 > int.MaxValue)
            { reason = "That listing's total value is too large - lower the quantity or price."; return 0; }

            // A client may request a shorter window, but never a longer one than the config allows.
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

            // Runs outside the lock and before any escrow, so a denial leaves no side effects.
            KMH.Sdk.Server.Hooks.KmhHookVerdict verdict = Extensibility.KmhHooks.Instance.CheckMarketplaceListing(
                new KMH.Sdk.Server.Hooks.KmhMarketplaceListingContext(sellerUsername, itemDefName, qty, milli / 1000.0));
            if (verdict.Denied) { reason = verdict.Reason; return 0; }

            string escrowKey = Util.ItemKey.Compose(itemDefName, stuffDefName, qualityIndex);

            // Written down before the vault is debited: between the debit and the listing reaching disk nothing else owns the goods.
            Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.OpenTake(
                sellerUsername, Transactions.KmhTxType.Listing, $"marketplace post {qty}x", 0, 0,
                new Dictionary<string, int> { { escrowKey, qty } }, null);
            if (post == null) { reason = "The server couldn't record that listing - nothing was taken."; return 0; }

            if (!Treasury.TreasuryStore.WithdrawItemForTxn(sellerUsername, escrowKey, qty, post.TakeMarker, note: "marketplace post escrow"))
            {
                Transactions.KmhTransactionRepository.Abort(post);
                reason = "Your treasury doesn't have that many to list.";
                return 0;
            }

            long assignedId = 0;
            long now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                // Re-checked because escrow released the lock, so a concurrent post could have taken the last slot.
                int open = 0;
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, sellerUsername, StringComparison.OrdinalIgnoreCase)) open++;
                overCap = open >= cfg.MarketplaceMaxOpenListingsPerUser;
                if (!overCap)
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
            }
            if (overCap)
            {
                Items.KmhPayloadEscrow.DeliverCompact(sellerUsername, escrowKey, qty, "marketplace post refund (listing limit reached)", "marketplace post refund could not be returned");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = $"You already have the max {cfg.MarketplaceMaxOpenListingsPerUser} open listings - your items were returned.";
                return 0;
            }
            // The goods already left the treasury, so an unwritten post is undone and the escrow handed straight back.
            if (!SaveToDisk())
            {
                lock (_lock) _byId.Remove(assignedId);
                Items.KmhPayloadEscrow.DeliverCompact(sellerUsername, escrowKey, qty, "marketplace post could not be saved", "marketplace post refund could not be returned");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = "The server couldn't save that listing - your items were returned. Try again shortly.";
                return 0;
            }
            // The listing owns the goods now, so the ledger releases its claim on them.
            Transactions.KmhTransactionRepository.Settle(post);
            Extensibility.KmhEventBus.Instance.RaiseMarketplacePost(new KMH.Sdk.Server.Events.MarketplacePostEvent { ListingId = assignedId, SellerUsername = sellerUsername, ItemDefName = itemDefName, Qty = qty, UnitPriceSilver = unitPriceSilver, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            reason = $"Listed {qty}x at {Util.SilverFmt.Format(unitPriceSilver)} each.";
            return assignedId;
        }

        // Escrows the exact payloads, so a listed item keeps its own state instead of becoming a generic stack.
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
            { reason = $"Minimum unit price is {cfg.MarketplaceMinUnitPrice:0.###} silver."; return 0; }
            if (milli > cfg.MarketplaceMaxUnitPriceMilli)
            { reason = $"Maximum unit price is {cfg.MarketplaceMaxUnitPrice:0.###} silver."; return 0; }
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

            // Id first so the vault can stamp its marker, then the row is written with the exact instances - a rebuilt copy loses their state.
            Transactions.KmhTransaction post = Transactions.KmhTransactionRepository.PrepareTake(
                sellerUsername, Transactions.KmhTxType.Listing, "marketplace post (full state)", 0);
            if (post == null) { reason = "No seller."; return 0; }

            List<Items.KmhThingPayload> escrow = Treasury.TreasuryStore.WithdrawPayloadsForTxn(
                sellerUsername, fingerprint, qty, post.TakeMarker, note: "marketplace post escrow",
                refundMarker: post.RefundMarker);
            if (escrow == null || escrow.Count == 0) { reason = "Your treasury doesn't have that item to list."; return 0; }
            int totalUnits = 0; foreach (Items.KmhThingPayload p in escrow) totalUnits += p.StackCount;
            Items.KmhThingPayload meta = escrow[0];

            if (!Transactions.KmhTransactionRepository.CommitTake(post, escrow))
            {
                Treasury.TreasuryStore.ReturnPendingTake(sellerUsername, post.TakeMarker, post.RefundMarker, escrow,
                                                        "marketplace post could not be recorded");
                reason = "The server couldn't record that listing - your items were returned.";
                return 0;
            }
            // Not optional: a pending take outliving its settled row is handed back to the seller while the listing still holds the goods.
            if (!Treasury.TreasuryStore.ClearPendingTake(sellerUsername, post.TakeMarker))
            {
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post could not be recorded");
                reason = "The server couldn't record that listing - your items were returned.";
                return 0;   // the transaction row stays open, so boot reconciliation still sees this
            }

            // The def is only known after the escrow pop, so a refusal returns it on the transaction's own refund marker - the key recovery would use.
            if (!CheckUnderpricing(meta.DefName, sellerUsername, milli, out reason))
            {
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post rejected - refund");
                Transactions.KmhTransactionRepository.Settle(post);
                return 0;
            }

            long assignedId = 0, now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                int open = 0;
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l.SellerUsername, sellerUsername, StringComparison.OrdinalIgnoreCase)) open++;
                overCap = open >= cfg.MarketplaceMaxOpenListingsPerUser;
                if (!overCap)
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
            }
            if (overCap)
            {
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post refund (listing limit reached)");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = $"You already have the max {cfg.MarketplaceMaxOpenListingsPerUser} open listings - your items were returned.";
                return 0;
            }
            // Payloads cannot be re-created from a def name, so an unwritten listing hands the exact instances back.
            if (!SaveToDisk())
            {
                lock (_lock) _byId.Remove(assignedId);
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post could not be saved");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = "The server couldn't save that listing - your items were returned. Try again shortly.";
                return 0;
            }
            // The listing owns the payloads now, so the ledger releases its claim.
            Transactions.KmhTransactionRepository.Settle(post);
            Extensibility.KmhEventBus.Instance.RaiseMarketplacePost(new KMH.Sdk.Server.Events.MarketplacePostEvent { ListingId = assignedId, SellerUsername = sellerUsername, ItemDefName = meta.DefName, Qty = totalUnits, UnitPriceSilver = unitPriceSilver, Visibility = string.IsNullOrEmpty(visibility) ? "public" : visibility });
            reason = $"Listed {totalUnits}x {meta.DisplayLabel} at {Util.SilverFmt.Format(unitPriceSilver)} each (full state kept).";
            return assignedId;
        }

        // Uses the shared take-loop, so a fill can never be charged for units it did not deliver. Caller holds _lock.
        private static List<Items.KmhThingPayload> PopPayloadsLocked(MarketplaceListing l, int qty)
            => Items.KmhPayloadEscrow.PopUnits(l.EscrowPayloads, qty);

        // An item with no trusted value cannot be judged, so it is allowed.
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
            double price = unitPriceMilli / 1000.0;   // fractional, so sub-silver ratios stay accurate
            double ratio = price / trusted;
            if (cfg.MarketplaceBlockSuspiciousUnderpricedListings && cfg.MarketplaceMinPercentOfTrustedValue > 0 &&
                ratio < cfg.MarketplaceMinFractionOfTrustedValue)
            {
                reason = $"That price ({price:0.###} silver) is only {ratio:P1} of the item's value ({Util.SilverFmt.Format(trusted)}); this server requires at least {cfg.MarketplaceMinPercentOfTrustedValue:0.#}% to prevent near-free transfers.";
                return false;
            }
            if (cfg.MarketplaceWarnBelowTrustedValuePercent > 0 && ratio < cfg.MarketplaceWarnFractionOfTrustedValue)
                Diagnostics.ServerLog.Warn($"Marketplace: {seller} listed {pureDef} at {price:0.###}s = {ratio:P1} of trusted value {trusted}s (underpriced - audit flag).");
            return true;
        }

        // A sellerless listing is malformed, not ownerless: there is nobody to return goods to.
        internal static int RefundableQty(string sellerUsername, int remainingQty)
            => string.IsNullOrEmpty(sellerUsername) ? 0 : Math.Max(0, remainingQty);

        private static void RefundPayloadsTo(string username, MarketplaceListing l, string note)
        {
            if (l.EscrowPayloads == null) return;
            // A deposit that cannot land is held rather than dropped.
            foreach (Items.KmhThingPayload p in l.EscrowPayloads)
                Items.KmhPayloadEscrow.Deliver(username, p, note, note);
            l.EscrowPayloads = null;
        }

        // Server-initiated, so there is no caller to validate; returns the seller so the sweeper can push them.
        public static bool ExpireListing(long listingId, out string sellerUsername)
        {
            sellerUsername = null;
            MarketplaceListing listing;
            lock (_lock) { if (!_byId.TryGetValue(listingId, out listing)) return false; }

            Transactions.KmhTransaction tx = OpenListingReturn(listing, listing.SellerUsername, listingId, "expiry");
            if (tx == null) return false;

            lock (_lock)
            {
                if (!_byId.ContainsKey(listingId)) { Transactions.KmhTransactionRepository.Abort(tx); return false; }
                _byId.Remove(listingId);
                if (!SaveToDisk()) { _byId[listingId] = listing; Transactions.KmhTransactionRepository.Abort(tx); return false; }
            }
            sellerUsername = listing.SellerUsername;
            int expiryRefund = RefundableQty(sellerUsername, listing.RemainingQty);
            if (expiryRefund > 0)
            {
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                    RefundPayloadsTo(sellerUsername, listing, $"marketplace listing #{listingId} expired");
                else
                    Items.KmhPayloadEscrow.DeliverCompact(sellerUsername,
                        Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), expiryRefund,
                        $"marketplace listing #{listingId} expired", "expired listing refund failed");
            }
            Transactions.KmhTransactionRepository.Settle(tx);
            return true;
        }

        // Payload value first, since a compact key only knows the def and would undervalue the goods.
        public static long EscrowValueFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long v = 0;
            lock (_lock)
                foreach (MarketplaceListing l in _byId.Values)
                {
                    if (l == null || !string.Equals(l.SellerUsername, username, StringComparison.OrdinalIgnoreCase)) continue;
                    long item = 0;
                    if (l.EscrowPayloads != null)
                        foreach (Items.KmhThingPayload p in l.EscrowPayloads)
                            item += Items.KmhItemSafety.GetTrustedMarketValue(p?.DefName ?? "") * Math.Max(1, p?.StackCount ?? 1);
                    if (item <= 0 && l.RemainingQty > 0)
                        item = Items.KmhItemSafety.GetTrustedMarketValue((l.ItemDefName ?? "").Split('|')[0]) * l.RemainingQty;
                    v += item;
                }
            return v;
        }

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

        public static bool Cancel(string callerUsername, long listingId)
        {
            if (string.IsNullOrEmpty(callerUsername)) return false;

            MarketplaceListing listing;
            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                if (!string.Equals(listing.SellerUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Written down before the listing is destroyed, or a crash before the refund leaves nothing saying the units were owed.
            Transactions.KmhTransaction tx = OpenListingReturn(listing, callerUsername, listingId, "cancel");
            if (tx == null) return false;

            lock (_lock)
            {
                if (!_byId.ContainsKey(listingId)) { Transactions.KmhTransactionRepository.Abort(tx); return false; }
                _byId.Remove(listingId);
                // Durable before the refund: the other order leaves the listing on disk, so a restart hands out the same units twice.
                if (!SaveToDisk()) { _byId[listingId] = listing; Transactions.KmhTransactionRepository.Abort(tx); return false; }
            }

            // Refunded outside the lock, since the treasury has its own.
            if (listing.RemainingQty > 0)
            {
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                    RefundPayloadsTo(callerUsername, listing, $"marketplace cancel listing #{listingId}");
                else
                    Items.KmhPayloadEscrow.DeliverCompact(callerUsername,
                        Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), listing.RemainingQty,
                        $"marketplace cancel listing #{listingId}", "cancelled listing refund failed");
            }
            Transactions.KmhTransactionRepository.Settle(tx);
            Extensibility.KmhEventBus.Instance.RaiseMarketplaceCancel(new KMH.Sdk.Server.Events.MarketplaceCancelEvent
            { ListingId = listingId, SellerUsername = listing.SellerUsername, RemainingQty = listing.RemainingQty });
            return true;
        }

        // The escrow a listing still holds, recorded against whoever it goes home to.
        private static Transactions.KmhTransaction OpenListingReturn(MarketplaceListing l, string toUser, long listingId, string why)
        {
            if (l == null || string.IsNullOrEmpty(toUser)) return null;
            bool hasPayloads = l.EscrowPayloads != null && l.EscrowPayloads.Count > 0;
            if (!hasPayloads && l.RemainingQty <= 0)
                return Transactions.KmhTransactionRepository.OpenReturn(toUser, Transactions.KmhTxType.Cancellation,
                    $"marketplace {why} #{listingId}", listingId, 0, null, null);

            var items = hasPayloads ? null : new Dictionary<string, int>
                { { Util.ItemKey.Compose(l.ItemDefName, l.StuffDefName, l.QualityIndex), l.RemainingQty } };
            return Transactions.KmhTransactionRepository.OpenReturn(toUser, Transactions.KmhTxType.Cancellation,
                $"marketplace {why} #{listingId}", listingId, 0, items, hasPayloads ? l.EscrowPayloads : null);
        }

        // Bypasses the owner check but still refunds the real seller.
        public static bool AdminCancelListing(long listingId, out string seller, out int refundedQty)
        {
            seller = null; refundedQty = 0;
            MarketplaceListing listing;
            lock (_lock) { if (!_byId.TryGetValue(listingId, out listing)) return false; }

            Transactions.KmhTransaction tx = OpenListingReturn(listing, listing.SellerUsername, listingId, "admin cancel");
            if (tx == null) return false;

            lock (_lock)
            {
                if (!_byId.ContainsKey(listingId)) { Transactions.KmhTransactionRepository.Abort(tx); return false; }
                _byId.Remove(listingId);
                if (!SaveToDisk()) { _byId[listingId] = listing; Transactions.KmhTransactionRepository.Abort(tx); return false; }
            }
            seller = listing.SellerUsername;
            refundedQty = RefundableQty(seller, listing.RemainingQty);
            if (refundedQty > 0)
            {
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                    RefundPayloadsTo(seller, listing, $"admin cancel listing #{listingId}");
                else if (!Treasury.TreasuryStore.DepositItem(seller, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), refundedQty, note: $"admin cancel listing #{listingId}"))
                    // An invalid seller means the goods are held in recovery rather than dropped.
                    Features.Recovery.RecoveryStore.HoldItem(seller, Items.KmhItemSafety.MarkLegacyPartial(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex, refundedQty, listing.ItemDefName),
                        $"admin cancel listing #{listingId}", "seller invalid on refund");
            }
            Transactions.KmhTransactionRepository.Settle(tx);
            Extensibility.KmhEventBus.Instance.RaiseMarketplaceCancel(new KMH.Sdk.Server.Events.MarketplaceCancelEvent
            { ListingId = listingId, SellerUsername = seller, RemainingQty = listing.RemainingQty });
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

        public static bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername)
            => Buy(buyerUsername, listingId, qty, out sellerUsername, out _, out _);

        // Restores a reservation exactly as it was found - quantity, popped payloads, and the row. Call under _lock.
        private static void UnreserveLocked(MarketplaceListing listing, long listingId, int qtyToSell,
                                            List<Items.KmhThingPayload> soldPayloads)
        {
            listing.RemainingQty = Util.KmhSafe.AddSaturating(listing.RemainingQty, qtyToSell);
            if (soldPayloads != null)
            {
                listing.EscrowPayloads = listing.EscrowPayloads ?? new List<Items.KmhThingPayload>();
                listing.EscrowPayloads.AddRange(soldPayloads);
            }
            if (!_byId.ContainsKey(listingId)) _byId[listingId] = listing;   // our own full reserve removed it
        }

        public static bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername,
                               out int boughtQty, out int paidSilver, string opId = "")
        {
            sellerUsername = null;
            boughtQty = 0;
            paidSilver = 0;
            if (string.IsNullOrEmpty(buyerUsername)) return false;
            if (qty <= 0) return false;

            // The in-memory op guard dies on restart; this ledger key does not, so a retry across one cannot buy twice.
            string requestKey = string.IsNullOrEmpty(opId) ? "" : "mkt.buy|" + buyerUsername + "|" + opId;
            if (!string.IsNullOrEmpty(requestKey) && Transactions.KmhTransactionRepository.IsDuplicate(requestKey)) return false;

            MarketplaceListing listing;
            int cost;
            int qtyToSell;
            bool removedOnReserve;
            List<Items.KmhThingPayload> soldPayloads = null;
            string buyerGuild = Guilds.GuildStore.CurrentGuildOf(buyerUsername);

            // Read out first so the checks below run with no lock held, one of them being a third-party callback.
            string peekSeller, peekDef;
            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out MarketplaceListing peek)) return false;
                peekSeller = peek.SellerUsername; peekDef = peek.ItemDefName;
            }

            // Refused before anything is reserved or charged, because there would be nobody to pay.
            if (string.IsNullOrEmpty(peekSeller))
            {
                Diagnostics.ServerLog.Error(
                    $"Marketplace: listing #{listingId} has no seller - purchase refused. The row is malformed; " +
                    $"remove it with 'kmh cancel marketplace {listingId}'.");
                return false;
            }

            // Consulted before the lock, so it is best-effort by contract rather than atomic with the sale.
            if (Extensibility.KmhHooks.Instance.HasMarketplaceVisibility
                && !string.Equals(peekSeller, buyerUsername, StringComparison.OrdinalIgnoreCase)
                && !Extensibility.KmhHooks.Instance.CheckMarketplaceVisible(buyerUsername, listingId, peekSeller, peekDef))
                return false;

            lock (_lock)
            {
                if (!_byId.TryGetValue(listingId, out listing)) return false;
                if (string.Equals(listing.SellerUsername, buyerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                // Enforced on the action too, since a crafted client could target a hidden listing by id.
                if (!Guilds.GuildVisibility.IsVisibleTo(listing.SellerTreasuryKey, listing.Visibility, buyerGuild, prefetched: true))
                    return false;
                qtyToSell = Math.Min(qty, listing.RemainingQty);
                if (qtyToSell <= 0) return false;
                // Floored at 1, so a tiny sub-silver buy cannot round down to a free transfer.
                long unitMilli = listing.UnitPriceMilli > 0 ? listing.UnitPriceMilli : (long)listing.UnitPriceSilver * 1000;
                long costL = Math.Max(1, (long)Math.Round(unitMilli * (double)qtyToSell / 1000.0));
                if (costL > int.MaxValue) return false;
                cost = (int)costL;

                // Reserved atomically with the availability check, and payloads are popped so a rollback can restore them.
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

                // The reservation goes to disk before any silver moves, or a restart re-lists units the buyer already paid for.
                if (!SaveToDisk())
                {
                    UnreserveLocked(listing, listingId, qtyToSell, soldPayloads);
                    Diagnostics.ServerLog.Warn($"Marketplace: refused a buy on listing #{listingId} - the reservation could not be saved.");
                    return false;
                }
            }
            sellerUsername = listing.SellerUsername;

            // Written down before the buyer is charged: from here both the units and the silver are in flight, and a crash without a record loses both.
            Transactions.KmhTransaction tx = Transactions.KmhTransaction.Create(
                buyerUsername, "marketplace", Transactions.KmhTxType.Purchase, $"buy #{listingId} x{qtyToSell}");
            tx.Destination   = "personal_treasury";
            tx.RequestKey    = requestKey;
            tx.TargetId      = listingId;
            tx.EscrowSilver  = cost;
            tx.CounterPlayer = listing.SellerUsername;
            if (soldPayloads != null) tx.CounterPayloads.AddRange(soldPayloads);
            else tx.CounterItems[Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex)] = qtyToSell;
            tx.Advance(Transactions.KmhTxState.Validating);
            tx.Advance(Transactions.KmhTxState.Reserved);
            if (!Transactions.KmhTransactionRepository.Add(tx))
            {
                lock (_lock)
                {
                    UnreserveLocked(listing, listingId, qtyToSell, soldPayloads);
                    SaveToDisk();
                }
                Diagnostics.ServerLog.Warn($"Marketplace: refused a buy on listing #{listingId} - the transaction record could not be saved.");
                sellerUsername = null;
                return false;
            }

            if (!Treasury.TreasuryStore.WithdrawSilverForTxn(buyerUsername, cost, tx.TakeMarker, note: $"marketplace buy listing #{listingId}"))
            {
                // A concurrent cancel may have orphaned the listing, so reserved units go to the seller instead.
                bool refundReservedToSeller = false;
                lock (_lock)
                {
                    if (_byId.ContainsKey(listingId) || removedOnReserve)
                    {
                        UnreserveLocked(listing, listingId, qtyToSell, soldPayloads);
                        // The reservation is on disk, so undoing it in memory alone strands the units on a buy that never happened.
                        SaveToDisk();
                    }
                    else refundReservedToSeller = true;   // listing closed mid-buy by another thread
                }
                if (refundReservedToSeller)
                {
                    string rbNote = $"marketplace listing #{listingId} closed mid-buy - reserved units refunded";
                    if (soldPayloads != null)
                        foreach (Items.KmhThingPayload sp in soldPayloads)
                            Items.KmhPayloadEscrow.Deliver(listing.SellerUsername, sp, rbNote,
                                                           "reserved units could not be returned to the seller");
                    else
                        Items.KmhPayloadEscrow.DeliverCompact(listing.SellerUsername,
                            Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), qtyToSell,
                            rbNote, "reserved units could not be returned to the seller");
                }
                // The units are back with the listing or the seller, so the ledger must not also owe them.
                tx.CounterItems.Clear();
                tx.CounterPayloads.Clear();
                tx.Advance(Transactions.KmhTxState.Rejected);
                Transactions.KmhTransactionRepository.Update(tx);
                sellerUsername = null;
                return false;
            }

            tx.Advance(Transactions.KmhTxState.Approved);
            Transactions.KmhTransactionRepository.Update(tx);

            // One breakdown, so the buyer's charge always equals payout plus taxes and nothing is minted.
            Economy.SaleSplit split = Economy.SaleSplit.Compute(listing.SellerUsername, listing.ItemDefName, cost,
                                                                demandDrift: true, worldPayoutEvents: true);
            split.CommitBoost($"marketplace listing #{listingId} world-event sale boost");
            split.Settle(listing.SellerUsername, $"marketplace sale listing #{listingId}", tx.Id);
            int sellerNet = (int)Math.Min(int.MaxValue, split.SellerPayout);

            // The buyer's silver is already gone, so neither the payout nor the goods may be dropped on failure.
            Items.KmhPayloadEscrow.DeliverSilver(listing.SellerUsername, sellerNet,
                $"marketplace sale listing #{listingId} to {buyerUsername}", "sale payout could not be credited");
            string buyNote = $"marketplace buy listing #{listingId} from {listing.SellerUsername}";
            if (soldPayloads != null)
                foreach (Items.KmhThingPayload sp in soldPayloads)
                    Items.KmhPayloadEscrow.Deliver(buyerUsername, sp, buyNote, "buyer delivery failed");
            else
                Items.KmhPayloadEscrow.DeliverCompact(buyerUsername,
                    Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), qtyToSell, buyNote, "buyer delivery failed");

            // Both sides have been handed their side, so nothing is owed back and the record settles.
            tx.Advance(Transactions.KmhTxState.Delivered);
            tx.Advance(Transactions.KmhTxState.Confirmed);
            Transactions.KmhTransactionRepository.Update(tx);

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
            boughtQty  = qtyToSell;
            paidSilver = cost;
            return true;
        }

        public static long DrainHousePool(string toUsername)
        {
            if (string.IsNullOrEmpty(toUsername)) return 0;
            long drained;
            lock (_lock)
            {
                if (_houseSilverPool <= 0) return 0;
                drained = _houseSilverPool;
                _houseSilverPool = 0;
                // Emptied on disk before the credit, or a restart refills the pool with silver already in someone's vault.
                if (!SaveToDisk()) { _houseSilverPool = drained; return 0; }
            }
            // The pool is already debited, so anything that cannot be credited is held rather than dropped.
            Items.KmhPayloadEscrow.DeliverSilver(toUsername, drained, "marketplace house-pool drain",
                "house-pool drain could not be credited");
            Persistence.TransactionLedger.RecordHousePool(credit: false, amount: drained, note: $"house-pool drain to {toUsername}");
            return drained;
        }

        public static long HousePoolBalance()
        {
            lock (_lock) { return _houseSilverPool; }
        }

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

        public static bool TryDebitHousePool(long amount, string note = "house-pool debit")
        {
            if (amount <= 0) return true;
            lock (_lock)
            {
                if (_houseSilverPool < amount) return false;
                _houseSilverPool -= amount;
                // The caller spends this on the strength of the return value, so an unwritten debit is a refusal.
                if (!SaveToDisk()) { _houseSilverPool += amount; return false; }
            }
            // Spends are ledgered too, or the pool's audit trail could never be reconciled.
            Persistence.TransactionLedger.RecordHousePool(credit: false, amount: amount, note: note);
            return true;
        }

        // The fee already came out of the payer, so a credit that will not stick is parked back to them.
        public static void CreditFeeToHousePool(string payer, long amount, string note)
        {
            if (amount <= 0) return;
            if (TryCreditHousePool(amount, note)) return;
            Recovery.RecoveryStore.HoldSilver(payer, amount, note, "fee could not be credited to the house pool");
        }

        // No other owner to park it with, so a failed write keeps the credit in memory for the save retry rather than dropping it.
        public static void ReturnToHousePool(long amount, string note)
        {
            if (amount <= 0) return;
            lock (_lock) { _houseSilverPool += amount; }
            Persistence.TransactionLedger.RecordHousePool(credit: true, amount: amount, note: note);
            if (!SaveToDisk())
                Diagnostics.ServerLog.Warn($"House pool: returned {amount} ({note}) but the store could not be written - " +
                                           "held in memory for the save retry.");
        }

        // Saved with the balance itself, so a settlement replayed after a crash cannot add the same tax twice.
        private static readonly HashSet<string> _creditMarkers = new HashSet<string>(System.StringComparer.Ordinal);
        private const int MaxCreditMarkers = 512;

        // Credits once per marker. True also when this marker was already applied, since the pool holds it either way.
        public static bool TryCreditHousePoolOnce(string marker, long amount, string note = "house-pool credit")
        {
            if (string.IsNullOrEmpty(marker)) return TryCreditHousePool(amount, note);
            if (amount <= 0) return true;
            lock (_lock)
            {
                if (_creditMarkers.Contains(marker)) return true;
                _houseSilverPool += amount;
                _creditMarkers.Add(marker);
                while (_creditMarkers.Count > MaxCreditMarkers)
                {
                    var it = _creditMarkers.GetEnumerator();
                    if (!it.MoveNext()) break;
                    _creditMarkers.Remove(it.Current);
                }
                if (!SaveToDisk()) { _houseSilverPool -= amount; _creditMarkers.Remove(marker); return false; }
            }
            Persistence.TransactionLedger.RecordHousePool(credit: true, amount: amount, note: note);
            return true;
        }

        // False means the pool holds the silver in memory only; a caller settling value it already took needs to know.
        public static bool TryCreditHousePool(long amount, string note = "house-pool credit (tax/refund)")
        {
            if (amount <= 0) return true;
            lock (_lock) { _houseSilverPool += amount; }
            Persistence.TransactionLedger.RecordHousePool(credit: true, amount: amount, note: note);
            if (SaveToDisk()) return true;
            lock (_lock) { _houseSilverPool -= amount; }
            return false;
        }

        // The only injection of new money on the server; everything after it is the closed tax loop.
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
            if (seeded)
            {
                Persistence.TransactionLedger.RecordHousePool(credit: true, amount: amount, note: "house-pool seed (one-time, new server)");
                Diagnostics.ServerLog.Info($"Marketplace: seeded house pool with {amount} silver (one-time, new server).");
            }
        }

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
                    _creditMarkers.Clear();
                    if (state.CreditMarkers != null) foreach (string m in state.CreditMarkers) _creditMarkers.Add(m);
                }
                Diagnostics.ServerLog.Info($"Marketplace: loaded {state.Listings?.Count ?? 0} listing(s) from disk");
            }
        }

        // Open listings escrow items outside the treasury, so a save reset must drop them or they shelter value.
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

        // False means the change is in memory only; a reservation that never reached disk comes back whole and sells the same goods twice.
        public static bool SaveToDisk()
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
                state.CreditMarkers           = new List<string>(_creditMarkers);
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
            }
            if (!JsonFileStore.Save(KmhDataPaths.MarketplaceFile, state, seq)) return false;
            Maintenance.KmhInvalidation.MarketplaceCommitted();
            return true;
        }

        private class PersistedState
        {
            public List<MarketplaceListing> Listings              { get; set; } = new List<MarketplaceListing>();
            public long                     NextId                { get; set; } = 1;
            public long                     HouseSilverPool       { get; set; } = 0;
            public bool                     HousePoolSeeded       { get; set; } = false;
            public long                     LifetimeTradesCompleted { get; set; } = 0;
            public long                     LifetimeSilverTraded  { get; set; } = 0;
            public List<string>             CreditMarkers         { get; set; } = new List<string>();
        }
    }
}
