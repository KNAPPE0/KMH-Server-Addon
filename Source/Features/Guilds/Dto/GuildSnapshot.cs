using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Guilds.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.Guilds.Dto.
    public class GuildSnapshotEnvelope
    {
        [JsonProperty("in_guild")] public bool          InGuild { get; set; } = false;
        [JsonProperty("guild")]    public GuildSnapshot Guild   { get; set; }

        // Standing invites FOR this player (guildless users only) - lets the client offer accept/decline.
        [JsonProperty("my_invites")] public List<GuildInviteDto> MyInvites { get; set; } = new List<GuildInviteDto>();
    }

    // One standing invite as the invitee sees it.
    public class GuildInviteDto
    {
        [JsonProperty("guild_name")]        public string GuildName       { get; set; } = "";
        [JsonProperty("inviter")]           public string Inviter         { get; set; } = "";
        [JsonProperty("created_utc_ticks")] public long   CreatedUtcTicks { get; set; } = 0;
        [JsonProperty("members")]           public int    Members         { get; set; } = 0;
    }

    // One row of the invite picker: a known, guildless player an officer can invite.
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

        // Guild vault silver (silver-only vault). Wire-only: populated at snapshot-build time from TreasuryStore; the
        // persisted copy stays 0 and is never read back, so this is never a stale duplicate of the authoritative vault.
        [JsonProperty("guild_silver")]           public long GuildSilver { get; set; } = 0;
        [JsonProperty("guild_treasury_enabled")] public bool GuildTreasuryEnabled { get; set; } = true;  // false when GuildTreasuryAccessMode=Disabled
        // Donations awaiting the donor's save-confirm - visible but NOT spendable (not part of guild_silver).
        [JsonProperty("pending_donations_silver")] public long PendingDonationsSilver { get; set; } = 0;

        [JsonProperty("relationships")]
        public Dictionary<string, string> Relationships
            { get; set; } = new Dictionary<string, string>();

        // Invite-only by default - admins/mods invite, the invitee joins. Set open_join to let anyone in
        [JsonProperty("open_join")]       public bool OpenJoin { get; set; } = false;
        [JsonProperty("pending_invites")] public List<string> PendingInvites { get; set; } = new List<string>();

        // Who issued each pending invite and when (keyed by invitee, additive beside pending_invites).
        [JsonProperty("invite_meta")]     public Dictionary<string, GuildInviteMetaDto> InviteMeta { get; set; }
            = new Dictionary<string, GuildInviteMetaDto>(System.StringComparer.OrdinalIgnoreCase);

        // Optional physical Guild Hall. Lives ON the guild so it can't be orphaned or inherited by a reused name.
        // null / HasHall=false = no hall (the default and the compat state for pre-P8 guilds).
        [JsonProperty("hall")]            public GuildHallDto Hall { get; set; }

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

    // A guild's physical hall: a world-tile record with the radius that counts as "near". Server-authoritative.
    public class GuildHallDto
    {
        [JsonProperty("has_hall")]          public bool   HasHall         { get; set; } = false;
        [JsonProperty("tile")]              public int    Tile            { get; set; } = -1;
        [JsonProperty("leader")]            public string Leader          { get; set; } = "";  // who set it
        [JsonProperty("radius_tiles")]      public int    RadiusTiles     { get; set; } = 0;
        [JsonProperty("created_utc_ticks")] public long   CreatedUtcTicks { get; set; } = 0;
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
