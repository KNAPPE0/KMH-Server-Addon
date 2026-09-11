using System;
using System.Collections.Generic;
using KMHServerAddon.Features.WantBoard.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.WantBoard
{
    internal static class WantStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, WantDto> _byId = new Dictionary<long, WantDto>();
        private static long _nextId = 1;


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

        // An open want escrows silver outside the treasury, so a save-reset that skipped it would shelter value.
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

        public static void ClearForNewSeason()
        {
            lock (_lock) { _byId.Clear(); _nextId = 1; }
            SaveToDisk();
        }

        // False means the change is in memory only; escrow lives here, not in the treasury, so an unwritten fill pays the seller and refunds the buyer.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock) { s.Wants.AddRange(_byId.Values); s.NextId = _nextId; seq = JsonFileStore.NextSequence(); }
            return JsonFileStore.Save(KmhDataPaths.WantsFile, s, seq);
        }


        // Buyers whose want could be hidden from a same-guild viewer - see GuildVisibility.SnapshotShareKey.
        public static HashSet<string> BuyersOfNonPublic()
        {
            HashSet<string> buyers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                foreach (WantDto w in _byId.Values)
                    if (w != null && !string.IsNullOrEmpty(w.BuyerUsername)
                        && !string.Equals(w.Visibility, Guilds.GuildVisibility.Public, StringComparison.OrdinalIgnoreCase))
                        buyers.Add(w.BuyerUsername);
            return buyers;
        }

        public static WantSnapshot BuildSnapshot(string caller)
        {
            string callerGuild = string.IsNullOrEmpty(caller) ? null : Guilds.GuildStore.CurrentGuildOf(caller);
            WantSnapshot s = new WantSnapshot();
            lock (_lock)
            {
                s.Revision = Util.KmhSnapshotRevision.Next();
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


        public static (long id, string reason) Post(string buyer, string itemDef, int qtyWanted, int unitPrice,
            int durationHours, string visibility,
            int minQuality = 0, string requiredStuff = "", bool allowComplex = false, bool allowTainted = false, bool allowDamaged = false)
        {
            if (string.IsNullOrEmpty(buyer))  return (0, "No buyer.");
            if (string.IsNullOrEmpty(itemDef)) return (0, "No item.");
            if (qtyWanted <= 0) return (0, "Quantity must be > 0.");

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            // Want board deals in whole silver, so clamp to the integer bounds of the marketplace decimal floor/ceiling.
            unitPrice = Clamp(unitPrice, Math.Max(1, (int)Math.Ceiling(cfg.MarketplaceMinUnitPrice)), (int)cfg.MarketplaceMaxUnitPrice);
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

            // Extension veto hooks (custom rules); runs before escrow so a denial has no side effects. No hook = allow.
            KMH.Sdk.Server.Hooks.KmhHookVerdict verdict = Extensibility.KmhHooks.Instance.CheckWant(
                new KMH.Sdk.Server.Hooks.KmhWantContext(buyer, itemDef, qtyWanted, unitPrice));
            if (verdict.Denied) return (0, verdict.Reason);

            // Escrow the full ask out of the buyer's treasury (treasury has its own lock).
            if (!Treasury.TreasuryStore.WithdrawSilver(buyer, (int)total, note: "want-board escrow"))
                return (0, "Your treasury doesn't have enough silver to back that want.");

            long id = 0, now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                // The count above ran before escrow released the lock, so a concurrent post could have taken the last slot.
                int open = 0;
                foreach (WantDto w in _byId.Values) if (Eq(w.BuyerUsername, buyer)) open++;
                overCap = open >= cfg.WantMaxOpenPerUser;
                if (!overCap)
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
                        AllowComplex     = NeedsPayloadFulfil(allowComplex, allowTainted, allowDamaged),
                        AllowTainted     = allowTainted,
                        AllowDamaged     = allowDamaged,
                    };
                }
            }
            if (overCap)
            {
                Items.KmhPayloadEscrow.DeliverSilver(buyer, total, "want-board refund (open-want limit reached)", "want refund could not be credited");
                return (0, $"You already have the max {cfg.WantMaxOpenPerUser} open wants - your escrow was refunded.");
            }
            // The escrow has already left the buyer's treasury; a want the disk never took would strand it there.
            if (!SaveToDisk())
            {
                lock (_lock) _byId.Remove(id);
                Items.KmhPayloadEscrow.DeliverSilver(buyer, total, "want post could not be saved", "want refund could not be credited");
                return (0, "The server couldn't save that want - your escrow was refunded. Try again shortly.");
            }
            return (id, $"Want posted: up to {qtyWanted}x at {Util.SilverFmt.Format(unitPrice)} each ({Util.SilverFmt.Format(total)} escrowed).");
        }


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

        // Taint, damage and gear state exist only on payloads; material and quality ride in a compact key and must not force it.
        internal static bool NeedsPayloadFulfil(bool allowComplex, bool allowTainted, bool allowDamaged)
            => allowComplex || allowTainted || allowDamaged;

        // Items leave the seller first, and a lost race returns their own goods rather than minting any.
        public static FulfillResult Fulfill(string seller, long wantId, int qty)
        {
            FulfillResult r = new FulfillResult();
            if (string.IsNullOrEmpty(seller) || qty <= 0) { r.Reason = "Bad fulfill."; return r; }

            // A state-constrained want is filled from payloads only, or the buyer could be handed what they excluded.
            bool complexWant = false;
            lock (_lock) { if (_byId.TryGetValue(wantId, out WantDto peek)) complexWant = peek.AllowComplex; }
            if (complexWant) return FulfillFromPayloads(seller, wantId, qty);

            long now = DateTime.UtcNow.Ticks;
            int fillable;
            string itemDef; int unitPrice; string buyer; string reqStuff; int minQ;
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
                reqStuff  = pw.RequiredStuff;
                minQ      = pw.MinQuality;
            }

            // Withdrawn as composed stacks, so a stuff/quality item keeps its exact material whether delivered or returned.
            if (!Treasury.TreasuryStore.TryWithdrawMatching(seller, itemDef, minQ, fillable, $"want #{wantId} fulfill",
                    out List<KeyValuePair<string, int>> taken, reqStuff))
                // The same def can sit in the vault as payloads, so an empty compact result is not an empty treasury.
                return FulfillFromPayloads(seller, wantId, qty);

            int commit; bool completed = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto w) || w.EndsUtcTicks <= DateTime.UtcNow.Ticks)
                {
                    // Want vanished / expired in the gap - hand the seller's goods back (same keys) and bail.
                    foreach (KeyValuePair<string, int> t in taken)
                        Items.KmhPayloadEscrow.DeliverCompact(seller, t.Key, t.Value, $"want #{wantId} fulfill returned", "seller return failed");
                    r.Reason = "That want just closed - your items were returned.";
                    return r;
                }
                int remaining = w.QtyWanted - w.QtyFilled;
                commit = Math.Min(fillable, remaining);
                if (commit <= 0)
                {
                    foreach (KeyValuePair<string, int> t in taken)
                        Items.KmhPayloadEscrow.DeliverCompact(seller, t.Key, t.Value, $"want #{wantId} fulfill returned", "seller return failed");
                    r.Reason = "That want was just filled by someone else - your items were returned.";
                    return r;
                }
                w.QtyFilled      += commit;
                w.EscrowRemaining -= (long)unitPrice * commit;
                completed = w.QtyFilled >= w.QtyWanted;
                if (completed) _byId.Remove(wantId);
                // Durable before the goods and payout move, or a restart restores the want's full escrow after the seller was paid from it.
                if (!SaveToDisk())
                {
                    w.QtyFilled       -= commit;
                    w.EscrowRemaining += (long)unitPrice * commit;
                    if (completed) _byId[wantId] = w;
                    foreach (KeyValuePair<string, int> t in taken)
                        Items.KmhPayloadEscrow.DeliverCompact(seller, t.Key, t.Value, $"want #{wantId} fulfill returned", "seller return failed");
                    r.Reason = "The server couldn't record that fill - your items were returned. Try again shortly.";
                    return r;
                }
            }

            // Surplus from a lost race goes back under the same keys, so the seller's material is never altered.
            int toBuyer = commit;
            foreach (KeyValuePair<string, int> t in taken)
            {
                int give = Math.Min(t.Value, toBuyer);
                if (give > 0) { Items.KmhPayloadEscrow.DeliverCompact(buyer, t.Key, give, $"want #{wantId} received", "buyer delivery failed"); toBuyer -= give; }
                int back = t.Value - give;
                if (back > 0) Items.KmhPayloadEscrow.DeliverCompact(seller, t.Key, back, $"want #{wantId} surplus returned", "seller return failed");
            }

            // Pay the seller from escrow via the authoritative split (escrow == payout + server tax + guild tax).
            long gross = (long)unitPrice * commit;
            Economy.SaleSplit split = Economy.SaleSplit.Compute(seller, itemDef, gross, demandDrift: false, worldPayoutEvents: false);
            split.CommitBoost($"want #{wantId} world-event sale boost");
            // No settlement key: a want fulfil has no durable row that could replay it.
            split.Settle(seller, $"want #{wantId} fulfilled", null);
            long net = split.SellerPayout;
            Items.KmhPayloadEscrow.DeliverSilver(seller, net, $"want #{wantId} fulfilled", "want payout could not be credited");

            Diagnostics.ServerLog.Info($"WantBoard: {seller} fulfilled {commit}x {itemDef} for want #{wantId} (net {net}, tax {split.ServerTax}, guild {split.GuildTax})");

            r.Ok = true; r.FilledQty = commit; r.Completed = completed;
            r.Buyer = buyer; r.ItemDefName = itemDef; r.UnitPrice = unitPrice; r.SellerNet = net;
            return r;
        }

        // State that does not meet the want is never withdrawn, so the buyer cannot receive what the want refused.
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
                int askable   = Math.Min(takenUnits, remaining);
                // An atomic stack larger than what is left cannot be split, so counting it would bill for goods that go back.
                commit = Items.KmhPayloadEscrow.DeliverableUnits(taken, askable);
                if (commit <= 0)
                {
                    Items.KmhPayloadEscrow.RefundTo(seller, taken, $"want #{wantId} returned");
                    r.Reason = askable <= 0
                        ? "That want was just filled - your items were returned."
                        : "Those items can't be split to fit what's left of this want - they were returned.";
                    return r;
                }
                w.QtyFilled += commit; w.EscrowRemaining -= (long)unitPrice * commit;
                completed = w.QtyFilled >= w.QtyWanted; if (completed) _byId.Remove(wantId);
                if (!SaveToDisk())
                {
                    w.QtyFilled       -= commit;
                    w.EscrowRemaining += (long)unitPrice * commit;
                    if (completed) _byId[wantId] = w;
                    Items.KmhPayloadEscrow.RefundTo(seller, taken, $"want #{wantId} returned");
                    r.Reason = "The server couldn't record that fill - your items were returned. Try again shortly.";
                    return r;
                }
            }

            // A deposit that cannot land parks in the recovery queue rather than vanishing.
            int toBuyer = commit;
            foreach (Items.KmhThingPayload p in taken)
            {
                if (toBuyer >= p.StackCount) { Items.KmhPayloadEscrow.Deliver(buyer, p, $"want #{wantId} received", $"want #{wantId} received"); toBuyer -= p.StackCount; }
                else if (toBuyer > 0 && Items.KmhPayloadEscrow.IsSplittable(p))
                {
                    Items.KmhPayloadEscrow.Deliver(buyer,  Items.KmhPayloadEscrow.Clone(p, toBuyer), $"want #{wantId} received", $"want #{wantId} received");
                    Items.KmhPayloadEscrow.Deliver(seller, Items.KmhPayloadEscrow.Clone(p, p.StackCount - toBuyer), $"want #{wantId} surplus returned", $"want #{wantId} surplus returned");
                    toBuyer = 0;
                }
                else Items.KmhPayloadEscrow.Deliver(seller, p, $"want #{wantId} surplus returned", $"want #{wantId} surplus returned");
            }

            long gross = (long)unitPrice * commit;
            Economy.SaleSplit split = Economy.SaleSplit.Compute(seller, itemDef, gross, demandDrift: false, worldPayoutEvents: false);
            split.CommitBoost($"want #{wantId} world-event sale boost");
            // No settlement key: a want fulfil has no durable row that could replay it.
            split.Settle(seller, $"want #{wantId} fulfilled", null);
            long net = split.SellerPayout;
            Items.KmhPayloadEscrow.DeliverSilver(seller, net, $"want #{wantId} fulfilled", "want payout could not be credited");
            Diagnostics.ServerLog.Info($"WantBoard: {seller} fulfilled {commit}x {itemDef} (full-state) for want #{wantId} (net {net}, tax {split.ServerTax}, guild {split.GuildTax})");

            r.Ok = true; r.FilledQty = commit; r.Completed = completed;
            r.Buyer = buyer; r.ItemDefName = itemDef; r.UnitPrice = unitPrice; r.SellerNet = net;
            return r;
        }


        public static (bool ok, string reason) Cancel(string buyer, long wantId)
        {
            WantDto w; long refund;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w)) return (false, "Want not found.");
                if (!Eq(w.BuyerUsername, buyer))        return (false, "That isn't your want.");
                refund = w.EscrowRemaining;
                _byId.Remove(wantId);
                // Durable before the refund, or a restart reopens a want whose escrow is already back with the buyer.
                if (!SaveToDisk()) { _byId[wantId] = w; return (false, "The server couldn't save that - nothing changed. Try again shortly."); }
            }
            if (refund > 0) Items.KmhPayloadEscrow.DeliverSilver(buyer, refund, $"want #{wantId} cancelled", "want refund could not be credited");
            return (true, refund > 0
                ? $"Want cancelled - {Util.SilverFmt.Format(refund)} refunded to your treasury."
                : "Want cancelled.");
        }


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

        // Reuses the expiry refund exactly, so an admin cancel cannot pay out differently from a natural one.
        public static ExpireOutcome AdminCancel(long wantId)
        {
            ExpireOutcome o = new ExpireOutcome();
            WantDto w;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w)) return o;
                _byId.Remove(wantId);
                if (!SaveToDisk()) { _byId[wantId] = w; return o; }
            }
            o.Done = true; o.Buyer = w.BuyerUsername; o.Refunded = w.EscrowRemaining;
            bool paid = RefundEscrow(w.BuyerUsername, w.EscrowRemaining, $"want #{w.Id} cancelled by admin");
            if (paid)
                Diagnostics.ServerLog.Info($"WantBoard: want #{w.Id} cancelled by admin, refunded {w.EscrowRemaining} to {w.BuyerUsername}");
            return o;
        }

        // False when the escrow did not reach the buyer, so no caller logs a refund that never happened.
        private static bool RefundEscrow(string buyer, long amount, string note)
        {
            if (Items.KmhPayloadEscrow.DeliverSilver(buyer, amount, note, "want escrow could not be refunded")) return true;
            Diagnostics.ServerLog.Error($"WantBoard: {amount} silver for '{buyer}' could not be refunded ({note}) - held in recovery.");
            return false;
        }


        // Unspent escrow only: the filled part already left as payment.
        public static long EscrowValueFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long v = 0;
            lock (_lock)
                foreach (WantDto w in _byId.Values)
                    if (w != null && w.EscrowRemaining > 0
                        && string.Equals(w.BuyerUsername, username, StringComparison.OrdinalIgnoreCase))
                        v += w.EscrowRemaining;
            return v;
        }

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
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w)) return o;
                _byId.Remove(wantId);
                // Durable before the refund, or the sweeper expires and refunds the same want again after a restart.
                if (!SaveToDisk()) { _byId[wantId] = w; return o; }
            }
            o.Done = true; o.Buyer = w.BuyerUsername; o.Refunded = w.EscrowRemaining;
            bool paid = RefundEscrow(w.BuyerUsername, w.EscrowRemaining, $"want #{w.Id} expired");
            if (paid)
                Diagnostics.ServerLog.Info($"WantBoard: want #{w.Id} expired, refunded {w.EscrowRemaining} to {w.BuyerUsername}");
            return o;
        }

        // Clones every field: a hand-written list silently drops the buyer's match constraints from each snapshot.
        private static WantDto Clone(WantDto w) => w.ShallowClone();

        internal static WantDto CopyForTest(WantDto w) => Clone(w);
    }
}
