using System.Collections.Generic;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Leaderboards
{
    // Every board, from the same engine the website and Discord read, so the game never ranks on its own.
    internal sealed class LeaderboardsSnapshot
    {
        // A window shows the top of each board; the website holds the rest.
        public const int RowsPerBoard = 50;

        [JsonProperty("season")]             public int    Season;
        [JsonProperty("season_started_utc")] public long   SeasonStartedUnix;
        [JsonProperty("generated_utc")]      public long   GeneratedUnix;
        [JsonProperty("boards")]             public List<LeaderboardBoardDto> Boards = new List<LeaderboardBoardDto>();
        // The website page Global last said shows these standings; blank while it shows none.
        [JsonProperty("page")]               public string Page = "";
    }

    internal static class LeaderboardHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.LeaderboardsRequest, OnRequest);
        }

        // Identical for every caller; the engine caches its store reads, so a window refreshing every 12 s costs nothing new.
        private static void OnRequest(ServerClient client, KmhEnvelope env) => KmhRouter.SendTo(client, KmhProtocol.Kind.LeaderboardsSnapshot, Build());

        internal static LeaderboardsSnapshot Build()
        {
            var snap = new LeaderboardsSnapshot { Season = Seasons.SeasonStore.CurrentSeason, Page = Global.KmhGlobalService.LeaderboardPageFor("") };
            foreach (LeaderboardBoardDto b in LeaderboardEngine.BuildAll())
            {
                snap.SeasonStartedUnix = b.SeasonStartedUnix;
                snap.GeneratedUnix = b.GeneratedUnix;
                var copy = new LeaderboardBoardDto
                {
                    Key = b.Key, Title = b.Title, Group = b.Group, Unit = b.Unit, Scope = b.Scope, Source = b.Source,
                    Description = b.Description, Season = b.Season, SeasonStartedUnix = b.SeasonStartedUnix,
                    GeneratedUnix = b.GeneratedUnix, Total = b.Total,
                    Rows = b.Rows.Count > LeaderboardsSnapshot.RowsPerBoard ? b.Rows.GetRange(0, LeaderboardsSnapshot.RowsPerBoard) : b.Rows,
                };
                snap.Boards.Add(copy);
            }
            return snap;
        }
    }
}
