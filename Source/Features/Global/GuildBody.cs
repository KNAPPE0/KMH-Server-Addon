using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Each Canonical() matches Global's request byte for byte under its own domain, so proving a link can never
    // verify as moving silver, nor reading a balance as proving a link.

    internal sealed class GuildInviteBody
    {
        [JsonProperty("server_id")]     public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]     public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]         public string Nonce { get; set; } = "";
        [JsonProperty("generation")]    public int    Generation { get; set; }
        [JsonProperty("link_id")]       public string LinkId { get; set; } = "";
        [JsonProperty("officer")]       public string Officer { get; set; } = "";
        [JsonProperty("target_server")] public string TargetServerId { get; set; } = "";
        [JsonProperty("signature")]     public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildInvite)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(TargetServerId ?? "").Build();
    }

    internal sealed class GuildInviteRevokeBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("invite")]     public string Invite { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildInviteRevoke)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(NormalizeInvite(Invite)).Build();

        // The same rule Global signs over: case and dashes are typing, not identity.
        internal static string NormalizeInvite(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            var sb = new System.Text.StringBuilder(12);
            foreach (char ch in code.Trim().ToUpperInvariant())
                if (ch != '-' && ch != ' ') sb.Append(ch);
            return sb.ToString();
        }
    }

    internal sealed class GuildReproveBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("officer")]    public string Officer { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildReprove)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Build();
    }

    internal sealed class GuildStatusBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildStatus)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Build();
    }

    internal sealed class GuildDepositBody
    {
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]    public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]        public string Nonce { get; set; } = "";
        [JsonProperty("generation")]   public int    Generation { get; set; }
        [JsonProperty("link_id")]      public string LinkId { get; set; } = "";
        [JsonProperty("amount")]       public long   Amount { get; set; }
        [JsonProperty("operation_id")] public string OperationId { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildDeposit)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Amount).Add(OperationId ?? "").Build();
    }

    internal sealed class GuildReserveBody
    {
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]    public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]        public string Nonce { get; set; } = "";
        [JsonProperty("generation")]   public int    Generation { get; set; }
        [JsonProperty("link_id")]      public string LinkId { get; set; } = "";
        [JsonProperty("amount")]       public long   Amount { get; set; }
        [JsonProperty("operation_id")] public string OperationId { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildReserve)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Amount).Add(OperationId ?? "").Build();
    }

    internal sealed class GuildAckBody
    {
        [JsonProperty("server_id")]      public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]      public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]          public string Nonce { get; set; } = "";
        [JsonProperty("generation")]     public int    Generation { get; set; }
        [JsonProperty("reservation_id")] public string ReservationId { get; set; } = "";
        // True only once delivery to the guild vault is durably proven; false releases the hold.
        [JsonProperty("delivered")]      public bool   Delivered { get; set; }
        [JsonProperty("signature")]      public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.GuildAck)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(ReservationId ?? "").Add(Delivered ? 1 : 0).Build();
    }
}
