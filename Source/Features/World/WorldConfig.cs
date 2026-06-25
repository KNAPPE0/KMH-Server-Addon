using KMHServerAddon.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.World
{
    // World Engine tunables (KMH-Data/Config/World.json), clamped on load. Auto-roll/auto-gen off by default; reload
    // via /kmh reload-world.
    internal sealed class WorldConfig
    {
        // Schema version for this file. Bumped when fields are renamed/reshaped so a future load migrates
        // deterministically instead of guessing from which keys are present. An absent value (old file) reads as the
        // current baseline; the v1->v2 hour->minute upgrade below is detected by the leftover *Hours keys.
        // v2 = durations stored in minutes (was hours).
        public int SchemaVersion { get; set; } = 2;

        // events
        public bool   EventsEnabled         { get; set; } = true;  // gates auto-roll; manual triggers always work
        public bool   AutoRollEvents        { get; set; } = false;
        public int    EventRollEveryMinutes { get; set; } = 360;   // how often auto-roll considers an event (6h)
        public int    EventDefaultMinutes   { get; set; } = 180;   // event length when the command gives none (3h)
        public double EventRollChance       { get; set; } = 0.5;

        // auto-roll weights (0 = never auto-rolls; still manually triggerable)
        public int WeightTaxHoliday      { get; set; } = 3;
        public int WeightMarketBoom      { get; set; } = 2;
        public int WeightMarketCrash     { get; set; } = 1;
        public int WeightDoubleWorkerXp  { get; set; } = 2;
        public int WeightHouseStipend    { get; set; } = 1;

        // default magnitudes when a roll/trigger omits one
        public int MarketSwingPercent    { get; set; } = 25;
        public int WorkerXpMultPercent   { get; set; } = 200;
        public int StipendSilver         { get; set; } = 100;

        // server quests
        public bool AutoGenerateQuests     { get; set; } = false;
        public int  QuestGenEveryMinutes   { get; set; } = 720;    // how often auto-gen considers a quest (12h)
        public int  QuestDefaultMinutes    { get; set; } = 1440;   // quest length when the command gives none (24h)
        public int  QuestRewardHousePoolPercent { get; set; } = 50; // auto-gen reward = this % of the house pool
        public int  QuestAutoGoalMin     { get; set; } = 20;
        public int  QuestAutoGoalMax     { get; set; } = 60;

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
            "build|Sandbags|Fortify the Frontier|Throw up sandbags for the war effort.",
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
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
