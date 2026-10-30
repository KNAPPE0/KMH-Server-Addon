using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.World
{
    // Server-run events and world quests only; the player-posted quest board is a separate system in Quests.json.
    internal sealed class WorldConfig
    {
        public int SchemaVersion { get; set; } = 3;

        public WorldEventSettings Events { get; set; } = new WorldEventSettings();

        // Named for 'kmh worldquest' / 'kmh wq' so an owner can act on what they read.
        public WorldQuestSettings WorldQuests { get; set; } = new WorldQuestSettings();

        // Injected exactly once on a new server; everything after it is the closed tax loop. 0 = no seed.
        public int HousePoolSeed { get; set; } = 5000;

        private static WorldConfig _current;
        public static WorldConfig Current => _current ?? (_current = LoadOrDefault());

        public static WorldConfig LoadOrDefault()
        {
            WorldConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.WorldConfigFile, out WorldConfig loaded) && loaded != null
                ? loaded : new WorldConfig();
            cfg.Clamp();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.WorldConfigFile))
                JsonFileStore.Save(KmhDataPaths.WorldConfigFile, new WorldConfig());
        }

        public static void Reload() => _current = LoadOrDefault();

        // Seam for the regroup harness: a hand-edited file can null a nested block, and Clamp has to survive it.
        internal static WorldConfig ClampForTest(WorldConfig cfg) { cfg.Clamp(); return cfg; }

        // Owner policy decides whether a self-reported objective pays, so proving the rule needs it flipped in memory.
        internal static WorldConfig ApplyForTest(WorldConfig cfg) { WorldConfig prev = _current; _current = cfg; return prev; }

        private void Clamp()
        {
            // A hand-edited file can null a nested object outright, which would throw on the first read.
            Events      ??= new WorldEventSettings();
            WorldQuests ??= new WorldQuestSettings();
            Events.Clamp();
            WorldQuests.Clamp();
            HousePoolSeed = Bound(HousePoolSeed, 0, 100_000_000);
        }

        internal static int Bound(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public bool WeatherDefAllowed(string defName) => Events.Weather.Allowed(defName);
        public bool ObjectiveAllowed(string objective) => WorldQuests.ObjectiveAllowed(objective);
        public bool EventTypeAllowed(string type) => Events.TypeAllowed(type);
    }
}
