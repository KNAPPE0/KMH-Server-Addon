using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Frontier.Dto
{
    // The nonce is what makes independent client answers comparable, since the server holds no geography of its own.
    public class PlacementRequest
    {
        [JsonProperty("token_id")]      public string TokenId  { get; set; } = "";
        [JsonProperty("nonce")]         public long   Nonce    { get; set; } = 0;
        [JsonProperty("template")]      public string Template { get; set; } = "";
        [JsonProperty("exclude_tiles")] public List<int> ExcludeTiles { get; set; } = new List<int>();
        [JsonProperty("expires_utc")]   public long   ExpiresUtcTicks { get; set; } = 0;
    }
}
