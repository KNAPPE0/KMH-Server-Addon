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

    // Handing a grant to the transport is not receipt, so the server keeps owing it until the client acks from durable save state.
    internal static class DeliveryStore
    {
        private sealed class PersistedState
        {
            [JsonProperty("next")]       public long NextSeq { get; set; } = 1;
            [JsonProperty("deliveries")] public List<OutboundDelivery> Deliveries { get; set; } = new List<OutboundDelivery>();
        }

        private static readonly object _lock = new object();
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
            }
            int owed = OwedCount;
            if (owed > 0) Diagnostics.ServerLog.Warn($"Delivery: {owed} value delivery(ies) are still owed to players from a previous run - they replay on reconnect.");
        }

        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock) { s.NextSeq = _nextSeq; s.Deliveries = new List<OutboundDelivery>(_all); }
            return JsonFileStore.Save(KmhDataPaths.DeliveryFile, s);
        }

        public static int OwedCount { get { lock (_lock) { int n = 0; foreach (OutboundDelivery d in _all) if (!d.Acked) n++; return n; } } }

        // Namespaced by this data set's identity, so one server's receipt can never suppress another's delivery.
        private static string NewIdLocked()
            => KmhServerIdentity.Id + ":" + (_nextSeq++).ToString("x");

        // Null when the record could not be written - the caller still holds the value and must refuse rather than hand it to the transport.
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

            lock (_lock) { d.Id = NewIdLocked(); _all.Add(d); }
            if (SaveToDisk()) return d;
            lock (_lock) { _all.Remove(d); _nextSeq--; }
            Diagnostics.ServerLog.Error($"Delivery: refusing to hand {player} a grant that could not be written down - the value stays where it is.");
            return null;
        }

        // Moves no value, so a forged ack only costs its sender their own delivery; ownership is still checked so nobody discharges another's.
        public static bool Ack(string player, string deliveryId)
        {
            if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(deliveryId)) return false;
            if (!deliveryId.StartsWith(KmhServerIdentity.Id + ":", StringComparison.Ordinal)) return false;
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

        // Unknown reads as settled: a delivery pruned long after its ack must not keep a transaction alive forever.
        public static bool IsOwed(string deliveryId)
        {
            if (string.IsNullOrEmpty(deliveryId)) return false;
            lock (_lock)
                foreach (OutboundDelivery d in _all)
                    if (string.Equals(d.Id, deliveryId, StringComparison.Ordinal)) return !d.Acked;
            return false;
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
            int dropped;
            lock (_lock)
                dropped = _all.RemoveAll(d => string.Equals(d.Player, player, StringComparison.OrdinalIgnoreCase));
            if (dropped > 0) SaveToDisk();
            return dropped;
        }

        // Settled rows are kept for a while so a late duplicate ack still finds its row rather than looking unknown.
        private static void PruneAcked()
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

        internal static void ClearForTest() { lock (_lock) { _all.Clear(); _nextSeq = 1; } }
        internal static OutboundDelivery FindForTest(string id) { lock (_lock) return _all.Find(d => d.Id == id); }
    }
}
