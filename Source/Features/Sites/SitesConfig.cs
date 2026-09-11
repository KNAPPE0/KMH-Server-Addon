using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites
{
    internal sealed class SitesConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Gates custom OUTPUT; AllowCustomArchetype below gates the Custom ARCHETYPE, which is a different thing.
        public bool AllowCustomSites { get; set; } = true;

        // Off withdraws existing segments from the snapshot too, or disabling it would leave roads orphaned on the shared map.
        public bool AllowRoadworks { get; set; } = true;

        // Demolishing refunds nothing, so a building is a sink and never stored value.
        public int SiteBuildingCostSilver { get; set; } = 750;

        // Nothing in KMH lowers stability on its own, so this only matters alongside an admin command or an extension.
        public int SiteRepairCostPerPoint { get; set; } = 25;

        // Lives here rather than in Frontier.json because the claim UI reads it from the site snapshot.
        public int OutpostClaimWindowMinutes { get; set; } = 60;

        // A GLOBAL scalar over every archetype despite the name; Custom's own 1.35 premium is applied separately.
        public double CustomSitePriceMultiplier { get; set; } = 3.0;

        public double MinBuildCostPerUnit { get; set; } = 20.0;

        public double CycleMinutesPerRewardUnit { get; set; } = 1.5;

        public int CustomSiteMaxRewardAmount { get; set; } = 50;

        public double WorkerXpMultiplier { get; set; } = 1.0;

        // Widens what counts as a simple resource; the output tiers below still decide what may actually be produced.
        public string[] ExtraSimpleResourceDefs { get; set; } = System.Array.Empty<string>();

        public bool UseSiteOutputTiers       { get; set; } = true;

        // Tier 4 is disabled, so anything unrecognised is refused rather than guessed into a producible tier.
        public int  DefaultUnknownOutputTier { get; set; } = 4;
        public int  MaxAllowedSiteOutputTier { get; set; } = 3;

        // Off so no fake account-worker is created; a site produces only once real pawns are assigned.
        public bool AutoAddOwnerAsSiteWorker { get; set; } = false;

        // Existing account workers load as legacy/disabled rather than being dropped when this is on.
        public bool RequirePawnSiteWorkers   { get; set; } = true;

        // A complex item also needs an exact owner allowlist entry, so keyword allowlists can never bypass this.
        public bool AllowExplicitComplexSiteOutputs { get; set; } = false;
        public SiteOutputTier[] OutputTiers  { get; set; } = SiteOutputTier.Defaults();

        // SplitTotal shares one pool; PerWorkerCopy gives each recipient a full copy and so multiplies output.
        public string SiteRewardDistributionMode { get; set; } = "SplitTotal";

        // Only reachable with UseSiteOutputTiers off, and restores the old unrestricted all-def behaviour.
        public bool AllowUnsafeLegacySiteOutputs { get; set; } = false;

        // 0 = unlimited.
        public int MaxSitesPerPlayer      { get; set; } = 3;
        public int MaxSitesPerGuild       { get; set; } = 10;
        public int MaxSitesServerWide     { get; set; } = 200;
        public int MaxWorkerSitesPerPlayer { get; set; } = 5;
        public int BuildCooldownMinutes   { get; set; } = 10;

        // Off makes every site a Custom site at the Custom cost, which is how sites behaved before archetypes existed.
        public bool ArchetypesEnabled { get; set; } = true;

        // Gates the Custom ARCHETYPE; AllowCustomSites above gates custom OUTPUT.
        public bool AllowCustomArchetype { get; set; } = true;

        // "defName=family" corrections for content the classifier misreads; an unknown family is ignored, not applied.
        public string[] OutputFamilyOverrides { get; set; } = System.Array.Empty<string>();

        // Left empty deliberately, because guessing a family is what made everything fall back to Crafting.
        public string UnknownOutputFamily { get; set; } = "";

        // Resolved once per config load, or a few hundred entries would be re-parsed for every item in the catalog.
        private System.Collections.Generic.Dictionary<string, string> _overrideMap;

        public string FamilyOverrideFor(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName)) return "";
            if (_overrideMap == null) BuildOverrideMap();
            return _overrideMap.TryGetValue(defName.Trim(), out string f) ? f : "";
        }

        private void BuildOverrideMap()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string raw in OutputFamilyOverrides ?? System.Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0 || eq >= raw.Length - 1) continue;
                string def = raw.Substring(0, eq).Trim();
                string fam = raw.Substring(eq + 1).Trim().ToLowerInvariant();
                if (def.Length == 0 || !SiteOutputFamilies.IsKnown(fam)) continue;
                map[def] = fam;
            }
            _overrideMap = map;
        }

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

        // Internal rather than private so the self-test can prove a hand-edited file really is safe afterwards.
        internal void Clamp()
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
            ExtraSimpleResourceDefs = ExtraSimpleResourceDefs ?? System.Array.Empty<string>();
            OutputFamilyOverrides   = OutputFamilyOverrides   ?? System.Array.Empty<string>();
            _overrideMap = null;   // a reloaded config must not keep the previous file's overrides
            string uf = (UnknownOutputFamily ?? "").Trim().ToLowerInvariant();
            UnknownOutputFamily = SiteOutputFamilies.IsKnown(uf) ? uf : "";
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
            SiteOutputTier[] tierDefaults = SiteOutputTier.Defaults();
            for (int i = 0; i < OutputTiers.Length; i++)
            {
                // A JSON null passes the length check above and would then throw the first time a tier is read.
                if (OutputTiers[i] == null)
                    OutputTiers[i] = i < tierDefaults.Length ? tierDefaults[i] : new SiteOutputTier();
                SiteOutputTier t = OutputTiers[i];
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
