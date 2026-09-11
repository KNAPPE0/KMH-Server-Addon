using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Reputation.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
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
