using Newtonsoft.Json;

namespace KMHServerAddon.Features.Guilds.Contributions
{
    internal enum KmhContributionType { Silver, Item, Return, Adjustment }

    // Immutable events, because a cumulative counter could not stay truthful once the guild spends what was donated.
    internal sealed class KmhGuildContribution
    {
        [JsonProperty("id")]         public string Id            { get; set; } = "";
        [JsonProperty("guild")]      public string GuildId       { get; set; } = "";
        [JsonProperty("player")]     public string PlayerId      { get; set; } = "";
        [JsonProperty("tx")]         public string TransactionId { get; set; } = "";   // links to the unified ledger when present
        [JsonProperty("type")]       public KmhContributionType Type { get; set; }
        [JsonProperty("silver")]     public long   SilverValue   { get; set; } = 0;
        [JsonProperty("item_value")] public long   ItemValue     { get; set; } = 0;    // market value of a contributed item
        [JsonProperty("item")]       public string ItemPayload   { get; set; } = "";   // optional describe/fingerprint of the item
        [JsonProperty("utc")]        public string TimestampUtc  { get; set; } = "";
        [JsonProperty("save_gen")]   public long   SaveGeneration { get; set; } = 0;
        [JsonProperty("reverses")]   public string ReversalOfId  { get; set; } = "";   // Return/Adjustment: the record it undoes
        [JsonProperty("reason")]     public string Reason        { get; set; } = "";   // owner adjustment reason
    }
}
