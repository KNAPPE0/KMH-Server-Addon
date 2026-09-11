using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites.Dto
{
    // The client renders this and never re-implements the tier rules, or the two drift apart.
    public class SiteCatalogSnapshot
    {
        [JsonProperty("entries")]           public List<SiteCatalogEntry> Entries { get; set; } = new List<SiteCatalogEntry>();
        [JsonProperty("tiers_enabled")]     public bool TiersEnabled       { get; set; } = true;
        [JsonProperty("max_allowed_tier")]  public int  MaxAllowedTier     { get; set; } = 3;
        [JsonProperty("tier4_enabled")]     public bool Tier4Enabled       { get; set; } = false;
        [JsonProperty("includes_blocked")]  public bool IncludesBlocked    { get; set; } = false;
        // A fresh server's catalog is still sparse, and the client says "still loading" rather than "nothing here".
        [JsonProperty("catalog_sparse")]    public bool CatalogSparse      { get; set; } = false;
        // In display order, so adding an archetype stays a server-side change.
        [JsonProperty("archetypes")]        public List<SiteArchetypeInfo> Archetypes { get; set; } = new List<SiteArchetypeInfo>();
        // False means nobody has pushed metadata yet, so the picker must not pretend items are ineligible.
        [JsonProperty("archetypes_ready")]  public bool ArchetypesReady    { get; set; } = false;
        // The catalog the server ADOPTED, so a client can tell "my push was applied" from "my push was sent"; empty when it holds none.
        [JsonProperty("catalog_fingerprint")] public string CatalogFingerprint { get; set; } = "";
    }

    public class SiteCatalogEntry
    {
        [JsonProperty("def_name")]      public string DefName        { get; set; } = "";
        [JsonProperty("label")]         public string Label          { get; set; } = "";
        [JsonProperty("tier")]          public int    Tier           { get; set; } = 1;
        [JsonProperty("tier_name")]     public string TierName       { get; set; } = "";
        [JsonProperty("skill")]         public string RelevantSkill  { get; set; } = "";
        [JsonProperty("max_amount")]    public int    MaxAmount      { get; set; } = 1;
        [JsonProperty("est_cost")]      public int    EstBuildCost   { get; set; } = 0;   // for a max-amount build
        [JsonProperty("est_cycle_min")] public int    EstCycleMinutes{ get; set; } = 0;   // base cycle at max amount
        [JsonProperty("allowed")]       public bool   Allowed        { get; set; } = true;
        [JsonProperty("block_reason")]  public string BlockReason    { get; set; } = "";
        // plant / forestry / mineral / animal / crafted / construction / unknown
        [JsonProperty("family")]        public string Family         { get; set; } = "unknown";
        // Comma-separated archetype ids, computed server-side because Roadworks eligibility is a predicate, not a family.
        [JsonProperty("arch")]          public string AllowedArchetypes { get; set; } = "";
    }

    public class SiteArchetypeInfo
    {
        [JsonProperty("id")]           public string Id          { get; set; } = "";
        [JsonProperty("name")]         public string DisplayName { get; set; } = "";
        [JsonProperty("desc")]         public string Description { get; set; } = "";
        [JsonProperty("skill")]        public string WorkerSkill { get; set; } = "";
        [JsonProperty("cost_mult")]    public double CostMultiplier { get; set; } = 1.0;
        [JsonProperty("perk")]         public string PerkText    { get; set; } = "";
        [JsonProperty("custom")]       public bool   IsCustom    { get; set; } = false;
        [JsonProperty("roadworks")]    public bool   UnlocksRoadworks { get; set; } = false;
    }

}
