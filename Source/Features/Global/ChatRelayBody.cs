using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Field order must match Global's GlobalChatRelayRequest Canonical(), or the signature verifies nothing.
    internal sealed class ChatRelayBody
    {
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("generation")]   public int    Generation { get; set; } = 1;
        [JsonProperty("issued_at")]    public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]        public string Nonce { get; set; } = "";
        [JsonProperty("display_name")] public string DisplayName { get; set; } = "";
        [JsonProperty("body")]         public string Body { get; set; } = "";
        [JsonProperty("source")]       public string Source { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.ChatRelay)
            .Add(ServerId)
            .Add(Generation)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(DisplayName)
            .Add(Body)
            .Add(Source)
            .Build();
    }

    internal sealed class ChatRelayReadBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; } = 1;
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("limit")]      public int    Limit { get; set; }
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.ChatRelayRead)
            .Add(ServerId)
            .Add(Generation)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Limit)
            .Build();
    }

    internal static class ChatRelaySources
    {
        public const string Discord = "discord";
        public const string InGame  = "ingame";
    }
}
