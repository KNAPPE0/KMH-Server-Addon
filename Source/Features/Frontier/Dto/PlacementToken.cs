using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Frontier.Dto
{
    // Single-use, so a client cannot replay a proposal against the same question.
    public class PlacementToken
    {
        [JsonProperty("id")]           public string Id       { get; set; } = "";
        [JsonProperty("nonce")]        public long   Nonce    { get; set; } = 0;
        [JsonProperty("template")]     public string Template { get; set; } = "";
        [JsonProperty("exclude_tiles")] public List<int> ExcludeTiles { get; set; } = new List<int>();
        [JsonProperty("issued_utc")]   public long   IssuedUtcTicks  { get; set; } = 0;
        [JsonProperty("expires_utc")]  public long   ExpiresUtcTicks { get; set; } = 0;
        [JsonProperty("consumed")]     public bool   Consumed { get; set; } = false;
        [JsonProperty("proposals")]    public List<PlacementProposal> Proposals { get; set; } = new List<PlacementProposal>();

        public PlacementToken ShallowClone() => (PlacementToken)MemberwiseClone();

        // A shallow copy suffices because a proposal is written once and never edited afterwards.
        public PlacementToken CloneForRead()
        {
            PlacementToken t = ShallowClone();
            t.ExcludeTiles = new List<int>(ExcludeTiles ?? new List<int>());
            t.Proposals    = new List<PlacementProposal>(Proposals ?? new List<PlacementProposal>());
            return t;
        }
    }

    public class PlacementProposal
    {
        [JsonProperty("username")]    public string Username { get; set; } = "";
        [JsonProperty("tile")]        public int    Tile     { get; set; } = -1;
        [JsonProperty("layer")]       public int    Layer    { get; set; } = 0;
        // Shows two clients describe the same planet, but is derived and never authoritative on its own.
        [JsonProperty("fingerprint")] public string WorldFingerprint { get; set; } = "";
        [JsonProperty("utc")]         public long   ReceivedUtcTicks { get; set; } = 0;
    }
}
