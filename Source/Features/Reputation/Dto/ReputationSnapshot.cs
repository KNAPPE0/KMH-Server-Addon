using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Reputation.Dto
{
    // Wire shape for player reputation. Mirror of the patch-side DTO - the client uses it to show tier badges next
    // to usernames on the quest board
    public class ReputationSnapshot
    {
        [JsonProperty("entries")] public List<ReputationEntryDto> Entries { get; set; } = new List<ReputationEntryDto>();
    }

    public class ReputationEntryDto
    {
        [JsonProperty("username")] public string Username { get; set; } = "";
        [JsonProperty("score")]    public int    Score    { get; set; } = 0;
        [JsonProperty("tier")]     public string Tier     { get; set; } = "Neutral";
    }
}
