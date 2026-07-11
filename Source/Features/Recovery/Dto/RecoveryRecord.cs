using Newtonsoft.Json;

namespace KMHServerAddon.Features.Recovery.Dto
{
    // One parked item/silver amount that couldn't reach its owner; held for admin retry/refund/drop instead of lost.
    public class RecoveryRecord
    {
        [JsonProperty("id")]          public long   Id { get; set; }
        [JsonProperty("utc_ticks")]   public long   UtcTicks { get; set; }
        [JsonProperty("user")]        public string User { get; set; } = "";     // intended owner (may be empty = orphaned)
        [JsonProperty("source")]      public string Source { get; set; } = "";   // e.g. "auction #12 refund", "want #5 return"
        [JsonProperty("reason")]      public string Reason { get; set; } = "";   // why delivery failed
        [JsonProperty("kind")]        public string Kind { get; set; } = "item"; // "item" | "silver"
        [JsonProperty("silver")]      public long   Silver { get; set; }         // when kind == "silver"
        [JsonProperty("payload")]     public Items.KmhThingPayload Payload { get; set; } // when kind == "item"
        [JsonProperty("status")]      public string Status { get; set; } = "held"; // "held" | "resolved"
        [JsonProperty("resolution")]  public string Resolution { get; set; } = ""; // how it was resolved + when
    }
}
