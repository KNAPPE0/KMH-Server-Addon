using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.ItemLabels.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class ItemLabelsPush
    {
        [JsonProperty("labels")]
        public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

        // Additive: an older client omits it.
        [JsonProperty("values")]
        public Dictionary<string, long> Values { get; set; } = new Dictionary<string, long>();

        // Additive: an older client omits it.
        [JsonProperty("fungible")]
        public List<string> Fungible { get; set; } = new List<string>();

        // 0 means a single unchunked push from an older client.
        [JsonProperty("chunk_index")] public int ChunkIndex { get; set; } = 0;
        [JsonProperty("chunk_total")] public int ChunkTotal { get; set; } = 0;
    }
}
