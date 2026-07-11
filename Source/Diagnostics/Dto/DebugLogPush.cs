using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Diagnostics.Dto
{
    // Wire DTO for kmh.debug.log - a batch of preformatted client KMH log lines. Mirror of the patch DTO.
    public class DebugLogPush
    {
        [JsonProperty("lines")] public List<string> Lines { get; set; } = new List<string>();
    }
}
