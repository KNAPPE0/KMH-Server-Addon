using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Quests
{
    internal sealed class QuestsConfig
    {
        public int SchemaVersion       { get; set; } = 1;

        public int MaxOpenPerUser      { get; set; } = 10;
        public int MaxTitleLength      { get; set; } = 80;
        public int MaxDescriptionLength { get; set; } = 1024;
        public int MaxBountyItems      { get; set; } = 20;

        // The verify report is client-tracked, so a claim younger than this cannot complete and macros gain nothing.
        public int AutoVerifyMinClaimSeconds { get; set; } = 120;
        // Routes an auto-verified quest to the poster for sign-off instead of paying out immediately.
        public bool AutoVerifyRequiresPosterReview { get; set; } = false;

        private static QuestsConfig _current;
        public static QuestsConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

        // The open-quest cap is enforced across an escrow that releases the lock, so the race needs it set in memory.
        internal static QuestsConfig ApplyForTest(QuestsConfig cfg) { QuestsConfig prev = _current; _current = cfg; return prev; }

        public static QuestsConfig LoadOrDefault()
        {
            QuestsConfig cfg =
                JsonFileStore.TryLoad(KmhDataPaths.QuestsConfigFile, out QuestsConfig loaded) && loaded != null
                    ? loaded : new QuestsConfig();
            cfg.MaxOpenPerUser      = Clamp(cfg.MaxOpenPerUser, 1, 1000);
            cfg.MaxTitleLength      = Clamp(cfg.MaxTitleLength, 8, 200);
            cfg.MaxDescriptionLength = Clamp(cfg.MaxDescriptionLength, 16, 8000);
            cfg.MaxBountyItems      = Clamp(cfg.MaxBountyItems, 1, 200);
            cfg.AutoVerifyMinClaimSeconds = Clamp(cfg.AutoVerifyMinClaimSeconds, 0, 3600);
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
