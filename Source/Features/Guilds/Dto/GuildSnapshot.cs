using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Guilds.Dto
{
    // JsonProperty names must stay identical to the patch mod's mirror of these DTOs.
    public class GuildSnapshotEnvelope
    {
        // Monotonic under the store lock; two transports can deliver out of order, so the client drops anything older.
        [JsonProperty("revision")] public long Revision { get; set; } = 0;
        [JsonProperty("in_guild")] public bool          InGuild { get; set; } = false;
        [JsonProperty("guild")]    public GuildSnapshot Guild   { get; set; }

        // Only ever populated for a guildless player, since it drives the accept/decline offer.
        [JsonProperty("my_invites")] public List<GuildInviteDto> MyInvites { get; set; } = new List<GuildInviteDto>();
    }

    public class GuildInviteDto
    {
        [JsonProperty("guild_name")]        public string GuildName       { get; set; } = "";
        [JsonProperty("inviter")]           public string Inviter         { get; set; } = "";
        [JsonProperty("created_utc_ticks")] public long   CreatedUtcTicks { get; set; } = 0;
        [JsonProperty("members")]           public int    Members         { get; set; } = 0;
    }

    public class InvitablePlayerDto
    {
        [JsonProperty("username")] public string Username { get; set; } = "";
        [JsonProperty("online")]   public bool   Online   { get; set; } = false;
    }

    public class GuildInvitablesSnapshot
    {
        [JsonProperty("players")] public List<InvitablePlayerDto> Players { get; set; } = new List<InvitablePlayerDto>();
    }

    public class GuildSnapshot
    {
        [JsonProperty("name")]      public string Name { get; set; } = "";
        [JsonProperty("motd")]      public string Motd { get; set; } = "";

        [JsonProperty("members")]   public List<GuildMemberDto> Members
            { get; set; } = new List<GuildMemberDto>();

        [JsonProperty("perks")]     public GuildPerksDto    Perks    { get; set; } = new GuildPerksDto();
        [JsonProperty("settings")]  public GuildSettingsDto Settings { get; set; } = new GuildSettingsDto();

        // Wire-only: filled from TreasuryStore at build time, and the persisted copy is never read back.
        [JsonProperty("guild_silver")]           public long GuildSilver { get; set; } = 0;
        [JsonProperty("guild_treasury_enabled")] public bool GuildTreasuryEnabled { get; set; } = true;

        // Visible but not spendable, so it is deliberately excluded from guild_silver.
        [JsonProperty("pending_donations_silver")] public long PendingDonationsSilver { get; set; } = 0;

        [JsonProperty("relationships")]
        public Dictionary<string, string> Relationships
            { get; set; } = new Dictionary<string, string>();

        [JsonProperty("open_join")]       public bool OpenJoin { get; set; } = false;
        [JsonProperty("pending_invites")] public List<string> PendingInvites { get; set; } = new List<string>();

        // Keyed by invitee and additive beside pending_invites, so an older client still reads the list.
        [JsonProperty("invite_meta")]     public Dictionary<string, GuildInviteMetaDto> InviteMeta { get; set; }
            = new Dictionary<string, GuildInviteMetaDto>(System.StringComparer.OrdinalIgnoreCase);

        // Held on the guild so a reused guild name cannot inherit someone else's hall.
        [JsonProperty("hall")]            public GuildHallDto Hall { get; set; }

        public GuildSnapshot ShallowClone() => (GuildSnapshot)MemberwiseClone();

        public const string RelationNone            = "none";
        public const string RelationAlliedRequested = "allied_requested";
        public const string RelationAllied          = "allied";
        public const string RelationHostile         = "hostile";
    }

    public class GuildInviteMetaDto
    {
        [JsonProperty("inviter")]           public string Inviter         { get; set; } = "";
        [JsonProperty("created_utc_ticks")] public long   CreatedUtcTicks { get; set; } = 0;
    }

    // Server-owned, so a client's proximity answer is checked against these values rather than its own.
    public class GuildHallDto
    {
        [JsonProperty("has_hall")]          public bool   HasHall         { get; set; } = false;
        [JsonProperty("tile")]              public int    Tile            { get; set; } = -1;
        [JsonProperty("leader")]            public string Leader          { get; set; } = "";  // who set it
        [JsonProperty("radius_tiles")]      public int    RadiusTiles     { get; set; } = 0;
        [JsonProperty("created_utc_ticks")] public long   CreatedUtcTicks { get; set; } = 0;

        public GuildHallDto ShallowClone() => (GuildHallDto)MemberwiseClone();
    }

    public class GuildMemberDto
    {
        public const string RankMember    = "member";
        public const string RankOfficer   = "officer";
        public const string RankModerator = "moderator";
        public const string RankAdmin     = "admin";
        public const string RankOwner     = "owner";   // exactly one per guild; only ownership transfer can assign it

        [JsonProperty("username")]            public string Username           { get; set; } = "";
        [JsonProperty("rank")]                public string Rank               { get; set; } = RankMember;

        [JsonProperty("silver_contributed")]  public long   SilverContributed  { get; set; } = 0;
        [JsonProperty("items_contributed")]   public long   ItemsContributed   { get; set; } = 0;
        [JsonProperty("quests_completed")]    public int    QuestsCompleted    { get; set; } = 0;
        [JsonProperty("joined_utc_ticks")]    public long   JoinedUtcTicks     { get; set; } = 0;

        // Persisted, because the in-memory tally is empty after a restart and the cap would reset with it.
        [JsonProperty("withdrawn_today")]     public long   WithdrawnTodaySilver { get; set; } = 0;
        [JsonProperty("withdraw_day_ticks")]  public long   WithdrawDayStartUtc  { get; set; } = 0;

        public GuildMemberDto ShallowClone() => (GuildMemberDto)MemberwiseClone();
    }

    public class GuildPerksDto
    {
        public const int MaxLevel = 3;

        [JsonProperty("site_max_workers_bonus_level")]    public int SiteMaxWorkersBonusLevel    { get; set; } = 0;
        [JsonProperty("marketplace_tax_reduction_level")] public int MarketplaceTaxReductionLevel { get; set; } = 0;
        [JsonProperty("worker_xp_bonus_level")]           public int WorkerXpBonusLevel          { get; set; } = 0;
        [JsonProperty("custom_site_cost_discount_level")] public int CustomSiteCostDiscountLevel { get; set; } = 0;

        public GuildPerksDto ShallowClone() => (GuildPerksDto)MemberwiseClone();

        // JsonIgnore so a derived effect never persists beside the level it came from.
        [JsonIgnore] public int SiteMaxWorkersBonus => Clamp(SiteMaxWorkersBonusLevel) * 2;

        [JsonIgnore] public int MarketplaceTaxReductionPoints => Clamp(MarketplaceTaxReductionLevel);

        [JsonIgnore]
        public double WorkerXpMultiplier
        {
            get
            {
                switch (Clamp(WorkerXpBonusLevel))
                {
                    case 0:  return 1.0;
                    case 1:  return 1.25;
                    case 2:  return 1.5;
                    default: return 2.0;
                }
            }
        }

        [JsonIgnore] public double CustomSiteCostMultiplier => 1.0 - 0.10 * Clamp(CustomSiteCostDiscountLevel);

        public static int CostFor(int currentLevel)
        {
            int next = System.Math.Max(0, currentLevel + 1);
            return 5_000 * next * (next + 1) / 2;
        }

        private static int Clamp(int level) => level < 0 ? 0 : (level > MaxLevel ? MaxLevel : level);
    }

    public class GuildSettingsDto
    {
        [JsonProperty("site_reward_silver_tax_percent")]  public int SiteRewardSilverTaxPercent { get; set; } = 0;
        [JsonProperty("marketplace_sale_tax_percent")]    public int MarketplaceSaleTaxPercent  { get; set; } = 0;

        [JsonProperty("member_daily_withdraw_cap")]       public int MemberDailyWithdrawCap    { get; set; } = 0;
        [JsonProperty("officer_daily_withdraw_cap")]      public int OfficerDailyWithdrawCap   { get; set; } = 0;
        [JsonProperty("moderator_daily_withdraw_cap")]    public int ModeratorDailyWithdrawCap { get; set; } = 0;
        [JsonProperty("admin_daily_withdraw_cap")]        public int AdminDailyWithdrawCap     { get; set; } = -1;

        [JsonProperty("default_listings_guild_only")]     public bool DefaultListingsGuildOnly { get; set; } = false;

        public GuildSettingsDto ShallowClone() => (GuildSettingsDto)MemberwiseClone();
    }
}
