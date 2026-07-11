using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Items
{
    // State-preserving item payload shared by every KMH item flow. Security boundary: ScribeXml is an opaque
    // client-produced blob the server never parses - it stores the payload verbatim and returns it on withdraw for the
    // client to rebuild the exact item, using only the metadata itself for identity/display/ledger/review.
    // Fidelity: full = exact (ScribeXml); metadata = big-4 state only (hp/taint/quality/stuff), partial; legacy = def+count, flagged.
    public class KmhThingPayload
    {
        public const int CurrentSchema = 1;

        public const string FidelityFull     = "full";
        public const string FidelityMetadata = "metadata";
        public const string FidelityLegacy   = "legacy";

        [JsonProperty("schema_version")] public int    SchemaVersion { get; set; } = CurrentSchema;
        [JsonProperty("def_name")]       public string DefName       { get; set; } = "";
        [JsonProperty("stuff_def_name")] public string StuffDefName  { get; set; } = "";
        [JsonProperty("stack_count")]    public int    StackCount    { get; set; } = 1;

        [JsonProperty("hit_points")]     public int    HitPoints     { get; set; } = -1;   // -1 = unknown
        [JsonProperty("max_hit_points")] public int    MaxHitPoints  { get; set; } = -1;
        [JsonProperty("quality")]        public int    Quality       { get; set; } = 0;    // 0 = none, 1..7
        [JsonProperty("tainted")]        public bool   Tainted       { get; set; } = false;

        // Opaque deep-serialized Thing (client-produced). Present only at "full" fidelity. Stripped from wire snapshots.
        [JsonProperty("scribe_xml")]     public string ScribeXml     { get; set; } = "";
        [JsonProperty("fidelity")]       public string Fidelity      { get; set; } = FidelityLegacy;

        [JsonProperty("display_label")]  public string DisplayLabel  { get; set; } = "";   // UI only
        [JsonProperty("market_value")]   public long   MarketValue   { get; set; } = 0;    // display/audit only
        [JsonProperty("fingerprint")]    public string Fingerprint   { get; set; } = "";   // storage identity + merge check
        [JsonProperty("legacy")]         public bool   Legacy        { get; set; } = false;
        [JsonProperty("warnings")]       public List<string> Warnings { get; set; } = new List<string>();
    }
}
