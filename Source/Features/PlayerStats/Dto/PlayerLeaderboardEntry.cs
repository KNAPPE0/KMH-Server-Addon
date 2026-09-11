using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.PlayerStats.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class PlayerLeaderboardEntry
    {
        [JsonProperty("username")]             public string Username             { get; set; } = "";
        [JsonProperty("guild_name")]           public string GuildName            { get; set; } = "";
        [JsonProperty("is_linked_to_discord")] public bool   IsLinkedToDiscord    { get; set; } = false;
        // A staff claim, so it is derived every snapshot and never read from a client report.
        [JsonProperty("staff_role")]           public string StaffRole            { get; set; } = "";
        [JsonProperty("first_seen_utc_ticks")] public long   FirstSeenUtcTicks    { get; set; } = 0;
        // Server-clamped to the time that actually passed, so playtime cannot be claimed faster than it elapses.
        [JsonProperty("last_seen_utc_ticks")]  public long   LastSeenUtcTicks     { get; set; } = 0;
        [JsonProperty("active_seconds")]       public long   ActiveSeconds        { get; set; } = 0;
        [JsonProperty("connected_seconds")]    public long   ConnectedSeconds     { get; set; } = 0;

        [JsonProperty("silver_donated")]       public long   SilverDonated        { get; set; } = 0;
        [JsonProperty("sales_earned")]         public long   SalesEarned          { get; set; } = 0;
        [JsonProperty("purchases_spent")]      public long   PurchasesSpent       { get; set; } = 0;
        [JsonProperty("quests_completed")]     public int    QuestsCompleted      { get; set; } = 0;
        [JsonProperty("quests_posted")]        public int    QuestsPosted         { get; set; } = 0;
        [JsonProperty("marketplace_sales")]    public int    MarketplaceSales     { get; set; } = 0;
        // Cumulative: counts a newly built site only, so losing one never takes it back.
        [JsonProperty("sites_built")]          public int    SitesBuilt           { get; set; } = 0;
        // Derived per snapshot, never stored: what the player controls right now.
        [JsonProperty("sites_owned")]          public int    SitesOwned           { get; set; } = 0;
        [JsonProperty("outposts_held")]        public int    OutpostsHeld         { get; set; } = 0;
        // Cumulative: a committed capture stays counted even at zero outposts held.
        [JsonProperty("frontier_captures")]    public int    FrontierCaptures     { get; set; } = 0;
        [JsonProperty("worker_xp")]            public long   WorkerXp             { get; set; } = 0;
        [JsonProperty("economy_score")]        public long   EconomyScore         { get; set; } = 0;

        // Client-reported and display-only - none of this may feed a server-side decision.
        [JsonProperty("colony_name")]          public string ColonyName           { get; set; } = "";
        [JsonProperty("colony_age_days")]      public int    ColonyAgeDays        { get; set; } = 0;
        [JsonProperty("time_played_hours")]    public int    TimePlayedHours      { get; set; } = 0;
        [JsonProperty("wealth")]               public long   Wealth               { get; set; } = 0;
        // Server-authoritative off-map value; Wealth stays the client's map report so the two never merge.
        [JsonProperty("kmh_wealth")]           public long   KmhWealth            { get; set; } = 0;
        [JsonProperty("settlements")]          public List<SettlementReport> Settlements { get; set; } = new List<SettlementReport>();
        // Derived, never stored: every wealth ranking reads this, so no source can reach one board and miss another.
        [JsonIgnore] public long TotalWealth => Wealth + KmhWealth;
        [JsonProperty("kills")]                public long   Kills                { get; set; } = 0;
        [JsonProperty("top_colonist_name")]        public string TopColonistName         { get; set; } = "";
        [JsonProperty("top_colonist_title")]       public string TopColonistTitle        { get; set; } = "";
        [JsonProperty("top_colonist_kills")]       public int    TopColonistKills        { get; set; } = 0;
        [JsonProperty("last_report_utc_ticks")] public long  LastReportUtcTicks   { get; set; } = 0;

        // Server-derived, not reported by the client.
        [JsonProperty("site_silver_produced")]  public long  SiteSilverProduced   { get; set; } = 0;

        // Server-tracked as quests complete or fail.
        [JsonProperty("contracts_bounty")]   public int  ContractsBounty  { get; set; } = 0;
        [JsonProperty("contracts_deliver")]  public int  ContractsDeliver { get; set; } = 0;
        [JsonProperty("contracts_hunt")]     public int  ContractsHunt    { get; set; } = 0;
        [JsonProperty("contracts_defend")]   public int  ContractsDefend  { get; set; } = 0;
        [JsonProperty("contracts_failed")]   public int  ContractsFailed  { get; set; } = 0;
        [JsonProperty("contract_streak")]    public int  ContractStreak   { get; set; } = 0;

        // Server-tracked on each marketplace sale.
        [JsonProperty("items_sold")]    public long ItemsSold   { get; set; } = 0;
        [JsonProperty("items_bought")]  public long ItemsBought { get; set; } = 0;
        [JsonProperty("largest_sale")]  public long LargestSale { get; set; } = 0;

        // Client-reported and display-only.
        [JsonProperty("population")]        public int  Population       { get; set; } = 0;
        [JsonProperty("kills_humanlike")]   public long KillsHumanlike   { get; set; } = 0;
        [JsonProperty("kills_mechanoid")]   public long KillsMechanoid   { get; set; } = 0;
        [JsonProperty("kills_animal")]      public long KillsAnimal      { get; set; } = 0;
        [JsonProperty("raids_survived")]    public int  RaidsSurvived    { get; set; } = 0;
        [JsonProperty("pawns_lost")]        public int  PawnsLost        { get; set; } = 0;
        [JsonProperty("development_score")] public int  DevelopmentScore { get; set; } = 0;
        [JsonProperty("defense_score")]     public int  DefenseScore     { get; set; } = 0;
    }

    public class PlayerStatsSnapshot
    {
        [JsonProperty("entries")]
        public List<PlayerLeaderboardEntry> Entries { get; set; } = new List<PlayerLeaderboardEntry>();
    }
}
