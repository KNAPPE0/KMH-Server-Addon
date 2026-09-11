using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Frontier
{
    internal sealed class FrontierConfig
    {
        // The activity caps below, not this switch, are what keep the Director from filling the planet.
        public bool Enabled { get; set; } = true;

        public int MaxOutposts          { get; set; } = 3;
        public int MaxActiveOperations  { get; set; } = 2;
        public int MinMinutesBetweenActions { get; set; } = 90;
        public int TargetCooldownMinutes    { get; set; } = 720;

        public int SpawnBudget          { get; set; } = 2;    // actions available per refill window
        public int BudgetRefillHours    { get; set; } = 24;

        public int ResolutionRetention  { get; set; } = 100;  // finished records kept for audit

        // On by default because a small server may only ever have one client online to answer.
        public bool AllowSingleProposer { get; set; } = true;
        public int  PlacementWindowMinutes { get; set; } = 5;

        public string ReclaimMaterialDefName { get; set; } = "Steel";
        public int    ReclaimMaterialQty     { get; set; } = 150;
        public int    ReclaimRewardSilver    { get; set; } = 500;
        public int    ReclaimDurationMinutes { get; set; } = 1440;
        // An operation the house pool cannot fund to this is never created, rather than created underfunded.
        public int    MinOperationReward     { get; set; } = 100;

        // One multiplier on both cost and reward, so a busier server is a bigger contest and not a better deal.
        public double ReclaimScalePerActivePlayer { get; set; } = 0.15;   // per player online beyond the first
        public double ReclaimScalePerKnownPlayer  { get; set; } = 0.05;   // per player who has ever joined, beyond the first
        public double ReclaimScaleMax             { get; set; } = 5.0;    // ceiling, so a large roster cannot run away

        private static FrontierConfig _current;
        public static FrontierConfig Current => _current ?? (_current = Load());

        public static FrontierConfig Load()
        {
            bool existed = JsonFileStore.TryLoad(KmhDataPaths.FrontierConfigFile, out FrontierConfig cfg) && cfg != null;
            if (!existed) cfg = new FrontierConfig();
            cfg.Clamp();
            _current = cfg;
            if (!existed) JsonFileStore.Save(KmhDataPaths.FrontierConfigFile, cfg, JsonFileStore.NextSequence());
            return cfg;
        }

        // Seeded at boot rather than on the Director's first tick, so the knobs exist before an owner looks for them.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.FrontierConfigFile))
                JsonFileStore.Save(KmhDataPaths.FrontierConfigFile, new FrontierConfig(), JsonFileStore.NextSequence());
        }

        public static void Reload() { _current = null; _ = Current; }

        private void Clamp()
        {
            MaxOutposts             = Clamp(MaxOutposts, 0, 50);
            MaxActiveOperations     = Clamp(MaxActiveOperations, 0, 20);
            MinMinutesBetweenActions = Clamp(MinMinutesBetweenActions, 1, 10080);
            TargetCooldownMinutes   = Clamp(TargetCooldownMinutes, 1, 43200);
            SpawnBudget             = Clamp(SpawnBudget, 0, 100);
            BudgetRefillHours       = Clamp(BudgetRefillHours, 1, 720);
            ResolutionRetention     = Clamp(ResolutionRetention, 10, 5000);
            PlacementWindowMinutes  = Clamp(PlacementWindowMinutes, 1, 120);
            ReclaimMaterialQty      = Clamp(ReclaimMaterialQty, 1, 100000);
            ReclaimRewardSilver     = Clamp(ReclaimRewardSilver, 0, 10000000);
            ReclaimDurationMinutes  = Clamp(ReclaimDurationMinutes, 10, 43200);
            MinOperationReward      = Clamp(MinOperationReward, 0, 10000000);
            ReclaimScalePerActivePlayer = Clamp(ReclaimScalePerActivePlayer, 0d, 10d);
            ReclaimScalePerKnownPlayer  = Clamp(ReclaimScalePerKnownPlayer,  0d, 10d);
            ReclaimScaleMax             = Clamp(ReclaimScaleMax,             1d, 100d);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static double Clamp(double v, double lo, double hi)
            => double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }
}
