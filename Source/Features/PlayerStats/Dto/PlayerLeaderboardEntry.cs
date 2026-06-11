using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.PlayerStats.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.PlayerStats.Dto. PlayerLeaderboardEntry - same JSON property
    // names (snake_case), same field set, same defaults. Drift here = unparsed fields on the client
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
    }

    // Wrapper for the kmh.player_stats.snapshot envelope payload.
    public class PlayerStatsSnapshot
    {
        [JsonProperty("entries")]
        public List<PlayerLeaderboardEntry> Entries { get; set; } = new List<PlayerLeaderboardEntry>();
    }
}
