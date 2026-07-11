using KMHServerAddon.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.World
{
    // World Engine tunables (Config/World.json, clamped on load); auto-roll/auto-gen on by default since v1.2.0.
    internal sealed class WorldConfig
    {
        // File schema version; v2 = durations in minutes (the v1 *Hours keys migrate below).
        public int SchemaVersion { get; set; } = 2;

        // events
        public bool   EventsEnabled         { get; set; } = true;  // master switch for all events (manual + auto)
        // Event types owners allow to fire at all (manual + auto). Remove one to block it entirely.
        public string[] AllowedEventTypes   { get; set; } = new[]
        {
            "tax_holiday", "market_boom", "market_crash", "resource_shortage",
            "double_worker_xp", "house_stipend", "bounty_target", "world_weather",
        };
        public bool   AutoRollEvents        { get; set; } = true;  // on by default since v1.2.0 (living world)
        public int    EventRollEveryMinutes { get; set; } = 240;   // how often auto-roll considers an event (4h)
        public int    EventDefaultMinutes   { get; set; } = 180;   // event length when the command gives none (3h)
        public double EventRollChance       { get; set; } = 0.4;   // ~1 event per 10h on average, organically spaced

        // auto-roll weights (0 = never auto-rolls; still manually triggerable)
        public int WeightTaxHoliday      { get; set; } = 3;
        public int WeightMarketBoom      { get; set; } = 2;
        public int WeightMarketCrash     { get; set; } = 1;
        public int WeightDoubleWorkerXp  { get; set; } = 2;
        public int WeightHouseStipend    { get; set; } = 1;
        public int WeightWorldWeather    { get; set; } = 2;

        // Weather pool ("GameConditionDef|Title|Description"); any vanilla/modded def works, no code needed.
        public string[] WeatherConditionDefs { get; set; } = new[]
        {
            "Aurora|Aurora|An aurora shimmers over every colony. Colonists outdoors gain mood.",
            "Eclipse|Eclipse|An eclipse darkens the world - solar power gutters out.",
            "ColdSnap|Cold Snap|A cold snap grips the region. Protect crops and colonists.",
            "HeatWave|Heat Wave|A heat wave bakes the region. Watch for heatstroke.",
        };

        // Also roll client-reported GameConditionDefs (incl. mods). Off by default - modded conditions can be brutal.
        public bool     AutoDiscoverWeather  { get; set; } = false;
        // Never rolled even when discovered (colony-wreckers + catastrophic/map-ending conditions); substring,
        // case-insensitive. From real modded-catalog coverage: catastrophic/apocalypse-style conditions blocked by
        // default so an owner can't accidentally auto-roll a map-ender.
        public string[] ExcludedWeatherDefs  { get; set; } = new[]
        {
            "ToxicFallout", "VolcanicWinter", "Flashstorm", "ToxicSpewer", "NoxiousHaze",
            "DeadlifeDust", "PsychicDrone", "PsychicSuppression", "GiantSmokeCloud", "UnnaturalDarkness",
            // catastrophic / map-ending (default-blocked):
            "Planetkiller", "Apocalypse", "DeathPall", "UnnaturalHeat", "Bloodmoon", "Bloodrain", "DemonAssault",
            "ElementalAssault", "Manastorm", "ManaDrain", "TimeQuake", "Earthquake", "LavaFlow", "Resurrect",
        };

        // --- generated quests (live catalog instead of premade templates) ---
        // % of auto-gen cycles that synthesize a deliver quest from the live item catalog (templates as fallback).
        public int  GeneratedQuestChance    { get; set; } = 60;
        // Per-unit value band for eligible items (bulk goods, not persona cores).
        public long GeneratedItemMinValue   { get; set; } = 1;
        public long GeneratedItemMaxValue   { get; set; } = 60;
        // Target total goods value per generated quest; goal qty = this / item value.
        public long GeneratedQuestGoalValue { get; set; } = 1500;

        // default magnitudes when a roll/trigger omits one
        public int MarketSwingPercent    { get; set; } = 25;
        public int WorkerXpMultPercent   { get; set; } = 200;
        public int StipendSilver         { get; set; } = 100;

        // server quests
        public bool QuestsEnabled          { get; set; } = true;   // master switch; off blocks all global quests (manual + auto)
        // Objective types owners allow. Removing one blocks it for manual creation and auto-gen. Values: hunt/build/deliver.
        public string[] AllowedObjectives  { get; set; } = new[] { "hunt", "build", "deliver" };
        public bool AutoGenerateQuests     { get; set; } = true;   // on by default since v1.2.0 (living world)
        public int  MaxActiveAutoQuests    { get; set; } = 2;      // how many auto-gen quests may run at once
        public int  QuestGenEveryMinutes   { get; set; } = 720;    // how often auto-gen considers a quest (12h)
        public int  QuestDefaultMinutes    { get; set; } = 1440;   // quest length when the command gives none (24h)
        public int  QuestRewardHousePoolPercent { get; set; } = 50; // auto-gen reward = this % of the house pool
        public int  QuestAutoGoalMin     { get; set; } = 20;   // legacy fallback when player-scaling is off
        public int  QuestAutoGoalMax     { get; set; } = 60;

        // --- global-quest balance (v1.2.0): scale hunt/build goal + duration by ACTIVE players so a quiet server
        // isn't asked for 58 kills in 24h. Goal = clamp(Base + activePlayers * PerPlayer * difficulty, Min, Max). ---
        public bool   GlobalQuestScaleByActivePlayers   { get; set; } = true;
        public int    GlobalQuestBaseTargetCount        { get; set; } = 10;
        public int    GlobalQuestTargetsPerActivePlayer { get; set; } = 6;
        public int    GlobalQuestMinTargetCount         { get; set; } = 10;
        public int    GlobalQuestMaxTargetCount         { get; set; } = 80;
        public double GlobalQuestDifficultyMultiplier   { get; set; } = 1.0;
        public bool   GlobalQuestUseOnlinePlayersOnly   { get; set; } = false; // false = recently-active (maps to online today)
        public int    GlobalQuestRecentlyActiveMinutes  { get; set; } = 120;
        public int    GlobalQuestMinActivePlayers       { get; set; } = 1;    // don't auto-gen below this many active players
        public int    GlobalQuestDefaultDurationHours   { get; set; } = 48;
        public int    GlobalQuestMinDurationHours       { get; set; } = 24;
        public int    GlobalQuestMaxDurationHours       { get; set; } = 72;
        public bool   GlobalQuestAllowSameTargetRepeat  { get; set; } = false;
        public int    GlobalQuestRepeatTargetCooldownHours { get; set; } = 24;
        // Reward: on top of the economy-driven target below, add this per target unit so bigger quests pay more.
        public int    GlobalQuestRewardPerTarget        { get; set; } = 50;

        // --- reward funding (economy-driven, always backed by real silver) ---
        // Auto-gen won't spawn a quest it can't fund to at least this. Stops $0 grind quests; a quest only appears
        // when the house pool can actually pay it.
        public int  QuestMinReward            { get; set; } = 250;
        // Reward target also scales with the live server economy: this many silver per 1000 of total reported
        // colony wealth (RimWorld's own economy, summed across colonies). 0 = ignore wealth, use the pool % only.
        public int  QuestRewardWealthPermille { get; set; } = 2;     // 0.2% of total colony wealth
        // Cap on an auto-gen reward so a rich server can't sink the whole pool into one quest.
        public int  QuestRewardMaxReward      { get; set; } = 5000;
        // Reward target can also track the real RimWorld value of the requested goods: this % of (goal x the item's
        // BaseMarketValue, reported by clients). 100 = pay roughly what the goods are worth; 0 = ignore item value.
        public int  QuestRewardValuePercent   { get; set; } = 100;
        // One-time prime of the house pool on a brand-new server, so the first quests can pay before any tax
        // revenue accrues. Injected exactly once (tracked in the marketplace store); everything after is the closed
        // tax loop. 0 = no seed.
        public int  HousePoolSeed             { get; set; } = 5000;
        // Central bank: when the house pool can't fully back a quest's reward, mint the shortfall (up to the same
        // caps above) so rewards never dry up on a busy server. Only the pool-backed part is refunded on expiry, so
        // minting never inflates the pool. Set false to stay strictly closed-loop (rewards then capped to the pool).
        public bool AllowMintedRewards        { get; set; } = true;

        // "objective|defName|title|description"; objective = hunt/build. defName must exist on players' games (server
        // has no def db), so defaults are vanilla-only.
        public string[] QuestTemplates   { get; set; } = new[]
        {
            "hunt|Muffalo|Thin the Herds|Hunters are needed across the colonies to cull the muffalo.",
            "hunt|Hare|Pest Control|Cull the hares before they overrun the fields.",
            "hunt|Boomalope|Volatile Culling|Cull the boomalopes before a stray spark ignites the herds.",
            "hunt|Bear_Grizzly|Bear Season|Grizzlies are menacing the trade routes. Thin them out.",
            "hunt|Alphabeaver|Timber Menace|Alphabeavers are stripping the forests bare. Stop them.",
            "build|Sandbags|Fortify the Frontier|Throw up sandbags for the war effort.",
            "build|Turret_MiniTurret|Arms Race|The realm calls for defensive emplacements.",
            "build|SculptureSmall|Patron of the Arts|Commission sculptures to lift the realm's spirits.",
            "deliver|Steel|Steel Drive|The foundries are starving - deliver steel to the cause.",
            "deliver|MedicineHerbal|Field Hospitals|Herbal medicine is needed for the front lines.",
            "deliver|MealSimple|Famine Relief|Deliver meals to keep the outlying settlements fed.",
            "deliver|Cloth|The Weavers' Call|Cloth for uniforms, tents and bandages.",
        };

        // Catches any field in World.json we don't have a property for - used to upgrade the pre-1.1.0 hour fields
        // (EventDefaultHours etc.) to the new minute fields so a re-save keeps the owner's tuning instead of losing it.
        [JsonExtensionData] private System.Collections.Generic.IDictionary<string, JToken> LegacyData { get; set; }
        [JsonIgnore] private bool _migratedFromHours;

        private static WorldConfig _current;
        public static WorldConfig Current => _current ?? (_current = LoadOrDefault());

        public static WorldConfig LoadOrDefault()
        {
            WorldConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.WorldConfigFile, out WorldConfig loaded) && loaded != null
                ? loaded : new WorldConfig();
            cfg.MigrateLegacy();
            cfg.Clamp();
            return cfg;
        }

        // First run: write defaults. Otherwise upgrade an older hour-based file to the minute fields IN PLACE, so
        // owners never have to delete World.json. (Newly ADDED fields need no migration - Json just fills a missing
        // field with its default, which is why every other config keeps working across updates too.)
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.WorldConfigFile))
            {
                JsonFileStore.Save(KmhDataPaths.WorldConfigFile, new WorldConfig());
                return;
            }
            WorldConfig cfg = LoadOrDefault();
            if (cfg._migratedFromHours)
            {
                JsonFileStore.Save(KmhDataPaths.WorldConfigFile, cfg);
                Diagnostics.ServerLog.Info("World: upgraded World.json hour settings to the new minute fields (no data lost).");
            }
        }

        // Convert any pre-1.1.0 *Hours field that's present to the matching *Minutes field, then drop the obsolete
        // keys so they're never written back.
        private void MigrateLegacy()
        {
            _migratedFromHours = false;
            if (LegacyData == null || LegacyData.Count == 0) return;
            if (TryLegacyHours("EventRollEveryHours", out int v1)) EventRollEveryMinutes = v1 * 60;
            if (TryLegacyHours("EventDefaultHours",   out int v2)) EventDefaultMinutes   = v2 * 60;
            if (TryLegacyHours("QuestGenEveryHours",  out int v3)) QuestGenEveryMinutes  = v3 * 60;
            if (TryLegacyHours("QuestDefaultHours",   out int v4)) QuestDefaultMinutes   = v4 * 60;
            LegacyData = null;
        }

        private bool TryLegacyHours(string key, out int hours)
        {
            hours = 0;
            if (LegacyData != null && LegacyData.TryGetValue(key, out JToken t))
            {
                try { hours = t.Value<int>(); _migratedFromHours = true; return true; } catch { }
            }
            return false;
        }

        public static void Reload() => _current = LoadOrDefault();

        private void Clamp()
        {
            EventRollEveryMinutes = Clamp(EventRollEveryMinutes, 1, 43200); // up to 30d
            EventDefaultMinutes   = Clamp(EventDefaultMinutes, 1, 10080);   // up to 7d
            if (EventRollChance < 0) EventRollChance = 0;
            if (EventRollChance > 1) EventRollChance = 1;
            MarketSwingPercent  = Clamp(MarketSwingPercent, 1, 90);
            WorkerXpMultPercent = Clamp(WorkerXpMultPercent, 100, 1000);
            StipendSilver       = Clamp(StipendSilver, 0, 1_000_000);
            MaxActiveAutoQuests  = Clamp(MaxActiveAutoQuests, 1, 50);
            AllowedObjectives    = NormalizeObjectives(AllowedObjectives);
            AllowedEventTypes    = NormalizeEventTypes(AllowedEventTypes);
            QuestGenEveryMinutes = Clamp(QuestGenEveryMinutes, 1, 43200);
            QuestDefaultMinutes  = Clamp(QuestDefaultMinutes, 1, 43200);
            QuestRewardHousePoolPercent = Clamp(QuestRewardHousePoolPercent, 0, 100);
            QuestAutoGoalMin    = Clamp(QuestAutoGoalMin, 1, 100000);
            QuestAutoGoalMax    = Clamp(QuestAutoGoalMax, QuestAutoGoalMin, 100000);
            QuestMinReward            = Clamp(QuestMinReward, 0, 10_000_000);
            QuestRewardWealthPermille = Clamp(QuestRewardWealthPermille, 0, 1000);
            QuestRewardMaxReward      = Clamp(QuestRewardMaxReward, QuestMinReward, 100_000_000);
            QuestRewardValuePercent   = Clamp(QuestRewardValuePercent, 0, 1000);
            HousePoolSeed             = Clamp(HousePoolSeed, 0, 100_000_000);
            QuestTemplates      = QuestTemplates ?? System.Array.Empty<string>();
            WeightTaxHoliday     = Clamp(WeightTaxHoliday, 0, 100);
            WeightMarketBoom     = Clamp(WeightMarketBoom, 0, 100);
            WeightMarketCrash    = Clamp(WeightMarketCrash, 0, 100);
            WeightDoubleWorkerXp = Clamp(WeightDoubleWorkerXp, 0, 100);
            WeightHouseStipend   = Clamp(WeightHouseStipend, 0, 100);
            WeightWorldWeather   = Clamp(WeightWorldWeather, 0, 100);
            WeatherConditionDefs = NormalizeWeather(WeatherConditionDefs);
            ExcludedWeatherDefs  = ExcludedWeatherDefs ?? System.Array.Empty<string>();
            GeneratedQuestChance = Clamp(GeneratedQuestChance, 0, 100);
            if (GeneratedItemMinValue < 0) GeneratedItemMinValue = 0;
            if (GeneratedItemMaxValue < GeneratedItemMinValue) GeneratedItemMaxValue = GeneratedItemMinValue;
            if (GeneratedQuestGoalValue < 1) GeneratedQuestGoalValue = 1500;

        }

        // True if this defName may roll as discovered weather (not excluded by the owner).
        // Substring (case-insensitive) so an exclusion like "ToxicFallout" or "Bloodmoon" also catches modded/themed
        // variants ("ToxicFalloutSmall", "VFEA_Bloodmoon"). Labels aren't authority - the defName marker is.
        public bool WeatherDefAllowed(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName) || ExcludedWeatherDefs == null) return true;
            string d = defName.Trim();
            foreach (string x in ExcludedWeatherDefs)
                if (!string.IsNullOrWhiteSpace(x) && d.IndexOf(x.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // True if owners allow this objective. Unknown/blank -> blocked.
        public bool ObjectiveAllowed(string objective)
        {
            if (string.IsNullOrWhiteSpace(objective) || AllowedObjectives == null) return false;
            foreach (string o in AllowedObjectives)
                if (string.Equals(o, objective, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Keep only the known objective tags (lowercased); an empty/garbage list falls back to all three.
        private static string[] NormalizeObjectives(string[] raw)
        {
            var keep = new System.Collections.Generic.List<string>();
            if (raw != null)
                foreach (string s in raw)
                {
                    string o = (s ?? "").Trim().ToLowerInvariant();
                    if ((o == "hunt" || o == "build" || o == "deliver") && !keep.Contains(o)) keep.Add(o);
                }
            return keep.Count > 0 ? keep.ToArray() : new[] { "hunt", "build", "deliver" };
        }

        // True if owners allow this event type to fire. Unknown/blank -> blocked.
        public bool EventTypeAllowed(string type)
        {
            if (string.IsNullOrWhiteSpace(type) || AllowedEventTypes == null) return false;
            foreach (string t in AllowedEventTypes)
                if (string.Equals(t, type, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] KnownEventTypes =
        {
            "tax_holiday", "market_boom", "market_crash", "resource_shortage",
            "double_worker_xp", "house_stipend", "bounty_target", "world_weather",
        };

        // Keep only "DefName|Title|Description" entries with a non-empty defName; garbage list -> defaults.
        private static string[] NormalizeWeather(string[] raw)
        {
            var keep = new System.Collections.Generic.List<string>();
            if (raw != null)
                foreach (string s in raw)
                    if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(s.Split('|')[0]))
                        keep.Add(s.Trim());
            return keep.Count > 0 ? keep.ToArray() : new WorldConfig().WeatherConditionDefs;
        }

        // Keep only known event tags (lowercased); an empty/garbage list falls back to all of them.
        private static string[] NormalizeEventTypes(string[] raw)
        {
            var keep = new System.Collections.Generic.List<string>();
            if (raw != null)
                foreach (string s in raw)
                {
                    string t = (s ?? "").Trim().ToLowerInvariant();
                    if (System.Array.IndexOf(KnownEventTypes, t) >= 0 && !keep.Contains(t)) keep.Add(t);
                }
            return keep.Count > 0 ? keep.ToArray() : (string[])KnownEventTypes.Clone();
        }
    }
}
