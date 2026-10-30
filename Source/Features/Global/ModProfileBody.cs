using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Signed with the heartbeat key; must match Global's ModProfileRequest bytes.
    internal sealed class ModProfileBody
    {
        [JsonProperty("server_id")]        public string   ServerId { get; set; } = "";
        [JsonProperty("issued_at")]        public long     IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]            public string   Nonce { get; set; } = "";

        [JsonProperty("policy")]           public string   Policy { get; set; } = "";
        [JsonProperty("enforce_settings")] public bool     EnforceSettings { get; set; }
        [JsonProperty("enforce_order")]    public bool     EnforceOrder { get; set; }
        [JsonProperty("required")]         public string[] Required { get; set; } = new string[0];
        [JsonProperty("optional")]         public string[] Optional { get; set; } = new string[0];
        // Package ids from FL_ModOrder (RimWorld's ModsConfig.xml); empty when the server publishes none.
        [JsonProperty("order")]            public string[] Order { get; set; } = new string[0];
        // RWT's SHA-256 of that ModsConfig.xml: a client proves the file it wrote is the server's, not a reassembly.
        [JsonProperty("order_hash")]       public string   OrderHash { get; set; } = "";
        [JsonProperty("hash")]             public string   Hash { get; set; } = "";
        [JsonProperty("revision")]         public long     Revision { get; set; }
        // Signed because a client's Subscribe button acts on these Workshop ids.
        [JsonProperty("entries")]          public Mods.ModEntry[] Entries { get; set; } = new Mods.ModEntry[0];

        [JsonProperty("signature")]        public string   Signature { get; set; } = "";

        // Field order is the contract: adding one here but not in Global's Canonical() signs bytes nobody verifies.
        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.ModProfile)
            .Add(ServerId)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Policy)
            .Add(EnforceSettings ? 1 : 0)
            .Add(EnforceOrder ? 1 : 0)
            .AddList(Required)
            .AddList(Optional)
            .AddList(Order)
            .Add(OrderHash)
            .Add(Hash)
            .Add(Revision)
            .AddEntries(Entries)
            .Build();
    }
}
