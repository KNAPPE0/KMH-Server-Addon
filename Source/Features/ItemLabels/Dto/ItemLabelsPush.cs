using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.ItemLabels.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class ItemLabelsPush
    {
        [JsonProperty("labels")]
        public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

        // defName -> RimWorld BaseMarketValue. Mirror of the patch DTO. Additive: a pre-1.1.0 client omits it.
        [JsonProperty("values")]
        public Dictionary<string, long> Values { get; set; } = new Dictionary<string, long>();

        // Fungible defNames in this chunk (mirror of the patch DTO). Additive: an older client omits it.
        [JsonProperty("fungible")]
        public List<string> Fungible { get; set; } = new List<string>();

        // Chunk metadata for large modpacks (mirror of the patch DTO). 0 = single (old-client) push.
        [JsonProperty("chunk_index")] public int ChunkIndex { get; set; } = 0;
        [JsonProperty("chunk_total")] public int ChunkTotal { get; set; } = 0;
    }
}
