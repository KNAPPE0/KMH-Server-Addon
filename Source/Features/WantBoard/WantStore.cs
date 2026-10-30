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
        // Server-side only, and saved with the wants: which fills this file committed, and which row funded each post.
        private static readonly List<string> _recentFills = new List<string>();
        private const int MaxRecentFills = 2000;
        private static readonly Dictionary<long, string> _postTx = new Dictionary<long, string>();

        // Written in the same save that closes a want: closing first and refunding after lost the escrow to a crash in between.
        internal sealed class PendingRefund
        {
            [Newtonsoft.Json.JsonProperty("marker")] public string Marker { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("buyer")]  public string Buyer  { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("silver")] public long   Silver { get; set; }
            [Newtonsoft.Json.JsonProperty("note")]   public string Note   { get; set; } = "";
        }
        private static readonly Dictionary<string, PendingRefund> _pendingRefunds = new Dictionary<string, PendingRefund>(StringComparer.Ordinal);

        private sealed class PersistedState
        {
            public List<WantDto> Wants  { get; set; } = new List<WantDto>();
            public long          NextId { get; set; } = 1;
            public List<string>  RecentFills { get; set; } = new List<string>();
            public Dictionary<long, string> PostTx { get; set; } = new Dictionary<long, string>();
            public List<PendingRefund> PendingRefunds { get; set; } = new List<PendingRefund>();
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.WantsFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _byId.Clear();
                if (s.Wants != null) foreach (WantDto w in s.Wants) if (w != null) _byId[w.Id] = w;
                _nextId = Math.Max(1, s.NextId);
                _recentFills.Clear();
                if (s.RecentFills != null) _recentFills.AddRange(s.RecentFills);
                _postTx.Clear();
                if (s.PostTx != null) foreach (KeyValuePair<long, string> kv in s.PostTx) _postTx[kv.Key] = kv.Value;
                _pendingRefunds.Clear();
                if (s.PendingRefunds != null)
                    foreach (PendingRefund p in s.PendingRefunds)
                        if (p != null && !string.IsNullOrEmpty(p.Marker)) _pendingRefunds[p.Marker] = p;
            }
            Diagnostics.ServerLog.Diag($"WantBoard: loaded {s.Wants?.Count ?? 0} open want(s)");
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
                foreach (long id in ids) ForgetLocked(id);
            }
            if (ids.Count > 0) SaveToDisk();
            return ids.Count;
        }

        public static void ClearForNewSeason()
        {
            // Refunds owed to the old season go with it: paid at the next boot they would land in the new season, and
            // ids restart at 1, so an old "want:1:refund" marker would make the new want #1's refund a no-op.
            lock (_lock) { _byId.Clear(); _postTx.Clear(); _pendingRefunds.Clear(); _nextId = 1; }
            SaveToDisk();
        }

        // False means in-memory only; escrow lives here, so an unwritten fill would both pay the seller and refund the buyer.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock)
            {
                s.Wants.AddRange(KmhDeepCopy.OfList(_byId.Values)); s.NextId = _nextId;
                s.RecentFills.AddRange(KmhDeepCopy.OfList(_recentFills));
                foreach (KeyValuePair<long, string> kv in _postTx) s.PostTx[kv.Key] = kv.Value;
                s.PendingRefunds.AddRange(KmhDeepCopy.OfList(_pendingRefunds.Values));
                seq = JsonFileStore.NextSequence();
            }
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


        // unitPriceMilli is the canonical price (1000 = 1 silver); -1 is an old client, whose whole-silver price is used.
        public static (long id, string reason) Post(string buyer, string itemDef, int qtyWanted, int unitPrice,
            int durationHours, string visibility,
            int minQuality = 0, string requiredStuff = "", bool allowComplex = false, bool allowTainted = false, bool allowDamaged = false,
            long unitPriceMilli = -1)
        {
            if (string.IsNullOrEmpty(buyer))  return (0, "No buyer.");
            if (string.IsNullOrEmpty(itemDef)) return (0, "No item.");
            if (qtyWanted <= 0) return (0, "Quantity must be > 0.");
            if (itemDef.Length > Util.KmhSafe.MaxDefLength || (requiredStuff ?? "").Length > Util.KmhSafe.MaxDefLength)
                return (0, "That item name is too long.");
            // The name is drawn on every player's board, so it must be an item name and nothing that renders.
            if (!Util.KmhSafe.IsItemKey(itemDef) || (!string.IsNullOrEmpty(requiredStuff) && !Util.KmhSafe.IsDefName(requiredStuff)))
                return (0, "That isn't an item name.");

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            // The marketplace's own price bounds, so a want can offer what a listing can ask.
            long milli = unitPriceMilli > 0 ? unitPriceMilli : (long)Math.Max(0, unitPrice) * 1000;
            milli = Math.Min(Math.Max(milli, Math.Max(1, cfg.Marketplace.MinUnitPriceMilli)), Math.Max(1, cfg.Marketplace.MaxUnitPriceMilli));
            unitPrice = (int)Math.Max(1, Math.Round(milli / 1000.0));   // for display, old clients and extensions
            int hours = durationHours > 0 ? Math.Min(durationHours, cfg.Wants.MaxDurationHours) : cfg.Wants.DefaultDurationHours;

            long total = CostUpTo(milli, qtyWanted);
            if (total > int.MaxValue) return (0, "That want is too large - lower the quantity or price.");

            lock (_lock)
            {
                int open = 0;
                foreach (WantDto w in _byId.Values) if (Eq(w.BuyerUsername, buyer)) open++;
                if (open >= cfg.Wants.MaxOpenPerUser)
                    return (0, $"You already have the max {cfg.Wants.MaxOpenPerUser} open wants.");
            }

            // Runs before escrow so an extension's denial has no side effects.
            KMH.Sdk.Server.Hooks.KmhHookVerdict verdict = Extensibility.KmhHooks.Instance.CheckWant(
                new KMH.Sdk.Server.Hooks.KmhWantContext(buyer, itemDef, qtyWanted, unitPrice));
            if (verdict.Denied) return (0, verdict.Reason);

            // Named before the debit, and the debit carries the row's marker: a crash anywhere leaves the silver in
            // the vault, in this want, or owed back by the row.
            Transactions.KmhTransaction tx = Transactions.KmhTransactionRepository.OpenTake(
                buyer, Transactions.KmhTxType.Listing, $"want post {qtyWanted}x {itemDef}", 0, total, null, null,
                destination: "want", targetKind: Transactions.KmhTransactionRepository.TargetWantPost);
            if (tx == null) return (0, "The server couldn't record that want - nothing was taken. Try again shortly.");
            if (!Treasury.TreasuryStore.WithdrawSilverForTxn(buyer, (int)total, tx.TakeMarker, note: "want-board escrow"))
            {
                Transactions.KmhTransactionRepository.Abort(tx);
                return (0, "Your treasury doesn't have enough silver to back that want.");
            }
            AtPostPhaseForTest?.Invoke("debited");

            long id = 0, now = DateTime.UtcNow.Ticks;
            bool overCap;
            lock (_lock)
            {
                // The count above ran before escrow released the lock, so a concurrent post could have taken the last slot.
                int open = 0;
                foreach (WantDto w in _byId.Values) if (Eq(w.BuyerUsername, buyer)) open++;
                overCap = open >= cfg.Wants.MaxOpenPerUser;
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
                        UnitPriceMilli   = milli,
                        EscrowRemaining  = total,
                        ListedUtcTicks   = now,
                        EndsUtcTicks     = now + TimeSpan.FromHours(hours).Ticks,
                        Visibility       = Guilds.GuildVisibility.Normalize(visibility),
                        MinQuality       = minQuality < 0 ? 0 : minQuality,
                        RequiredStuff    = requiredStuff ?? "",
                        AllowComplex     = NeedsPayloadFulfil(allowComplex, allowTainted, allowDamaged),
                        AllowTainted     = allowTainted,
                        AllowDamaged     = allowDamaged,
                    };
                    _postTx[id] = tx.Id;
                }
            }
            if (overCap)
            {
                RefundPost(tx);
                return (0, $"You already have the max {cfg.Wants.MaxOpenPerUser} open wants - your escrow was refunded.");
            }
            // The escrow has already left the buyer's treasury; a want the disk never took would strand it there.
            if (!SaveToDisk())
            {
                lock (_lock) ForgetLocked(id);
                RefundPost(tx);
                return (0, "The server couldn't save that want - your escrow was refunded. Try again shortly.");
            }
            AtPostPhaseForTest?.Invoke("saved");
            Transactions.KmhTransactionRepository.Settle(tx);
            return (id, $"Want posted: up to {qtyWanted}x at {PriceText(milli)} each ({Util.SilverFmt.Format(total)} escrowed).");
        }

        // Rounded up on the cumulative count, so each fill pays the difference and a want's fills sum to its escrow exactly.
        internal static long CostUpTo(long milli, long units) => units <= 0 || milli <= 0 ? 0 : (milli * units + 999) / 1000;

        internal static long FillGross(long milli, int filledBefore, int units) => CostUpTo(milli, (long)filledBefore + units) - CostUpTo(milli, filledBefore);

        internal static string PriceText(long milli)
            => milli % 1000 == 0 ? Util.SilverFmt.Format(milli / 1000)
                                 : "$" + (milli / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);


        // On the row's own refund marker, so this and boot recovery return the escrow once between them.
        private static void RefundPost(Transactions.KmhTransaction tx)
        {
            if (!Transactions.KmhTransactionRepository.Compensate(tx))
                Diagnostics.ServerLog.Error($"WantBoard: want post {tx.Id} could not be refunded yet - the row stays open and the next boot refunds it.");
        }

        public sealed class FulfillResult
        {
            public bool   Ok;
            public string Reason      = "";
            public int    FilledQty;
            public bool   Completed;
            public string Buyer       = "";
            public string ItemDefName = "";
            public int    UnitPrice;
            public long   UnitPriceMilli;
            public long   SellerNet;
        }

        // Taint, damage and gear state exist only on payloads; material and quality ride in a compact key and must not force it.
        internal static bool NeedsPayloadFulfil(bool allowComplex, bool allowTainted, bool allowDamaged)
            => allowComplex || allowTainted || allowDamaged;

        // Fires once inside Fulfill's race window; the test clears it so a later suite never sees it.
        internal static Action DuringFulfillForTest;

        // Fired between the steps of a fulfil, which is the only window a crash can land in.
        internal static Action<string> AtFulfillPhaseForTest;
        internal static Action<string> AtPostPhaseForTest;

        // Items leave the seller first, and a lost race returns their own goods rather than minting any.
        public static FulfillResult Fulfill(string seller, long wantId, int qty)
        {
            FulfillResult r = new FulfillResult();
            if (string.IsNullOrEmpty(seller) || qty <= 0) { r.Reason = "Bad fulfill."; return r; }

            // A state-constrained want is filled from payloads only, or the buyer could be handed what they excluded.
            bool complexWant = false;
            lock (_lock) { if (_byId.TryGetValue(wantId, out WantDto peek)) complexWant = peek.AllowComplex; }
            if (complexWant) return FulfillFromPayloads(seller, wantId, qty);

            if (!PreCheck(seller, wantId, qty, r, out Terms terms)) return r;

            // The window this exists to expose: between the pre-check above and the commit below, another filler
            // can take what was left. Nothing production calls this, and the commit re-clamps for exactly that.
            DuringFulfillForTest?.Invoke();

            // Planned first, so the row names the exact stacks before any of them leave the vault.
            List<KeyValuePair<string, int>> plan = Treasury.TreasuryStore.PlanMatching(seller, terms.ItemDef, terms.MinQuality,
                                                                                       terms.Fillable, terms.RequiredStuff);
            // The same def can sit in the vault as payloads, so an empty compact result is not an empty treasury.
            if (plan == null) return FulfillFromPayloads(seller, wantId, qty);
            var planned = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in plan) { planned.TryGetValue(kv.Key, out int n); planned[kv.Key] = Util.KmhSafe.AddSaturating(n, kv.Value); }

            Transactions.KmhTransaction tx = Transactions.KmhTransactionRepository.OpenTake(
                seller, Transactions.KmhTxType.WantBoardFill, $"want #{wantId} fill", wantId, 0, planned, null,
                destination: "want", targetKind: Transactions.KmhTransactionRepository.TargetWantFill);
            if (tx == null) { r.Reason = "The server couldn't record that fill - nothing was taken."; return r; }
            if (!Treasury.TreasuryStore.WithdrawItemsForTxn(seller, plan, tx.TakeMarker, $"want #{wantId} fulfill"))
            {
                Transactions.KmhTransactionRepository.Abort(tx);
                return FulfillFromPayloads(seller, wantId, qty);
            }
            return CommitFill(r, tx, terms, wantId, isPayload: false);
        }

        private sealed class Terms
        {
            public int Fillable, UnitPrice, MinQuality;
            public string ItemDef, Buyer, RequiredStuff;
            public bool AllowTainted, AllowDamaged;
        }

        // Cheap pre-check so nothing is withdrawn for a fill that cannot happen.
        private static bool PreCheck(string seller, long wantId, int qty, FulfillResult r, out Terms terms)
        {
            terms = null;
            long now = DateTime.UtcNow.Ticks;
            string sellerGuild = Guilds.GuildStore.CurrentGuildOf(seller); // resolve before the lock
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto pw)) { r.Reason = "Want not found."; return false; }
                if (pw.EndsUtcTicks <= now)        { r.Reason = "This want has expired.";            return false; }
                if (Eq(pw.BuyerUsername, seller))  { r.Reason = "You can't fulfill your own want.";   return false; }
                // Visibility is enforced on the action too, since a crafted client can target a hidden id.
                if (!Guilds.GuildVisibility.IsVisibleTo(pw.BuyerTreasuryKey, pw.Visibility, sellerGuild, prefetched: true))
                { r.Reason = "You can't fulfill that want."; return false; }
                int remaining = pw.QtyWanted - pw.QtyFilled;
                if (remaining <= 0) { r.Reason = "This want is already filled."; return false; }
                terms = new Terms
                {
                    Fillable = Math.Min(qty, remaining), ItemDef = pw.ItemDefName, UnitPrice = pw.UnitPriceSilver,
                    Buyer = pw.BuyerUsername, RequiredStuff = pw.RequiredStuff, MinQuality = pw.MinQuality,
                    AllowTainted = pw.AllowTainted, AllowDamaged = pw.AllowDamaged,
                };
            }
            return true;
        }

        // State that does not meet the want is never withdrawn, so the buyer cannot receive what the want refused.
        private static FulfillResult FulfillFromPayloads(string seller, long wantId, int qty)
        {
            FulfillResult r = new FulfillResult();
            if (!PreCheck(seller, wantId, qty, r, out Terms terms)) return r;

            // The instances are known only once taken, so the id comes first and the vault keeps a pending take
            // until the row naming them is written.
            Transactions.KmhTransaction tx = Transactions.KmhTransactionRepository.PrepareTake(
                seller, Transactions.KmhTxType.WantBoardFill, $"want #{wantId} fill", wantId);
            if (tx == null) { r.Reason = "The server couldn't record that fill - nothing was taken."; return r; }
            tx.Destination = "want";
            tx.TargetKind  = Transactions.KmhTransactionRepository.TargetWantFill;
            List<Items.KmhThingPayload> taken = Treasury.TreasuryStore.TryWithdrawMatchingPayloads(
                seller, terms.ItemDef, terms.RequiredStuff, terms.MinQuality, terms.AllowTainted, terms.AllowDamaged,
                terms.Fillable, $"want #{wantId} fulfill", tx.TakeMarker, tx.RefundMarker);
            if (taken.Count == 0)
            { r.Reason = $"You have no matching {ItemLabels.ItemLabelCache.LabelFor(terms.ItemDef)} that meets this want (material/quality/condition)."; return r; }
            if (!Transactions.KmhTransactionRepository.CommitTake(tx, taken))
            {
                Treasury.TreasuryStore.ReturnPendingTake(seller, tx.TakeMarker, tx.RefundMarker, taken, $"want #{wantId} fulfill returned");
                r.Reason = "The server couldn't record that fill - your items were returned.";
                return r;
            }
            // Safe to fail: the row now names the take, so recovery drops the entry without paying it.
            Treasury.TreasuryStore.ClearPendingTake(seller, tx.TakeMarker);
            return CommitFill(r, tx, terms, wantId, isPayload: true);
        }

        // The take is marked and named by tx. From here every exit either records the fill in this store's save,
        // or hands the goods back; recovery reads that save to tell the two apart.
        private static FulfillResult CommitFill(FulfillResult r, Transactions.KmhTransaction tx, Terms terms, long wantId, bool isPayload)
        {
            int takenUnits = 0;
            foreach (int q in tx.EscrowItems.Values) takenUnits += q;
            foreach (Items.KmhThingPayload p in tx.EscrowPayloads) takenUnits += p.StackCount;

            int commit, askable, filledBefore;
            long milli;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto w) || w.EndsUtcTicks <= DateTime.UtcNow.Ticks)
                { UndoFill(tx); r.Reason = "That want just closed - your items were returned."; return r; }
                filledBefore = w.QtyFilled;
                milli = w.EffectiveMilli;
                askable = Math.Min(takenUnits, w.QtyWanted - w.QtyFilled);
                // An atomic stack larger than what is left cannot be split, so counting it would bill for goods that go back.
                commit = isPayload ? Items.KmhPayloadEscrow.DeliverableUnits(tx.EscrowPayloads, askable) : askable;
            }
            if (commit <= 0)
            {
                UndoFill(tx);
                r.Reason = isPayload && askable > 0
                    ? "Those items can't be split to fit what's left of this want - they were returned."
                    : "That want was just filled by someone else - your items were returned.";
                return r;
            }

            long gross = FillGross(milli, filledBefore, commit);
            Economy.SaleSplit split = Economy.SaleSplit.Compute(tx.Player, terms.ItemDef, gross, demandDrift: false, worldPayoutEvents: false);
            // Taken before the legs are written, so the payout they record is what the pool could actually fund.
            split.CommitBoost($"want #{wantId} world-event sale boost");
            SplitLegs(tx, commit, isPayload);
            tx.CounterPlayer = terms.Buyer;
            tx.PayoutSilver  = split.SellerPayout;
            tx.HouseTax      = split.ServerTax + split.PoolReturn;
            tx.GuildTax      = string.IsNullOrEmpty(split.GuildName) ? 0 : split.GuildTax;
            tx.TaxGuild      = split.GuildName ?? "";
            tx.TaxGuildIdentity = Guilds.GuildIdentity.Of(split.GuildName) ?? "";
            AtFulfillPhaseForTest?.Invoke("taken");
            if (!Transactions.KmhTransactionRepository.Update(tx))
            { UndoFill(tx, split.PoolBoost); r.Reason = "The server couldn't record that fill - your items were returned."; return r; }
            AtFulfillPhaseForTest?.Invoke("legs-written");

            bool completed;
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out WantDto w) || w.EndsUtcTicks <= DateTime.UtcNow.Ticks
                    || w.QtyWanted - w.QtyFilled < commit)
                { UndoFill(tx, split.PoolBoost); r.Reason = "That want was just filled by someone else - your items were returned."; return r; }
                // A fractional price pays by how much is already filled, so a fill priced before another landed is redone.
                if (milli % 1000 != 0 && w.QtyFilled != filledBefore)
                { UndoFill(tx, split.PoolBoost); r.Reason = "Someone filled part of that want at the same moment - your items were returned. Try again."; return r; }
                _postTx.TryGetValue(wantId, out string postTx);
                w.QtyFilled       += commit;
                w.EscrowRemaining -= gross;
                completed = w.QtyFilled >= w.QtyWanted;
                if (completed) ForgetLocked(wantId);
                NoteFillLocked(tx.Id);
                // The fill's one commit point: durable here means recovery finishes it, absent means it is undone.
                if (!SaveToDisk())
                {
                    w.QtyFilled       -= commit;
                    w.EscrowRemaining += gross;
                    if (completed) { _byId[wantId] = w; if (postTx != null) _postTx[wantId] = postTx; }
                    _recentFills.Remove(tx.Id);
                    UndoFill(tx, split.PoolBoost);
                    r.Reason = "The server couldn't record that fill - your items were returned. Try again shortly.";
                    return r;
                }
            }
            AtFulfillPhaseForTest?.Invoke("fill-recorded");

            long net = tx.PayoutSilver;
            CompleteFill(tx);
            Diagnostics.ServerLog.Info($"WantBoard: {tx.Player} fulfilled {commit}x {terms.ItemDef}{(isPayload ? " (full-state)" : "")} for want #{wantId} (net {net}, tax {split.ServerTax}, guild {split.GuildTax})");

            r.Ok = true; r.FilledQty = commit; r.Completed = completed;
            r.Buyer = terms.Buyer; r.ItemDefName = terms.ItemDef; r.UnitPrice = terms.UnitPrice; r.UnitPriceMilli = milli; r.SellerNet = net;
            return r;
        }

        // What was taken sits on the primary leg; the buyer's share moves to the counter leg and the rest stays as surplus.
        private static void SplitLegs(Transactions.KmhTransaction tx, int toBuyer, bool isPayload)
        {
            tx.CounterItems.Clear(); tx.CounterPayloads.Clear();
            if (!isPayload)
            {
                foreach (string key in new List<string>(tx.EscrowItems.Keys))
                {
                    int have = tx.EscrowItems[key];
                    int give = Math.Min(have, toBuyer);
                    if (give <= 0) continue;
                    tx.CounterItems[key] = give;
                    if (have - give > 0) tx.EscrowItems[key] = have - give; else tx.EscrowItems.Remove(key);
                    toBuyer -= give;
                }
                return;
            }
            var surplus = new List<Items.KmhThingPayload>();
            foreach (Items.KmhThingPayload p in tx.EscrowPayloads)
            {
                if (toBuyer >= p.StackCount) { tx.CounterPayloads.Add(p); toBuyer -= p.StackCount; }
                else if (toBuyer > 0 && Items.KmhPayloadEscrow.IsSplittable(p))
                {
                    tx.CounterPayloads.Add(Items.KmhPayloadEscrow.Clone(p, toBuyer));
                    surplus.Add(Items.KmhPayloadEscrow.Clone(p, p.StackCount - toBuyer));
                    toBuyer = 0;
                }
                else surplus.Add(p);
            }
            tx.EscrowPayloads.Clear();
            tx.EscrowPayloads.AddRange(surplus);
        }

        // Each leg rides its own marker, so boot recovery re-running this after a crash pays every leg at most once.
        // False leaves the row open for the next boot to finish.
        internal static bool CompleteFill(Transactions.KmhTransaction tx)
        {
            if (tx == null) return false;
            string label = $"want #{tx.TargetId}";
            bool ok = true;
            if (tx.CounterItems.Count > 0 || tx.CounterPayloads.Count > 0)
                ok &= Treasury.TreasuryStore.DepositEscrowOnce(tx.CounterPlayer, tx.CounterMarker, 0, tx.CounterItems,
                                                               tx.CounterPayloads, $"{label} received");
            AtFulfillPhaseForTest?.Invoke("goods-delivered");
            if (tx.EscrowItems.Count > 0 || tx.EscrowPayloads.Count > 0)
                ok &= Treasury.TreasuryStore.DepositEscrowOnce(tx.Player, tx.SurplusMarker, 0, tx.EscrowItems,
                                                               tx.EscrowPayloads, $"{label} surplus returned");
            // The same keys SaleSplit.Settle uses, so the taxes land once whoever gets here first. A leg that landed
            // is cleared, so the row only ever names what it still owes; its marker stops a replay regardless.
            if (tx.HouseTax > 0)
            {
                bool paid = Marketplace.MarketplaceStore.TryCreditHousePoolOnce(tx.Id + ":house", tx.HouseTax, $"{label} fulfilled");
                if (paid) tx.HouseTax = 0; else ok = false;
            }
            if (tx.GuildTax > 0 && !string.IsNullOrEmpty(tx.TaxGuild))
            {
                bool paid = Treasury.TreasuryStore.SettleGuildTaxOnce(tx.TaxGuild, tx.TaxGuildIdentity, tx.Id + ":guild",
                                                                      tx.GuildTax, tx.Player, $"{label} fulfilled - guild sale tax");
                if (paid) tx.GuildTax = 0; else ok = false;
            }
            AtFulfillPhaseForTest?.Invoke("taxes-taken");
            if (tx.PayoutSilver > 0)
            {
                bool paid = Treasury.TreasuryStore.DepositEscrowOnce(tx.Player, tx.PayoutMarker, tx.PayoutSilver, null, null, $"{label} fulfilled");
                if (paid) tx.PayoutSilver = 0; else ok = false;
            }
            if (!ok)
            {
                // The legs that landed are written as cleared, so the next boot does not lean on a marker that may have aged out.
                Transactions.KmhTransactionRepository.Update(tx);
                Diagnostics.ServerLog.Error($"WantBoard: {label} fill {tx.Id} could not pay every leg - the row stays open and the next boot finishes it.");
                return false;
            }
            Transactions.KmhTransactionRepository.Settle(tx);
            return true;
        }

        // Nothing was recorded against the want, so every unit taken goes back to the seller, once. A boost the
        // live path already drew goes back to the pool; one drawn just before a crash is the pool's loss, not a player's.
        internal static bool UndoFill(Transactions.KmhTransaction tx, long boostDrawn = 0)
        {
            if (tx == null) return false;
            if (boostDrawn > 0) Marketplace.MarketplaceStore.ReturnToHousePool(boostDrawn, $"want #{tx.TargetId} fill undone - boost returned");
            var items = new Dictionary<string, int>(tx.EscrowItems, StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in tx.CounterItems) { items.TryGetValue(kv.Key, out int n); items[kv.Key] = Util.KmhSafe.AddSaturating(n, kv.Value); }
            var loads = new List<Items.KmhThingPayload>(tx.EscrowPayloads);
            loads.AddRange(tx.CounterPayloads);
            bool taken = Treasury.TreasuryStore.KnowsTxnMarker(tx.Player, tx.TakeMarker);
            if (taken && (items.Count > 0 || loads.Count > 0)
                && !Treasury.TreasuryStore.DepositEscrowOnce(tx.Player, tx.RefundMarker, 0, items, loads,
                                                            $"want #{tx.TargetId} fill returned"))
            {
                Diagnostics.ServerLog.Error($"WantBoard: want #{tx.TargetId} fill {tx.Id} could not return the seller's goods - the row stays open.");
                return false;
            }
            Treasury.TreasuryStore.ClearPendingTake(tx.Player, tx.TakeMarker);
            Transactions.KmhTransactionRepository.Abort(tx);
            return true;
        }

        // Kept only long enough for a restart to see it: a fill row is settled moments after its fill.
        private static void NoteFillLocked(string txId)
        {
            _recentFills.Add(txId);
            while (_recentFills.Count > MaxRecentFills) _recentFills.RemoveAt(0);
        }

        internal static bool RecordedFill(string txId)
        {
            if (string.IsNullOrEmpty(txId)) return false;
            lock (_lock) return _recentFills.Contains(txId);
        }

        // The row that funded an open want, so recovery can see that a post reached the disk.
        internal static bool HoldsPost(string txId)
        {
            if (string.IsNullOrEmpty(txId)) return false;
            lock (_lock)
                foreach (KeyValuePair<long, string> kv in _postTx)
                    if (kv.Value == txId && _byId.ContainsKey(kv.Key)) return true;
            return false;
        }

        private static void ForgetLocked(long wantId)
        {
            _byId.Remove(wantId);
            _postTx.Remove(wantId);
        }

        public static (bool ok, string reason) Cancel(string buyer, long wantId)
        {
            WantDto w; long refund; string marker;
            string note = $"want #{wantId} cancelled";
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w)) return (false, "Want not found.");
                if (!Eq(w.BuyerUsername, buyer))        return (false, "That isn't your want.");
                refund = w.EscrowRemaining;
                if (!TryCloseLocked(wantId, w, note, out marker))
                    return (false, "The server couldn't save that - nothing changed. Try again shortly.");
            }
            if (refund <= 0) return (true, "Want cancelled.");
            return (true, SettleRefund(marker, w.BuyerUsername, refund, note)
                ? $"Want cancelled - {Util.SilverFmt.Format(refund)} refunded to your treasury."
                : $"Want cancelled - the {Util.SilverFmt.Format(refund)} refund is recorded and reaches your treasury after the server's next restart.");
        }

        // Durable before the refund, or a restart reopens (or the sweeper re-expires) a want whose escrow is already back.
        private static bool TryCloseLocked(long wantId, WantDto w, string note, out string marker)
        {
            ForgetLocked(wantId);
            marker = RecordRefundLocked(wantId, w.BuyerUsername, w.EscrowRemaining, note);
            if (SaveToDisk()) return true;
            _byId[wantId] = w;
            if (marker != null) _pendingRefunds.Remove(marker);
            return false;
        }

        private static string RecordRefundLocked(long wantId, string buyer, long silver, string note)
        {
            if (silver <= 0 || string.IsNullOrEmpty(buyer)) return null;
            string marker = $"want:{wantId}:refund";
            _pendingRefunds[marker] = new PendingRefund { Marker = marker, Buyer = buyer, Silver = silver, Note = note };
            return marker;
        }

        internal static Action<string> AtRefundPhaseForTest;

        // On the marker, so a boot replaying it pays nothing twice; a refused credit keeps the record for that boot.
        private static bool SettleRefund(string marker, string buyer, long silver, string note)
        {
            if (marker == null) return true;
            AtRefundPhaseForTest?.Invoke("refund-recorded");
            if (!Treasury.TreasuryStore.DepositEscrowOnce(buyer, marker, silver, null, null, note))
            {
                Diagnostics.ServerLog.Error($"WantBoard: {silver} silver for '{buyer}' could not be refunded ({note}) - held as {marker} and retried at the next boot.");
                return false;
            }
            AtRefundPhaseForTest?.Invoke("refund-credited");
            lock (_lock) _pendingRefunds.Remove(marker);
            SaveToDisk();
            return true;
        }

        // At boot, behind the damage gate: a refund still recorded was interrupted, and its marker makes a landed one a no-op.
        public static void RecoverPendingRefunds()
        {
            List<PendingRefund> owed;
            lock (_lock) owed = new List<PendingRefund>(_pendingRefunds.Values);
            if (owed.Count == 0) return;
            int paid = 0;
            foreach (PendingRefund p in owed)
            {
                if (p == null || string.IsNullOrEmpty(p.Marker)) continue;
                // Owed to a save that is being destroyed: dropped with it, never paid into the new one.
                if (Economy.KmhEconomyReset.Resetting(p.Buyer)) { lock (_lock) _pendingRefunds.Remove(p.Marker); continue; }
                if (!Treasury.TreasuryStore.DepositEscrowOnce(p.Buyer, p.Marker, p.Silver, null, null, p.Note ?? "want refund resumed")) continue;
                lock (_lock) _pendingRefunds.Remove(p.Marker);
                paid++;
            }
            SaveToDisk();
            if (paid > 0) Diagnostics.ServerLog.Warn($"WantBoard: resumed {paid} interrupted want refund(s) from a previous run.");
            int left; lock (_lock) left = _pendingRefunds.Count;
            if (left > 0) Diagnostics.ServerLog.Error($"WantBoard: {left} want refund(s) still cannot be credited - retried next boot.");
        }

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropPendingRefundsFor(string buyer)
        {
            if (string.IsNullOrEmpty(buyer)) return 0;
            int dropped = 0;
            lock (_lock)
                foreach (PendingRefund p in new List<PendingRefund>(_pendingRefunds.Values))
                    if (p != null && Eq(p.Buyer, buyer) && _pendingRefunds.Remove(p.Marker)) dropped++;
            return dropped;
        }

        internal static int PendingRefundCountForTest { get { lock (_lock) return _pendingRefunds.Count; } }


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
            string marker, note = $"want #{wantId} cancelled by admin";
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w) || !TryCloseLocked(wantId, w, note, out marker)) return o;
            }
            o.Done = true; o.Buyer = w.BuyerUsername; o.Refunded = w.EscrowRemaining;
            if (SettleRefund(marker, w.BuyerUsername, w.EscrowRemaining, note))
                Diagnostics.ServerLog.Info($"WantBoard: want #{w.Id} cancelled by admin, refunded {w.EscrowRemaining} to {w.BuyerUsername}");
            return o;
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

        public static ExpireOutcome ExpireRefund(long wantId)
        {
            ExpireOutcome o = new ExpireOutcome();
            WantDto w;
            string marker, note = $"want #{wantId} expired";
            lock (_lock)
            {
                if (!_byId.TryGetValue(wantId, out w) || !TryCloseLocked(wantId, w, note, out marker)) return o;
            }
            o.Done = true; o.Buyer = w.BuyerUsername; o.Refunded = w.EscrowRemaining;
            if (SettleRefund(marker, w.BuyerUsername, w.EscrowRemaining, note))
                Diagnostics.ServerLog.Info($"WantBoard: want #{w.Id} expired, refunded {w.EscrowRemaining} to {w.BuyerUsername}");
            return o;
        }

        // Clones every field: a hand-written list silently drops the buyer's match constraints from each snapshot.
        private static WantDto Clone(WantDto w) => w.ShallowClone();

        internal static WantDto CopyForTest(WantDto w) => Clone(w);
    }
}
