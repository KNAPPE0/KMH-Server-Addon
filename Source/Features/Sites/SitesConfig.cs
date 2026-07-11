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

        // Build cost also floored at amount * this (client can't fake amount). 0 = off.
        public double MinBuildCostPerUnit { get; set; } = 20.0;

        // Cycle minutes added per amount/cycle, so big sites are paced by throughput. 0 = value-only.
        public double CycleMinutesPerRewardUnit { get; set; } = 1.5;

        // Cap on items produced per cycle (anti-abuse on the chosen amount).
        public int CustomSiteMaxRewardAmount { get; set; } = 50;

        // Global multiplier on worker cycle XP. 1.0 = default.
        public double WorkerXpMultiplier { get; set; } = 1.0;

        // Poll cadence ONLY - how often the sweeper checks sites for a due payout. This is NOT the payout timer: each
        // site's own EffectiveCycleMinutes (base cycle / worker speed) decides when it actually produces. Lowering
        // this just checks more often; it does not make sites pay out faster.
        public int RewardIntervalSeconds { get; set; } = 60;

        // Restrict site outputs to simple generated resources (no quality/HP/comp state to lose). Off = legacy
        // behavior (any def). Keep ON until full item-payload support ships.
        public bool RestrictOutputsToSimpleResources { get; set; } = true;
        // Extra defNames treated as simple resources beyond the built-in list (plain stackables only).
        public string[] ExtraSimpleResourceDefs { get; set; } = System.Array.Empty<string>();

        // --- server-authoritative output tiers (server decides what a site can print, and how hard) ---
        // Classify each output into a tier; the tier gates allow/block, amount cap, cost/cycle multipliers, and how
        // much workers can scale speed vs output. Unknown/suspicious items default to Tier 4 (disabled) so a site can
        // never become a dev-mode printer for gear/tech/drugs. See SiteOutputRules.
        public bool UseSiteOutputTiers       { get; set; } = true;
        public int  DefaultUnknownOutputTier { get; set; } = 4;   // where auto-classify puts anything it doesn't recognize
        public int  MaxAllowedSiteOutputTier { get; set; } = 3;   // tiers above this are blocked server-wide
        // Owner is NOT a worker. Off by default so no fake account-worker is created; a site produces only once real
        // pawns are assigned. A private/solo host can opt back in.
        public bool AutoAddOwnerAsSiteWorker { get; set; } = false;
        // New sites need a real pawn (caravan -> assign), not an account. Account-only joins are rejected; existing
        // account workers load as legacy/disabled until a pawn replaces them.
        public bool RequirePawnSiteWorkers   { get; set; } = true;
        // Off by default: a complex/gear item can be produced ONLY if the owner exact-allowlists it (defName or label)
        // AND flips this on. Keyword allowlists never bypass complex safety.
        public bool AllowExplicitComplexSiteOutputs { get; set; } = false;
        public SiteOutputTier[] OutputTiers  { get; set; } = SiteOutputTier.Defaults();

        // How a cycle's output is shared. SplitTotal (default, safe): ONE pool split between owner+workers - production
        // does NOT multiply by worker count. OwnerOnly: owner gets it all, workers earn XP only. PerWorkerCopy: legacy
        // high-economy mode where every recipient gets a full copy (an output multiplier - off by default).
        public string SiteRewardDistributionMode { get; set; } = "SplitTotal";
        // Unsafe legacy escape hatch: only when UseSiteOutputTiers=false AND this is true will Sites fall back to the
        // old all-def behavior. Loud startup + audit warnings. Normal players never see a generic all-def picker.
        public bool AllowUnsafeLegacySiteOutputs { get; set; } = false;

        // Anti-spam / economy caps (0 = unlimited).
        public int MaxSitesPerPlayer      { get; set; } = 3;
        public int MaxSitesPerGuild       { get; set; } = 10;
        public int MaxSitesServerWide     { get; set; } = 200;
        public int MaxWorkerSitesPerPlayer { get; set; } = 5;
        public int BuildCooldownMinutes   { get; set; } = 10;   // between a player's site builds

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
            if (MinBuildCostPerUnit < 0) MinBuildCostPerUnit = 0;
            if (MinBuildCostPerUnit > 100000) MinBuildCostPerUnit = 100000;
            if (CycleMinutesPerRewardUnit < 0) CycleMinutesPerRewardUnit = 0;
            if (CycleMinutesPerRewardUnit > 240) CycleMinutesPerRewardUnit = 240;
            if (CustomSiteMaxRewardAmount < 1)   CustomSiteMaxRewardAmount = 1;
            if (CustomSiteMaxRewardAmount > 10000) CustomSiteMaxRewardAmount = 10000;
            if (WorkerXpMultiplier < 0) WorkerXpMultiplier = 0;
            if (WorkerXpMultiplier > 100) WorkerXpMultiplier = 100;
            if (RewardIntervalSeconds < 10) RewardIntervalSeconds = 10;
            if (RewardIntervalSeconds > 3600) RewardIntervalSeconds = 3600;
            ExtraSimpleResourceDefs = ExtraSimpleResourceDefs ?? System.Array.Empty<string>();
            if (MaxSitesPerPlayer < 0) MaxSitesPerPlayer = 0;
            if (MaxSitesPerGuild < 0) MaxSitesPerGuild = 0;
            if (MaxSitesServerWide < 0) MaxSitesServerWide = 0;
            if (MaxWorkerSitesPerPlayer < 0) MaxWorkerSitesPerPlayer = 0;
            if (BuildCooldownMinutes < 0) BuildCooldownMinutes = 0;
            if (BuildCooldownMinutes > 1440) BuildCooldownMinutes = 1440;

            DefaultUnknownOutputTier = ClampInt(DefaultUnknownOutputTier, 1, 4);
            MaxAllowedSiteOutputTier = ClampInt(MaxAllowedSiteOutputTier, 1, 4);
            string m = (SiteRewardDistributionMode ?? "").Trim();
            SiteRewardDistributionMode =
                  string.Equals(m, "OwnerOnly", System.StringComparison.OrdinalIgnoreCase)     ? "OwnerOnly"
                : string.Equals(m, "PerWorkerCopy", System.StringComparison.OrdinalIgnoreCase) ? "PerWorkerCopy"
                : "SplitTotal";
            if (OutputTiers == null || OutputTiers.Length < 4) OutputTiers = SiteOutputTier.Defaults();
            foreach (SiteOutputTier t in OutputTiers)
            {
                if (t == null) continue;
                if (t.MaxAmount < 1) t.MaxAmount = 1;
                if (t.CostMultiplier < 0.01) t.CostMultiplier = 0.01;
                if (t.CycleMultiplier < 0.01) t.CycleMultiplier = 0.01;
                if (t.MaxSpeedMultiplier < 1.0) t.MaxSpeedMultiplier = 1.0;
                if (t.MaxOutputMultiplier < 1.0) t.MaxOutputMultiplier = 1.0;
                t.AllowedDefNames ??= System.Array.Empty<string>();
                t.BlockedDefNames ??= System.Array.Empty<string>();
                t.AllowedKeywords ??= System.Array.Empty<string>();
                t.BlockedKeywords ??= System.Array.Empty<string>();
            }
        }

        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
