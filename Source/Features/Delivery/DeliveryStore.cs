using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Delivery
{
    internal sealed class OutboundDelivery
    {
        [JsonProperty("id")]      public string Id       { get; set; } = "";
        [JsonProperty("player")]  public string Player   { get; set; } = "";
        [JsonProperty("txn")]     public string SourceTxnId { get; set; } = "";
        [JsonProperty("silver")]  public long   Silver   { get; set; } = 0;
        [JsonProperty("items")]   public Dictionary<string, int> Items { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        [JsonProperty("loads")]   public List<Items.KmhThingPayload> Payloads { get; set; } = new List<Items.KmhThingPayload>();
        [JsonProperty("acked")]   public bool   Acked    { get; set; } = false;
        [JsonProperty("created")] public long   CreatedUtcTicks { get; set; }
        [JsonProperty("updated")] public long   UpdatedUtcTicks { get; set; }
    }

    // Handing a grant to the transport is not receipt: the server owes it until the client acks from durable save state.
    internal static class DeliveryStore
    {
        private sealed class PersistedState
        {
            [JsonProperty("next")]       public long NextSeq { get; set; } = 1;
            [JsonProperty("deliveries")] public List<OutboundDelivery> Deliveries { get; set; } = new List<OutboundDelivery>();
        }

        private static readonly object _lock = new object();
        // Each change is saved before the next one starts, so no other save can write down a row whose own write then fails.
        private static readonly object _writeLock = new object();
        private static readonly List<OutboundDelivery> _all = new List<OutboundDelivery>();
        private static long _nextSeq = 1;

        private const int MaxAckedRetained = 512;

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.DeliveryFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _all.Clear();
                if (s.Deliveries != null) _all.AddRange(s.Deliveries);
                _nextSeq = Math.Max(1, s.NextSeq);
                _runToken = NewRunToken();
            }
            int owed = OwedCount;
            if (owed > 0) Diagnostics.ServerLog.Warn($"Delivery: {owed} value delivery(ies) are still owed to players from a previous run - they replay on reconnect.");
        }

        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock) { s.NextSeq = _nextSeq; s.Deliveries = KmhDeepCopy.OfList(_all); seq = JsonFileStore.NextSequence(); }
            return JsonFileStore.Save(KmhDataPaths.DeliveryFile, s, seq);
        }

        public static int OwedCount { get { lock (_lock) { int n = 0; foreach (OutboundDelivery d in _all) if (!d.Acked) n++; return n; } } }

        // Namespaced per server and per load: a restored backup reissues old sequence numbers, and the client skips (and acks)
        // any id it holds a receipt for, so a reissued id would be charged and never delivered.
        private static string _runToken = NewRunToken();
        private static string NewRunToken() => Guid.NewGuid().ToString("N").Substring(0, 8);

        private static string NewIdLocked()
            => KmhServerIdentity.Id + ":" + _runToken + ":" + (_nextSeq++).ToString("x");

        // Null when the record could not be written: the caller still holds the value and must refuse to send it.
        public static OutboundDelivery OweForOperation(string player, Transactions.KmhTxType type, string summary,
                                                       long silver, IDictionary<string, int> items,
                                                       IEnumerable<Items.KmhThingPayload> payloads)
        {
            Transactions.KmhTransaction t = Transactions.KmhTransactionRepository.OpenDelivery(player, type, summary, silver, items, payloads);
            if (t == null) return null;
            OutboundDelivery d = Owe(player, silver, items, payloads, t.Id);
            if (d == null) { Transactions.KmhTransactionRepository.Compensate(t); return null; }
            t.DeliveryId = d.Id;
            t.Advance(Transactions.KmhTxState.Delivered);
            Transactions.KmhTransactionRepository.Update(t);
            return d;
        }

        internal static Action<string> AtWithdrawPhaseForTest;
        internal static Action AfterOweOnceMissForTest;
        internal static string FeeKeyFor(string txId) => txId + ":fee";

        internal static long SilverForSource(string sourceTxnId)
        {
            if (string.IsNullOrEmpty(sourceTxnId)) return 0;
            lock (_lock)
                foreach (OutboundDelivery d in _all)
                    if (string.Equals(d.SourceTxnId, sourceTxnId, StringComparison.Ordinal)) return d.Silver;
            return 0;
        }

        internal static string IdForSource(string sourceTxnId)
        {
            if (string.IsNullOrEmpty(sourceTxnId)) return null;
            lock (_lock)
                foreach (OutboundDelivery d in _all)
                    if (string.Equals(d.SourceTxnId, sourceTxnId, StringComparison.Ordinal)) return d.Id;
            return null;
        }

        // An unwritable delivery is compensated with exactly what the vault confirms was taken; the fee is credited
        // on the row's own key, so recovery can finish it after a crash without paying it twice.
        public static OutboundDelivery OweForWithdrawal(Transactions.KmhTransaction t, long silver, IDictionary<string, int> items,
                                                        IEnumerable<Items.KmhThingPayload> payloads, long fee = 0)
        {
            if (t == null) return null;
            OutboundDelivery d = Owe(t.Player, silver, items, payloads, t.Id);
            if (d == null) { Transactions.KmhTransactionRepository.Compensate(t); return null; }
            AtWithdrawPhaseForTest?.Invoke("owed");
            // On the row's key, which recovery uses too. A pool that will not take it now is owed it, not left without it.
            if (fee > 0 && !Marketplace.MarketplaceStore.TryCreditHousePoolOnce(FeeKeyFor(t.Id), fee, $"treasury withdraw fee ({t.Player})"))
                Treasury.TreasuryStore.OweHouseFee(FeeKeyFor(t.Id), fee, t.Player, $"treasury withdraw fee ({t.Player})");
            t.DeliveryId = d.Id;
            if (t.State == Transactions.KmhTxState.Reserved) t.Advance(Transactions.KmhTxState.Approved);
            t.Advance(Transactions.KmhTxState.Delivered);
            Transactions.KmhTransactionRepository.Update(t);
            return d;
        }

        // The operation id is the idempotency key: a delivery already minted for it, owed or already landed, is
        // handed back instead of a second one. The op's own record of it lives in another file and can be lost.
        public static OutboundDelivery OweOnce(string player, long silver, string sourceTxnId)
        {
            if (string.IsNullOrEmpty(sourceTxnId)) return null;
            // Under the write lock, so two callers for one operation cannot both miss and both mint.
            lock (_writeLock)
            {
                lock (_lock)
                    foreach (OutboundDelivery existing in _all)
                        if (string.Equals(existing.SourceTxnId, sourceTxnId, StringComparison.Ordinal)
                            && string.Equals(existing.Player, player, StringComparison.OrdinalIgnoreCase))
                            return existing;
                AfterOweOnceMissForTest?.Invoke();
                return Owe(player, silver, null, null, sourceTxnId);
            }
        }

        public static OutboundDelivery OweItemsOnce(string player, IDictionary<string, int> items, string sourceTxnId)
        {
            if (string.IsNullOrEmpty(sourceTxnId)) return null;
            lock (_writeLock)
            {
                lock (_lock)
                    foreach (OutboundDelivery existing in _all)
                        if (string.Equals(existing.SourceTxnId, sourceTxnId, StringComparison.Ordinal)
                            && string.Equals(existing.Player, player, StringComparison.OrdinalIgnoreCase))
                            return existing;
                return Owe(player, 0, items, null, sourceTxnId);
            }
        }

        public static OutboundDelivery Owe(string player, long silver,
                                           IDictionary<string, int> items,
                                           IEnumerable<Items.KmhThingPayload> payloads, string sourceTxnId)
        {
            if (string.IsNullOrEmpty(player)) return null;
            // Without the identity prefix the sequence number is the whole id and every server mints the same ones.
            if (string.IsNullOrEmpty(KmhServerIdentity.Id))
            {
                Diagnostics.ServerLog.Error("Delivery: refusing to issue a delivery before this data set's identity is known.");
                return null;
            }
            OutboundDelivery d = new OutboundDelivery
            {
                Player = player, SourceTxnId = sourceTxnId ?? "",
                Silver = silver < 0 ? 0 : silver,
                CreatedUtcTicks = DateTime.UtcNow.Ticks, UpdatedUtcTicks = DateTime.UtcNow.Ticks,
            };
            if (items != null) foreach (KeyValuePair<string, int> kv in items) if (kv.Value > 0) d.Items[kv.Key] = kv.Value;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) d.Payloads.Add(p);

            // The client lands one grant message per id and skips the rest, so a second kind would be acked unlanded.
            int kinds = (d.Silver > 0 ? 1 : 0) + d.Items.Count + (d.Payloads.Count > 0 ? 1 : 0);
            if (kinds != 1)
            {
                Diagnostics.ServerLog.Error($"Delivery: refusing a grant for {player} that carries {kinds} kinds of value - a delivery must be exactly one.");
                return null;
            }

            lock (_writeLock)
            {
                lock (_lock) { d.Id = NewIdLocked(); _all.Add(d); }
                if (SaveToDisk()) return d;
                // The sequence is not stepped back: a gap costs nothing, a reissued id loses a delivery.
                lock (_lock) _all.Remove(d);
            }
            Diagnostics.ServerLog.Error($"Delivery: refusing to hand {player} a grant that could not be written down - the value stays where it is.");
            return null;
        }

        // Moves no value, so a forged ack only costs its sender their own delivery; ownership is still checked.
        public static bool Ack(string player, string deliveryId)
        {
            if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(deliveryId)) return false;
            if (!deliveryId.StartsWith(KmhServerIdentity.Id + ":", StringComparison.Ordinal)) return false;
            lock (_writeLock)
            {
                bool changed = false;
                lock (_lock)
                    foreach (OutboundDelivery d in _all)
                        if (string.Equals(d.Id, deliveryId, StringComparison.Ordinal)
                            && string.Equals(d.Player, player, StringComparison.OrdinalIgnoreCase)
                            && !d.Acked)
                        { d.Acked = true; d.UpdatedUtcTicks = DateTime.UtcNow.Ticks; changed = true; break; }
                if (!changed) return false;
                if (SaveToDisk()) { PruneAcked(); return true; }
                lock (_lock)
                    foreach (OutboundDelivery d in _all)
                        if (string.Equals(d.Id, deliveryId, StringComparison.Ordinal)) { d.Acked = false; break; }
                return false;
            }
        }

        // Unknown reads as settled: a delivery pruned long after its ack must not keep a transaction alive forever.
        public static bool IsOwed(string deliveryId)
        {
            if (string.IsNullOrEmpty(deliveryId)) return false;
            lock (_lock)
                foreach (OutboundDelivery d in _all)
                    if (string.Equals(d.Id, deliveryId, StringComparison.Ordinal)) return !d.Acked;
            return false;
        }

        // A client that never acknowledges keeps every withdrawal owed: each one is replayed on every join and
        // rewritten with the ledger, so a player's own withdrawals stop here until the earlier ones land.
        internal const int MaxOwedPerPlayer = 32;

        public static bool TooManyOwed(string player, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(player)) return false;
            int owed = 0;
            lock (_lock)
                foreach (OutboundDelivery d in _all)
                    if (!d.Acked && string.Equals(d.Player, player, StringComparison.OrdinalIgnoreCase)) owed++;
            if (owed < MaxOwedPerPlayer) return false;
            reason = $"{owed} deliveries are still on their way to you - load your colony so they land, then withdraw again.";
            return true;
        }

        public static List<OutboundDelivery> OwedFor(string player)
        {
            var outl = new List<OutboundDelivery>();
            if (string.IsNullOrEmpty(player)) return outl;
            lock (_lock)
                foreach (OutboundDelivery d in _all)
                    if (!d.Acked && string.Equals(d.Player, player, StringComparison.OrdinalIgnoreCase)) outl.Add(d);
            return outl;
        }

        // A delivery still owed after a wipe would replay on the next join and undo it one reconnect later.
        public static int PurgeUser(string player)
        {
            if (string.IsNullOrEmpty(player)) return 0;
            lock (_writeLock)
            {
                int dropped;
                lock (_lock)
                    dropped = _all.RemoveAll(d => string.Equals(d.Player, player, StringComparison.OrdinalIgnoreCase));
                if (dropped > 0) SaveToDisk();
                return dropped;
            }
        }

        // Settled rows are kept for a while so a late duplicate ack still finds its row rather than looking unknown.
        private static void PruneAcked()
        {
            lock (_writeLock)
            {
                bool trimmed = false;
                lock (_lock)
                {
                    int acked = 0;
                    foreach (OutboundDelivery d in _all) if (d.Acked) acked++;
                    while (acked > MaxAckedRetained)
                    {
                        int i = _all.FindIndex(x => x.Acked);
                        if (i < 0) break;
                        _all.RemoveAt(i); acked--; trimmed = true;
                    }
                }
                if (trimmed) SaveToDisk();
            }
        }

        internal static void ClearForTest() { lock (_lock) { _all.Clear(); _nextSeq = 1; } }
    }
}
