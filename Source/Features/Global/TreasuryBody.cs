using KMHServerAddon.Features.Global;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Each Canonical() must match Global's request byte for byte (contract 73): a field added on one side only
    // would verify as a different message rather than fail.
    internal sealed class TreasuryDepositBody
    {
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]    public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]        public string Nonce { get; set; } = "";
        [JsonProperty("generation")]   public int    Generation { get; set; }
        [JsonProperty("user_id")]      public string UserId { get; set; } = "";
        [JsonProperty("amount")]       public long   Amount { get; set; }
        // Reused across retries of one logical deposit; that is what makes a retry safe.
        [JsonProperty("operation_id")] public string OperationId { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.TreasuryDeposit)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(UserId ?? "").Add(Amount).Add(OperationId ?? "").Build();
    }

    internal sealed class TreasuryReserveBody
    {
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]    public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]        public string Nonce { get; set; } = "";
        [JsonProperty("generation")]   public int    Generation { get; set; }
        [JsonProperty("user_id")]      public string UserId { get; set; } = "";
        [JsonProperty("amount")]       public long   Amount { get; set; }
        [JsonProperty("operation_id")] public string OperationId { get; set; } = "";
        // Unsigned to keep the deployed reserve signature; Global binds it to player, server, amount and operation.
        [JsonProperty("grant")]        public string Grant { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.TreasuryReserve)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(UserId ?? "").Add(Amount).Add(OperationId ?? "").Build();
    }

    internal sealed class TreasuryAckBody
    {
        [JsonProperty("server_id")]      public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]      public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]          public string Nonce { get; set; } = "";
        [JsonProperty("generation")]     public int    Generation { get; set; }
        [JsonProperty("reservation_id")] public string ReservationId { get; set; } = "";
        // True only once delivery to a colony is durably proven; false releases the hold.
        [JsonProperty("delivered")]      public bool   Delivered { get; set; }
        [JsonProperty("signature")]      public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.TreasuryAck)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(ReservationId ?? "").Add(Delivered ? 1 : 0).Build();
    }
}
