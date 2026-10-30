using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Names and bytes must match Global's LeaderboardPublishRequest, or the two sides sign different things.
    internal sealed class LeaderboardPublishRow
    {
        [JsonProperty("rank")]    public int    Rank { get; set; }
        [JsonProperty("name")]    public string Name { get; set; } = "";
        [JsonProperty("guild")]   public string Guild { get; set; } = "";
        [JsonProperty("colony")]  public string Colony { get; set; } = "";
        [JsonProperty("value")]   public long   Value { get; set; }
        [JsonProperty("display")] public string Display { get; set; } = "";
        [JsonProperty("online")]  public bool   Online { get; set; }
    }

    internal sealed class LeaderboardPublishBoard
    {
        [JsonProperty("key")]         public string Key { get; set; } = "";
        [JsonProperty("title")]       public string Title { get; set; } = "";
        [JsonProperty("group")]       public string Group { get; set; } = "";
        [JsonProperty("unit")]        public string Unit { get; set; } = "";
        [JsonProperty("scope")]       public string Scope { get; set; } = "";
        [JsonProperty("source")]      public string Source { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("total")]       public int    Total { get; set; }
        [JsonProperty("rows")]        public List<LeaderboardPublishRow> Rows { get; set; } = new List<LeaderboardPublishRow>();
    }

    internal sealed class LeaderboardPublishBody
    {
        // Global refuses a publish over these limits.
        public const int MaxRows = 100, MaxBoards = 40;

        [JsonProperty("server_id")]          public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]          public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]              public string Nonce { get; set; } = "";
        [JsonProperty("generation")]         public int    Generation { get; set; }
        [JsonProperty("season")]             public int    Season { get; set; }
        [JsonProperty("season_started_utc")] public long   SeasonStartedUnix { get; set; }
        [JsonProperty("generated_utc")]      public long   GeneratedUnix { get; set; }
        [JsonProperty("boards")]             public List<LeaderboardPublishBoard> Boards { get; set; } = new List<LeaderboardPublishBoard>();
        [JsonProperty("signature")]          public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.Leaderboards)
            .Add(ServerId)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Generation)
            .Add(Season)
            .Add(SeasonStartedUnix)
            .Add(GeneratedUnix)
            .AddBoards(Boards)
            .Build();
    }
}
