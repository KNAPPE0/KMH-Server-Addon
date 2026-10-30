using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global.Federation
{
    // Global decides levels and objectives; these only cap how much of that shows up on this server.
    internal sealed class FederationConfig
    {
        public int    SchemaVersion             { get; set; } = 1;

        // Off keeps the federation and its renown but applies no local bonus here.
        public bool   Benefits                  { get; set; } = true;
        public double MaxSiteOutputBonusPercent { get; set; } = 3.0;
        public double MaxRoadworksBonusPercent  { get; set; } = 3.0;

        public bool   ObjectiveRewards          { get; set; } = true;
        public double RewardScale               { get; set; } = 1.0;

        // Off also means this server's guilds earn no renown.
        public bool   ReportContributions       { get; set; } = true;

        private static FederationConfig _current;
        public static FederationConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

        public static FederationConfig LoadOrDefault()
        {
            FederationConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.FederationConfigFile, out FederationConfig loaded) && loaded != null
                ? loaded : new FederationConfig();
            cfg.Clamp();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.FederationConfigFile))
                JsonFileStore.Save(KmhDataPaths.FederationConfigFile, new FederationConfig());
        }

        // Defaults are the ceiling: an owner can lower a bonus, never raise it past what ships.
        public void Clamp()
        {
            MaxSiteOutputBonusPercent = Clamp(MaxSiteOutputBonusPercent, 0, 3);
            MaxRoadworksBonusPercent  = Clamp(MaxRoadworksBonusPercent, 0, 3);
            RewardScale               = Clamp(RewardScale, 0, 2);
        }

        private static double Clamp(double v, double min, double max)
            => double.IsNaN(v) ? min : v < min ? min : v > max ? max : v;

        internal static FederationConfig ApplyForTest(FederationConfig cfg) { FederationConfig prev = _current; _current = cfg; return prev; }
    }
}
