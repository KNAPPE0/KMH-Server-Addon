using System;
using System.Collections.Generic;
using KMHServerAddon.Features.WantBoard.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.WantBoard
{
    // Want Board: buyer escrow, seller fills, partials/refunds, and all moves stay server-ledgered.
    internal static class WantStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, WantDto> _byId = new Dictionary<long, WantDto>();
        private static long _nextId = 1;

        // -- persistence --

        private sealed class PersistedState
        {
            public List<WantDto> Wants  { get; set; } = new List<WantDto>();
            public long          NextId { get; set; } = 1;
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.WantsFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _byId.Clear();
                if (s.Wants != null) foreach (WantDto w in s.Wants) if (w != null) _byId[w.Id] = w;
                _nextId = Math.Max(1, s.NextId);
            }
            Diagnostics.ServerLog.Info($"WantBoard: loaded {s.Wants?.Count ?? 0} open want(s)");
        }

        public static void SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock) { s.Wants.AddRange(_byId.Values); s.NextId = _nextId; seq = JsonFileStore.NextSequence(); }
            JsonFileStore.Save(KmhDataPaths.WantsFile, s, seq);
        }

        // -- snapshot (guild visibility; caller always sees their own) --

        public static WantSnapshot BuildSnapshot(string caller)
        {
            string callerGuild = string.IsNullOrEmpty(caller) ? null : Guilds.GuildStore.CurrentGuildOf(caller);
            WantSnapshot s = new WantSnapshot();
            lock (_lock)
            {
                foreach (WantDto w in _byId.Values)
                {
                    bool mine = !string.IsNullOrEmpty(caller) && Eq(w.BuyerUsername, caller);
                    if (!mine && !Guilds.GuildVisibility.IsVisibleTo(w.BuyerTreasuryKey, w.Visibility, callerGuild, prefetched: true))
                        continue;
                    s.Wants.Add(Clone(w));
                }
            }
            return s;
        }

        // Total still-open demand for an item across the want board (qty wanted minus already filled). Feeds the
        // marketplace's supply/demand tax drift.
        public static long OpenDemandQty(string itemDefName)
        {
            if (string.IsNullOrEmpty(itemDefName)) return 0;
            long total = 0;
            lock (_lock)
                foreach (WantDto w in _byId.Values)
                    if (w != null && Eq(w.ItemDefName, itemDefName))
                        total += Math.Max(0, w.QtyWanted - w.QtyFilled);
            return total;
        }

        // -- post --

        public static (long id, string reason) Post(string buyer, string itemDef, int qtyWanted, int unitPrice,
            int durationHours, string visibility)
        {
            if (string.IsNullOrEmpty(buyer))  return (0, "No buyer.");
            if (string.IsNullOrEmpty(itemDef)) return (0, "No item.");
            if (qtyWanted <= 0) return (0, "Quantity must be > 0.");

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            unitPrice = Clamp(unitPrice, cfg.MarketplaceMinUnitPrice, cfg.MarketplaceMaxUnitPrice);
            int hours = durationHours > 0 ? Math.Min(durationHours, cfg.WantMaxDurationHours) : cfg.WantDefaultDurationHours;

            long total = (long)unitPrice * qtyWanted;
            if (total > int.MaxValue) return (0, "That want is too large - lower the quantity or price.");

            lock (_lock)
            {
                int open = 0;
                foreach (WantDto w in _byId.Values) if (Eq(w.BuyerUsername, buyer)) open++;
                if (open >= cfg.WantMaxOpenPerUser)
                    return (0, $"You already have the max {cfg.WantMaxOpenPerUser} open wants.");
            }

            // Escrow the full ask out of the buyer's treasury (treasury has its own lock).
            if (!Treasury.TreasuryStore.WithdrawSilver(buyer, (int)total, note: "want-board escrow"))
                return (0, "Your treasury doesn't have enough silver to back that want.");

            long id, now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                id = _nextId++;
                _byId[id] = new WantDto
                {
                    Id               = id,
                    BuyerUsername    = buyer,
                    BuyerTreasuryKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(buyer),
                    ItemDefName      = itemDef,
                    QtyWanted        = qtyWanted,
                    QtyFilled        = 0,
                    UnitPriceSilver  = unitPrice,
                    EscrowRemaining  = total,
                    ListedUtcTicks   = now,
                    EndsUtcTicks     = now + TimeSpan.FromHours(hours).Ticks,
                    Visibility       = string.IsNullOrEmpty(visibility) ? "public" : visibility,
                };
            }
            SaveToDisk();
            return (id, $"Want posted: up to {qtyWanted}x at {Util.SilverFmt.Format(unitPrice)} each ({Util.SilverFmt.Format(total)} escrowed).");
        }

        // -- fulfill (seller delivers from their treasury, gets paid from escrow) --

        public sealed class FulfillResult
        {
            public bool   Ok;
            public string Reason      = "";
            public int    FilledQty;            // units actually accepted this call
            public bool   Completed;            // the want is now fully filled and removed
            public string Buyer       = "";
            public string ItemDefName = "";
            public int    UnitPrice;
            public long   SellerNet;            // what the seller received (price*qty - tax)
        }

        // Delivers up to qty units toward a want. Items leave the seller's treasury first; on a lost race the
        // unusable portion is returned to the seller (their own goods, never a mint). Payment comes from escrow.
        public static FulfillResult Fulfill(string seller, long wantId, int qty)
        {
            FulfillResult r = new FulfillResult();
            if (string.IsNullOrEmpty(seller) || qty <= 0) { r.Reason = "Bad fulfill."; return r; }

            long now = DateTime.UtcNow.Ticks;
            int fillable;
            string itemDef; int unitPrice; string buyer;

            // Cheap pre-check so we never withdraw items for an invalid fulfill.
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto pw)) { r.Reason = "Want not found."; return r; }
                if (pw.EndsUtcTicks <= now)        { r.Reason = "This want has expired.";            return r; }
                if (Eq(pw.BuyerUsername, seller))  { r.Reason = "You can't fulfill your own want.";   return r; }
                int remaining = pw.QtyWanted - pw.QtyFilled;
                if (remaining <= 0) { r.Reason = "This want is already filled."; return r; }
                fillable  = Math.Min(qty, remaining);
                itemDef   = pw.ItemDefName;
                unitPrice = pw.UnitPriceSilver;
                buyer     = pw.BuyerUsername;
            }

            // Withdraw matching composed stacks up front so stuff/quality items keep their exact material when delivered or refunded.
            if (!Treasury.TreasuryStore.TryWithdrawMatching(seller, itemDef, 0, fillable, $"want #{wantId} fulfill",
                    out List<KeyValuePair<string, int>> taken))
            { r.Reason = $"Your treasury doesn't have {fillable}x {ItemLabels.ItemLabelCache.LabelFor(itemDef)}."; return r; }

            int commit; bool completed = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto w) || w.EndsUtcTicks <= DateTime.UtcNow.Ticks)
                {
                    // Want vanished / expired in the gap - hand the seller's goods back (same keys) and bail.
                    foreach (KeyValuePair<string, int> t in taken)
                        Treasury.TreasuryStore.DepositItem(seller, t.Key, t.Value, note: $"want #{wantId} fulfill returned");
                    r.Reason = "That want just closed - your items were returned.";
                    return r;
                }
                int remaining = w.QtyWanted - w.QtyFilled;
                commit = Math.Min(fillable, remaining);
                if (commit <= 0)
                {
                    foreach (KeyValuePair<string, int> t in taken)
                        Treasury.TreasuryStore.DepositItem(seller, t.Key, t.Value, note: $"want #{wantId} fulfill returned");
                    r.Reason = "That want was just filled by someone else - your items were returned.";
                    return r;
                }
                w.QtyFilled      += commit;
                w.EscrowRemaining -= (long)unitPrice * commit;
                completed = w.QtyFilled >= w.QtyWanted;
                if (completed) _byId.Remove(wantId);
            }

            // Split the withdrawn stacks: deliver `commit` units to the buyer (material/quality preserved), return any
            // surplus (lost part of the race) to the seller under the same keys.
            int toBuyer = commit;
            foreach (KeyValuePair<string, int> t in taken)
            {
                int give = Math.Min(t.Value, toBuyer);
                if (give > 0) { Treasury.TreasuryStore.DepositItem(buyer, t.Key, give, note: $"want #{wantId} received"); toBuyer -= give; }
                int back = t.Value - give;
                if (back > 0) Treasury.TreasuryStore.DepositItem(seller, t.Key, back, note: $"want #{wantId} surplus returned");
            }

            // Pay the seller from escrow (price - house tax) and credit the tax.
            long gross = (long)unitPrice * commit;
            long tax   = HouseTax(seller, gross);
            long net   = Math.Max(0, gross - tax);
            Treasury.TreasuryStore.DepositSilver(seller, (int)net, note: $"want #{wantId} fulfilled");
            if (tax > 0) Marketplace.MarketplaceStore.CreditHousePool(tax);

            SaveToDisk();
            Diagnostics.ServerLog.Info($"WantBoard: {seller} fulfilled {commit}x {itemDef} for want #{wantId} (net {net}, tax {tax})");

            r.Ok = true; r.FilledQty = commit; r.Completed = completed;
            r.Buyer = buyer; r.ItemDefName = itemDef; r.UnitPrice = unitPrice; r.SellerNet = net;
            return r;
        }

        // -- cancel (buyer; refunds unspent escrow) --

        public static (bool ok, string reason) Cancel(string buyer, long wantId)
        {
            WantDto w; long refund;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w)) return (false, "Want not found.");
                if (!Eq(w.BuyerUsername, buyer))        return (false, "That isn't your want.");
                refund = w.EscrowRemaining;
                _byId.Remove(wantId);
            }
            if (refund > 0) Treasury.TreasuryStore.DepositSilver(buyer, (int)Math.Min(refund, int.MaxValue), note: $"want #{wantId} cancelled");
            SaveToDisk();
            return (true, refund > 0
                ? $"Want cancelled - {Util.SilverFmt.Format(refund)} refunded to your treasury."
                : "Want cancelled.");
        }

        // -- admin recovery --

        // Every open want regardless of visibility - for `kmh inspect`/`kmh cancel`, never sent to a normal client.
        public static List<WantDto> AllForAdmin()
        {
            lock (_lock)
            {
                List<WantDto> all = new List<WantDto>(_byId.Count);
                foreach (WantDto w in _byId.Values) all.Add(Clone(w));
                return all;
            }
        }

        // Admin force-cancel of a stuck want: remove it and refund the buyer's unspent escrow (same effect as expiry,
        // logged as an admin action). Returns the buyer + refunded amount so the caller can push their treasury.
        public static ExpireOutcome AdminCancel(long wantId)
        {
            ExpireOutcome o = new ExpireOutcome();
            WantDto w;
            lock (_lock) { if (!_byId.TryGetValue(wantId, out w)) return o; _byId.Remove(wantId); }
            o.Done = true; o.Buyer = w.BuyerUsername; o.Refunded = w.EscrowRemaining;
            if (w.EscrowRemaining > 0)
                Treasury.TreasuryStore.DepositSilver(w.BuyerUsername, (int)Math.Min(w.EscrowRemaining, int.MaxValue), note: $"want #{w.Id} cancelled by admin");
            SaveToDisk();
            Diagnostics.ServerLog.Info($"WantBoard: want #{w.Id} cancelled by admin, refunded {w.EscrowRemaining} to {w.BuyerUsername}");
            return o;
        }

        // -- expiry sweep --

        public static List<long> CollectEndedIds(long now)
        {
            List<long> ended = new List<long>();
            lock (_lock)
                foreach (WantDto w in _byId.Values)
                    if (w.EndsUtcTicks > 0 && now >= w.EndsUtcTicks) ended.Add(w.Id);
            return ended;
        }

        public sealed class ExpireOutcome { public bool Done; public string Buyer = ""; public long Refunded; }

        // Expire one want: refund its unspent escrow to the buyer.
        public static ExpireOutcome ExpireRefund(long wantId)
        {
            ExpireOutcome o = new ExpireOutcome();
            WantDto w;
            lock (_lock) { if (!_byId.TryGetValue(wantId, out w)) return o; _byId.Remove(wantId); }
            o.Done = true; o.Buyer = w.BuyerUsername; o.Refunded = w.EscrowRemaining;
            if (w.EscrowRemaining > 0)
                Treasury.TreasuryStore.DepositSilver(w.BuyerUsername, (int)Math.Min(w.EscrowRemaining, int.MaxValue), note: $"want #{w.Id} expired");
            SaveToDisk();
            Diagnostics.ServerLog.Info($"WantBoard: want #{w.Id} expired, refunded {w.EscrowRemaining} to {w.BuyerUsername}");
            return o;
        }

        // -- helpers --

        // House tax on a fulfillment: marketplace tax %, reduced by the seller guild's perk, waived on a tax holiday.
        private static long HouseTax(string seller, long amount)
        {
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            if (World.WorldStore.IsTaxHoliday()) return 0;
            int reduction = Guilds.GuildStore.GetMarketplaceTaxReductionPoints(Guilds.GuildStore.CurrentGuildOf(seller));
            int pct = Math.Max(0, cfg.MarketplaceTaxPercent - reduction);
            return (long)Math.Round(amount * (pct / 100.0));
        }

        private static WantDto Clone(WantDto w) => new WantDto
        {
            Id = w.Id, BuyerUsername = w.BuyerUsername, BuyerTreasuryKey = w.BuyerTreasuryKey,
            ItemDefName = w.ItemDefName, QtyWanted = w.QtyWanted, QtyFilled = w.QtyFilled,
            UnitPriceSilver = w.UnitPriceSilver, EscrowRemaining = w.EscrowRemaining,
            ListedUtcTicks = w.ListedUtcTicks, EndsUtcTicks = w.EndsUtcTicks, Visibility = w.Visibility,
        };
    }
}
