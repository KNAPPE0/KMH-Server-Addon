using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites.Dto
{
    // Server-authoritative site output catalog. The server classifies its known item catalog (ItemLabelCache) through
    // SiteOutputRules and sends only what a site may actually produce (plus, in debug mode, blocked entries with a
    // reason). The client picker renders this - it never re-implements the tier rules, so it can't drift or become a
    // dev-mode all-def palette. Mirror of the patch-side DTO.
    public class SiteCatalogSnapshot
    {
        [JsonProperty("entries")]           public List<SiteCatalogEntry> Entries { get; set; } = new List<SiteCatalogEntry>();
        [JsonProperty("tiers_enabled")]     public bool TiersEnabled       { get; set; } = true;
        [JsonProperty("max_allowed_tier")]  public int  MaxAllowedTier     { get; set; } = 3;
        [JsonProperty("tier4_enabled")]     public bool Tier4Enabled       { get; set; } = false;
        [JsonProperty("includes_blocked")]  public bool IncludesBlocked    { get; set; } = false;
        // True when the server's item catalog is still sparse (fresh server) - client shows a "still loading" hint.
        [JsonProperty("catalog_sparse")]    public bool CatalogSparse      { get; set; } = false;
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
    }
}
