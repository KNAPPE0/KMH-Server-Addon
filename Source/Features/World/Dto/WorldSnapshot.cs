using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.World.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class WorldSnapshot
    {
        // Monotonic under the store lock; two transports can deliver out of order, so the client drops anything older.
        [JsonProperty("revision")] public long Revision { get; set; } = 0;
        [JsonProperty("events")]        public List<WorldEventDto>  Events       { get; set; } = new List<WorldEventDto>();
        [JsonProperty("server_quests")] public List<ServerQuestDto> ServerQuests { get; set; } = new List<ServerQuestDto>();
    }

    // Time-limited server-wide event. Magnitude's meaning depends on Type; Target is an optional def the event scopes to.
    public class WorldEventDto
    {
        public const string TaxHoliday      = "tax_holiday";       // house tax -> 0
        public const string MarketBoom      = "market_boom";       // sale payouts up by Magnitude percent
        public const string MarketCrash     = "market_crash";      // sale payouts down by Magnitude percent
        public const string ResourceShortage = "resource_shortage"; // a category's min price spikes (display/advisory)
        public const string DoubleWorkerXp  = "double_worker_xp";  // site worker XP * (Magnitude/100)
        public const string HouseStipend    = "house_stipend";     // one-shot payout of Magnitude silver to each online player
        public const string BountyTarget    = "bounty_target";     // everyone hunts Target for a shared pot of Magnitude
        public const string WorldWeather    = "world_weather";     // clients apply GameConditionDef Target to their maps

        [JsonProperty("id")]                public long   Id              { get; set; } = 0;
        [JsonProperty("type")]              public string Type            { get; set; } = "";
        [JsonProperty("title")]             public string Title           { get; set; } = "";
        [JsonProperty("description")]       public string Description     { get; set; } = "";
        [JsonProperty("magnitude")]         public double Magnitude       { get; set; } = 0;
        [JsonProperty("target")]            public string Target          { get; set; } = "";
        [JsonProperty("started_utc_ticks")] public long   StartedUtcTicks { get; set; } = 0;
        [JsonProperty("ends_utc_ticks")]    public long   EndsUtcTicks    { get; set; } = 0; // <= now means over; 0 only in pre-1.2.2 data

        public WorldEventDto ShallowClone() => (WorldEventDto)MemberwiseClone();
    }

    // A server-owned quest (distinct from player-posted), funded from the house pool.
    public class ServerQuestDto
    {
        public const string KindCooperative = "cooperative"; // shared progress, contributors split the reward
        public const string KindCompetitive = "competitive"; // a race, first to finish takes the pot

        public const string ObjDeliver = "deliver";
        public const string ObjHunt    = "hunt";
        public const string ObjBuild   = "build";

        public const string OpNone     = "";
        public const string OpAssault  = "assault";
        public const string OpCapture  = "capture";
        public const string OpSupply   = "supply";
        public const string OpRepair   = "repair";
        public const string OpReclaim  = "reclaim";
        public const string OpDefend   = "defend";

        public static readonly string[] AllOperationTypes =
            { OpAssault, OpCapture, OpSupply, OpRepair, OpReclaim, OpDefend };

        public const string SourceWorldDirector = "world_director";

        public const string ConsequenceNone             = "";
        public const string ConsequenceOutpostClaimable = "outpost_claimable";
        public const string ConsequenceOutpostReclaimed = "outpost_reclaimed";
        public const string ConsequenceOutpostCaptured  = "outpost_captured";

        public static readonly string[] AllConsequences =
            { ConsequenceOutpostClaimable, ConsequenceOutpostReclaimed, ConsequenceOutpostCaptured };

        public const string StateActive    = "active";
        public const string StateCompleted = "completed";
        public const string StateExpired   = "expired";

        [JsonProperty("id")]              public long   Id            { get; set; } = 0;
        [JsonProperty("kind")]            public string Kind          { get; set; } = KindCooperative;
        [JsonProperty("objective")]       public string Objective     { get; set; } = ObjDeliver;
        [JsonProperty("title")]           public string Title         { get; set; } = "";
        [JsonProperty("description")]     public string Description   { get; set; } = "";
        [JsonProperty("target_def_name")] public string TargetDefName { get; set; } = "";
        [JsonProperty("goal_qty")]        public int    GoalQty       { get; set; } = 0;
        [JsonProperty("progress_qty")]    public int    ProgressQty   { get; set; } = 0;
        [JsonProperty("reward_pool")]     public long   RewardPool    { get; set; } = 0;
        // Only this portion is refunded on expiry, because returning the minted part would inflate the pool.
        [JsonProperty("reserved_from_pool")] public long ReservedFromPool { get; set; } = 0;
        [JsonProperty("state")]           public string State         { get; set; } = StateActive;
        [JsonProperty("winner")]          public string Winner        { get; set; } = ""; // competitive only
        [JsonProperty("ends_utc_ticks")]  public long   EndsUtcTicks  { get; set; } = 0;

        [JsonProperty("operation_type")]   public string OperationType   { get; set; } = OpNone;
        [JsonProperty("operation_source")] public string OperationSource { get; set; } = "";
        [JsonProperty("target_site_tile")] public int    TargetSiteTile  { get; set; } = -1;
        [JsonProperty("consequence")]      public string Consequence     { get; set; } = ConsequenceNone;
        [JsonProperty("window_ends_utc")]  public long   WindowEndsUtcTicks { get; set; } = 0;

        // Breaks a tie on quantity by who started first, so the winner is a rule rather than dictionary order.
        [JsonProperty("contributor_first_utc")] public Dictionary<string, long> ContributorFirstUtc { get; set; }
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("contributors")]    public Dictionary<string, int> Contributors
            { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Both dictionaries above are mutated by every delivery, so a snapshot must own its own copies.
        public ServerQuestDto ShallowClone() => (ServerQuestDto)MemberwiseClone();
    }
}
