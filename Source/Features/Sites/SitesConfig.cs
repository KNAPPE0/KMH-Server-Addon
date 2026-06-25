using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites
{
    // Tunables for KMH custom sites, loaded from KMH-Data/Config/Sites.json and generated with defaults on first
    // boot. Clamped on load so a hand-edited file can't break the economy. Reload via /kmh server reload-economy
    internal sealed class SitesConfig
    {
        // Schema version for forward-compatible migrations (absent = 1). Changes so far are additive; this is the
        // anchor a future field rename would key on.
        public int SchemaVersion { get; set; } = 1;

        // Master switch - when false, build requests are rejected.
        public bool AllowCustomSites { get; set; } = true;

        // Build cost = max(500, marketValue * amount * this). Higher = pricier.
        public double CustomSitePriceMultiplier { get; set; } = 3.0;

        // Cap on items produced per cycle (anti-abuse on the chosen amount).
        public int CustomSiteMaxRewardAmount { get; set; } = 50;

        // Global multiplier on worker cycle XP. 1.0 = default.
        public double WorkerXpMultiplier { get; set; } = 1.0;

        // How often the reward sweeper runs. The per-site cycle time gates actual payouts, so this is just the
        // polling cadence
        public int RewardIntervalSeconds { get; set; } = 60;

        private static SitesConfig _current;
        public static SitesConfig Current => _current ?? (_current = LoadOrDefault());

        public static SitesConfig LoadOrDefault()
        {
            SitesConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.SitesConfigFile, out SitesConfig loaded) && loaded != null
                ? loaded : new SitesConfig();
            cfg.Clamp();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.SitesConfigFile))
                JsonFileStore.Save(KmhDataPaths.SitesConfigFile, new SitesConfig());
        }

        public static void Reload() => _current = LoadOrDefault();

        private void Clamp()
        {
            if (CustomSitePriceMultiplier < 0.1) CustomSitePriceMultiplier = 0.1;
            if (CustomSitePriceMultiplier > 100) CustomSitePriceMultiplier = 100;
            if (CustomSiteMaxRewardAmount < 1)   CustomSiteMaxRewardAmount = 1;
            if (CustomSiteMaxRewardAmount > 10000) CustomSiteMaxRewardAmount = 10000;
            if (WorkerXpMultiplier < 0) WorkerXpMultiplier = 0;
            if (WorkerXpMultiplier > 100) WorkerXpMultiplier = 100;
            if (RewardIntervalSeconds < 10) RewardIntervalSeconds = 10;
            if (RewardIntervalSeconds > 3600) RewardIntervalSeconds = 3600;
        }
    }
}
