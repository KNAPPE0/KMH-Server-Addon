using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.World
{
    internal sealed class WorldEventSettings
    {
        public bool   Enabled          { get; set; } = true;
        public bool   AutoRoll         { get; set; } = true;
        public int    RollEveryMinutes { get; set; } = 240;
        public double RollChance       { get; set; } = 0.4;
        public int    DefaultMinutes   { get; set; } = 180;

        public string[] AllowedTypes { get; set; } = (string[])KnownTypes.Clone();

        public EventWeightSettings Weights { get; set; } = new EventWeightSettings();

        public WorldWeatherSettings Weather { get; set; } = new WorldWeatherSettings();

        // Defaults when a roll or trigger omits a magnitude.
        public int MarketSwingPercent  { get; set; } = 25;
        public int WorkerXpMultPercent { get; set; } = 200;
        public int StipendSilver       { get; set; } = 100;

        internal static readonly string[] KnownTypes =
        {
            "tax_holiday", "market_boom", "market_crash", "resource_shortage",
            "double_worker_xp", "house_stipend", "bounty_target", "world_weather",
        };

        public bool TypeAllowed(string type)
        {
            if (string.IsNullOrWhiteSpace(type) || AllowedTypes == null) return false;
            foreach (string t in AllowedTypes)
                if (string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal void Clamp()
        {
            RollEveryMinutes = WorldConfig.Bound(RollEveryMinutes, 1, 43200);
            DefaultMinutes   = WorldConfig.Bound(DefaultMinutes, 1, 10080);
            if (RollChance < 0) RollChance = 0;
            if (RollChance > 1) RollChance = 1;
            MarketSwingPercent  = WorldConfig.Bound(MarketSwingPercent, 1, 90);
            WorkerXpMultPercent = WorldConfig.Bound(WorkerXpMultPercent, 100, 1000);
            StipendSilver       = WorldConfig.Bound(StipendSilver, 0, 1_000_000);
            AllowedTypes = Normalize(AllowedTypes);
            Weights ??= new EventWeightSettings();
            Weather ??= new WorldWeatherSettings();
            Weights.Clamp();
            Weather.Clamp();
        }

        private static string[] Normalize(string[] raw)
        {
            var keep = new List<string>();
            if (raw != null)
                foreach (string s in raw)
                {
                    string t = (s ?? "").Trim().ToLowerInvariant();
                    if (Array.IndexOf(KnownTypes, t) >= 0 && !keep.Contains(t)) keep.Add(t);
                }
            return keep.Count > 0 ? keep.ToArray() : (string[])KnownTypes.Clone();
        }
    }

    internal sealed class WorldWeatherSettings
    {
        // "GameConditionDef|Title|Description"; any vanilla/modded def works, no code needed.
        public string[] ConditionDefs { get; set; } =
        {
            "Aurora|Aurora|An aurora shimmers over every colony. Colonists outdoors gain mood.",
            "Eclipse|Eclipse|An eclipse darkens the world - solar power gutters out.",
            "ColdSnap|Cold Snap|A cold snap grips the region. Protect crops and colonists.",
            "HeatWave|Heat Wave|A heat wave bakes the region. Watch for heatstroke.",
        };

        // Also roll client-reported GameConditionDefs (incl. mods). Off by default - modded conditions can be brutal.
        public bool AutoDiscover { get; set; } = false;

        // Blocked by default so an owner cannot accidentally auto-roll a map-ending condition.
        public string[] ExcludedDefs { get; set; } =
        {
            "ToxicFallout", "VolcanicWinter", "Flashstorm", "ToxicSpewer", "NoxiousHaze",
            "DeadlifeDust", "PsychicDrone", "PsychicSuppression", "GiantSmokeCloud", "UnnaturalDarkness",
            "Planetkiller", "Apocalypse", "DeathPall", "UnnaturalHeat", "Bloodmoon", "Bloodrain", "DemonAssault",
            "ElementalAssault", "Manastorm", "ManaDrain", "TimeQuake", "Earthquake", "LavaFlow", "Resurrect",
        };

        // Substring matched, so an exclusion also catches modded variants like "ToxicFalloutSmall".
        public bool Allowed(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName) || ExcludedDefs == null) return true;
            string d = defName.Trim();
            foreach (string x in ExcludedDefs)
                if (!string.IsNullOrWhiteSpace(x) && d.IndexOf(x.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        internal void Clamp()
        {
            ConditionDefs = NormalizeDefs(ConditionDefs);
            ExcludedDefs  = ExcludedDefs ?? Array.Empty<string>();
        }

        private static string[] NormalizeDefs(string[] raw)
        {
            var keep = new List<string>();
            if (raw != null)
                foreach (string s in raw)
                    if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(s.Split('|')[0]))
                        keep.Add(s.Trim());
            return keep.Count > 0 ? keep.ToArray() : new WorldWeatherSettings().ConditionDefs;
        }
    }

    internal sealed class EventWeightSettings
    {
        // 0 = never auto-rolls; still manually triggerable.
        public int TaxHoliday     { get; set; } = 3;
        public int MarketBoom     { get; set; } = 2;
        public int MarketCrash    { get; set; } = 1;
        public int DoubleWorkerXp { get; set; } = 2;
        public int HouseStipend   { get; set; } = 1;
        public int WorldWeather   { get; set; } = 2;

        internal void Clamp()
        {
            TaxHoliday     = WorldConfig.Bound(TaxHoliday, 0, 100);
            MarketBoom     = WorldConfig.Bound(MarketBoom, 0, 100);
            MarketCrash    = WorldConfig.Bound(MarketCrash, 0, 100);
            DoubleWorkerXp = WorldConfig.Bound(DoubleWorkerXp, 0, 100);
            HouseStipend   = WorldConfig.Bound(HouseStipend, 0, 100);
            WorldWeather   = WorldConfig.Bound(WorldWeather, 0, 100);
        }
    }

    internal sealed class WorldQuestSettings
    {
        public bool Enabled { get; set; } = true;

        public string[] AllowedObjectives { get; set; } = { "hunt", "build", "deliver" };

        // Hunt/build progress is the client's own word, so off means those quests still run and announce but pay nothing.
        public bool PayForSelfReported { get; set; } = false;

        public int DefaultMinutes { get; set; } = 1440;

        // "objective|defName|title|description" - defaults stay vanilla-only because the server has no def database.
        public string[] Templates { get; set; } =
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

        public WorldQuestAutoGen  AutoGen { get; set; } = new WorldQuestAutoGen();
        public WorldQuestScaling  Scaling { get; set; } = new WorldQuestScaling();
        public WorldQuestRewards  Rewards { get; set; } = new WorldQuestRewards();

        public bool ObjectiveAllowed(string objective)
        {
            if (string.IsNullOrWhiteSpace(objective) || AllowedObjectives == null) return false;
            foreach (string o in AllowedObjectives)
                if (string.Equals(o, objective, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal void Clamp()
        {
            DefaultMinutes    = WorldConfig.Bound(DefaultMinutes, 1, 43200);
            AllowedObjectives = NormalizeObjectives(AllowedObjectives);
            Templates         = Templates ?? Array.Empty<string>();
            AutoGen ??= new WorldQuestAutoGen();
            Scaling ??= new WorldQuestScaling();
            Rewards ??= new WorldQuestRewards();
            AutoGen.Clamp();
            Scaling.Clamp();
            Rewards.Clamp();
        }

        private static string[] NormalizeObjectives(string[] raw)
        {
            var keep = new List<string>();
            if (raw != null)
                foreach (string s in raw)
                {
                    string o = (s ?? "").Trim().ToLowerInvariant();
                    if ((o == "hunt" || o == "build" || o == "deliver") && !keep.Contains(o)) keep.Add(o);
                }
            return keep.Count > 0 ? keep.ToArray() : new[] { "hunt", "build", "deliver" };
        }
    }

    internal sealed class WorldQuestAutoGen
    {
        public bool Enabled      { get; set; } = true;
        public int  MaxActive    { get; set; } = 2;
        public int  EveryMinutes { get; set; } = 720;

        // A random length between MinMinutes and MaxMinutes, weighted toward DefaultMinutes; all three equal fixes it.
        public int DefaultMinutes { get; set; } = 2880;
        public int MinMinutes     { get; set; } = 1440;
        public int MaxMinutes     { get; set; } = 4320;

        public int  FromCatalogPercent  { get; set; } = 60;
        public long CatalogGoalValue    { get; set; } = 1500;   // target total goods value; goal qty = this / item value
        public long CatalogItemMinValue { get; set; } = 1;      // per-unit band for eligible items (bulk goods, not cores)
        public long CatalogItemMaxValue { get; set; } = 60;

        public int GoalMin { get; set; } = 20;   // legacy fallback when player-scaling is off
        public int GoalMax { get; set; } = 60;

        public bool AllowSameTargetRepeat  { get; set; } = false;
        public int  RepeatCooldownMinutes  { get; set; } = 1440;

        internal void Clamp()
        {
            MaxActive      = WorldConfig.Bound(MaxActive, 1, 50);
            EveryMinutes   = WorldConfig.Bound(EveryMinutes, 1, 43200);
            MinMinutes     = WorldConfig.Bound(MinMinutes, 1, 525600);
            MaxMinutes     = WorldConfig.Bound(MaxMinutes, MinMinutes, 525600);
            DefaultMinutes = WorldConfig.Bound(DefaultMinutes, MinMinutes, MaxMinutes);
            RepeatCooldownMinutes = WorldConfig.Bound(RepeatCooldownMinutes, 0, 525600);
            FromCatalogPercent = WorldConfig.Bound(FromCatalogPercent, 0, 100);
            GoalMin = WorldConfig.Bound(GoalMin, 1, 100000);
            GoalMax = WorldConfig.Bound(GoalMax, GoalMin, 100000);
            if (CatalogItemMinValue < 0) CatalogItemMinValue = 0;
            if (CatalogItemMaxValue < CatalogItemMinValue) CatalogItemMaxValue = CatalogItemMinValue;
            if (CatalogGoalValue < 1) CatalogGoalValue = 1500;
        }
    }

    internal sealed class WorldQuestScaling
    {
        // Goal = clamp(Base + activePlayers * PerPlayer * difficulty, Min, Max), so a quiet server is asked for less.
        public bool   ByActivePlayers        { get; set; } = true;
        public int    BaseTargetCount        { get; set; } = 10;
        public int    TargetsPerActivePlayer { get; set; } = 6;
        public int    MinTargetCount         { get; set; } = 10;
        public int    MaxTargetCount         { get; set; } = 80;
        public double DifficultyMultiplier   { get; set; } = 1.0;

        // false = recently active within the window below, rather than only players connected right now.
        public bool OnlinePlayersOnly  { get; set; } = false;
        public int  ActivityWindowMinutes { get; set; } = 2880;
        public int  MinActivePlayers    { get; set; } = 1;

        internal void Clamp()
        {
            ActivityWindowMinutes  = WorldConfig.Bound(ActivityWindowMinutes, 60, 43200);
            BaseTargetCount        = WorldConfig.Bound(BaseTargetCount, 0, 100000);
            TargetsPerActivePlayer = WorldConfig.Bound(TargetsPerActivePlayer, 0, 100000);
            MinTargetCount         = WorldConfig.Bound(MinTargetCount, 1, 100000);
            MaxTargetCount         = WorldConfig.Bound(MaxTargetCount, MinTargetCount, 100000);
            MinActivePlayers       = WorldConfig.Bound(MinActivePlayers, 0, 1000);
            if (DifficultyMultiplier < 0.1) DifficultyMultiplier = 0.1;
            if (DifficultyMultiplier > 10) DifficultyMultiplier = 10;
        }
    }

    internal sealed class WorldQuestRewards
    {
        public int HousePoolPercent { get; set; } = 50;

        // A quest only appears when the house pool can actually pay it, so there are no zero-reward grinds.
        public int Min { get; set; } = 250;
        // Cap on an auto-gen reward so a rich server can't sink the whole pool into one quest.
        public int Max { get; set; } = 5000;

        // % of silver banked in KMH treasuries, deliberately not client-reported colony wealth; 0 ignores the economy.
        public double WealthPercent { get; set; } = 0.2;
        // Percent of goal x the item's reported BaseMarketValue; 100 pays roughly what the goods are worth, 0 ignores it.
        public int ValuePercent { get; set; } = 100;
        // On top of the economy-driven reward, so a bigger quest pays more.
        public int PerTarget { get; set; } = 50;

        // Mints only the shortfall, and only the pool-backed part is ever refunded, so minting cannot inflate the pool.
        public bool AllowMinted { get; set; } = true;

        internal void Clamp()
        {
            HousePoolPercent = WorldConfig.Bound(HousePoolPercent, 0, 100);
            Min = WorldConfig.Bound(Min, 0, 10_000_000);
            Max = WorldConfig.Bound(Max, Min, 100_000_000);
            if (WealthPercent < 0)   WealthPercent = 0;
            if (WealthPercent > 100) WealthPercent = 100;
            ValuePercent = WorldConfig.Bound(ValuePercent, 0, 1000);
            PerTarget    = WorldConfig.Bound(PerTarget, 0, 10_000_000);
        }
    }
}
