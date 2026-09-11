using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Diagnostics.Dto
{
    // Shape must stay identical to the patch mod's DTO.
    public class DebugLogPush
    {
        [JsonProperty("lines")] public List<string> Lines { get; set; } = new List<string>();

        // Blank from a pre-1.3.0 client, so nothing may require it.
        [JsonProperty("session_id")] public string SessionId { get; set; } = "";
        [JsonProperty("seq")]        public long   Sequence  { get; set; } = 0;
    }
}
