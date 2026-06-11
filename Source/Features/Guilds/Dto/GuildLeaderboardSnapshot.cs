using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Guilds.Dto
{
    // Wire DTO for the cross-guild leaderboard. Mirror of the patch mod's
    // KMHPatch.Features.Guilds.Dto.GuildLeaderboardSnapshot - same JSON property names, same fields. Drift = the
    // patch dialog renders broken data
    //
    // Metrics our stores already track (member count + treasury silver). Adding fields is backwards-compatible -
    // older clients deserialize unknown JSON properties to default
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
    }
}
