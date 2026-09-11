using Newtonsoft.Json;

namespace KMHServerAddon.Features.Mail.Dto
{
    // Field names must stay byte-identical with the client DTO (KMH-Patch Features/Mail/Dto/MailMessage.cs).
    public sealed class MailMessage
    {
        [JsonProperty("id")]             public long   Id            { get; set; }
        [JsonProperty("from")]           public string FromUsername  { get; set; } = "";   // server-set, never trusted from the wire
        [JsonProperty("to")]             public string ToUsername    { get; set; } = "";
        [JsonProperty("subject")]        public string Subject       { get; set; } = "";
        [JsonProperty("body")]           public string Body          { get; set; } = "";
        [JsonProperty("sent_utc_ticks")] public long   SentUtcTicks  { get; set; }
        [JsonProperty("read_utc_ticks")] public long   ReadUtcTicks  { get; set; }   // 0 = unread

        [JsonProperty("attach_silver")]   public long   AttachedSilver { get; set; }
        [JsonProperty("attach_items")]    public System.Collections.Generic.Dictionary<string, int> AttachedItems { get; set; }
        [JsonProperty("attach_payloads")] public System.Collections.Generic.List<Items.KmhThingPayload> AttachedPayloads { get; set; }
        [JsonProperty("attach_state")]    public int    AttachState    { get; set; }   // MailStore.Attach*: 0 none / 1 escrowed / 2 claimed / 3 refunded
    }
}
