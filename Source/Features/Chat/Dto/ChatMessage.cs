using Newtonsoft.Json;

namespace KMHServerAddon.Features.Chat.Dto
{
    // Field names must stay byte-identical with the client DTO in KMH-Patch.
    public sealed class ChatMessage
    {
        [JsonProperty("id")]             public long   Id           { get; set; }
        [JsonProperty("channel")]        public string Channel      { get; set; } = "";
        [JsonProperty("from")]           public string FromUsername { get; set; } = "";
        [JsonProperty("body")]           public string Body         { get; set; } = "";
        [JsonProperty("sent_utc_ticks")] public long   SentUtcTicks { get; set; }
        [JsonProperty("origin")]         public string Origin       { get; set; } = "ingame";   // ingame | discord
        // Absent on pre-1.3.0 history, which then reads as unverified, and that is the safe direction.
        [JsonProperty("verified")]       public bool   SenderVerified { get; set; }

        // Server-vetted, but the client still waits for a click, because fetching reveals the player's IP to that host.
        [JsonProperty("image")]          public string ImageUrl      { get; set; } = "";
        [JsonProperty("is_video")]       public bool   IsVideo       { get; set; }

        // An id rather than bytes, so chat history stays text; ImageUrl remains the fallback for older clients.
        [JsonProperty("media_id")]       public string MediaId       { get; set; } = "";
        [JsonProperty("media_ref")]      public string MediaRef      { get; set; } = "";
    }
}
