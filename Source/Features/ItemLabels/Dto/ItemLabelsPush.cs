using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.ItemLabels.Dto
{
    // Wire DTO for the kmh.item_labels envelope payload. Mirror of the patch mod's
    // KMHPatch.Features.ItemLabels.Dto.ItemLabelsPush - same JSON property names, same field set. Drift here = the
    // server gets an empty labels dict because the property name didn't match
    public class ItemLabelsPush
    {
        [JsonProperty("labels")]
        public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
    }
}
