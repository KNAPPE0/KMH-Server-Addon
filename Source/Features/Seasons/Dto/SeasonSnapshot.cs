using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Seasons.Dto
{
    // Season archive wire shapes. Byte-identical to the patch-side DTO. A "season roll" snapshots the current
    // leaders into a SeasonArchiveDto and folds the bests into the all-time server records. Lifetime stats are
    // never wiped - the archive just preserves each season's highlights.
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

    // One record holder for a category, e.g. ("Richest Colony", "Taz", "$15.4M", 15400000).
    public class SeasonRecordDto
    {
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("holder")]   public string Holder   { get; set; } = "";
        [JsonProperty("detail")]   public string Detail   { get; set; } = "";
        [JsonProperty("value")]    public long   Value    { get; set; } = 0;
        [JsonProperty("season")]   public int    Season   { get; set; } = 0;   // which season set the record (server records)
    }
}
