using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Features.Seasons.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Seasons
{
    internal static class SeasonStore
    {
        private static readonly object _lock = new object();
        private static int  _currentSeason = 1;
        private static long _seasonStartedTicks;
        private static readonly List<SeasonArchiveDto> _past = new List<SeasonArchiveDto>();
        private static readonly List<SeasonRecordDto>  _serverRecords = new List<SeasonRecordDto>();

        private sealed class PersistedState
        {
            public int  CurrentSeason      { get; set; } = 1;
            public long SeasonStartedTicks { get; set; } = 0;
            public List<SeasonArchiveDto> Past          { get; set; } = new List<SeasonArchiveDto>();
            public List<SeasonRecordDto>  ServerRecords { get; set; } = new List<SeasonRecordDto>();
        }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.SeasonsFile, out PersistedState s) && s != null)
            {
                lock (_lock)
                {
                    _currentSeason = Math.Max(1, s.CurrentSeason);
                    _seasonStartedTicks = s.SeasonStartedTicks;
                    _past.Clear(); if (s.Past != null) _past.AddRange(s.Past);
                    _serverRecords.Clear(); if (s.ServerRecords != null) _serverRecords.AddRange(s.ServerRecords);
                }
                Diagnostics.ServerLog.Diag($"Seasons: loaded season {_currentSeason}, {_past.Count} archived");
            }
            // Stamped outside the load branch so a first boot with no file still gives the season an age.
            lock (_lock) if (_seasonStartedTicks == 0) _seasonStartedTicks = DateTime.UtcNow.Ticks;
            SaveToDisk();
        }

        // Reported, so a season reset does not drop its journal over a write that never landed.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock)
            {
                s.CurrentSeason = _currentSeason;
                s.SeasonStartedTicks = _seasonStartedTicks;
                s.Past = KmhDeepCopy.OfList(_past);
                s.ServerRecords = KmhDeepCopy.OfList(_serverRecords);
            }
            return JsonFileStore.Save(KmhDataPaths.SeasonsFile, s);
        }

        public static int CurrentSeason { get { lock (_lock) return _currentSeason; } }
        public static long SeasonStartedTicks { get { lock (_lock) return _seasonStartedTicks; } }

        // A roll archives and advances only - PlayerStats stays cumulative until a destructive reset clears it.
        public static (int season, int recordCount) RollSeason()
        {
            List<SeasonRecordDto> leaders = BuildCurrentLeaders();
            int rolled;
            lock (_lock)
            {
                long now = DateTime.UtcNow.Ticks;
                rolled = _currentSeason;
                _past.Insert(0, new SeasonArchiveDto
                {
                    Season = _currentSeason, StartedUtcTicks = _seasonStartedTicks, EndedUtcTicks = now,
                    Records = leaders,
                });
                foreach (SeasonRecordDto r in leaders) MergeServerRecordLocked(r, _currentSeason);
                _currentSeason++;
                _seasonStartedTicks = now;
                if (_past.Count > 50) _past.RemoveRange(50, _past.Count - 50);
            }
            SaveToDisk();
            return (rolled, leaders.Count);
        }

        private static void MergeServerRecordLocked(SeasonRecordDto r, int season)
        {
            SeasonRecordDto existing = _serverRecords.Find(x => x.Category == r.Category);
            if (existing == null)
                _serverRecords.Add(new SeasonRecordDto { Category = r.Category, Holder = r.Holder, Detail = r.Detail, Value = r.Value, Season = season });
            else if (r.Value > existing.Value)
            { existing.Holder = r.Holder; existing.Detail = r.Detail; existing.Value = r.Value; existing.Season = season; }
        }

        public static SeasonArchiveSnapshot BuildSnapshot()
        {
            SeasonArchiveSnapshot snap = new SeasonArchiveSnapshot { Current = BuildCurrentLeaders() };
            lock (_lock)
            {
                snap.CurrentSeason = _currentSeason;
                snap.SeasonStartedUtcTicks = _seasonStartedTicks;
                snap.Past = new List<SeasonArchiveDto>(_past);
                snap.ServerRecords = new List<SeasonRecordDto>(_serverRecords);
            }
            return snap;
        }

        public static List<SeasonRecordDto> BuildCurrentLeaders()
            => LeadersFrom(PlayerStats.PlayerStatsStore.BuildSnapshot().Entries, Reputation.ReputationStore.BuildSnapshot().Entries,
                           PlayerStats.PlayerStatsStore.BuildColonistRoster().Colonists, Guilds.GuildStore.ComputeLeaderboard(),
                           Treasury.TreasuryStore.TopVaults(50), Leaderboards.LeaderboardEngine.HiddenNow(),
                           Simulation.SimulatedAccounts.ExcludesFromRankings);

        // A ranking: it hides whom the boards hide - banned players, and simulated ones (and guilds) while unranked.
        internal static List<SeasonRecordDto> LeadersFrom(List<PlayerLeaderboardEntry> allPlayers, List<Reputation.Dto.ReputationEntryDto> allReps,
            List<ColonistEntry> allColonists, List<Guilds.GuildStore.GuildSummary> allGuilds,
            List<(string OwnerKey, long Silver, bool IsGuild)> vaults, Func<string, bool> hidden, bool hideSimulatedGuilds)
        {
            List<SeasonRecordDto> list = new List<SeasonRecordDto>();
            List<PlayerLeaderboardEntry> players = (allPlayers ?? new List<PlayerLeaderboardEntry>()).FindAll(e => e != null && !hidden(e.Username));

            Top(list, "Richest Colony",  players, e => e.Wealth,         ColonyOf,           v => Util.SilverFmt.Format(v));
            Top(list, "Deadliest Player",players, e => e.Kills,          e => e.Username,    v => $"{v:N0} kills");
            Top(list, "Best Trader",     players, e => e.SalesEarned,    e => e.Username,    v => Util.SilverFmt.Format(v));
            Top(list, "Most Contracts",  players, e => e.QuestsCompleted,e => e.Username,    v => $"{v} contracts");
            Top(list, "Oldest Colony",   players, e => e.ColonyAgeDays,  ColonyOf,           v => $"{v} days");
            Top(list, "Largest Sale",    players, e => e.LargestSale,    e => e.Username,    v => Util.SilverFmt.Format(v));
            Top(list, "Top Site Owner",  players, e => e.SiteSilverProduced, e => e.Username, v => Util.SilverFmt.Format(v));
            // A claim is history, so this counts captures rather than what the player still holds today.
            Top(list, "Frontier Claims", players, e => e.FrontierCaptures, e => e.Username, v => $"{v} claimed");

            Reputation.Dto.ReputationEntryDto bestRep = null;
            foreach (Reputation.Dto.ReputationEntryDto r in allReps ?? new List<Reputation.Dto.ReputationEntryDto>())
                if (r != null && !hidden(r.Username) && (bestRep == null || r.Score > bestRep.Score)) bestRep = r;
            if (bestRep != null && bestRep.Score > 0)
                list.Add(new SeasonRecordDto { Category = "Most Reliable", Holder = bestRep.Username, Detail = $"{bestRep.Score:N0} rep", Value = bestRep.Score });

            ColonistEntry bestCol = null;
            foreach (ColonistEntry c in allColonists ?? new List<ColonistEntry>())
                if (c != null && !hidden(c.Owner) && (bestCol == null || c.Kills > bestCol.Kills)) bestCol = c;
            if (bestCol != null && bestCol.Kills > 0)
                list.Add(new SeasonRecordDto { Category = "Deadliest Colonist", Holder = $"{bestCol.Name} ({bestCol.Owner})", Detail = $"{bestCol.Kills} kills", Value = bestCol.Kills });

            // GuildSummary is a struct, so the scan seeds from the first row rather than null.
            List<Guilds.GuildStore.GuildSummary> guilds = (allGuilds ?? new List<Guilds.GuildStore.GuildSummary>())
                .FindAll(g => !(hideSimulatedGuilds && g.AllSimulated));
            if (guilds.Count > 0)
            {
                Guilds.GuildStore.GuildSummary topGuild = guilds[0], richGuild = guilds[0];
                foreach (Guilds.GuildStore.GuildSummary g in guilds)
                {
                    if (g.MemberCount    > topGuild.MemberCount)    topGuild  = g;
                    if (g.TreasurySilver > richGuild.TreasurySilver) richGuild = g;
                }
                list.Add(new SeasonRecordDto { Category = "Top Guild",        Holder = topGuild.Name,  Detail = $"{topGuild.MemberCount} members", Value = topGuild.MemberCount });
                list.Add(new SeasonRecordDto { Category = "Highest Treasury", Holder = richGuild.Name, Detail = Util.SilverFmt.Format(richGuild.TreasurySilver), Value = richGuild.TreasurySilver });
            }

            // TopVaults is sorted descending, so the first non-guild vault is the richest player.
            foreach ((string OwnerKey, long Silver, bool IsGuild) v in vaults ?? new List<(string, long, bool)>())
            {
                if (v.IsGuild || v.Silver <= 0 || string.IsNullOrEmpty(v.OwnerKey)) continue;
                string user = v.OwnerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase)
                    ? v.OwnerKey.Substring("_personal:".Length) : v.OwnerKey;
                if (hidden(user)) continue;
                list.Add(new SeasonRecordDto { Category = "Top Banker", Holder = user, Detail = Util.SilverFmt.Format(v.Silver), Value = v.Silver });
                break;
            }

            return list;
        }

        private static string ColonyOf(PlayerLeaderboardEntry e)
            => string.IsNullOrEmpty(e.ColonyName) ? e.Username : e.ColonyName;

        private static void Top(List<SeasonRecordDto> list, string category, List<PlayerLeaderboardEntry> rows,
            Func<PlayerLeaderboardEntry, long> metric, Func<PlayerLeaderboardEntry, string> holder, Func<long, string> fmt)
        {
            PlayerLeaderboardEntry best = null; long bestV = long.MinValue;
            foreach (PlayerLeaderboardEntry e in rows) { long v = metric(e); if (v > bestV) { bestV = v; best = e; } }
            if (best != null && bestV > 0)
                list.Add(new SeasonRecordDto { Category = category, Holder = holder(best), Detail = fmt(bestV), Value = bestV });
        }
    }
}
