using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.LinkedAccounts.Dto
{
    // Mirror of patch mod's KMHPatch.Features.LinkedAccounts.Dto. Server is the source of truth; client cache
    // replaces wholesale on each push
    public class LinkedAccountsSnapshot
    {
        [JsonProperty("links")]
        public Dictionary<string, string> Links { get; set; } = new Dictionary<string, string>();
    }
}
