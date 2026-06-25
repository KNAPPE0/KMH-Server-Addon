using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Quests
{
    // Owner-tunable quest limits, loaded from KMH-Data/Config/Quests.json. Anti-spam / anti-forge caps the server
    // enforces on posting. Generated with the KMH defaults on first boot; clamped on load. Applied at startup
    // - restart after editing.
    internal sealed class QuestsConfig
    {
        // Schema version for forward-compatible migrations (absent = 1). Changes so far are additive.
        public int SchemaVersion       { get; set; } = 1;

        public int MaxOpenPerUser      { get; set; } = 10;
        public int MaxTitleLength      { get; set; } = 80;
        public int MaxDescriptionLength { get; set; } = 1024;
        public int MaxBountyItems      { get; set; } = 20;

        private static QuestsConfig _current;
        public static QuestsConfig Current => _current ?? (_current = LoadOrDefault());

        public static QuestsConfig LoadOrDefault()
        {
            QuestsConfig cfg =
                JsonFileStore.TryLoad(KmhDataPaths.QuestsConfigFile, out QuestsConfig loaded) && loaded != null
                    ? loaded : new QuestsConfig();
            cfg.MaxOpenPerUser      = Clamp(cfg.MaxOpenPerUser, 1, 1000);
            cfg.MaxTitleLength      = Clamp(cfg.MaxTitleLength, 8, 200);
            cfg.MaxDescriptionLength = Clamp(cfg.MaxDescriptionLength, 16, 8000);
            cfg.MaxBountyItems      = Clamp(cfg.MaxBountyItems, 1, 200);
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.QuestsConfigFile))
                JsonFileStore.Save(KmhDataPaths.QuestsConfigFile, new QuestsConfig());
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
