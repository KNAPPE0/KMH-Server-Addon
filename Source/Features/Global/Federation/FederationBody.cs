using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Byte-identical to Global's requests, each under its own domain so no signature stands in for another.
    internal sealed class FederationCreateBody
    {
        [JsonProperty("server_id")]   public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]   public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]       public string Nonce { get; set; } = "";
        [JsonProperty("generation")]  public int    Generation { get; set; }
        [JsonProperty("local_guild")] public string LocalGuild { get; set; } = "";
        [JsonProperty("officer")]     public string Officer { get; set; } = "";
        [JsonProperty("name")]        public string Name { get; set; } = "";
        [JsonProperty("tag")]         public string Tag { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("color")]       public string Color { get; set; } = "";
        [JsonProperty("signature")]   public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationCreate)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LocalGuild ?? "").Add(Officer ?? "").Add(Name ?? "").Add(Tag ?? "")
            .Add(Description ?? "").Add(Color ?? "").Build();
    }

    internal sealed class FederationJoinBody
    {
        [JsonProperty("server_id")]   public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]   public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]       public string Nonce { get; set; } = "";
        [JsonProperty("generation")]  public int    Generation { get; set; }
        [JsonProperty("local_guild")] public string LocalGuild { get; set; } = "";
        [JsonProperty("officer")]     public string Officer { get; set; } = "";
        [JsonProperty("invite")]      public string Invite { get; set; } = "";
        [JsonProperty("signature")]   public string Signature { get; set; } = "";

        // The invite is signed, so a copied join cannot be replayed with another code.
        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationJoin)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LocalGuild ?? "").Add(Officer ?? "").Add(NormalizeInvite(Invite)).Build();

        private static string NormalizeInvite(string code) => GuildInviteRevokeBody.NormalizeInvite(code);
    }

    internal sealed class FederationViewBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationView)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Build();
    }

    internal sealed class FederationInviteRevokeBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("officer")]    public string Officer { get; set; } = "";
        [JsonProperty("invite_id")]  public string InviteId { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationInviteRevoke)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(InviteId ?? "").Build();
    }

    internal sealed class FederationLeaveBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("officer")]    public string Officer { get; set; } = "";
        // "" for a player's choice; "guild_disbanded" when the local guild no longer exists.
        [JsonProperty("reason")]     public string Reason { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationLeave)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(Reason ?? "").Build();
    }

    internal sealed class FederationRemoveBody
    {
        [JsonProperty("server_id")]      public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]      public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]          public string Nonce { get; set; } = "";
        [JsonProperty("generation")]     public int    Generation { get; set; }
        [JsonProperty("link_id")]        public string LinkId { get; set; } = "";
        [JsonProperty("officer")]        public string Officer { get; set; } = "";
        [JsonProperty("target_link_id")] public string TargetLinkId { get; set; } = "";
        [JsonProperty("signature")]      public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationRemove)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(TargetLinkId ?? "").Build();
    }

    internal sealed class FederationTransferBody
    {
        [JsonProperty("server_id")]      public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]      public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]          public string Nonce { get; set; } = "";
        [JsonProperty("generation")]     public int    Generation { get; set; }
        [JsonProperty("link_id")]        public string LinkId { get; set; } = "";
        [JsonProperty("officer")]        public string Officer { get; set; } = "";
        [JsonProperty("target_link_id")] public string TargetLinkId { get; set; } = "";
        [JsonProperty("signature")]      public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationTransfer)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(TargetLinkId ?? "").Build();
    }

    internal sealed class FederationUpdateBody
    {
        [JsonProperty("server_id")]   public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]   public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]       public string Nonce { get; set; } = "";
        [JsonProperty("generation")]  public int    Generation { get; set; }
        [JsonProperty("link_id")]     public string LinkId { get; set; } = "";
        [JsonProperty("officer")]     public string Officer { get; set; } = "";
        [JsonProperty("name")]        public string Name { get; set; } = "";
        [JsonProperty("tag")]         public string Tag { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("color")]       public string Color { get; set; } = "";
        [JsonProperty("signature")]   public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationUpdate)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(Name ?? "").Add(Tag ?? "")
            .Add(Description ?? "").Add(Color ?? "").Build();
    }

    internal sealed class FederationDisbandBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("officer")]    public string Officer { get; set; } = "";
        // The leader types the federation's tag; Global compares it so a stray click cannot disband.
        [JsonProperty("confirm")]    public string Confirm { get; set; } = "";
        [JsonProperty("reason")]     public string Reason { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationDisband)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(Officer ?? "").Add(Confirm ?? "").Add(Reason ?? "").Build();
    }

    // The id is persisted before sending, so a retry after a lost answer is the same report and counts once.
    internal sealed class FederationContributionEntry
    {
        [JsonProperty("report_id")]   public string ReportId { get; set; } = "";
        [JsonProperty("category")]    public string Category { get; set; } = "";
        [JsonProperty("amount")]      public int    Amount { get; set; } = 1;
        // A Global user id proven by a join ticket, or "" for a guild-level event.
        [JsonProperty("player")]      public string Player { get; set; } = "";
        [JsonProperty("occurred_at")] public long   OccurredAtUnix { get; set; }
    }

    internal sealed class FederationContributeBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("batch_id")]   public string BatchId { get; set; } = "";
        [JsonProperty("entries")]    public List<FederationContributionEntry> Entries { get; set; } = new List<FederationContributionEntry>();
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.FederationContribute)
            .Add(ServerId ?? "").Add(IssuedAtUnix).Add(Nonce ?? "").Add(Generation)
            .Add(LinkId ?? "").Add(BatchId ?? "").AddContributions(Entries).Build();
    }

    internal sealed class BackendRegisterBody
    {
        [JsonProperty("challenge_id")] public string ChallengeId { get; set; } = "";
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("public_key")]   public string PublicKey { get; set; } = "";
        [JsonProperty("name")]         public string Name { get; set; } = "";
        [JsonProperty("signature")]    public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.BackendRegistration)
            .Add(ChallengeId ?? "").Add(ServerId ?? "").Add(PublicKey ?? "").Add(Name ?? "").Build();
    }
}
