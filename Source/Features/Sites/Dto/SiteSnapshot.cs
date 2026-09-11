using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites.Dto
{
    // Access mode and reward destination ride as strings so a rename cannot corrupt old data.
    public class SiteSnapshot
    {
        // Monotonic under the store lock; two transports can deliver out of order, so the client drops anything older.
        [JsonProperty("revision")] public long Revision { get; set; } = 0;
        [JsonProperty("sites")]   public List<SiteEntry> Sites { get; set; } = new List<SiteEntry>();
        [JsonProperty("allow_custom_sites")] public bool AllowCustomSites { get; set; } = true;
        [JsonProperty("price_multiplier")]   public double PriceMultiplier  { get; set; } = 3.0;
        [JsonProperty("max_reward_amount")]  public int    MaxRewardAmount  { get; set; } = 50;

        // Sent so the client prices a button from the server's own constants instead of restating the rules.
        [JsonProperty("building_cost")]        public int BuildingCostSilver  { get; set; } = 750;
        [JsonProperty("repair_per_point")]     public int RepairCostPerPoint  { get; set; } = 25;
        [JsonProperty("storage_per_building")] public int StoragePerBuilding  { get; set; } = 100;
        [JsonProperty("max_storage")]          public int MaxStorageUnits     { get; set; } = 300;
        [JsonProperty("workers_per_housing")]  public int WorkersPerHousing   { get; set; } = 2;
        [JsonProperty("max_housing_bonus")]    public int MaxHousingBonus     { get; set; } = 4;
        [JsonProperty("production_bonus_pct")] public int ProductionBonusPct  { get; set; } = 10;
        [JsonProperty("max_production_pct")]   public int MaxProductionPct    { get; set; } = 30;
    }

    // Echoes the request back, so a reply that lands after the player has typed on can be discarded.
    public class SiteBuildQuote
    {
        [JsonProperty("ok")]            public bool   Ok           { get; set; }
        [JsonProperty("reason")]        public string Reason       { get; set; } = "";
        [JsonProperty("def")]           public string ItemDefName  { get; set; } = "";
        [JsonProperty("amount")]        public int    Amount       { get; set; }
        [JsonProperty("archetype")]     public string Archetype    { get; set; } = "";
        [JsonProperty("family")]        public string Family       { get; set; } = "";
        [JsonProperty("cost")]          public int    Cost         { get; set; }
        // The server's own figures, sent so the quote can be checked against Cost rather than taken on faith.
        [JsonProperty("value")]         public float  MarketValuePerUnit { get; set; }
        [JsonProperty("cost_mult")]     public double CostMultiplier     { get; set; } = 1.0;
        [JsonProperty("cycle_minutes")] public int    CycleMinutes { get; set; }
        [JsonProperty("max_amount")]    public int    MaxAmount    { get; set; }
        [JsonProperty("balance")]       public long   Balance      { get; set; }
        [JsonProperty("affordable")]    public bool   Affordable   { get; set; }
    }

    public class SiteEntry
    {
        public const string OwnerPlayer    = "player";
        public const string OwnerGuildKind = "guild";
        public const string OwnerSystem    = "system";
        public const string OwnerNeutral   = "neutral";

        public const string AccessGuildOnly = "guild_only";
        public const string AccessPublic    = "public";
        public const string AccessPrivate   = "private";

        public const string DestCaravan     = "caravan";
        public const string DestTreasury    = "treasury";
        public const string DestMarketplace = "marketplace";
        public const string DestStorage     = "storage";

        public const string TemplateNone      = "";
        public const string TemplateRuins     = "ruins";
        public const string TemplateResource  = "resource";
        public const string TemplateDepot     = "depot";
        public const string TemplateFortified = "fortified";
        public const string TemplateRelay     = "relay";

        public static readonly string[] AllTemplates =
            { TemplateRuins, TemplateResource, TemplateDepot, TemplateFortified, TemplateRelay };

        public const string OutpostNone      = "";
        public const string OutpostDerelict  = "derelict";
        public const string OutpostHostile   = "hostile";
        public const string OutpostDefeated  = "defeated";
        public const string OutpostClaimable = "claimable";
        public const string OutpostCaptured  = "captured";
        public const string OutpostDormant   = "dormant";

        public static readonly string[] AllOutpostStates =
            { OutpostDerelict, OutpostHostile, OutpostDefeated, OutpostClaimable, OutpostCaptured, OutpostDormant };

        public const string ArchetypeCustom    = "custom";
        public const string ArchetypeFarmland  = "farmland";
        public const string ArchetypeQuarry    = "quarry";
        public const string ArchetypeWoodland  = "woodland";
        public const string ArchetypeRoadworks = "roadworks";
        // Additive in v1.3.0: a site saved by an older build simply never carries it.
        public const string ArchetypeRanch     = "ranch";

        [JsonProperty("tile")]              public int    Tile             { get; set; } = -1;
        [JsonProperty("owner_username")]    public string OwnerUsername    { get; set; } = "";
        [JsonProperty("owner_guild")]       public string OwnerGuild       { get; set; } = "";

        // The only authority discriminator; absent means player, so every existing site is unchanged.
        [JsonProperty("owner_kind")]        public string OwnerKind       { get; set; } = OwnerPlayer;
        // Authoritative for owner_kind == guild, and derived by the server rather than taken from a client.
        [JsonProperty("controlling_guild")] public string ControllingGuild { get; set; } = "";
        [JsonProperty("controller_faction")] public string ControllerFaction { get; set; } = "";
        // Presentation only - it never determines permission.
        [JsonProperty("site_name")]         public string SiteName        { get; set; } = "";

        // Blank means an ordinary Site, which is what keeps pre-Frontier data untouched.
        [JsonProperty("outpost_template")]  public string OutpostTemplate { get; set; } = TemplateNone;
        [JsonProperty("outpost_state")]     public string OutpostState    { get; set; } = OutpostNone;
        [JsonProperty("established_utc")]   public long   EstablishedUtcTicks { get; set; } = 0;
        [JsonProperty("claim_window_ends_utc")] public long ClaimWindowEndsUtcTicks { get; set; } = 0;   // 0 = not claimable
        [JsonProperty("origin_operation_id")] public long OriginOperationId { get; set; } = 0;
        // Kept so captured infrastructure does not read like something built from scratch.
        [JsonProperty("captured_by")]       public string CapturedBy    { get; set; } = "";
        // Kept here because the operation that decided it is pruned long before the claim window closes.
        [JsonProperty("claim_eligible_username")] public string ClaimEligibleUsername { get; set; } = "";
        // Caller-scoped and decided by the server: whether THIS caller may claim it right now.
        [JsonProperty("can_claim")]         public bool   CanClaim      { get; set; } = false;



        // Identity and defaults only: it does not change how production is computed, so an old site produces the same.
        [JsonProperty("archetype")]         public string Archetype       { get; set; } = "";

        // 0-100, defaulting to 100 because a site saved without it must read as healthy rather than broken.
        [JsonProperty("stability")]         public int    Stability       { get; set; } = 100;

        // The Site Core is implicit and never stored here, so it cannot consume a slot or vanish on an old save.
        [JsonProperty("buildings")]         public List<SiteBuilding> Buildings { get; set; } = new List<SiteBuilding>();

        // Still the owner's value, so it counts as off-map wealth.
        [JsonProperty("stored_items")]      public Dictionary<string, int> StoredItems { get; set; }
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("item_def_name")]     public string ItemDefName      { get; set; } = "";
        [JsonProperty("base_amount")]       public int    BaseAmountPerCycle { get; set; } = 1;
        [JsonProperty("market_value")]      public float  MarketValuePerUnit { get; set; } = 0f;
        [JsonProperty("base_cycle_ms")]     public double BaseCycleTimeMs  { get; set; } = 1800000;

        [JsonProperty("access_mode")]       public string AccessMode       { get; set; } = AccessGuildOnly;
        [JsonProperty("owner_tax_percent")] public int    OwnerTaxPercent  { get; set; } = 10;

        [JsonProperty("workers")]           public List<string> Workers    { get; set; } = new List<string>();
        [JsonProperty("max_workers")]       public int    MaxWorkers       { get; set; } = 5;
        [JsonProperty("worker_progress")]   public Dictionary<string, WorkerProgressDto> WorkerProgress
            { get; set; } = new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("owner_destination")] public string OwnerRewardDestination { get; set; } = DestTreasury;
        [JsonProperty("marketplace_unit_price")] public int MarketplaceUnitPrice { get; set; } = 1;
        [JsonProperty("relevant_skill")]    public string RelevantSkillDef { get; set; } = "Crafting";

        // Persisted rather than derived, so a config change cannot move an existing site's reward cycle.
        [JsonProperty("output_tier")]       public int    OutputTier            { get; set; } = 1;
        [JsonProperty("tier_max_speed")]    public double TierMaxSpeedMultiplier { get; set; } = 3.0;
        [JsonProperty("tier_max_output")]   public double TierMaxOutputMultiplier { get; set; } = 2.0;
        // Paused pending admin review, so a legacy site never silently keeps producing a now-illegal output.
        [JsonProperty("blocked_output")]    public bool   BlockedOutput         { get; set; } = false;

        [JsonProperty("last_reward_utc_ticks")] public long  LastRewardUtcTicks   { get; set; } = 0;
        [JsonProperty("total_silver_generated")] public double TotalSilverGenerated { get; set; } = 0;

        // Derived each snapshot rather than persisted, and effective_cycle_minutes is 0 when paused, never Infinity.
        [JsonProperty("production_multiplier")] public double ProductionMultiplier { get; set; } = 0;
        [JsonProperty("effective_cycle_minutes")] public double EffectiveCycleMinutes { get; set; } = 0;
        [JsonProperty("is_producing")]      public bool   IsProducing { get; set; } = false;
        [JsonProperty("paused_reason")]     public string PausedReason { get; set; } = "";

        // Scalars only - every collection must still be replaced by the caller or the copy shares the store's state.
        public SiteEntry ShallowClone() => (SiteEntry)MemberwiseClone();
    }

    // Deliberately just a kind, a level and a condition - a fatter record is how upgrade trees creep in.
    public class SiteBuilding
    {
        public const string KindProduction = "production";
        public const string KindHousing    = "housing";
        public const string KindStorage    = "storage";
        public const string KindLogistics  = "logistics";
        public const string KindDefense    = "defense";

        // Damage is tracked per site as Stability, so these exist to keep every effect gated on Operational.
        public const string StateOperational = "operational";
        public const string StateDamaged     = "damaged";
        public const string StateRuined      = "ruined";

        [JsonProperty("kind")]  public string Kind  { get; set; } = "";
        [JsonProperty("level")] public int    Level { get; set; } = 1;
        [JsonProperty("state")] public string State { get; set; } = StateOperational;

        public SiteBuilding ShallowClone() => (SiteBuilding)MemberwiseClone();
    }

    public class WorkerProgressDto
    {
        [JsonProperty("joined_utc_ticks")] public long   JoinedUtcTicks  { get; set; } = 0;
        [JsonProperty("cycles_completed")] public int    CyclesCompleted { get; set; } = 0;
        [JsonProperty("xp")]               public double Xp              { get; set; } = 0;
        [JsonProperty("base_skill_level")] public int    BaseSkillLevel  { get; set; } = 0;
        [JsonProperty("destination")]      public string Destination     { get; set; } = SiteEntry.DestTreasury;

        // Client-reported and advisory: a headless server cannot verify the pawn, so nothing gates on it.
        [JsonProperty("pawn_name")]         public string PawnName          { get; set; } = "";
        [JsonProperty("pawn_load_id")]      public int    PawnLoadId        { get; set; } = -1;
        [JsonProperty("last_validated_utc")] public long  LastValidatedUtc  { get; set; } = 0;

        // An old account worker with no real pawn: disabled until reassigned rather than deleted.
        [JsonProperty("legacy")]            public bool   Legacy            { get; set; } = false;
        [JsonProperty("blocked_reason")]    public string BlockedReason     { get; set; } = "";

        [JsonIgnore] public bool IsActivePawnWorker => PawnLoadId > 0 && !Legacy && string.IsNullOrEmpty(BlockedReason);

        public WorkerProgressDto ShallowClone() => (WorkerProgressDto)MemberwiseClone();

        // Server-owned XP and the level brought in at join only - never the client's reported pawn skill, which bought a free skill 20.
        [JsonIgnore] public int EarnedLevel
        {
            get
            {
                int xpLevel = 0;
                double remaining = Xp;
                while (xpLevel < 20)
                {
                    double need = 1000 + xpLevel * 1000;
                    if (remaining < need) break;
                    remaining -= need;
                    xpLevel++;
                }
                return xpLevel;
            }
        }

        // What a player is shown: their pawn's reported skill, or their earned level once it overtakes it.
        [JsonIgnore] public int CurrentLevel => Math.Max(BaseSkillLevel, EarnedLevel);
    }
}
