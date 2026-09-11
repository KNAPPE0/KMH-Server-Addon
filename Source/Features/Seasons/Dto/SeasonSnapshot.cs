using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Seasons.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class SeasonArchiveSnapshot
    {
        [JsonProperty("current_season")]            public int  CurrentSeason          { get; set; } = 1;
        [JsonProperty("season_started_utc_ticks")]  public long SeasonStartedUtcTicks  { get; set; } = 0;
        [JsonProperty("current")]        public List<SeasonRecordDto>  Current      { get; set; } = new List<SeasonRecordDto>();
        [JsonProperty("past")]           public List<SeasonArchiveDto> Past         { get; set; } = new List<SeasonArchiveDto>();
        [JsonProperty("server_records")] public List<SeasonRecordDto>  ServerRecords { get; set; } = new List<SeasonRecordDto>();
    }

    public class SeasonArchiveDto
    {
        [JsonProperty("season")]            public int  Season           { get; set; } = 0;
        [JsonProperty("started_utc_ticks")] public long StartedUtcTicks  { get; set; } = 0;
        [JsonProperty("ended_utc_ticks")]   public long EndedUtcTicks    { get; set; } = 0;
        [JsonProperty("records")]           public List<SeasonRecordDto> Records { get; set; } = new List<SeasonRecordDto>();
    }

    public class SeasonRecordDto
    {
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("holder")]   public string Holder   { get; set; } = "";
        [JsonProperty("detail")]   public string Detail   { get; set; } = "";
        [JsonProperty("value")]    public long   Value    { get; set; } = 0;
        [JsonProperty("season")]   public int    Season   { get; set; } = 0;   // in server records, which season set it
    }
}
