using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Guilds.Dto
{
    // Cross-guild leaderboard wire DTO; JSON property names must mirror the patch mod's copy exactly or the dialog
    // renders broken data. Adding fields is backward-compatible (old clients default unknown properties).
    public class GuildLeaderboardSnapshot
    {
        [JsonProperty("guilds")]
        public List<GuildLeaderboardEntry> Guilds { get; set; } = new List<GuildLeaderboardEntry>();
    }

    public class GuildLeaderboardEntry
    {
        [JsonProperty("name")]            public string Name           { get; set; } = "";
        [JsonProperty("member_count")]    public int    MemberCount    { get; set; } = 0;
        [JsonProperty("treasury_silver")] public long   TreasurySilver { get; set; } = 0;
        [JsonProperty("open_join")]       public bool   OpenJoin       { get; set; } = false;
    }
}
