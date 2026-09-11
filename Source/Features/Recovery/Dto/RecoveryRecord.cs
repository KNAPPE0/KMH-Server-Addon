using Newtonsoft.Json;

namespace KMHServerAddon.Features.Recovery.Dto
{
    // Value that could not reach its owner is parked here rather than dropped.
    public class RecoveryRecord
    {
        [JsonProperty("id")]          public long   Id { get; set; }
        [JsonProperty("utc_ticks")]   public long   UtcTicks { get; set; }
        [JsonProperty("user")]        public string User { get; set; } = "";     // intended owner (may be empty = orphaned)
        [JsonProperty("source")]      public string Source { get; set; } = "";   // e.g. "auction #12 refund", "want #5 return"
        [JsonProperty("reason")]      public string Reason { get; set; } = "";
        [JsonProperty("kind")]        public string Kind { get; set; } = "item"; // "item" | "silver"
        [JsonProperty("silver")]      public long   Silver { get; set; }         // when kind == "silver"
        [JsonProperty("payload")]     public Items.KmhThingPayload Payload { get; set; } // when kind == "item"
        // "held" | "delivering" | "resolved"; "delivering" is written before the value moves so a crash mid-triage says so.
        [JsonProperty("status")]      public string Status { get; set; } = "held";
        [JsonProperty("resolution")]  public string Resolution { get; set; } = "";
    }
}
