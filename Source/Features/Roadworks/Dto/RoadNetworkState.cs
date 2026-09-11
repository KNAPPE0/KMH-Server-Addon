using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Roadworks.Dto
{
    // Deliberately independent of RWT's road feature - RWT roads are external baseline, never read from here.
    public class RoadNetworkState
    {
        [JsonProperty("schema")]    public int SchemaVersion { get; set; } = 1;
        [JsonProperty("revision")]  public long Revision     { get; set; } = 0;   // bumps on any change; clients sync deltas
        [JsonProperty("segments")]  public List<RoadSegment> Segments { get; set; } = new List<RoadSegment>();
        [JsonProperty("projects")]  public List<RoadProject> Projects { get; set; } = new List<RoadProject>();
        [JsonProperty("next_project_id")] public long NextProjectId { get; set; } = 1;
    }

    // PlanetTile carries a layer in 1.6, so a bare tile id would conflate surface and non-surface tiles.
    public class RoadTile
    {
        [JsonProperty("layer")] public int LayerId { get; set; } = 0;
        [JsonProperty("tile")]  public int TileId  { get; set; } = -1;

        public bool IsValid => TileId >= 0;
        public string Key => LayerId + ":" + TileId;
    }

    public class RoadSegment
    {
        [JsonProperty("a")]         public RoadTile A { get; set; } = new RoadTile();
        [JsonProperty("b")]         public RoadTile B { get; set; } = new RoadTile();
        [JsonProperty("tier")]      public string Tier { get; set; } = RoadTiers.Trail;
        [JsonProperty("owner")]     public string OwnerUsername { get; set; } = "";
        [JsonProperty("site_tile")] public int    SourceSiteTile { get; set; } = -1;
        [JsonProperty("project")]   public long   ProjectId { get; set; } = 0;
        [JsonProperty("built_utc")] public long   BuiltUtcTicks { get; set; } = 0;
    }

    public class RoadProject
    {
        public const string StateBuilding  = "building";
        public const string StateComplete  = "complete";
        public const string StateCancelled = "cancelled";

        [JsonProperty("id")]        public long   Id { get; set; } = 0;
        [JsonProperty("owner")]     public string OwnerUsername { get; set; } = "";
        [JsonProperty("site_tile")] public int    SiteTile { get; set; } = -1;
        [JsonProperty("tier")]      public string Tier { get; set; } = RoadTiers.Trail;

        // Ordered route. Segment i connects Route[i] -> Route[i+1].
        [JsonProperty("route")]     public List<RoadTile> Route { get; set; } = new List<RoadTile>();
        [JsonProperty("index")]     public int    CurrentSegment { get; set; } = 0;
        [JsonProperty("progress")]  public double CurrentProgress { get; set; } = 0;   // 0..1

        public RoadProject ShallowClone() => (RoadProject)MemberwiseClone();
        [JsonProperty("state")]     public string State { get; set; } = StateBuilding;

        [JsonProperty("created_utc")] public long CreatedUtcTicks { get; set; } = 0;
        [JsonProperty("updated_utc")] public long UpdatedUtcTicks { get; set; } = 0;

        // Reserved for the whole project at start; segments never built are refunded.
        [JsonProperty("escrow_silver")]       public int EscrowSilver { get; set; } = 0;
        [JsonProperty("escrow_silver_spent")] public int EscrowSilverSpent { get; set; } = 0;
    }
}
