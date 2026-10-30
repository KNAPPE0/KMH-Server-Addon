using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Must match Global's HeartbeatRequest; contract 73 asserts both sides agree.
    internal sealed class HeartbeatBody
    {
        [JsonProperty("server_id")]         public string ServerId { get; set; } = "";
        // Bumped only by an authenticated identity op, so a restored snapshot cannot replay old sequences.
        [JsonProperty("generation")]        public int    Generation { get; set; } = 1;
        [JsonProperty("sequence")]          public long   Sequence { get; set; }
        [JsonProperty("issued_at")]         public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]             public string Nonce { get; set; } = "";
        [JsonProperty("status")]            public string Status { get; set; } = "";
        [JsonProperty("endpoint")]          public string Endpoint { get; set; } = "";
        [JsonProperty("players")]           public int    Players { get; set; }
        [JsonProperty("max_players")]       public int    MaxPlayers { get; set; }
        [JsonProperty("rimworld_version")]  public string RimWorldVersion { get; set; } = "";
        [JsonProperty("rwt_version")]       public string RwtVersion { get; set; } = "";
        [JsonProperty("kmh_version")]       public string KmhVersion { get; set; } = "";
        [JsonProperty("protocol")]          public int    Protocol { get; set; }
        [JsonProperty("capabilities")]      public List<string> Capabilities { get; set; } = new List<string>();
        [JsonProperty("manifest_revision")] public long   ManifestRevision { get; set; }
        [JsonProperty("manifest_hash")]     public string ManifestHash { get; set; } = "";
        [JsonProperty("metadata_revision")] public long   MetadataRevision { get; set; }
        // Server-reported and signed, never owner-typed, so the browser never offers a join RWT would refuse.
        [JsonProperty("requires_password")] public bool   RequiresPassword { get; set; }
        [JsonProperty("signature")]         public string Signature { get; set; } = "";

        public string CapabilityHash() => KmhCanonicalPayload.HashSet(Capabilities);

        // Field order is the contract: adding one here but not in Global's Canonical() signs bytes nobody verifies.
        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.Heartbeat)
            .Add(ServerId)
            .Add(Generation)
            .Add(Sequence)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Status)
            .Add(Endpoint)
            .Add(Players)
            .Add(MaxPlayers)
            .Add(RimWorldVersion)
            .Add(RwtVersion)
            .Add(KmhVersion)
            .Add(Protocol)
            .Add(CapabilityHash())
            .Add(ManifestRevision)
            .Add(ManifestHash)
            .Add(MetadataRevision)
            .Add(RequiresPassword ? "1" : "0")
            .Build();
    }

    internal sealed class RegisterBody
    {
        [JsonProperty("challenge_id")] public string ChallengeId { get; set; } = "";
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("public_key")]   public string PublicKey { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";
        [JsonProperty("name")]         public string Name { get; set; } = "";
        [JsonProperty("visibility")]   public string Visibility { get; set; } = "Public";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.RegistrationChallenge)
            .Add(ChallengeId)
            .Add(ServerId)
            .Add(PublicKey)
            .Build();
    }
}
