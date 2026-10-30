using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Signed with the heartbeat key (same proof of server identity); must match Global's MetadataUpdateRequest bytes.
    internal sealed class MetadataBody
    {
        [JsonProperty("server_id")]   public string   ServerId { get; set; } = "";
        [JsonProperty("issued_at")]   public long     IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]       public string   Nonce { get; set; } = "";

        [JsonProperty("name")]        public string   Name { get; set; } = "";
        [JsonProperty("description")] public string   Description { get; set; } = "";
        [JsonProperty("region")]      public string   Region { get; set; } = "";
        [JsonProperty("languages")]   public string[] Languages { get; set; } = new string[0];
        [JsonProperty("tags")]        public string[] Tags { get; set; } = new string[0];
        [JsonProperty("discord_url")] public string   DiscordUrl { get; set; } = "";
        [JsonProperty("website_url")] public string   WebsiteUrl { get; set; } = "";
        [JsonProperty("rules_text")]  public string   RulesText { get; set; } = "";
        [JsonProperty("visibility")]  public string   Visibility { get; set; } = "";

        [JsonProperty("signature")]   public string   Signature { get; set; } = "";

        // Owner fields only, so any change republishes now instead of waiting for the next boot.
        public string ContentKey()
            => string.Join("\u001f", Name, Description, Region, string.Join("\u001e", Languages ?? new string[0]),
                           string.Join("\u001e", Tags ?? new string[0]), DiscordUrl, WebsiteUrl, RulesText, Visibility);

        // Field order is the contract: adding one here but not in Global's Canonical() signs bytes nobody verifies.
        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.Metadata)
            .Add(ServerId)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Name)
            .Add(Description)
            .Add(Region)
            .AddList(Languages)
            .AddList(Tags)
            .Add(DiscordUrl)
            .Add(WebsiteUrl)
            .Add(RulesText)
            .Add(Visibility)
            .Build();
    }
}
