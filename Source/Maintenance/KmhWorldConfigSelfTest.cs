using System;
using System.Collections.Generic;
using KMHServerAddon.Features.World;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhWorldConfigSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // A hand-edited file can null a block, and Clamp runs on every load, so it must rebuild rather than throw.
            Check("World config: a nulled block is rebuilt, not thrown on", () =>
            {
                WorldConfig cfg = WorldConfig.ClampForTest(new WorldConfig
                {
                    Events = null, WorldQuests = null,
                });
                bool ok = cfg.Events != null && cfg.WorldQuests != null
                       && cfg.Events.Weights != null && cfg.Events.Weather != null
                       && cfg.WorldQuests.AutoGen != null && cfg.WorldQuests.Scaling != null
                       && cfg.WorldQuests.Rewards != null;
                return (ok, ok ? "every block is present after a clamp" : "a nested block stayed null");
            });

            Check("World config: nested blocks nulled one level down are rebuilt too", () =>
            {
                WorldConfig cfg = WorldConfig.ClampForTest(new WorldConfig
                {
                    Events      = new WorldEventSettings { Weights = null, Weather = null },
                    WorldQuests = new WorldQuestSettings { AutoGen = null, Scaling = null, Rewards = null },
                });
                bool ok = cfg.Events.Weights != null && cfg.Events.Weather != null
                       && cfg.WorldQuests.AutoGen != null && cfg.WorldQuests.Rewards != null;
                return (ok, ok ? "the second level is rebuilt as well" : "a second-level block stayed null");
            });

            // Every duration in World.json is minutes, so no block may carry an hours value beside DefaultMinutes.
            Check("World config: auto-gen durations are minutes and stay ordered", () =>
            {
                WorldConfig cfg = WorldConfig.ClampForTest(new WorldConfig
                {
                    WorldQuests = new WorldQuestSettings
                    {
                        AutoGen = new WorldQuestAutoGen { MinMinutes = 5000, MaxMinutes = 100, DefaultMinutes = 1 },
                    },
                });
                WorldQuestAutoGen g = cfg.WorldQuests.AutoGen;
                bool ok = g.MinMinutes <= g.DefaultMinutes && g.DefaultMinutes <= g.MaxMinutes;
                return (ok, ok ? $"{g.MinMinutes} <= {g.DefaultMinutes} <= {g.MaxMinutes}"
                              : $"out of order: {g.MinMinutes}/{g.DefaultMinutes}/{g.MaxMinutes}");
            });

            Check("World config: a reward ceiling below its floor is raised, not left inverted", () =>
            {
                WorldConfig cfg = WorldConfig.ClampForTest(new WorldConfig
                {
                    WorldQuests = new WorldQuestSettings
                    {
                        Rewards = new WorldQuestRewards { Min = 9000, Max = 10 },
                    },
                });
                bool ok = cfg.WorldQuests.Rewards.Max >= cfg.WorldQuests.Rewards.Min;
                return (ok, ok ? $"min {cfg.WorldQuests.Rewards.Min}, max {cfg.WorldQuests.Rewards.Max}"
                              : "a quest could never be affordable");
            });

            // World.json's world quests (engine-generated) and Quests.json (player board) are different systems.
            Check("World config: the world-quest block is not the player quest board", () =>
            {
                var world = new WorldQuestSettings();
                var board = new Features.Quests.QuestsConfig();
                bool ok = world.GetType() != board.GetType()
                       && board.GetType().GetProperty("MaxOpenPerUser") != null
                       && world.GetType().GetProperty("MaxOpenPerUser") == null;
                return (ok, ok ? "separate types, separate files" : "the two quest systems have merged");
            });

            Check("World config: a garbage objective list falls back to all three", () =>
            {
                WorldConfig cfg = WorldConfig.ClampForTest(new WorldConfig
                {
                    WorldQuests = new WorldQuestSettings { AllowedObjectives = new[] { "nonsense", "" } },
                });
                string[] o = cfg.WorldQuests.AllowedObjectives;
                bool ok = o.Length == 3 && cfg.ObjectiveAllowed("hunt") && !cfg.ObjectiveAllowed("nonsense");
                return (ok, ok ? "hunt/build/deliver" : string.Join(",", o));
            });

            Check("World config: an exclusion still catches a modded variant of the same condition", () =>
            {
                var cfg = new WorldConfig();
                bool ok = !cfg.WeatherDefAllowed("ToxicFalloutSmall") && cfg.WeatherDefAllowed("Aurora");
                return (ok, ok ? "substring matched, Aurora untouched" : "the exclusion list stopped matching");
            });

            // Min/MaxMinutes must bound a drawn length, not merely clamp DefaultMinutes into one fixed length.
            Check("World config: an auto quest's length is drawn from MinMinutes..MaxMinutes, peaking at DefaultMinutes", () =>
            {
                var g = new WorldQuestAutoGen { MinMinutes = 1000, DefaultMinutes = 1500, MaxMinutes = 3000 };
                int lo = WorldEngine.AutoGenMinutes(g, 0.0), hi = WorldEngine.AutoGenMinutes(g, 1.0);
                int peak = WorldEngine.AutoGenMinutes(g, 0.25);
                bool inRange = true;
                for (int i = 0; i <= 100; i++) { int m = WorldEngine.AutoGenMinutes(g, i / 100.0); inRange &= m >= 1000 && m <= 3000; }
                int fixedLen = WorldEngine.AutoGenMinutes(new WorldQuestAutoGen { MinMinutes = 720, DefaultMinutes = 720, MaxMinutes = 720 }, 0.9);
                bool ok = lo == 1000 && hi == 3000 && peak == 1500 && inRange && fixedLen == 720;
                return (ok, $"low={lo} high={hi} at-mode={peak} fixed={fixedLen} inRange={inRange}");
            });

            // Multiplying the count by the animal factor would ask 1.5x as many thrumbos and 0.75x as many rats.
            Check("World config: a hunt asks for fewer dangerous animals than common ones, and the owner's multiplier still raises both", () =>
            {
                var cfg = new WorldConfig();
                int thrumbo = WorldEngine.ScaledTargetCount(cfg, "Thrumbo", 5), plain = WorldEngine.ScaledTargetCount(cfg, "Boar", 5);
                int rat = WorldEngine.ScaledTargetCount(cfg, "Rat", 5);
                cfg.WorldQuests.Scaling.DifficultyMultiplier = 2.0;
                int harder = WorldEngine.ScaledTargetCount(cfg, "Thrumbo", 5);
                bool ok = thrumbo < plain && plain < rat && harder > thrumbo;
                return (ok, $"5 players: thrumbo {thrumbo}, boar {plain}, rat {rat}; thrumbo at multiplier 2: {harder}");
            });

            return r;
        }
    }
}
