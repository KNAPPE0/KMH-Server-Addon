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

        // Save-reset: a buyer's open wants escrow silver OUTSIDE the treasury, so a save-reset must drop them too or
        // they shelter value. Purge burns the escrow (the reset burns the treasury alongside).
        public static bool HasBuyerWants(string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock) foreach (WantDto w in _byId.Values) if (Eq(w.BuyerUsername, user)) return true;
            return false;
        }

        public static int PurgeBuyer(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            List<long> ids = new List<long>();
            lock (_lock)
            {
                foreach (WantDto w in _byId.Values) if (Eq(w.BuyerUsername, user)) ids.Add(w.Id);
                foreach (long id in ids) _byId.Remove(id);
            }
            if (ids.Count > 0) SaveToDisk();
            return ids.Count;
        }

        // Season reset: clear all want-board orders.
        public static void ClearForNewSeason()
        {
            lock (_lock) { _byId.Clear(); _nextId = 1; }
            SaveToDisk();
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
            int durationHours, string visibility,
            int minQuality = 0, string requiredStuff = "", bool allowComplex = false, bool allowTainted = false, bool allowDamaged = false)
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
                    MinQuality       = minQuality < 0 ? 0 : minQuality,
                    RequiredStuff    = requiredStuff ?? "",
                    // Any state/complex constraint means this is a payload want (filled from full-state items).
                    AllowComplex     = allowComplex || allowTainted || allowDamaged || minQuality > 0 || !string.IsNullOrEmpty(requiredStuff),
                    AllowTainted     = allowTainted,
                    AllowDamaged     = allowDamaged,
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

            // Route: a want with state constraints is filled from full-state payloads (which the constraints gate),
            // so a buyer can't be handed tainted/damaged/wrong-material gear. Plain wants use the compact path.
            bool complexWant = false;
            lock (_lock) { if (_byId.TryGetValue(wantId, out WantDto peek)) complexWant = peek.AllowComplex; }
            if (complexWant) return FulfillFromPayloads(seller, wantId, qty);

            long now = DateTime.UtcNow.Ticks;
            int fillable;
            string itemDef; int unitPrice; string buyer;
            string sellerGuild = Guilds.GuildStore.CurrentGuildOf(seller); // resolve before the lock

            // Cheap pre-check so we never withdraw items for an invalid fulfill.
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto pw)) { r.Reason = "Want not found."; return r; }
                if (pw.EndsUtcTicks <= now)        { r.Reason = "This want has expired.";            return r; }
                if (Eq(pw.BuyerUsername, seller))  { r.Reason = "You can't fulfill your own want.";   return r; }
                // Enforce guild-only visibility on the action too (crafted client could target a hidden id).
                if (!Guilds.GuildVisibility.IsVisibleTo(pw.BuyerTreasuryKey, pw.Visibility, sellerGuild, prefetched: true))
                { r.Reason = "You can't fulfill that want."; return r; }
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

            // Pay the seller from escrow via the authoritative split (escrow == payout + server tax + guild tax).
            long gross = (long)unitPrice * commit;
            Economy.SaleSplit split = Economy.SaleSplit.Compute(seller, itemDef, gross, demandDrift: false, worldPayoutEvents: false);
            split.Settle(seller, $"want #{wantId} fulfilled");
            long net = split.SellerPayout;
            Treasury.TreasuryStore.DepositSilver(seller, (int)Math.Min(int.MaxValue, net), note: $"want #{wantId} fulfilled");

            SaveToDisk();
            Diagnostics.ServerLog.Info($"WantBoard: {seller} fulfilled {commit}x {itemDef} for want #{wantId} (net {net}, tax {split.ServerTax}, guild {split.GuildTax})");

            r.Ok = true; r.FilledQty = commit; r.Completed = completed;
            r.Buyer = buyer; r.ItemDefName = itemDef; r.UnitPrice = unitPrice; r.SellerNet = net;
            return r;
        }

        // Fill a state-constrained want from the seller's full-state payloads. Mirrors the compact Fulfill's atomic
        // race handling; delivers exact payloads to the buyer and returns surplus to the seller. State that doesn't
        // meet the want (wrong material/quality, disallowed taint/damage) simply isn't withdrawn, so the buyer can
        // never receive an item the want didn't accept.
        private static FulfillResult FulfillFromPayloads(string seller, long wantId, int qty)
        {
            FulfillResult r = new FulfillResult();
            long now = DateTime.UtcNow.Ticks;
            int fillable, unitPrice, minQ; string itemDef, buyer, reqStuff; bool aT, aD;
            string sellerGuild = Guilds.GuildStore.CurrentGuildOf(seller);
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto pw)) { r.Reason = "Want not found."; return r; }
                if (pw.EndsUtcTicks <= now)       { r.Reason = "This want has expired."; return r; }
                if (Eq(pw.BuyerUsername, seller))  { r.Reason = "You can't fulfill your own want."; return r; }
                if (!Guilds.GuildVisibility.IsVisibleTo(pw.BuyerTreasuryKey, pw.Visibility, sellerGuild, prefetched: true))
                { r.Reason = "You can't fulfill that want."; return r; }
                int remaining = pw.QtyWanted - pw.QtyFilled;
                if (remaining <= 0) { r.Reason = "This want is already filled."; return r; }
                fillable = Math.Min(qty, remaining);
                itemDef = pw.ItemDefName; unitPrice = pw.UnitPriceSilver; buyer = pw.BuyerUsername;
                reqStuff = pw.RequiredStuff; minQ = pw.MinQuality; aT = pw.AllowTainted; aD = pw.AllowDamaged;
            }

            List<Items.KmhThingPayload> taken = Treasury.TreasuryStore.TryWithdrawMatchingPayloads(
                seller, itemDef, reqStuff, minQ, aT, aD, fillable, $"want #{wantId} fulfill");
            if (taken.Count == 0)
            { r.Reason = $"You have no matching {ItemLabels.ItemLabelCache.LabelFor(itemDef)} that meets this want (material/quality/condition)."; return r; }
            int takenUnits = 0; foreach (Items.KmhThingPayload t in taken) takenUnits += t.StackCount;

            int commit; bool completed = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto w) || w.EndsUtcTicks <= DateTime.UtcNow.Ticks)
                { Items.KmhPayloadEscrow.RefundTo(seller, taken, $"want #{wantId} returned"); r.Reason = "That want just closed - your items were returned."; return r; }
                int remaining = w.QtyWanted - w.QtyFilled;
                commit = Math.Min(takenUnits, remaining);
                if (commit <= 0)
                { Items.KmhPayloadEscrow.RefundTo(seller, taken, $"want #{wantId} returned"); r.Reason = "That want was just filled - your items were returned."; return r; }
                w.QtyFilled += commit; w.EscrowRemaining -= (long)unitPrice * commit;
                completed = w.QtyFilled >= w.QtyWanted; if (completed) _byId.Remove(wantId);
            }

            // Deliver `commit` payload-units to the buyer; any surplus (lost race) goes back to the seller. Guarded so
            // a deposit that can't land parks in the recovery queue instead of vanishing.
            int toBuyer = commit;
            foreach (Items.KmhThingPayload p in taken)
            {
                if (toBuyer >= p.StackCount) { Items.KmhPayloadEscrow.Deliver(buyer, p, $"want #{wantId} received", $"want #{wantId} received"); toBuyer -= p.StackCount; }
                else if (toBuyer > 0 && string.IsNullOrEmpty(p.ScribeXml))
                {
                    Items.KmhPayloadEscrow.Deliver(buyer,  Items.KmhPayloadEscrow.Clone(p, toBuyer), $"want #{wantId} received", $"want #{wantId} received");
                    Items.KmhPayloadEscrow.Deliver(seller, Items.KmhPayloadEscrow.Clone(p, p.StackCount - toBuyer), $"want #{wantId} surplus returned", $"want #{wantId} surplus returned");
                    toBuyer = 0;
                }
                else Items.KmhPayloadEscrow.Deliver(seller, p, $"want #{wantId} surplus returned", $"want #{wantId} surplus returned");
            }

            long gross = (long)unitPrice * commit;
            Economy.SaleSplit split = Economy.SaleSplit.Compute(seller, itemDef, gross, demandDrift: false, worldPayoutEvents: false);
            split.Settle(seller, $"want #{wantId} fulfilled");
            long net = split.SellerPayout;
            Treasury.TreasuryStore.DepositSilver(seller, (int)Math.Min(int.MaxValue, net), note: $"want #{wantId} fulfilled");
            SaveToDisk();
            Diagnostics.ServerLog.Info($"WantBoard: {seller} fulfilled {commit}x {itemDef} (full-state) for want #{wantId} (net {net}, tax {split.ServerTax}, guild {split.GuildTax})");

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

        private static WantDto Clone(WantDto w) => new WantDto
        {
            Id = w.Id, BuyerUsername = w.BuyerUsername, BuyerTreasuryKey = w.BuyerTreasuryKey,
            ItemDefName = w.ItemDefName, QtyWanted = w.QtyWanted, QtyFilled = w.QtyFilled,
            UnitPriceSilver = w.UnitPriceSilver, EscrowRemaining = w.EscrowRemaining,
            ListedUtcTicks = w.ListedUtcTicks, EndsUtcTicks = w.EndsUtcTicks, Visibility = w.Visibility,
        };
    }
}
