using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Roadworks.Dto
{
    // Built per caller - segments go to everyone, projects and escrow do not.
    public class RoadworksSnapshot
    {
        [JsonProperty("revision")]      public long Revision { get; set; } = 0;
        [JsonProperty("segments")]      public List<RoadSegmentDto> Segments { get; set; } = new List<RoadSegmentDto>();
        [JsonProperty("projects")]      public List<RoadProjectDto> Projects { get; set; } = new List<RoadProjectDto>();

        // Folded into raid/threat wealth so a road project cannot shelter silver.
        [JsonProperty("escrow_silver")] public long EscrowSilver { get; set; } = 0;

        // Enough for the client to quote a route before sending it.
        [JsonProperty("silver_per_segment")]   public Dictionary<string, int> SilverPerSegment { get; set; } = new Dictionary<string, int>();
        [JsonProperty("max_route_segments")]   public int MaxRouteSegments { get; set; } = 64;
        [JsonProperty("allow_roadworks")]      public bool AllowRoadworks { get; set; } = true;

        // Work comes from the Roadworks site's own workers, once per production cycle.
        [JsonProperty("work_per_segment")]     public Dictionary<string, double> WorkPerSegment { get; set; } = new Dictionary<string, double>();
        [JsonProperty("work_per_worker")]      public double WorkPerWorker     { get; set; } = 1.0;
        [JsonProperty("work_per_skill_level")] public double WorkPerSkillLevel { get; set; } = 0.25;
    }

    public class RoadSegmentDto
    {
        [JsonProperty("layer_a")] public int    LayerA { get; set; } = 0;
        [JsonProperty("tile_a")]  public int    TileA  { get; set; } = -1;
        [JsonProperty("layer_b")] public int    LayerB { get; set; } = 0;
        [JsonProperty("tile_b")]  public int    TileB  { get; set; } = -1;
        [JsonProperty("tier")]    public string Tier   { get; set; } = "trail";
    }

    public class RoadProjectDto
    {
        public const string StateBuilding  = "building";
        public const string StateComplete  = "complete";
        public const string StateCancelled = "cancelled";

        [JsonProperty("id")]               public long   Id            { get; set; } = 0;
        [JsonProperty("site_tile")]        public int    SiteTile      { get; set; } = -1;
        [JsonProperty("tier")]             public string Tier          { get; set; } = "trail";
        [JsonProperty("state")]            public string State         { get; set; } = "building";
        [JsonProperty("segments_total")]   public int    SegmentsTotal { get; set; } = 0;
        [JsonProperty("segments_done")]    public int    SegmentsDone  { get; set; } = 0;
        [JsonProperty("current_progress")] public double CurrentProgress { get; set; } = 0;
        [JsonProperty("escrow_silver")]    public long   EscrowSilver  { get; set; } = 0;
        [JsonProperty("escrow_unspent")]   public long   EscrowUnspent { get; set; } = 0;
    }
}
