using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.PlayerStats
{
    // Roster of every player who's ever been seen. Persisted to KMH-Data/Players/PlayerStats.json via JsonFileStore
    // - survives server restarts. Saved after every mutation (cheap; the roster is small)
    //
    // Stats fields stay at zero until other features hook in: Treasury deposit -> bump SilverDonated Marketplace
    // sale -> bump SalesEarned + MarketplaceSales Quest complete -> bump QuestsCompleted etc. Each of those hooks
    // lands when its source feature lands
    //
    // Thread-safety: RWT's chat receive thread + login flow are both background threads, so all reads/writes go
    // through the lock
    internal static class PlayerStatsStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, PlayerLeaderboardEntry> _entries
            = new Dictionary<string, PlayerLeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

        // Add the player if not already present. Idempotent - repeated calls on every login are safe and cheap.
        // first_seen_utc_ticks is set on first add and never overwritten so tenure stays correct across reconnects
        public static void EnsurePlayer(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            bool added = false;
            lock (_lock)
            {
                if (!_entries.ContainsKey(username))
                {
                    _entries[username] = new PlayerLeaderboardEntry
                    {
                        Username           = username,
                        FirstSeenUtcTicks  = DateTime.UtcNow.Ticks,
                    };
                    added = true;
                }
            }
            if (added) SaveToDisk();
        }

        // Load at server bootstrap. Missing file = fresh roster (first run).
        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.PlayerStatsFile, out PersistedState state) && state?.Entries != null)
            {
                lock (_lock)
                {
                    _entries.Clear();
                    foreach (PlayerLeaderboardEntry e in state.Entries)
                    {
                        if (string.IsNullOrEmpty(e?.Username)) continue;
                        _entries[e.Username] = e;
                    }
                }
                Diagnostics.ServerLog.Info($"PlayerStats: loaded {state.Entries.Count} entries from disk");
            }
        }

        // Snapshot under lock, write outside lock (disk I/O can be slow;
        // shouldn't block concurrent mutations).
        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Entries = new List<PlayerLeaderboardEntry>(_entries.Values);
            }
            JsonFileStore.Save(KmhDataPaths.PlayerStatsFile, state);
        }

        // Tiny on-disk wrapper - keeps the JSON file as a single object rather than a top-level array (room to add
        // schema-version / last-saved-utc / etc. later without breaking compatibility)
        private class PersistedState
        {
            public List<PlayerLeaderboardEntry> Entries { get; set; } = new List<PlayerLeaderboardEntry>();
        }

        // Snapshot accessor. Entries are CLONES with guild + discord-link joined in live - the affiliation lives in
        // GuildStore/LinkedAccountsStore, never on the persisted roster, so it can't go stale on disk
        public static PlayerStatsSnapshot BuildSnapshot()
        {
            PlayerStatsSnapshot s = new PlayerStatsSnapshot();
            lock (_lock)
            {
                s.Entries = new List<PlayerLeaderboardEntry>(_entries.Count);
                foreach (PlayerLeaderboardEntry e in _entries.Values)
                {
                    s.Entries.Add(new PlayerLeaderboardEntry
                    {
                        Username          = e.Username,
                        FirstSeenUtcTicks = e.FirstSeenUtcTicks,
                        SilverDonated     = e.SilverDonated,
                        SalesEarned       = e.SalesEarned,
                        PurchasesSpent    = e.PurchasesSpent,
                        QuestsCompleted   = e.QuestsCompleted,
                        QuestsPosted      = e.QuestsPosted,
                        MarketplaceSales  = e.MarketplaceSales,
                        SitesBuilt        = e.SitesBuilt,
                        SitesRaided       = e.SitesRaided,
                        WorkerXp          = e.WorkerXp,
                        EconomyScore      = e.EconomyScore,
                    });
                }
            }
            // join outside the lock - GuildStore/LinkedAccountsStore have their own locks, never nest
            foreach (PlayerLeaderboardEntry e in s.Entries)
            {
                e.GuildName         = Guilds.GuildStore.CurrentGuildOf(e.Username) ?? "";
                e.IsLinkedToDiscord = LinkedAccounts.LinkedAccountsStore.IsLinked(e.Username);
            }
            return s;
        }

        // Mutation entry points for other features (no-op now; consumers arrive when Treasury/Marketplace/Quest
        // handlers port)
        public static void AddSilverDonated(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.SilverDonated += delta;
                    RecomputeEconomyScoreLocked(e);
                }
            }
            SaveToDisk();
        }

        public static void AddSalesEarned(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.SalesEarned += delta;
                    e.MarketplaceSales += 1;
                    RecomputeEconomyScoreLocked(e);
                }
            }
            SaveToDisk();
        }

        public static void BumpQuestsCompleted(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.QuestsCompleted += 1;
                    RecomputeEconomyScoreLocked(e);
                }
            }
            SaveToDisk();
        }

        public static void BumpQuestsPosted(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.QuestsPosted += 1;
                }
            }
            SaveToDisk();
        }

        // Rough weighted sum so the leaderboard's "Score" sort means something.
        private static void RecomputeEconomyScoreLocked(PlayerLeaderboardEntry e)
        {
            e.EconomyScore =
                e.SilverDonated
                + e.SalesEarned        / 2
                + e.QuestsCompleted    * 100L
                + e.SitesBuilt         * 50L
                + e.WorkerXp           / 10L;
        }
    }
}
