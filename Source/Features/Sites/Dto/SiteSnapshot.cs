using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites.Dto
{
    // Wire shapes for KMH custom sites. Mirror of the patch-side DTO. A custom site is a player-built economic node
    // tied to a world tile that produces a chosen item every cycle; workers boost output and earn XP. Access mode +
    // reward destination ride as strings so renames don't corrupt old data
    public class SiteSnapshot
    {
        [JsonProperty("sites")]   public List<SiteEntry> Sites { get; set; } = new List<SiteEntry>();
        [JsonProperty("allow_custom_sites")] public bool AllowCustomSites { get; set; } = true;
        [JsonProperty("price_multiplier")]   public double PriceMultiplier  { get; set; } = 3.0;
        [JsonProperty("max_reward_amount")]  public int    MaxRewardAmount  { get; set; } = 50;
    }

    public class SiteEntry
    {
        public const string AccessGuildOnly = "guild_only";
        public const string AccessPublic    = "public";
        public const string AccessPrivate   = "private";

        public const string DestCaravan     = "caravan";
        public const string DestTreasury    = "treasury";
        public const string DestMarketplace = "marketplace";

        [JsonProperty("tile")]              public int    Tile             { get; set; } = -1;
        [JsonProperty("owner_username")]    public string OwnerUsername    { get; set; } = "";
        [JsonProperty("owner_guild")]       public string OwnerGuild       { get; set; } = "";

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

        // Server-authoritative output tier + the worker-scaling ceilings it grants (persisted so the reward cycle is
        // stable even if the config changes). Old sites get these backfilled on load by re-classifying the item.
        [JsonProperty("output_tier")]       public int    OutputTier            { get; set; } = 1;
        [JsonProperty("tier_max_speed")]    public double TierMaxSpeedMultiplier { get; set; } = 3.0;
        [JsonProperty("tier_max_output")]   public double TierMaxOutputMultiplier { get; set; } = 2.0;
        // A legacy site whose output is now blocked by current rules: paused, needs admin review (never silently
        // keeps producing a now-illegal output).
        [JsonProperty("blocked_output")]    public bool   BlockedOutput         { get; set; } = false;

        [JsonProperty("last_reward_utc_ticks")] public long  LastRewardUtcTicks   { get; set; } = 0;
        [JsonProperty("total_silver_generated")] public double TotalSilverGenerated { get; set; } = 0;

        // Display-only fields the server fills for the client (not persisted as canonical truth - derived from the
        // live state each snapshot). effective_cycle_minutes is 0 when paused (never Infinity/NaN/huge).
        [JsonProperty("production_multiplier")] public double ProductionMultiplier { get; set; } = 0;
        [JsonProperty("effective_cycle_minutes")] public double EffectiveCycleMinutes { get; set; } = 0;
        [JsonProperty("is_producing")]      public bool   IsProducing { get; set; } = false;
        [JsonProperty("paused_reason")]     public string PausedReason { get; set; } = "";
    }

    public class WorkerProgressDto
    {
        [JsonProperty("joined_utc_ticks")] public long   JoinedUtcTicks  { get; set; } = 0;
        [JsonProperty("cycles_completed")] public int    CyclesCompleted { get; set; } = 0;
        [JsonProperty("xp")]               public double Xp              { get; set; } = 0;
        [JsonProperty("base_skill_level")] public int    BaseSkillLevel  { get; set; } = 0;
        [JsonProperty("destination")]      public string Destination     { get; set; } = SiteEntry.DestTreasury;

        // The actual colonist doing the work (client-reported; the headless server can't verify it, so it's advisory
        // for display + skill). Empty PawnName = a legacy account-level worker. Refreshed each join / re-validate.
        [JsonProperty("pawn_name")]         public string PawnName          { get; set; } = "";
        [JsonProperty("pawn_load_id")]      public int    PawnLoadId        { get; set; } = -1;
        [JsonProperty("last_validated_utc")] public long  LastValidatedUtc  { get; set; } = 0;

        // Legacy = an old account worker with no real pawn; disabled (doesn't produce) until reassigned. Not deleted.
        [JsonProperty("legacy")]            public bool   Legacy            { get; set; } = false;
        [JsonProperty("blocked_reason")]    public string BlockedReason     { get; set; } = "";

        [JsonIgnore] public bool IsActivePawnWorker => PawnLoadId > 0 && !Legacy && string.IsNullOrEmpty(BlockedReason);

        // XP-derived level: xpForLevel(L) = 1000 + L*1000, capped at 20. Current level is the better of the
        // brought-in skill and what XP has earned
        [JsonIgnore] public int CurrentLevel
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
                return Math.Max(BaseSkillLevel, xpLevel);
            }
        }
    }
}
