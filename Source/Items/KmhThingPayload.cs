using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Items
{
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

        // Opaque client-produced blob: stored verbatim, never parsed, and stripped from wire snapshots.
        [JsonProperty("scribe_xml")]     public string ScribeXml     { get; set; } = "";
        [JsonProperty("fidelity")]       public string Fidelity      { get; set; } = FidelityLegacy;

        [JsonProperty("display_label")]  public string DisplayLabel  { get; set; } = "";
        [JsonProperty("market_value")]   public long   MarketValue   { get; set; } = 0;    // display/audit only
        [JsonProperty("fingerprint")]    public string Fingerprint   { get; set; } = "";
        [JsonProperty("legacy")]         public bool   Legacy        { get; set; } = false;
        [JsonProperty("warnings")]       public List<string> Warnings { get; set; } = new List<string>();

        // The client's claim that this is a fungible resource; old payloads default to false.
        [JsonProperty("mergeable")]          public bool Mergeable        { get; set; } = false;

        // Whether units can be taken off THIS stack - a weaker claim than Mergeable, which combines two separately captured stacks.
        [JsonProperty("splittable")]         public bool Splittable       { get; set; } = false;
        [JsonProperty("rot_progress_ticks")] public long RotProgressTicks { get; set; } = -1;   // -1 = unknown
    }
}
