using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Signed by the new key to prove control of it, not just the account; a rotation is also signed by the old key.
    internal sealed class RecoverBody
    {
        [JsonProperty("challenge_id")]           public string ChallengeId { get; set; } = "";
        [JsonProperty("server_id")]              public string ServerId { get; set; } = "";
        [JsonProperty("public_key")]             public string PublicKey { get; set; } = "";
        [JsonProperty("signature")]              public string Signature { get; set; } = "";
        [JsonProperty("previous_key_signature")] public string PreviousKeySignature { get; set; }

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.KeyRotation)
            .Add(ChallengeId ?? "").Add(ServerId ?? "").Add(PublicKey ?? "").Build();
    }

    internal sealed class RebindProveBody
    {
        [JsonProperty("code")]       public string Code { get; set; } = "";
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("public_key")] public string PublicKey { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("links")]      public List<string> Links { get; set; } = new List<string>();
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        // Hand-typed from the operator's page, so case, spaces and dashes are forgiven, matching Global.
        public static string NormalizeCode(string code)
            => (code ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

        [JsonIgnore] public string NormalizedCode => NormalizeCode(Code);

        [JsonIgnore]
        public List<string> SortedLinks
        {
            get
            {
                List<string> sorted = new List<string>(Links ?? new List<string>());
                sorted.Sort(StringComparer.Ordinal);
                return sorted;
            }
        }

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.ServerRebind)
            .Add(NormalizedCode).Add(ServerId ?? "").Add(PublicKey ?? "").Add(IssuedAtUnix).Add(Nonce ?? "")
            .AddList(SortedLinks).Build();
    }
}
