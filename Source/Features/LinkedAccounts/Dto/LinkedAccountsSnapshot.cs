using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.LinkedAccounts.Dto
{
    // Mirrors the patch mod's DTO of the same name - a field changed here has to change there too.
    public class LinkedAccountsSnapshot
    {
        [JsonProperty("links")]
        public Dictionary<string, string> Links { get; set; } = new Dictionary<string, string>();
    }
}
