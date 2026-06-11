using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Guilds.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.Guilds.Dto.
    public class GuildSnapshotEnvelope
    {
        [JsonProperty("in_guild")] public bool          InGuild { get; set; } = false;
        [JsonProperty("guild")]    public GuildSnapshot Guild   { get; set; }
    }

    public class GuildSnapshot
    {
        [JsonProperty("name")]      public string Name { get; set; } = "";
        [JsonProperty("motd")]      public string Motd { get; set; } = "";

        [JsonProperty("members")]   public List<GuildMemberDto> Members
            { get; set; } = new List<GuildMemberDto>();

        [JsonProperty("perks")]     public GuildPerksDto    Perks    { get; set; } = new GuildPerksDto();
        [JsonProperty("settings")]  public GuildSettingsDto Settings { get; set; } = new GuildSettingsDto();

        [JsonProperty("relationships")]
        public Dictionary<string, string> Relationships
            { get; set; } = new Dictionary<string, string>();

        // Invite-only by default - admins/mods invite, the invitee joins. Set open_join to let anyone in
        [JsonProperty("open_join")]       public bool OpenJoin { get; set; } = false;
        [JsonProperty("pending_invites")] public List<string> PendingInvites { get; set; } = new List<string>();

        public const string RelationNone            = "none";
        public const string RelationAlliedRequested = "allied_requested";
        public const string RelationAllied          = "allied";
        public const string RelationHostile         = "hostile";
    }

    public class GuildMemberDto
    {
        public const string RankMember    = "member";
        public const string RankOfficer   = "officer";
        public const string RankModerator = "moderator";
        public const string RankAdmin     = "admin";

        [JsonProperty("username")]            public string Username           { get; set; } = "";
        [JsonProperty("rank")]                public string Rank               { get; set; } = RankMember;

        [JsonProperty("silver_contributed")]  public long   SilverContributed  { get; set; } = 0;
        [JsonProperty("items_contributed")]   public long   ItemsContributed   { get; set; } = 0;
        [JsonProperty("quests_completed")]    public int    QuestsCompleted    { get; set; } = 0;
        [JsonProperty("joined_utc_ticks")]    public long   JoinedUtcTicks     { get; set; } = 0;

        // Rolling daily vault-withdraw tally, reset when the UTC day rolls over.
        [JsonProperty("withdrawn_today")]     public long   WithdrawnTodaySilver { get; set; } = 0;
        [JsonProperty("withdraw_day_ticks")]  public long   WithdrawDayStartUtc  { get; set; } = 0;
    }

    public class GuildPerksDto
    {
        public const int MaxLevel = 3;

        [JsonProperty("site_max_workers_bonus_level")]    public int SiteMaxWorkersBonusLevel    { get; set; } = 0;
        [JsonProperty("marketplace_tax_reduction_level")] public int MarketplaceTaxReductionLevel { get; set; } = 0;
        [JsonProperty("worker_xp_bonus_level")]           public int WorkerXpBonusLevel          { get; set; } = 0;
        [JsonProperty("custom_site_cost_discount_level")] public int CustomSiteCostDiscountLevel { get; set; } = 0;

        // -- effect resolvers --
        // [JsonIgnore] so they stay out of the persisted/wire JSON; they're derived from the levels above

        /// <summary>+0/+2/+4/+6 to the MaxWorkers cap on members' custom sites.</summary>
        [JsonIgnore] public int SiteMaxWorkersBonus => Clamp(SiteMaxWorkersBonusLevel) * 2;

        /// <summary>Reduces marketplace house tax by 0/1/2/3 percentage points.</summary>
        [JsonIgnore] public int MarketplaceTaxReductionPoints => Clamp(MarketplaceTaxReductionLevel);

        /// <summary>Multiplies worker cycle XP by 1.0/1.25/1.5/2.0.</summary>
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

        /// <summary>Discounts custom-site build cost by 0/10/20/30%.</summary>
        [JsonIgnore] public double CustomSiteCostMultiplier => 1.0 - 0.10 * Clamp(CustomSiteCostDiscountLevel);

        /// <summary>
        /// Silver cost to advance from <paramref name="currentLevel"/> to the
        /// next level. Quadratic ladder: 5,000 → 15,000 → 30,000.
        /// </summary>
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
    }
}
