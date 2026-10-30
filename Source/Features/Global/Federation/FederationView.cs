using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global.Federation
{
    // Mirrors Global's FederationView field for field; Global is the authority on all of it.
    internal sealed class FederationView
    {
        [JsonProperty("federation")]   public FederationProfile Federation { get; set; } = new FederationProfile();
        [JsonProperty("self")]         public FederationSelf Self { get; set; } = new FederationSelf();
        [JsonProperty("members")]      public List<FederationMember> Members { get; set; } = new List<FederationMember>();
        [JsonProperty("objectives")]   public List<FederationObjective> Objectives { get; set; } = new List<FederationObjective>();
        [JsonProperty("achievements")] public List<FederationAchievement> Achievements { get; set; } = new List<FederationAchievement>();
        [JsonProperty("events")]       public List<FederationEvent> Events { get; set; } = new List<FederationEvent>();
        [JsonProperty("categories")]   public List<FederationCategory> Categories { get; set; } = new List<FederationCategory>();
        [JsonProperty("invites")]      public List<FederationInvite> Invites { get; set; } = new List<FederationInvite>();
    }

    internal sealed class FederationProfile
    {
        [JsonProperty("federation_id")]       public string Id { get; set; } = "";
        [JsonProperty("name")]                public string Name { get; set; } = "";
        [JsonProperty("tag")]                 public string Tag { get; set; } = "";
        [JsonProperty("description")]         public string Description { get; set; } = "";
        [JsonProperty("color")]               public string Color { get; set; } = "";
        [JsonProperty("state")]               public string State { get; set; } = "";
        [JsonProperty("created_at")]          public long   CreatedAt { get; set; }
        [JsonProperty("disbanded_at")]        public long   DisbandedAt { get; set; }
        [JsonProperty("disband_reason")]      public string DisbandReason { get; set; } = "";
        [JsonProperty("founder_server_id")]   public string FounderServerId { get; set; } = "";
        [JsonProperty("founder_server_name")] public string FounderServerName { get; set; } = "";
        [JsonProperty("founder_guild")]       public string FounderGuild { get; set; } = "";
        [JsonProperty("leader_link_id")]      public string LeaderLinkId { get; set; } = "";
        [JsonProperty("level")]               public int    Level { get; set; } = 1;
        [JsonProperty("renown")]              public long   Renown { get; set; }
        [JsonProperty("level_floor")]         public long   LevelFloor { get; set; }
        [JsonProperty("next_level_at")]       public long   NextLevelAt { get; set; }
        [JsonProperty("revision")]            public long   Revision { get; set; }
        [JsonProperty("active_members")]      public int    ActiveMembers { get; set; }
        [JsonProperty("lifetime_members")]    public int    LifetimeMembers { get; set; }
        [JsonProperty("verified_players_30d")]      public int VerifiedPlayers30d { get; set; }
        [JsonProperty("lifetime_verified_players")] public int LifetimeVerifiedPlayers { get; set; }
        [JsonProperty("objectives_completed")]      public int ObjectivesCompleted { get; set; }
        [JsonProperty("renown_today")]        public long   RenownToday { get; set; }
        [JsonProperty("renown_day_cap")]      public long   RenownDayCap { get; set; }
    }

    internal sealed class FederationSelf
    {
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        [JsonProperty("state")]      public string State { get; set; } = "";
        [JsonProperty("detail")]     public string Detail { get; set; } = "";
        [JsonProperty("is_leader")]  public bool   IsLeader { get; set; }
        [JsonProperty("joined_at")]  public long   JoinedAt { get; set; }
        [JsonProperty("ended_at")]   public long   EndedAt { get; set; }
        [JsonProperty("end_reason")] public string EndReason { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; }
    }

    internal sealed class FederationMember
    {
        [JsonProperty("link_id")]      public string LinkId { get; set; } = "";
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("server_name")]  public string ServerName { get; set; } = "";
        [JsonProperty("local_guild")]  public string LocalGuild { get; set; } = "";
        [JsonProperty("state")]        public string State { get; set; } = "";
        [JsonProperty("joined_at")]    public long   JoinedAt { get; set; }
        [JsonProperty("ended_at")]     public long   EndedAt { get; set; }
        [JsonProperty("is_leader")]    public bool   IsLeader { get; set; }
        [JsonProperty("online")]       public bool   Online { get; set; }
        [JsonProperty("renown_total")] public long   RenownTotal { get; set; }
        [JsonProperty("renown_7d")]    public long   Renown7d { get; set; }
    }

    internal sealed class FederationObjective
    {
        [JsonProperty("objective_id")]    public string Id { get; set; } = "";
        [JsonProperty("category")]        public string Category { get; set; } = "";
        [JsonProperty("target")]          public int    Target { get; set; }
        [JsonProperty("progress")]        public int    Progress { get; set; }
        [JsonProperty("starts_at")]       public long   StartsAt { get; set; }
        [JsonProperty("ends_at")]         public long   EndsAt { get; set; }
        [JsonProperty("state")]           public string State { get; set; } = "";
        [JsonProperty("reward_renown")]   public int    RewardRenown { get; set; }
        [JsonProperty("my_progress")]     public int    MyProgress { get; set; }
        [JsonProperty("reward_eligible")] public bool   RewardEligible { get; set; }
    }

    internal sealed class FederationAchievement
    {
        [JsonProperty("key")]         public string Key { get; set; } = "";
        [JsonProperty("achieved_at")] public long   AchievedAt { get; set; }
    }

    internal sealed class FederationEvent
    {
        [JsonProperty("at")]          public long   At { get; set; }
        [JsonProperty("kind")]        public string Kind { get; set; } = "";
        [JsonProperty("server_name")] public string ServerName { get; set; } = "";
        [JsonProperty("local_guild")] public string LocalGuild { get; set; } = "";
        [JsonProperty("detail")]      public string Detail { get; set; } = "";
    }

    internal sealed class FederationCategory
    {
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("total")]    public long   Total { get; set; }
        [JsonProperty("last_7d")]  public long   Last7d { get; set; }
    }

    internal sealed class FederationInvite
    {
        [JsonProperty("invite_id")]        public string InviteId { get; set; } = "";
        [JsonProperty("target_server")]    public string TargetServer { get; set; } = "";
        [JsonProperty("target_name")]      public string TargetName { get; set; } = "";
        [JsonProperty("expires_at")]       public long   ExpiresAt { get; set; }
        [JsonProperty("issued_by_server")] public string IssuedByServer { get; set; } = "";
        [JsonProperty("mine")]             public bool   Mine { get; set; }
    }
}
