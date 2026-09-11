using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;

namespace KMHServerAddon.Transactions
{
    // Contents stay opaque strings, so the ledger never has to understand every item shape.
    internal sealed class KmhTransaction
    {
        [JsonProperty("id")]         public string     Id            { get; set; } = "";
        // Client-stable across a reconnect, so a retry cannot run the action twice.
        [JsonProperty("req_key")]    public string     RequestKey    { get; set; } = "";
        [JsonProperty("player")]     public string     Player        { get; set; } = "";
        [JsonProperty("guild")]      public string     Guild         { get; set; } = "";   // "" when not guild-related
        [JsonProperty("save_gen")]   public long       SaveGeneration { get; set; } = 0;   // for rollback reconciliation (v1.4.0)
        [JsonProperty("source")]     public string     Source        { get; set; } = "";   // owning system
        [JsonProperty("type")]       public KmhTxType  Type          { get; set; }
        [JsonProperty("dest")]       public string     Destination   { get; set; } = "";   // receiving system
        [JsonProperty("requested")]  public string     Requested     { get; set; } = "";   // what was asked for
        [JsonProperty("validated")]  public string     Validated     { get; set; } = "";   // what the server accepted

        // What was actually taken from the source - refundable, so boot recovery can settle a crashed move.
        [JsonProperty("esc_silver")] public long       EscrowSilver  { get; set; } = 0;
        [JsonProperty("esc_items")]  public Dictionary<string, int> EscrowItems { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        [JsonProperty("esc_loads")]  public List<Items.KmhThingPayload> EscrowPayloads { get; set; } = new List<Items.KmhThingPayload>();

        // After a reset or restore the same target id names something else, so a replayed move must match the generation.
        [JsonProperty("target_id")]  public long       TargetId      { get; set; } = 0;
        [JsonProperty("target_gen")] public long       TargetGeneration { get; set; } = 0;

        // Seller goods go back to the seller, never to a resurrected listing - that is how a refunded listing sells twice.
        [JsonProperty("cp")]         public string     CounterPlayer { get; set; } = "";
        [JsonProperty("cp_silver")]  public long       CounterSilver { get; set; } = 0;
        [JsonProperty("cp_items")]   public Dictionary<string, int> CounterItems { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        [JsonProperty("cp_loads")]   public List<Items.KmhThingPayload> CounterPayloads { get; set; } = new List<Items.KmhThingPayload>();

        // An economic commit is not the end when the value's last stop is a colony that has not confirmed holding it.
        [JsonProperty("delivery")]   public string     DeliveryId    { get; set; } = "";

        // Suffixes for the per-leg markers the treasury stamps, so each leg settles at most once on its own.
        public string TakeMarker    => Id + ":w";
        public string RefundMarker  => Id + ":r";
        public string CounterMarker => Id + ":cp";

        public bool HasCounterEscrow
            => CounterSilver > 0
            || (CounterItems != null && CounterItems.Count > 0)
            || (CounterPayloads != null && CounterPayloads.Count > 0);
        [JsonProperty("created")]    public string     CreatedUtc    { get; set; } = "";
        [JsonProperty("updated")]    public string     UpdatedUtc    { get; set; } = "";
        [JsonProperty("state")]      public KmhTxState State         { get; set; } = KmhTxState.Requested;
        [JsonProperty("delivered")]  public bool       DeliveryConfirmed { get; set; } = false;
        // Links a refund/recovery back to the transaction it settles, instead of mutating the original.
        [JsonProperty("refund_of")]  public string     RefundOfId    { get; set; } = "";
        [JsonProperty("recovery_of")]public string     RecoveryOfId  { get; set; } = "";

        public bool HasEscrow
            => EscrowSilver > 0
            || (EscrowItems != null && EscrowItems.Count > 0)
            || (EscrowPayloads != null && EscrowPayloads.Count > 0);

        private static long _seq;

        // Ticks plus a monotonic counter, so two ids in the same tick still differ and sort in creation order.
        public static string NewId()
        {
            long now = DateTime.UtcNow.Ticks;
            long seq = Interlocked.Increment(ref _seq);
            return $"{now:x16}-{seq:x8}";
        }

        public static KmhTransaction Create(string player, string source, KmhTxType type, string requested)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            return new KmhTransaction
            {
                Id = NewId(), Player = player ?? "", Source = source ?? "", Type = type,
                Requested = requested ?? "", CreatedUtc = nowIso, UpdatedUtc = nowIso, State = KmhTxState.Requested,
                TargetGeneration = Features.Economy.KmhEconomyReset.GenerationOf(player),
            };
        }

        // Refuses an illegal jump rather than throwing, so a caller can never force one.
        public bool Advance(KmhTxState to)
        {
            if (!KmhTxStateMachine.CanTransition(State, to)) return false;
            State = to;
            UpdatedUtc = DateTime.UtcNow.ToString("o");
            if (to == KmhTxState.Confirmed) DeliveryConfirmed = true;
            return true;
        }
    }
}
