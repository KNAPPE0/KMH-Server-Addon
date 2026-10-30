using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.PlayerStats.Dto;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Leaderboards
{
    // JSON names are spelled out: this is the wire shape for the client and Global.
    internal sealed class LeaderboardRowDto
    {
        [JsonProperty("rank")]    public int    Rank;
        [JsonProperty("name")]    public string Name = "";
        [JsonProperty("guild")]   public string Guild = "";
        [JsonProperty("colony")]  public string Colony = "";
        [JsonProperty("value")]   public long   Value;
        [JsonProperty("display")] public string Display = "";
        [JsonProperty("online")]  public bool   Online;
    }

    internal sealed class LeaderboardBoardDto
    {
        [JsonProperty("key")]                public string Key = "";
        [JsonProperty("title")]              public string Title = "";
        [JsonProperty("group")]              public string Group = "";
        [JsonProperty("unit")]               public string Unit = "";
        [JsonProperty("scope")]              public string Scope = "";
        [JsonProperty("source")]             public string Source = "";
        [JsonProperty("description")]        public string Description = "";
        [JsonProperty("season")]             public int    Season;
        [JsonProperty("season_started_utc")] public long   SeasonStartedUnix;
        [JsonProperty("generated_utc")]      public long   GeneratedUnix;
        [JsonProperty("total")]              public int    Total;
        [JsonProperty("rows")]               public List<LeaderboardRowDto> Rows = new List<LeaderboardRowDto>();
    }

    // The one ranking every surface reads, built from the live stores so it cannot drift from a player's stats.
    internal static class LeaderboardEngine
    {
        // A ranking nobody scrolls past; also bounds what a board costs to send.
        public const int MaxRows = 500;
        internal static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(10);

        internal sealed class Inputs
        {
            public List<PlayerLeaderboardEntry> Players = new List<PlayerLeaderboardEntry>();
            public List<Features.Reputation.Dto.ReputationEntryDto> ReputationRows = new List<Features.Reputation.Dto.ReputationEntryDto>();
            public List<Features.Guilds.GuildStore.GuildSummary> GuildRows = new List<Features.Guilds.GuildStore.GuildSummary>();
            public HashSet<string> Online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Func<string, bool> Hidden = _ => false;
            public bool HideSimulatedGuilds;
            public int Season = 1;
            public long SeasonStartedTicks;
            public DateTime Now = DateTime.UtcNow;
        }

        private static readonly object _cacheLock = new object();
        private static Inputs _cached;
        private static DateTime _cachedAt;

        // One read of every store per CacheAge, shared by all categories: a Discord post of six boards reads them once.
        internal static Inputs Live()
        {
            DateTime now = DateTime.UtcNow;
            lock (_cacheLock)
            {
                if (_cached != null && now - _cachedAt < CacheAge) return _cached;
            }
            // Stores are read outside the lock (lock order); two callers may both read at the CacheAge edge, the newer is kept.
            var built = new Inputs
            {
                Players    = PlayerStats.PlayerStatsStore.BuildSnapshot().Entries ?? new List<PlayerLeaderboardEntry>(),
                ReputationRows = Features.Reputation.ReputationStore.BuildSnapshot().Entries ?? new List<Features.Reputation.Dto.ReputationEntryDto>(),
                GuildRows  = Features.Guilds.GuildStore.ComputeLeaderboard() ?? new List<Features.Guilds.GuildStore.GuildSummary>(),
                Online     = PlayerStats.PlayerStatsStore.OnlineNow(),
                Hidden     = HiddenNow(),
                HideSimulatedGuilds = Simulation.SimulatedAccounts.ExcludesFromRankings,
                Season     = Seasons.SeasonStore.CurrentSeason,
                SeasonStartedTicks = Seasons.SeasonStore.SeasonStartedTicks,
                Now        = now,
            };
            lock (_cacheLock)
            {
                if (_cached == null || now >= _cachedAt) { _cached = built; _cachedAt = now; }
                return _cached;
            }
        }

        internal static void ForgetCacheForTest() { lock (_cacheLock) _cached = null; }

        // One rule for the boards and the records clients are sent: a banned or frozen player is off all of them.
        internal static Func<string, bool> BannedNow()
        {
            long unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return name => !string.IsNullOrEmpty(name)
                        && (Moderation.BanStore.Find(name, "", "", unixNow) != null || Moderation.KmhEconomyGuard.IsFrozen(name));
        }

        // Boards, Colonist Records and the season archive also hide simulated accounts while unranked; player records do not.
        internal static Func<string, bool> HiddenNow()
        {
            Func<string, bool> banned = BannedNow();
            return name => banned(name) || Simulation.SimulatedAccounts.ExcludedFromRankings(name);
        }

        // The game's record tables follow the boards. The stores keep a banned player's rows; only what is sent drops them.
        internal static Features.PlayerStats.Dto.PlayerStatsSnapshot ForClients(Features.PlayerStats.Dto.PlayerStatsSnapshot s, Func<string, bool> hidden)
        { s?.Entries?.RemoveAll(e => e == null || hidden(e.Username)); return s; }

        internal static Features.PlayerStats.Dto.ColonistRosterSnapshot ForClients(Features.PlayerStats.Dto.ColonistRosterSnapshot s, Func<string, bool> hidden)
        {
            s?.Colonists?.RemoveAll(c => c == null || hidden(c.Owner));
            s?.Fallen?.RemoveAll(f => f?.Colonist == null || hidden(f.Colonist.Owner));
            return s;
        }

        internal static Features.Reputation.Dto.ReputationSnapshot ForClients(Features.Reputation.Dto.ReputationSnapshot s, Func<string, bool> hidden)
        { s?.Entries?.RemoveAll(e => e == null || hidden(e.Username)); return s; }

        public static LeaderboardBoardDto Build(string key) => Build(LeaderboardCatalog.Find(key), Live());

        internal static LeaderboardBoardDto Build(LeaderboardCategory c, Inputs inputs)
        {
            if (c == null || inputs == null) return null;
            var board = new LeaderboardBoardDto
            {
                Key = c.Key, Title = c.Title, Group = c.Group, Unit = c.Unit, Scope = c.Scope, Source = c.Source,
                Description = c.Description, Season = inputs.Season,
                SeasonStartedUnix = inputs.SeasonStartedTicks > 0 ? new DateTimeOffset(inputs.SeasonStartedTicks, TimeSpan.Zero).ToUnixTimeSeconds() : 0,
                GeneratedUnix = new DateTimeOffset(DateTime.SpecifyKind(inputs.Now, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            };

            var raw = new List<LeaderboardRowDto>();
            if (c.Scope == LeaderboardScope.Guild)
            {
                foreach (Features.Guilds.GuildStore.GuildSummary g in inputs.GuildRows)
                    if (!(inputs.HideSimulatedGuilds && g.AllSimulated)) raw.Add(new LeaderboardRowDto { Name = g.Name ?? "", Value = c.Key == LeaderboardCatalog.GuildMembers ? g.MemberCount : g.TreasurySilver });
            }
            else
            {
                Dictionary<string, PlayerLeaderboardEntry> byName = new Dictionary<string, PlayerLeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (PlayerLeaderboardEntry e in inputs.Players) if (!string.IsNullOrEmpty(e?.Username)) byName[e.Username] = e;

                if (c.Key == LeaderboardCatalog.Reputation)
                    foreach (Features.Reputation.Dto.ReputationEntryDto r in inputs.ReputationRows)
                        raw.Add(PlayerRow(r?.Username, r?.Score ?? 0, byName, inputs));
                else
                    foreach (PlayerLeaderboardEntry e in byName.Values)
                        raw.Add(PlayerRow(e.Username, c.PlayerValue(e), byName, inputs));
                raw.RemoveAll(row => inputs.Hidden(row.Name));
            }

            List<LeaderboardRowDto> ranked = Rank(raw);
            board.Total = ranked.Count;
            if (ranked.Count > MaxRows) ranked.RemoveRange(MaxRows, ranked.Count - MaxRows);
            foreach (LeaderboardRowDto row in ranked) row.Display = c.Format(row.Value);
            board.Rows = ranked;
            return board;
        }

        public static List<LeaderboardBoardDto> BuildAll()
        {
            Inputs inputs = Live();
            var boards = new List<LeaderboardBoardDto>();
            foreach (LeaderboardCategory c in LeaderboardCatalog.All) boards.Add(Build(c, inputs));
            return boards;
        }

        private static LeaderboardRowDto PlayerRow(string name, long value, Dictionary<string, PlayerLeaderboardEntry> byName, Inputs inputs)
        {
            byName.TryGetValue(name ?? "", out PlayerLeaderboardEntry e);
            return new LeaderboardRowDto
            {
                Name = name ?? "", Value = value, Guild = e?.GuildName ?? "", Colony = e?.ColonyName ?? "",
                Online = !string.IsNullOrEmpty(name) && inputs.Online.Contains(name),
            };
        }

        // Ties break by name so order never depends on which store answered first; equal values share a rank (1, 2, 2, 4).
        internal static List<LeaderboardRowDto> Rank(IEnumerable<LeaderboardRowDto> rows)
        {
            List<LeaderboardRowDto> list = rows.Where(r => r != null && r.Value > 0 && !string.IsNullOrWhiteSpace(r.Name))
                                               .OrderByDescending(r => r.Value)
                                               .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                                               .ThenBy(r => r.Name, StringComparer.Ordinal)
                                               .ToList();
            for (int i = 0; i < list.Count; i++)
                list[i].Rank = i > 0 && list[i].Value == list[i - 1].Value ? list[i - 1].Rank : i + 1;
            return list;
        }
    }
}
