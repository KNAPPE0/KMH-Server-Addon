using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Sites
{
    internal sealed class SitesConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Gates custom OUTPUT; Archetypes.AllowCustom gates the Custom ARCHETYPE, which is a different thing.
        public bool AllowCustomSites { get; set; } = true;

        // Off also withdraws existing segments, or disabling it would leave roads orphaned on the shared map.
        public bool AllowRoadworks { get; set; } = true;

        // Lives here rather than in Frontier.json because the claim UI reads it from the site snapshot.
        public int OutpostClaimWindowMinutes { get; set; } = 60;

        public SiteLimits     Limits     { get; set; } = new SiteLimits();
        public SiteCosts      Costs      { get; set; } = new SiteCosts();
        public SiteOutput     Output     { get; set; } = new SiteOutput();
        public SiteWorkers    Workers    { get; set; } = new SiteWorkers();
        public SiteArchetypeSettings Archetypes { get; set; } = new SiteArchetypeSettings();

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

        public string FamilyOverrideFor(string defName) => Output.FamilyOverrideFor(defName);

        // Internal rather than private so the self-test can prove a hand-edited file really is safe afterwards.
        internal void Clamp()
        {
            Limits     ??= new SiteLimits();
            Costs      ??= new SiteCosts();
            Output     ??= new SiteOutput();
            Workers    ??= new SiteWorkers();
            Archetypes ??= new SiteArchetypeSettings();
            Limits.Clamp();
            Costs.Clamp();
            Output.Clamp();
            Workers.Clamp();
            if (OutpostClaimWindowMinutes < 0) OutpostClaimWindowMinutes = 0;
        }

        internal static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    internal sealed class SiteLimits
    {
        // 0 = unlimited.
        public int PerPlayer       { get; set; } = 3;
        public int PerGuild        { get; set; } = 10;
        public int ServerWide      { get; set; } = 200;
        public int WorkerSitesPerPlayer { get; set; } = 5;

        // Pawn ids are the client's word (no server roster), so this bounds one client's claims; 0 trusts every client.
        public int WorkersPerPlayerPerSite { get; set; } = 3;

        public int BuildCooldownMinutes { get; set; } = 10;

        internal void Clamp()
        {
            if (PerPlayer < 0) PerPlayer = 0;
            if (PerGuild < 0) PerGuild = 0;
            if (ServerWide < 0) ServerWide = 0;
            if (WorkerSitesPerPlayer < 0) WorkerSitesPerPlayer = 0;
            WorkersPerPlayerPerSite = SitesConfig.ClampInt(WorkersPerPlayerPerSite, 0, 50);
            BuildCooldownMinutes    = SitesConfig.ClampInt(BuildCooldownMinutes, 0, 1440);
        }
    }

    internal sealed class SiteCosts
    {
        // Demolishing refunds nothing, so a building is a sink and never stored value.
        public int BuildingSilver { get; set; } = 750;

        // Nothing in KMH lowers stability on its own, so this only matters alongside an admin command or an extension.
        public int RepairPerPoint { get; set; } = 25;

        // Scales every archetype; Custom's own 1.35 premium is applied separately.
        public double PriceMultiplier { get; set; } = 3.0;

        public double MinPerUnit { get; set; } = 20.0;

        internal void Clamp()
        {
            if (PriceMultiplier < 0.1) PriceMultiplier = 0.1;
            if (PriceMultiplier > 100) PriceMultiplier = 100;
            if (MinPerUnit < 0) MinPerUnit = 0;
            if (MinPerUnit > 100000) MinPerUnit = 100000;
            if (BuildingSilver < 0) BuildingSilver = 0;
            if (RepairPerPoint < 0) RepairPerPoint = 0;
        }
    }

    internal sealed class SiteOutput
    {
        public bool UseTiers { get; set; } = true;

        // Tier 4 is disabled, so anything unrecognised is refused rather than guessed into a producible tier.
        public int DefaultUnknownTier { get; set; } = 4;
        public int MaxAllowedTier     { get; set; } = 3;

        public SiteOutputTier[] Tiers { get; set; } = SiteOutputTier.Defaults();

        // A complex item also needs an exact owner allowlist entry, so keyword allowlists can never bypass this.
        public bool AllowExplicitComplex { get; set; } = false;

        // Only reachable with UseTiers off, and then allows every def unrestricted.
        public bool AllowUnsafeLegacy { get; set; } = false;

        public double CycleMinutesPerRewardUnit { get; set; } = 1.5;
        public int    CustomMaxRewardAmount     { get; set; } = 50;

        // SplitTotal shares one pool; PerWorkerCopy gives each recipient a full copy and so multiplies output.
        public string RewardDistributionMode { get; set; } = "SplitTotal";

        // Widens what counts as a simple resource; the tiers above still decide what may actually be produced.
        public string[] ExtraSimpleResourceDefs { get; set; } = System.Array.Empty<string>();

        // "defName=family" corrections for content the classifier misreads; an unknown family is ignored, not applied.
        public string[] FamilyOverrides { get; set; } = System.Array.Empty<string>();

        // Empty on purpose: guessing a family makes everything fall back to Crafting.
        public string UnknownFamily { get; set; } = "";

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
            foreach (string raw in FamilyOverrides ?? System.Array.Empty<string>())
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

        internal void Clamp()
        {
            if (CycleMinutesPerRewardUnit < 0) CycleMinutesPerRewardUnit = 0;
            if (CycleMinutesPerRewardUnit > 240) CycleMinutesPerRewardUnit = 240;
            CustomMaxRewardAmount = SitesConfig.ClampInt(CustomMaxRewardAmount, 1, 10000);
            ExtraSimpleResourceDefs = ExtraSimpleResourceDefs ?? System.Array.Empty<string>();
            FamilyOverrides         = FamilyOverrides         ?? System.Array.Empty<string>();
            _overrideMap = null;   // a reloaded config must not keep the previous file's overrides
            string uf = (UnknownFamily ?? "").Trim().ToLowerInvariant();
            UnknownFamily = SiteOutputFamilies.IsKnown(uf) ? uf : "";

            DefaultUnknownTier = SitesConfig.ClampInt(DefaultUnknownTier, 1, 4);
            MaxAllowedTier     = SitesConfig.ClampInt(MaxAllowedTier, 1, 4);
            string m = (RewardDistributionMode ?? "").Trim();
            RewardDistributionMode =
                  string.Equals(m, "OwnerOnly", System.StringComparison.OrdinalIgnoreCase)     ? "OwnerOnly"
                : string.Equals(m, "PerWorkerCopy", System.StringComparison.OrdinalIgnoreCase) ? "PerWorkerCopy"
                : "SplitTotal";
            if (Tiers == null || Tiers.Length < 4) Tiers = SiteOutputTier.Defaults();
            SiteOutputTier[] tierDefaults = SiteOutputTier.Defaults();
            for (int i = 0; i < Tiers.Length; i++)
            {
                // A JSON null passes the length check above and would then throw the first time a tier is read.
                if (Tiers[i] == null)
                    Tiers[i] = i < tierDefaults.Length ? tierDefaults[i] : new SiteOutputTier();
                SiteOutputTier t = Tiers[i];
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
    }

    internal sealed class SiteWorkers
    {
        // Off so no fake account-worker is created; a site produces only once real pawns are assigned.
        public bool AutoAddOwner { get; set; } = false;

        // Existing account workers load as legacy/disabled rather than being dropped when this is on.
        public bool RequirePawns { get; set; } = true;

        public double XpMultiplier { get; set; } = 1.0;

        internal void Clamp()
        {
            if (XpMultiplier < 0) XpMultiplier = 0;
            if (XpMultiplier > 100) XpMultiplier = 100;
        }
    }

    internal sealed class SiteArchetypeSettings
    {
        // Off makes every site a Custom site at the Custom cost.
        public bool Enabled { get; set; } = true;

        // Gates the Custom ARCHETYPE; SitesConfig.AllowCustomSites gates custom OUTPUT.
        public bool AllowCustom { get; set; } = true;
    }
}
