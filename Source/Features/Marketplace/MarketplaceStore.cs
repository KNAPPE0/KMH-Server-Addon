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
        private static bool _housePoolSeeded         = false;

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
            c.PostTxId = null;
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
                s.ServerTaxPercent        = Economy.EconomyConfig.Current.Marketplace.TaxPercent;

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

        public static long Post(
            string sellerUsername, string itemDefName, int qty, int unitPriceSilver,
            string visibility, int expiresInHours = 0, string stuffDefName = "", int qualityIndex = 0)
            => Post(sellerUsername, itemDefName, qty, unitPriceSilver, visibility, expiresInHours, out _, stuffDefName, qualityIndex);

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
            int milli = unitPriceMilli > 0 ? unitPriceMilli : (int)System.Math.Min(int.MaxValue, (long)System.Math.Max(0, unitPriceSilver) * 1000);
            if (milli < cfg.Marketplace.MinUnitPriceMilli)
            { reason = $"Minimum unit price is {cfg.Marketplace.MinUnitPrice:0.###} silver."; return 0; }
            if (milli > cfg.Marketplace.MaxUnitPriceMilli)
            { reason = $"Maximum unit price is {cfg.Marketplace.MaxUnitPrice:0.###} silver."; return 0; }
            int priceSilver = (int)System.Math.Round(milli / 1000.0);   // rounded display / old clients
            if (!CheckUnderpricing(itemDefName, sellerUsername, milli, out reason)) return 0;

            // Guards the whole-listing total, so no partial buy can overflow to a negative cost.
            if ((long)milli * qty / 1000 > int.MaxValue)
            { reason = "That listing's total value is too large - lower the quantity or price."; return 0; }

            // A client may request a shorter window, but never a longer one than the config allows.
            int lifetimeHours = expiresInHours > 0
                ? Math.Min(expiresInHours, cfg.Marketplace.ListingLifetimeHours)
                : cfg.Marketplace.ListingLifetimeHours;

            lock (_lock)
            {
                if (OpenListingsLocked(sellerUsername) >= cfg.Marketplace.MaxOpenListingsPerUser)
                { reason = $"You already have the max {cfg.Marketplace.MaxOpenListingsPerUser} open listings."; return 0; }
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
                overCap = OpenListingsLocked(sellerUsername) >= cfg.Marketplace.MaxOpenListingsPerUser;
                if (!overCap)
                {
                    assignedId = _nextId++;
                    _byId[assignedId] = new MarketplaceListing
                    {
                        Id                = assignedId,
                        PostTxId          = post.Id,
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
                        Visibility        = Guilds.GuildVisibility.Normalize(visibility),
                    };
                }
            }
            if (overCap)
            {
                RefundPostOnMarker(post, sellerUsername, escrowKey, qty, "marketplace post refund (listing limit reached)");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = $"You already have the max {cfg.Marketplace.MaxOpenListingsPerUser} open listings - your items were returned.";
                return 0;
            }
            // The goods already left the treasury, so an unwritten post is undone and the escrow handed straight back.
            if (!SaveToDisk())
            {
                lock (_lock) _byId.Remove(assignedId);
                RefundPostOnMarker(post, sellerUsername, escrowKey, qty, "marketplace post could not be saved");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = "The server couldn't save that listing - your items were returned. Try again shortly.";
                return 0;
            }
            // The listing owns the goods now, so the ledger releases its claim on them.
            AtStorePhaseForTest?.Invoke("post-listed");
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
            if (milli < cfg.Marketplace.MinUnitPriceMilli)
            { reason = $"Minimum unit price is {cfg.Marketplace.MinUnitPrice:0.###} silver."; return 0; }
            if (milli > cfg.Marketplace.MaxUnitPriceMilli)
            { reason = $"Maximum unit price is {cfg.Marketplace.MaxUnitPrice:0.###} silver."; return 0; }
            int priceSilver = (int)System.Math.Round(milli / 1000.0);
            if ((long)milli * qty / 1000 > int.MaxValue)
            { reason = "That listing's total value is too large - lower the quantity or price."; return 0; }

            int lifetimeHours = expiresInHours > 0
                ? Math.Min(expiresInHours, cfg.Marketplace.ListingLifetimeHours)
                : cfg.Marketplace.ListingLifetimeHours;

            lock (_lock)
            {
                if (OpenListingsLocked(sellerUsername) >= cfg.Marketplace.MaxOpenListingsPerUser)
                { reason = $"You already have the max {cfg.Marketplace.MaxOpenListingsPerUser} open listings."; return 0; }
            }

            // Id first so the vault can stamp its marker; the row then holds the exact instances, as a rebuilt copy loses state.
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
            // False only for a missing owner or marker: a refused save keeps the clear in memory for the save retry.
            if (!Treasury.TreasuryStore.ClearPendingTake(sellerUsername, post.TakeMarker))
            {
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post could not be recorded");
                reason = "The server couldn't record that listing - your items were returned.";
                return 0;   // the transaction row stays open, so boot reconciliation still sees this
            }

            // The def is known only after the pop, so a refusal returns it on the row's refund marker, the key recovery would use.
            if (!CheckUnderpricing(meta.DefName, sellerUsername, milli, out reason))
            {
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post rejected - refund");
                Transactions.KmhTransactionRepository.Settle(post);
                return 0;
            }
            // The same veto the compact path asks, once the def is known; a gear listing is still a listing.
            KMH.Sdk.Server.Hooks.KmhHookVerdict vetoed = Extensibility.KmhHooks.Instance.CheckMarketplaceListing(
                new KMH.Sdk.Server.Hooks.KmhMarketplaceListingContext(sellerUsername, meta.DefName, totalUnits, milli / 1000.0));
            if (vetoed.Denied)
            {
                Treasury.TreasuryStore.DepositEscrowOnce(sellerUsername, post.RefundMarker, 0, null, escrow,
                                                        "marketplace post vetoed - refund");
                Transactions.KmhTransactionRepository.Settle(post);
                reason = vetoed.Reason;
                return 0;
            }

            long assignedId = 0, now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                overCap = OpenListingsLocked(sellerUsername) >= cfg.Marketplace.MaxOpenListingsPerUser;
                if (!overCap)
                {
                    assignedId = _nextId++;
                    _byId[assignedId] = new MarketplaceListing
                    {
                        Id                = assignedId,
                        PostTxId          = post.Id,
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
                        Visibility        = Guilds.GuildVisibility.Normalize(visibility),
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
                reason = $"You already have the max {cfg.Marketplace.MaxOpenListingsPerUser} open listings - your items were returned.";
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

        // Rounded up, never to nearest: 1.499 silver bought singly would cost 1. Floored at 1, so a sub-silver buy is never free.
        internal static long CostOf(long unitMilli, int qty) => Math.Max(1, (Math.Max(0, unitMilli) * Math.Max(0, qty) + 999) / 1000);

        private static int OpenListingsLocked(string sellerUsername)
        {
            int open = 0;
            foreach (MarketplaceListing l in _byId.Values)
                if (string.Equals(l.SellerUsername, sellerUsername, StringComparison.OrdinalIgnoreCase)) open++;
            return open;
        }

        // Uses the shared take-loop, so a fill can never be charged for units it did not deliver. Caller holds _lock.
        private static List<Items.KmhThingPayload> PopPayloadsLocked(MarketplaceListing l, int qty)
            => Items.KmhPayloadEscrow.PopUnits(l.EscrowPayloads, qty);

        // An item with no trusted value cannot be judged, so it is allowed.
        private static bool CheckUnderpricing(string itemDefName, string seller, int unitPriceMilli, out string reason)
        {
            reason = null;
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            if (cfg.Marketplace.WarnBelowTrustedValuePercent <= 0 &&
                !(cfg.Marketplace.BlockSuspiciousUnderpricedListings && cfg.Marketplace.MinPercentOfTrustedValue > 0))
                return true;
            Util.ItemKey.Split(itemDefName ?? "", out string pureDef, out _, out _);
            long trusted = Items.KmhItemSafety.GetTrustedMarketValue(pureDef);
            if (trusted <= 0) return true;
            double price = unitPriceMilli / 1000.0;   // fractional, so sub-silver ratios stay accurate
            double ratio = price / trusted;
            if (cfg.Marketplace.BlockSuspiciousUnderpricedListings && cfg.Marketplace.MinPercentOfTrustedValue > 0 &&
                ratio < cfg.Marketplace.MinFractionOfTrustedValue)
            {
                reason = $"That price ({price:0.###} silver) is only {ratio:P1} of the item's value ({Util.SilverFmt.Format(trusted)}); this server requires at least {cfg.Marketplace.MinPercentOfTrustedValue:0.#}% to prevent near-free transfers.";
                return false;
            }
            if (cfg.Marketplace.WarnBelowTrustedValuePercent > 0 && ratio < cfg.Marketplace.WarnFractionOfTrustedValue)
                Diagnostics.ServerLog.Warn($"Marketplace: {seller} listed {pureDef} at {price:0.###}s = {ratio:P1} of trusted value {trusted}s (underpriced - audit flag).");
            return true;
        }

        // A sellerless listing is malformed, not ownerless: there is nobody to return goods to.
        internal static int RefundableQty(string sellerUsername, int remainingQty)
            => string.IsNullOrEmpty(sellerUsername) ? 0 : Math.Max(0, remainingQty);

        // Recovery replays an unsettled row on its markers, so the live refund must stamp the same one or a crash
        // before Settle hands the goods back a second time.
        internal static void RefundPostOnMarker(Transactions.KmhTransaction post, string seller, string key, int qty, string note)
        {
            if (post != null && qty > 0
                && Treasury.TreasuryStore.DepositEscrowOnce(seller, post.RefundMarker, 0,
                                                           new Dictionary<string, int> { { key, qty } }, null, note))
                return;
            Items.KmhPayloadEscrow.DeliverCompact(seller, key, qty, note, "marketplace post refund could not be returned");
        }

        // A return row's legs are exactly what recovery would pay, so they are delivered on its counter marker.
        // The unmarked helpers below are only the fallback for a vault that refuses the marked deposit.
        private static void ReturnListingEscrow(Transactions.KmhTransaction tx, string user, MarketplaceListing l,
                                                int qty, string note, string failNote)
        {
            bool hasPayloads = l.EscrowPayloads != null && l.EscrowPayloads.Count > 0;
            if (!hasPayloads && qty <= 0) return;
            if (tx != null && (tx.CounterItems?.Count > 0 || tx.CounterPayloads?.Count > 0)
                && Treasury.TreasuryStore.DepositEscrowOnce(user, tx.CounterMarker, 0, tx.CounterItems, tx.CounterPayloads, note))
            {
                l.EscrowPayloads = null;
                return;
            }
            if (hasPayloads) RefundPayloadsTo(user, l, note);
            else Items.KmhPayloadEscrow.DeliverCompact(user,
                     Util.ItemKey.Compose(l.ItemDefName, l.StuffDefName, l.QualityIndex), qty, note, failNote);
        }

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
            => ExpireListing(listingId, out sellerUsername, out _, out _);

        public static bool ExpireListing(long listingId, out string sellerUsername, out string itemDefName, out int returnedQty)
        {
            sellerUsername = null; itemDefName = null; returnedQty = 0;
            Transactions.KmhTransaction tx = CloseWithReturn(listingId, _ => true, "expiry", "expiry-recorded", out MarketplaceListing listing, out int remaining);
            if (tx == null) return false;
            sellerUsername = listing.SellerUsername;
            itemDefName = listing.ItemDefName;
            int expiryRefund = RefundableQty(sellerUsername, remaining);
            returnedQty = expiryRefund;
            if (expiryRefund > 0)
                ReturnListingEscrow(tx, sellerUsername, listing, expiryRefund,
                                    $"marketplace listing #{listingId} expired", "expired listing refund failed");
            AtStorePhaseForTest?.Invoke("expire-returned");
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

            Transactions.KmhTransaction tx = CloseWithReturn(listingId,
                l => string.Equals(l.SellerUsername, callerUsername, StringComparison.OrdinalIgnoreCase),
                "cancel", "cancel-recorded", out MarketplaceListing listing, out int remaining);
            if (tx == null) return false;

            // Refunded outside the lock, since the treasury has its own.
            if (remaining > 0)
                ReturnListingEscrow(tx, callerUsername, listing, remaining,
                                    $"marketplace cancel listing #{listingId}", "cancelled listing refund failed");
            AtStorePhaseForTest?.Invoke("cancel-returned");
            Transactions.KmhTransactionRepository.Settle(tx);
            Extensibility.KmhEventBus.Instance.RaiseMarketplaceCancel(new KMH.Sdk.Server.Events.MarketplaceCancelEvent
            { ListingId = listingId, SellerUsername = listing.SellerUsername, RemainingQty = remaining });
            return true;
        }

        internal static bool Exists(long listingId) { lock (_lock) return _byId.ContainsKey(listingId); }

        internal static bool HoldsPost(string postTxId)
        {
            if (string.IsNullOrEmpty(postTxId)) return false;
            lock (_lock)
                foreach (MarketplaceListing l in _byId.Values)
                    if (string.Equals(l?.PostTxId, postTxId, StringComparison.Ordinal)) return true;
            return false;
        }

        // The escrow a listing still holds, recorded against whoever it goes home to, from a snapshot taken under the lock.
        private static Transactions.KmhTransaction OpenListingReturn(MarketplaceListing l, string toUser, long listingId, string why,
                                                                     int qty, List<Items.KmhThingPayload> payloads)
        {
            if (l == null || string.IsNullOrEmpty(toUser)) return null;
            bool hasPayloads = payloads != null && payloads.Count > 0;
            if (!hasPayloads && qty <= 0)
                return Transactions.KmhTransactionRepository.OpenReturn(toUser, Transactions.KmhTxType.Cancellation,
                    $"marketplace {why} #{listingId}", listingId, 0, null, null, Transactions.KmhTransactionRepository.TargetListing);

            var items = hasPayloads ? null : new Dictionary<string, int>
                { { Util.ItemKey.Compose(l.ItemDefName, l.StuffDefName, l.QualityIndex), qty } };
            return Transactions.KmhTransactionRepository.OpenReturn(toUser, Transactions.KmhTxType.Cancellation,
                $"marketplace {why} #{listingId}", listingId, 0, items, hasPayloads ? payloads : null,
                Transactions.KmhTransactionRepository.TargetListing);
        }

        // Removes the listing only if no purchase reserved or released units since the return was recorded, or a buy in
        // that gap is refunded as well; on a change the record is aborted and taken again.
        private static Transactions.KmhTransaction CloseWithReturn(long listingId, Func<MarketplaceListing, bool> mayClose, string why,
                                                                   string recordedPhase, out MarketplaceListing closed, out int qty)
        {
            closed = null; qty = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                MarketplaceListing listing;
                int snapQty;
                long snapChanges;
                List<Items.KmhThingPayload> snapPayloads;
                lock (_lock)
                {
                    if (!_byId.TryGetValue(listingId, out listing) || !mayClose(listing)) return null;
                    snapQty = listing.RemainingQty;
                    snapChanges = listing.Changes;
                    snapPayloads = listing.EscrowPayloads == null ? null : new List<Items.KmhThingPayload>(listing.EscrowPayloads);
                }

                // Recorded before the listing goes, or a crash before the refund leaves no record that the units were owed.
                Transactions.KmhTransaction tx = OpenListingReturn(listing, listing.SellerUsername, listingId, why, snapQty, snapPayloads);
                if (tx == null) return null;
                if (recordedPhase != null) AtStorePhaseForTest?.Invoke(recordedPhase);

                lock (_lock)
                {
                    bool unchanged = _byId.TryGetValue(listingId, out MarketplaceListing now) && ReferenceEquals(now, listing)
                                     && now.Changes == snapChanges;
                    if (!unchanged)
                    {
                        Transactions.KmhTransactionRepository.Abort(tx);
                        if (!_byId.ContainsKey(listingId)) return null;
                        continue;
                    }
                    _byId.Remove(listingId);
                    // Durable before the refund, or the listing stays on disk and a restart hands out the same units twice.
                    if (!SaveToDisk()) { _byId[listingId] = listing; Transactions.KmhTransactionRepository.Abort(tx); return null; }
                }
                closed = listing;
                qty = snapQty;
                return tx;
            }
            return null;
        }

        // Bypasses the owner check but still refunds the real seller.
        public static bool AdminCancelListing(long listingId, out string seller, out int refundedQty)
        {
            seller = null; refundedQty = 0;
            Transactions.KmhTransaction tx = CloseWithReturn(listingId, _ => true, "admin cancel", null,
                                                             out MarketplaceListing listing, out int remaining);
            if (tx == null) return false;
            seller = listing.SellerUsername;
            refundedQty = RefundableQty(seller, remaining);
            if (refundedQty > 0)
            {
                bool hasPayloads = listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0;
                if (tx != null && (tx.CounterItems?.Count > 0 || tx.CounterPayloads?.Count > 0)
                    && Treasury.TreasuryStore.DepositEscrowOnce(seller, tx.CounterMarker, 0, tx.CounterItems, tx.CounterPayloads,
                                                               $"admin cancel listing #{listingId}"))
                    listing.EscrowPayloads = null;
                else if (hasPayloads)
                    RefundPayloadsTo(seller, listing, $"admin cancel listing #{listingId}");
                else if (!Treasury.TreasuryStore.DepositItem(seller, Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex), refundedQty, note: $"admin cancel listing #{listingId}"))
                    // An invalid seller means the goods are held in recovery rather than dropped.
                    Features.Recovery.RecoveryStore.HoldItem(seller, Items.KmhItemSafety.MarkLegacyPartial(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex, refundedQty, listing.ItemDefName),
                        $"admin cancel listing #{listingId}", "seller invalid on refund");
            }
            Transactions.KmhTransactionRepository.Settle(tx);
            Extensibility.KmhEventBus.Instance.RaiseMarketplaceCancel(new KMH.Sdk.Server.Events.MarketplaceCancelEvent
            { ListingId = listingId, SellerUsername = seller, RemainingQty = remaining });
            return true;
        }

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

        // Saved with the listing change, so the two never disagree about whether the units left; the ledger row is
        // written afterwards, and this record covers the gap between them.
        internal class PendingReserve
        {
            [Newtonsoft.Json.JsonProperty("tx_id")]     public string TxId    { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("listing_id")] public long  ListingId { get; set; }
            [Newtonsoft.Json.JsonProperty("seller")]    public string Seller   { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("item_key")]  public string ItemKey  { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("qty")]       public int    Qty      { get; set; }
            [Newtonsoft.Json.JsonProperty("payloads")]  public List<Items.KmhThingPayload> Payloads { get; set; }
        }

        private static readonly Dictionary<string, PendingReserve> _pendingReserves =
            new Dictionary<string, PendingReserve>(StringComparer.Ordinal);

        internal static int PendingReserveCountForTest { get { lock (_lock) return _pendingReserves.Count; } }

        // Runs before anything else can touch a listing: a record whose transaction never landed is units that
        // are in no listing, no vault and no ledger, which is the one outcome with nobody to complain to.
        public static void RecoverPendingReserves()
        {
            List<PendingReserve> orphans = new List<PendingReserve>();
            lock (_lock)
            {
                foreach (PendingReserve p in _pendingReserves.Values)
                    if (p != null && Transactions.KmhTransactionRepository.Get(p.TxId) == null) orphans.Add(p);
                _pendingReserves.Clear();
            }
            if (orphans.Count == 0)
            {
                if (SaveToDisk()) return;
                Diagnostics.ServerLog.Warn("Marketplace: could not clear settled reservation records; they are re-read next boot.");
                return;
            }

            foreach (PendingReserve p in orphans)
            {
                // Listed in a save that is being destroyed: the units go with it, not into the new one.
                if (Economy.KmhEconomyReset.Resetting(p.Seller)) continue;
                string note = $"marketplace listing #{p.ListingId} - reservation interrupted before it was recorded";
                if (p.Payloads != null && p.Payloads.Count > 0)
                    foreach (Items.KmhThingPayload sp in p.Payloads)
                        Items.KmhPayloadEscrow.Deliver(p.Seller, sp, note, "reserved units could not be returned to the seller");
                else
                    Items.KmhPayloadEscrow.DeliverCompact(p.Seller, p.ItemKey, p.Qty, note,
                                                          "reserved units could not be returned to the seller");
            }
            SaveToDisk();
            Diagnostics.ServerLog.Warn($"Marketplace: returned {orphans.Count} interrupted reservation(s) to their sellers.");
        }

        internal static void PlantPendingReserveForTest(string seller, string itemKey, int qty)
        {
            string tx = "selftest-reserve-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            lock (_lock) _pendingReserves[tx] = new PendingReserve { TxId = tx, Seller = seller, ItemKey = itemKey, Qty = qty };
        }

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropPendingReservesFor(string seller)
        {
            if (string.IsNullOrEmpty(seller)) return 0;
            int dropped = 0;
            lock (_lock)
                foreach (PendingReserve p in new List<PendingReserve>(_pendingReserves.Values))
                    if (p != null && string.Equals(p.Seller, seller, StringComparison.OrdinalIgnoreCase) && _pendingReserves.Remove(p.TxId)) dropped++;
            return dropped;
        }

        private static void UnreserveLocked(MarketplaceListing listing, long listingId, int qtyToSell,
                                            List<Items.KmhThingPayload> soldPayloads)
        {
            listing.RemainingQty = Util.KmhSafe.AddSaturating(listing.RemainingQty, qtyToSell);
            listing.Changes++;
            if (soldPayloads != null)
            {
                listing.EscrowPayloads = listing.EscrowPayloads ?? new List<Items.KmhThingPayload>();
                listing.EscrowPayloads.AddRange(soldPayloads);
            }
            if (!_byId.ContainsKey(listingId)) _byId[listingId] = listing;   // our own full reserve removed it
        }

        // Fired at each step a restart could stop between: only a crash leaves a purchase part-done.
        internal static Action<string> AtBuyPhaseForTest;
        // The same seam for post and return paths, fired at the durable boundaries a crash can land between.
        internal static Action<string> AtStorePhaseForTest;

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
            Transactions.KmhTransaction tx;
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

            // A frozen seller's goods may be what they minted; staff decide before anyone pays for them.
            if (Moderation.KmhEconomyGuard.IsFrozen(peekSeller)) return false;

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
                long costL = CostOf(unitMilli, qtyToSell);
                if (costL > int.MaxValue) return false;
                cost = (int)costL;

                // Reserved atomically with the availability check, and payloads are popped so a rollback can restore them.
                if (listing.EscrowPayloads != null && listing.EscrowPayloads.Count > 0)
                {
                    soldPayloads = PopPayloadsLocked(listing, qtyToSell);
                    qtyToSell = 0; foreach (Items.KmhThingPayload sp in soldPayloads) qtyToSell += sp.StackCount;
                    if (qtyToSell <= 0) return false;
                    cost = (int)CostOf(unitMilli, qtyToSell);   // popped units never exceed the qty already checked above
                }
                listing.RemainingQty -= qtyToSell;
                listing.Changes++;
                removedOnReserve = listing.RemainingQty <= 0 && (listing.EscrowPayloads == null || listing.EscrowPayloads.Count == 0);
                if (removedOnReserve) _byId.Remove(listingId);

                // Created, not yet recorded: Create assigns the id without touching the ledger file, which is
                // what lets the reservation below name the transaction that has not been written yet.
                tx = Transactions.KmhTransaction.Create(
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

                _pendingReserves[tx.Id] = new PendingReserve
                {
                    TxId = tx.Id, ListingId = listingId, Seller = listing.SellerUsername,
                    ItemKey = Util.ItemKey.Compose(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex),
                    Qty = qtyToSell,
                    Payloads = soldPayloads == null ? null : new List<Items.KmhThingPayload>(soldPayloads),
                };

                // One save carries both the units leaving the listing and the note saying who is about to own
                // them, or a restart re-lists units the buyer already paid for.
                if (!SaveToDisk())
                {
                    _pendingReserves.Remove(tx.Id);
                    UnreserveLocked(listing, listingId, qtyToSell, soldPayloads);
                    Diagnostics.ServerLog.Warn($"Marketplace: refused a buy on listing #{listingId} - the reservation could not be saved.");
                    return false;
                }
            }
            sellerUsername = listing.SellerUsername;
            AtBuyPhaseForTest?.Invoke("reserved");

            // From here both the units and the silver are in flight. A crash before this lands leaves the
            // reservation record above, which boot recovery reads because no transaction will answer to its id.
            if (!Transactions.KmhTransactionRepository.Add(tx))
            {
                lock (_lock)
                {
                    _pendingReserves.Remove(tx.Id);
                    UnreserveLocked(listing, listingId, qtyToSell, soldPayloads);
                    SaveToDisk();
                }
                Diagnostics.ServerLog.Warn($"Marketplace: refused a buy on listing #{listingId} - the transaction record could not be saved.");
                sellerUsername = null;
                return false;
            }

            // The ledger answers for these units from here, so the record has nothing left to say. Losing this
            // save is harmless: boot finds the transaction and drops the record without moving anything.
            lock (_lock) _pendingReserves.Remove(tx.Id);
            SaveToDisk();

            AtBuyPhaseForTest?.Invoke("recorded");

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

            AtBuyPhaseForTest?.Invoke("charged");

            // One breakdown, so the buyer's charge always equals payout plus taxes and nothing is minted. The boost is
            // taken first, so the payout written below is what the pool could actually fund.
            Economy.SaleSplit split = Economy.SaleSplit.Compute(listing.SellerUsername, listing.ItemDefName, cost,
                                                                demandDrift: true, worldPayoutEvents: true, units: qtyToSell);
            split.CommitBoost($"marketplace listing #{listingId} world-event sale boost");
            int sellerNet = (int)Math.Min(int.MaxValue, split.SellerPayout);
            tx.Advance(Transactions.KmhTxState.Approved);
            AtBuyPhaseForTest?.Invoke("approved");

            // Everything below hands value OUT. The legs are written onto the row with the Delivered boundary, so boot
            // finishes the sale from them: a split computed again there would not come out the same, and a row still
            // charged-but-not-delivered on disk is refunded, so nothing may be paid until this write lands.
            tx.TargetKind   = Transactions.KmhTransactionRepository.TargetMarketBuy;
            tx.PayoutSilver = split.SellerPayout;
            tx.HouseTax     = split.ServerTax + split.PoolReturn;
            tx.GuildTax     = string.IsNullOrEmpty(split.GuildName) ? 0 : split.GuildTax;
            tx.TaxGuild     = split.GuildName ?? "";
            tx.TaxGuildIdentity = Guilds.GuildIdentity.Of(split.GuildName) ?? "";
            tx.Advance(Transactions.KmhTxState.Delivered);
            if (!Transactions.KmhTransactionRepository.Update(tx))
            {
                // Boot would refund this row, so it is refunded now rather than paid as well.
                if (split.PoolBoost > 0) ReturnBoost(split.Seller, split.PoolBoost, $"marketplace listing #{listingId} sale undone - boost returned");
                tx.PayoutSilver = 0; tx.HouseTax = 0; tx.GuildTax = 0;
                tx.Advance(Transactions.KmhTxState.Failed);
                Transactions.KmhTransactionRepository.Compensate(tx);
                sellerUsername = null;
                return false;
            }

            CompletePurchase(tx);

            lock (_lock)
            {
                _lifetimeTradesCompleted += 1;
                _lifetimeSilverTraded    += cost;
            }

            PlayerStats.PlayerStatsStore.AddSalesEarned(listing.SellerUsername, sellerNet);
            PlayerStats.PlayerStatsStore.RecordSale(listing.SellerUsername, qtyToSell, cost);
            PlayerStats.PlayerStatsStore.RecordPurchase(buyerUsername, qtyToSell, cost);
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseMarketplaceBuy(new KMH.Sdk.Server.Events.MarketplaceBuyEvent { ListingId = listingId, BuyerUsername = buyerUsername, SellerUsername = sellerUsername, ItemDefName = listing.ItemDefName, QtyBought = qtyToSell, TotalSilverPaid = cost });
            boughtQty  = qtyToSell;
            paidSilver = cost;
            return true;
        }

        // Each leg rides its own marker and is cleared on the row once it lands, so boot re-running this after a crash
        // pays every leg once. False leaves the row open for the next boot.
        internal static bool CompletePurchase(Transactions.KmhTransaction tx)
        {
            if (tx == null) return false;
            string note = $"marketplace sale listing #{tx.TargetId}";
            bool ok = true;
            // The same keys SaleSplit.Settle uses, so a tax lands once whoever gets there first.
            if (tx.HouseTax > 0)
            {
                if (TryCreditHousePoolOnce(tx.Id + ":house", tx.HouseTax, note)) tx.HouseTax = 0; else ok = false;
            }
            if (tx.GuildTax > 0 && !string.IsNullOrEmpty(tx.TaxGuild))
            {
                if (Treasury.TreasuryStore.SettleGuildTaxOnce(tx.TaxGuild, tx.TaxGuildIdentity, tx.Id + ":guild", tx.GuildTax,
                                                              tx.CounterPlayer, note + " - guild sale tax"))
                    tx.GuildTax = 0;
                else ok = false;
            }
            AtBuyPhaseForTest?.Invoke("settled");
            if (tx.PayoutSilver > 0)
            {
                if (Treasury.TreasuryStore.DepositEscrowOnce(tx.CounterPlayer, tx.PayoutMarker, tx.PayoutSilver, null, null, $"{note} to {tx.Player}"))
                    tx.PayoutSilver = 0;
                else ok = false;
            }
            AtBuyPhaseForTest?.Invoke("paid");
            if (tx.CounterItems.Count > 0 || tx.CounterPayloads.Count > 0)
            {
                if (Treasury.TreasuryStore.DepositEscrowOnce(tx.Player, tx.Id + ":goods", 0, tx.CounterItems, tx.CounterPayloads,
                                                             $"marketplace buy listing #{tx.TargetId} from {tx.CounterPlayer}"))
                { tx.CounterItems.Clear(); tx.CounterPayloads.Clear(); }
                else ok = false;
            }
            AtBuyPhaseForTest?.Invoke("delivered");
            if (!ok)
            {
                // The legs that landed are written as cleared, so the next boot does not lean on a marker that may have aged out.
                Transactions.KmhTransactionRepository.Update(tx);
                Diagnostics.ServerLog.Error($"Marketplace: sale {tx.Id} could not pay every leg - the row stays open and the next boot finishes it.");
                return false;
            }
            Transactions.KmhTransactionRepository.Settle(tx);
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
            Persistence.TransactionLedger.RecordHousePoolSaved(credit: false, amount: drained, note: $"house-pool drain to {toUsername}");
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
            Persistence.TransactionLedger.RecordHousePoolSaved(credit: false, amount: amount, note: note);
            return true;
        }

        // Boom boost paid per seller on the current UTC day, saved with the pool so a restart cannot reset the allowance.
        private static long _boostDay;
        private static readonly Dictionary<string, long> _boostPaidToday = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static long UtcDayNow() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay;

        // Caller holds _lock; a new UTC day starts every seller's allowance again.
        private static long BoostPaidLocked(string seller)
        {
            long day = UtcDayNow();
            if (_boostDay != day) { _boostDay = day; _boostPaidToday.Clear(); }
            return _boostPaidToday.TryGetValue(seller ?? "", out long paid) ? paid : 0;
        }

        public static long BoostAllowance(string seller)
        {
            long cap = Economy.EconomyConfig.Current.Marketplace.BoomBoostMaxPerSellerPerDay;
            lock (_lock) return Math.Max(0, Math.Min(_houseSilverPool, cap - BoostPaidLocked(seller)));
        }

        // Charged to the seller's allowance in the same save that debits the pool, so two racing sales cannot both spend it.
        public static bool TryDebitBoost(string seller, long amount, string note)
        {
            if (amount <= 0) return true;
            long cap = Economy.EconomyConfig.Current.Marketplace.BoomBoostMaxPerSellerPerDay;
            string key = seller ?? "";
            lock (_lock)
            {
                long paid = BoostPaidLocked(key);
                if (_houseSilverPool < amount || paid + amount > cap) return false;
                _houseSilverPool -= amount;
                _boostPaidToday[key] = paid + amount;
                if (!SaveToDisk())
                {
                    _houseSilverPool += amount;
                    if (paid > 0) _boostPaidToday[key] = paid; else _boostPaidToday.Remove(key);
                    return false;
                }
            }
            Persistence.TransactionLedger.RecordHousePoolSaved(credit: false, amount: amount, note: note);
            return true;
        }

        public static void ReturnBoost(string seller, long amount, string note)
        {
            if (amount <= 0) return;
            lock (_lock)
            {
                string key = seller ?? "";
                long paid = BoostPaidLocked(key);
                if (paid > amount) _boostPaidToday[key] = paid - amount; else _boostPaidToday.Remove(key);
            }
            ReturnToHousePool(amount, note);
        }

        internal static long BoostPaidTodayForTest(string seller) { lock (_lock) return BoostPaidLocked(seller); }

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
        // Oldest first. A HashSet enumerates a reused slot first, so trimming by its enumerator would evict the newest marker.
        private static readonly List<string> _creditMarkerOrder = new List<string>();
        private const int MaxCreditMarkers = 512;
        // Lowered only by the self-test, so trimming is reached without hundreds of real saves.
        internal static int CreditMarkerWindow = MaxCreditMarkers;

        // Credits once per marker. True also when this marker was already applied, since the pool holds it either way.
        public static bool TryCreditHousePoolOnce(string marker, long amount, string note = "house-pool credit")
        {
            if (string.IsNullOrEmpty(marker)) return TryCreditHousePool(amount, note);
            if (amount <= 0) return true;
            lock (_lock)
            {
                if (_creditMarkers.Contains(marker)) return true;
                _houseSilverPool += amount;
                List<string> aged = AddCreditMarkerLocked(marker);
                if (!SaveToDisk())
                {
                    _houseSilverPool -= amount;
                    UndoCreditMarkerLocked(marker, aged);
                    return false;
                }
            }
            Persistence.TransactionLedger.RecordHousePoolSaved(credit: true, amount: amount, note: note);
            return true;
        }

        // Debits once per marker, as much as the pool still holds: what it already paid out cannot be taken back.
        public static bool TryDebitHousePoolOnce(string marker, long amount, string note = "house-pool debit")
        {
            if (string.IsNullOrEmpty(marker) || amount <= 0) return true;
            long taken;
            lock (_lock)
            {
                if (_creditMarkers.Contains(marker)) return true;
                taken = Math.Min(amount, Math.Max(0, _houseSilverPool));
                _houseSilverPool -= taken;
                List<string> aged = AddCreditMarkerLocked(marker);
                if (!SaveToDisk())
                {
                    _houseSilverPool += taken;
                    UndoCreditMarkerLocked(marker, aged);
                    return false;
                }
            }
            if (taken > 0) Persistence.TransactionLedger.RecordHousePoolSaved(credit: false, amount: taken, note: note);
            if (taken < amount)
                Diagnostics.ServerLog.Warn($"House pool: {note} - only {taken} of {amount} taken back, the pool had already paid the rest out.");
            return true;
        }

        // Returns the markers aged out of the window, so a refused save can put them back.
        private static List<string> AddCreditMarkerLocked(string marker)
        {
            _creditMarkers.Add(marker);
            _creditMarkerOrder.Add(marker);
            int over = Math.Max(0, _creditMarkerOrder.Count - Math.Max(1, CreditMarkerWindow));
            List<string> aged = _creditMarkerOrder.GetRange(0, over);
            _creditMarkerOrder.RemoveRange(0, over);
            foreach (string m in aged) _creditMarkers.Remove(m);
            return aged;
        }

        private static void UndoCreditMarkerLocked(string marker, List<string> aged)
        {
            _creditMarkers.Remove(marker);
            _creditMarkerOrder.RemoveAt(_creditMarkerOrder.Count - 1);
            _creditMarkerOrder.InsertRange(0, aged);
            foreach (string m in aged) _creditMarkers.Add(m);
        }

        // False means the pool holds the silver in memory only; a caller settling value it already took needs to know.
        // Ledgered only once saved: this one is taken back on a failed write, so an earlier line would describe nothing.
        public static bool TryCreditHousePool(long amount, string note = "house-pool credit (tax/refund)")
        {
            if (amount <= 0) return true;
            lock (_lock) { _houseSilverPool += amount; }
            if (SaveToDisk())
            {
                Persistence.TransactionLedger.RecordHousePoolSaved(credit: true, amount: amount, note: note);
                return true;
            }
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
                Persistence.TransactionLedger.RecordHousePoolSaved(credit: true, amount: amount, note: "house-pool seed (one-time, new server)");
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
                    _creditMarkerOrder.Clear();
                    if (state.CreditMarkers != null)
                        foreach (string m in state.CreditMarkers)
                            if (!string.IsNullOrEmpty(m) && _creditMarkers.Add(m)) _creditMarkerOrder.Add(m);
                    _pendingReserves.Clear();
                    if (state.PendingReserves != null)
                        foreach (PendingReserve p in state.PendingReserves)
                            if (p != null && !string.IsNullOrEmpty(p.TxId)) _pendingReserves[p.TxId] = p;
                    _boostDay = state.BoostDay;
                    _boostPaidToday.Clear();
                    if (state.BoostPaidToday != null)
                        foreach (KeyValuePair<string, long> kv in state.BoostPaidToday)
                            if (kv.Key != null && kv.Value > 0) _boostPaidToday[kv.Key] = kv.Value;
                }
                Diagnostics.ServerLog.Diag($"Marketplace: loaded {state.Listings?.Count ?? 0} listing(s) from disk");
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

        // Clearing the seeded flag re-seeds the house pool on the next boot.
        public static void ClearForNewSeason()
        {
            lock (_lock)
            {
                _byId.Clear();
                _pendingReserves.Clear();
                _nextId = 1;
                _houseSilverPool = 0;
                _lifetimeTradesCompleted = 0;
                _lifetimeSilverTraded = 0;
                _housePoolSeeded = false;
                _boostPaidToday.Clear();
            }
            SaveToDisk();
        }

        // False means memory only; a reservation that never reached disk comes back whole and sells the same goods twice.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Listings                = KmhDeepCopy.OfList(_byId.Values);
                state.NextId                  = _nextId;
                state.HouseSilverPool         = _houseSilverPool;
                state.HousePoolSeeded         = _housePoolSeeded;
                state.LifetimeTradesCompleted = _lifetimeTradesCompleted;
                state.LifetimeSilverTraded    = _lifetimeSilverTraded;
                state.CreditMarkers           = new List<string>(_creditMarkerOrder);
                state.PendingReserves         = KmhDeepCopy.OfList(_pendingReserves.Values);
                state.BoostDay                = _boostDay;
                state.BoostPaidToday          = new Dictionary<string, long>(_boostPaidToday, StringComparer.OrdinalIgnoreCase);
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
            }
            if (!JsonFileStore.Save(KmhDataPaths.MarketplaceFile, state, seq)) return false;
            Maintenance.KmhInvalidation.MarketplaceCommitted();
            // A house-pool line recorded before this save reaches the ledger with it, not on the flusher's next beat.
            Persistence.TransactionLedger.Flush();
            return true;
        }

        private class PersistedState
        {
            public List<MarketplaceListing> Listings              { get; set; } = new List<MarketplaceListing>();
            // Absent in a file written before this existed, which reads correctly as "no reservation in flight".
            public List<PendingReserve>     PendingReserves       { get; set; } = new List<PendingReserve>();
            public long                     NextId                { get; set; } = 1;
            public long                     HouseSilverPool       { get; set; } = 0;
            public bool                     HousePoolSeeded       { get; set; } = false;
            public long                     LifetimeTradesCompleted { get; set; } = 0;
            public long                     LifetimeSilverTraded  { get; set; } = 0;
            public List<string>             CreditMarkers         { get; set; } = new List<string>();
            // Absent in older files, which read as no boost paid yet today.
            public long                     BoostDay              { get; set; } = 0;
            public Dictionary<string, long> BoostPaidToday        { get; set; } = new Dictionary<string, long>();
        }
    }
}
