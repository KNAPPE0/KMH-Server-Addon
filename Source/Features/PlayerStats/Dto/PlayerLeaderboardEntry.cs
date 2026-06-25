using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.PlayerStats.Dto
{
    // Wire mirror of the patch's PlayerLeaderboardEntry - same snake_case JSON property names, fields, and defaults.
    // Drift here = unparsed fields on the client.
    public class PlayerLeaderboardEntry
    {
        [JsonProperty("username")]             public string Username             { get; set; } = "";
        [JsonProperty("guild_name")]           public string GuildName            { get; set; } = "";
        [JsonProperty("is_linked_to_discord")] public bool   IsLinkedToDiscord    { get; set; } = false;
        [JsonProperty("first_seen_utc_ticks")] public long   FirstSeenUtcTicks    { get; set; } = 0;

        [JsonProperty("silver_donated")]       public long   SilverDonated        { get; set; } = 0;
        [JsonProperty("sales_earned")]         public long   SalesEarned          { get; set; } = 0;
        [JsonProperty("purchases_spent")]      public long   PurchasesSpent       { get; set; } = 0;
        [JsonProperty("quests_completed")]     public int    QuestsCompleted      { get; set; } = 0;
        [JsonProperty("quests_posted")]        public int    QuestsPosted         { get; set; } = 0;
        [JsonProperty("marketplace_sales")]    public int    MarketplaceSales     { get; set; } = 0;
        [JsonProperty("sites_built")]          public int    SitesBuilt           { get; set; } = 0;
        [JsonProperty("sites_raided")]         public int    SitesRaided          { get; set; } = 0;
        [JsonProperty("worker_xp")]            public long   WorkerXp             { get; set; } = 0;
        [JsonProperty("economy_score")]        public long   EconomyScore         { get; set; } = 0;

        // Client-reported colony summary (display-only; the owner's game is the source). Compact colonist fields
        // here drive the leaderboard row; the full colonist profile is fetched on demand (see ColonistProfile).
        [JsonProperty("colony_name")]          public string ColonyName           { get; set; } = "";
        [JsonProperty("colony_age_days")]      public int    ColonyAgeDays        { get; set; } = 0;
        [JsonProperty("time_played_hours")]    public int    TimePlayedHours      { get; set; } = 0;
        [JsonProperty("wealth")]               public long   Wealth               { get; set; } = 0;
        [JsonProperty("kills")]                public long   Kills                { get; set; } = 0;
        [JsonProperty("top_colonist_name")]        public string TopColonistName         { get; set; } = "";
        [JsonProperty("top_colonist_title")]       public string TopColonistTitle        { get; set; } = "";
        [JsonProperty("top_colonist_kills")]       public int    TopColonistKills        { get; set; } = 0;
        [JsonProperty("last_report_utc_ticks")] public long  LastReportUtcTicks   { get; set; } = 0;

        // Server-derived: total silver this player's custom sites have generated (summed across owned sites).
        [JsonProperty("site_silver_produced")]  public long  SiteSilverProduced   { get; set; } = 0;

        // Contract detail (server-tracked as quests complete / fail).
        [JsonProperty("contracts_bounty")]   public int  ContractsBounty  { get; set; } = 0;
        [JsonProperty("contracts_deliver")]  public int  ContractsDeliver { get; set; } = 0;
        [JsonProperty("contracts_hunt")]     public int  ContractsHunt    { get; set; } = 0;
        [JsonProperty("contracts_defend")]   public int  ContractsDefend  { get; set; } = 0;
        [JsonProperty("contracts_failed")]   public int  ContractsFailed  { get; set; } = 0;
        [JsonProperty("contract_streak")]    public int  ContractStreak   { get; set; } = 0;

        // Trade detail (server-tracked on each marketplace sale).
        [JsonProperty("items_sold")]    public long ItemsSold   { get; set; } = 0;
        [JsonProperty("items_bought")]  public long ItemsBought { get; set; } = 0;
        [JsonProperty("largest_sale")]  public long LargestSale { get; set; } = 0;

        // Colony + battle detail (client-reported).
        [JsonProperty("population")]        public int  Population       { get; set; } = 0;
        [JsonProperty("kills_humanlike")]   public long KillsHumanlike   { get; set; } = 0;
        [JsonProperty("kills_mechanoid")]   public long KillsMechanoid   { get; set; } = 0;
        [JsonProperty("kills_animal")]      public long KillsAnimal      { get; set; } = 0;
        [JsonProperty("raids_survived")]    public int  RaidsSurvived    { get; set; } = 0;
        [JsonProperty("pawns_lost")]        public int  PawnsLost        { get; set; } = 0;
        [JsonProperty("development_score")] public int  DevelopmentScore { get; set; } = 0;
        [JsonProperty("defense_score")]     public int  DefenseScore     { get; set; } = 0;
    }

    // Wrapper for the kmh.player_stats.snapshot envelope payload.
    public class PlayerStatsSnapshot
    {
        [JsonProperty("entries")]
        public List<PlayerLeaderboardEntry> Entries { get; set; } = new List<PlayerLeaderboardEntry>();
    }
}
