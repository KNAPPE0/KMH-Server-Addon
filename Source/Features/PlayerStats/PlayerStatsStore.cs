using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.PlayerStats
{
    // Every player ever seen. All access is under _lock - RWT's chat and login run on background threads.
    internal static class PlayerStatsStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, PlayerLeaderboardEntry> _entries
            = new Dictionary<string, PlayerLeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

        // Full top-colonist profiles (Bio/Health/Combat), kept OUT of the snapshot for size - served on demand.
        private static readonly Dictionary<string, ColonistProfile> _colonists
            = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase);

        // In-memory only: rebuilds from client reports after a restart.
        private static readonly Dictionary<string, List<ColonistEntry>> _rosters
            = new Dictionary<string, List<ColonistEntry>>(StringComparer.OrdinalIgnoreCase);

        // Separate from BuildSnapshot, which clones every row and joins the sites three times.
        public static int PlayerCount { get { lock (_lock) return _entries.Count; } }

        // The World Engine scales quest rewards to this, so a thriving server pays bigger pots than a quiet one.
        public static long TotalReportedWealth()
        {
            long total = 0;
            lock (_lock)
                foreach (PlayerLeaderboardEntry e in _entries.Values)
                    if (e != null && e.Wealth > 0) total += e.Wealth;
            return total;
        }

        public static bool HasPlayer(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            lock (_lock) return _entries.ContainsKey(username);
        }

        // Most recently here first: whoever a player wants to @ was on last night, not named with an A.
        public static List<string> UsernamesByLastSeen()
        {
            var rows = new List<KeyValuePair<string, long>>();
            lock (_lock)
                foreach (PlayerLeaderboardEntry e in _entries.Values)
                    rows.Add(new KeyValuePair<string, long>(e.Username, e.LastSeenUtcTicks));

            rows.Sort((a, b) =>
            {
                int byTime = b.Value.CompareTo(a.Value);
                return byTime != 0 ? byTime : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            var names = new List<string>(rows.Count);
            foreach (KeyValuePair<string, long> row in rows) names.Add(row.Key);
            return names;
        }

        public static long LastSeenTicks(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            lock (_lock) return _entries.TryGetValue(username, out PlayerLeaderboardEntry e) ? e.LastSeenUtcTicks : 0;
        }

        public static long ActiveSecondsOf(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            lock (_lock) return _entries.TryGetValue(username, out PlayerLeaderboardEntry e) ? e.ActiveSeconds : 0;
        }

        // Never more than the wall clock allowed since this player's last report, so playtime cannot outrun real time.
        public static long AddActiveSeconds(string username, int claimed, out long credited)
        {
            credited = 0;
            if (string.IsNullOrWhiteSpace(username) || claimed <= 0) { TouchSeen(username); return 0; }

            lock (_lock)
            {
                if (!_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) return 0;
                long now = DateTime.UtcNow.Ticks;
                credited = Believable(claimed, e.LastSeenUtcTicks, now);
                e.ActiveSeconds += credited;
                e.LastSeenUtcTicks = now;
                return e.ActiveSeconds;
            }
        }

        // First report of a session has no elapsed window to measure against, so it is allowed one report's worth.
        internal static long Believable(int claimed, long lastSeenTicks, long nowTicks)
        {
            if (claimed <= 0) return 0;
            long elapsed = lastSeenTicks <= 0 || nowTicks <= lastSeenTicks
                ? MaxReportSeconds
                : (long)TimeSpan.FromTicks(nowTicks - lastSeenTicks).TotalSeconds + 1;
            long allowed = Math.Min(elapsed, MaxReportSeconds);
            return Math.Min(claimed, allowed);
        }

        // One report cannot carry more than this, whatever it claims or how long the client was away.
        internal const long MaxReportSeconds = 300;

        public static void TouchSeen(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (_lock)
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) e.LastSeenUtcTicks = DateTime.UtcNow.Ticks;
        }

        // Open connections, so time on this server is measured here rather than taken from what a client reports.
        private static readonly Dictionary<string, long> _sessions
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // A clock jump, not a session. Nobody holds one connection for a week.
        internal const long MaxSessionRollSeconds = 7 * 24 * 60 * 60;

        public static void BeginSession(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (_lock) _sessions[username] = DateTime.UtcNow.Ticks;
        }

        // Credit the connection so far and re-anchor. Rolled on every heartbeat so a crash loses a minute, not a night.
        public static long RollSession(string username, bool ending)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(username, out long anchor)) return 0;
                long now = DateTime.UtcNow.Ticks;
                long credited = Rollable(anchor, now);
                if (credited > 0 && _entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                    e.ConnectedSeconds += credited;
                if (ending) _sessions.Remove(username); else _sessions[username] = now;
                return credited;
            }
        }

        internal static long Rollable(long anchorTicks, long nowTicks)
        {
            if (anchorTicks <= 0 || nowTicks <= anchorTicks) return 0;
            long seconds = (long)TimeSpan.FromTicks(nowTicks - anchorTicks).TotalSeconds;
            return seconds > MaxSessionRollSeconds ? MaxSessionRollSeconds : seconds;
        }

        public static long ConnectedSecondsOf(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            lock (_lock) return _entries.TryGetValue(username, out PlayerLeaderboardEntry e) ? e.ConnectedSeconds : 0;
        }

        // Idempotent: first_seen is set once and survives reconnects. Only a full season reset clears the row.
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

        public static void ClearForNewSeason()
        {
            lock (_lock)
            {
                _entries.Clear(); _colonists.Clear(); _rosters.Clear();
                // Re-anchor rather than drop: whoever is connected keeps accruing, but only from the reset onward.
                long now = DateTime.UtcNow.Ticks;
                foreach (string u in new List<string>(_sessions.Keys)) _sessions[u] = now;
            }
            SaveToDisk();
            SaveColonistsToDisk();
        }

        public static bool RemoveUser(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            bool removed;
            lock (_lock)
            {
                removed = _entries.Remove(username) | _colonists.Remove(username) | _rosters.Remove(username);
                // Or their next heartbeat re-adds them and credits time from before the removal.
                _sessions.Remove(username);
            }
            if (removed) { SaveToDisk(); SaveColonistsToDisk(); }
            return removed;
        }

        // Snapshot under lock, write outside it - disk I/O must not block concurrent mutations.
        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Entries = new List<PlayerLeaderboardEntry>(_entries.Values);
            }
            JsonFileStore.Save(KmhDataPaths.PlayerStatsFile, state);
        }

        // An object rather than a top-level array, so fields can be added later without breaking the file.
        private class PersistedState
        {
            public List<PlayerLeaderboardEntry> Entries { get; set; } = new List<PlayerLeaderboardEntry>();
        }

        // Display-only vanity stats: clamped non-negative but otherwise trusted, because nothing here gates a reward.
        public static void ApplyColonyReport(string username, ColonyReport r)
        {
            if (string.IsNullOrWhiteSpace(username) || r == null) return;
            EnsurePlayer(username);
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.ColonyName         = Cap(r.ColonyName, 64);
                    e.ColonyAgeDays      = (int)Clamp(r.ColonyAgeDays, 0, 2_000_000);
                    e.TimePlayedHours    = (int)Clamp(r.TimePlayedHours, 0, 1_000_000);
                    e.Wealth             = Clamp(r.Wealth, 0, 1_000_000_000_000L);
                    e.Kills              = Clamp(r.Kills, 0, 10_000_000);
                    e.Population         = (int)Clamp(r.Population, 0, 100_000);
                    e.KillsHumanlike     = Clamp(r.KillsHumanlike, 0, 10_000_000);
                    e.KillsMechanoid     = Clamp(r.KillsMechanoid, 0, 10_000_000);
                    e.KillsAnimal        = Clamp(r.KillsAnimal, 0, 10_000_000);
                    e.RaidsSurvived      = (int)Clamp(r.RaidsSurvived, 0, 1_000_000);
                    e.PawnsLost          = (int)Clamp(r.PawnsLost, 0, 1_000_000);
                    e.DevelopmentScore   = (int)Clamp(r.DevelopmentScore, 0, 100_000_000);
                    e.DefenseScore       = (int)Clamp(r.DefenseScore, 0, 100_000_000);
                    e.TopColonistName    = Cap(r.TopColonistName, 64);
                    e.TopColonistTitle   = Cap(r.TopColonistTitle, 64);
                    e.TopColonistKills   = (int)Clamp(r.TopColonistKills, 0, 10_000_000);
                    e.Settlements        = SanitizeSettlements(r.Settlements);
                    e.LastReportUtcTicks = DateTime.UtcNow.Ticks;
                }
                if (r.Colonist != null) _colonists[username] = Sanitize(r.Colonist);
                else                    _colonists.Remove(username);
                _rosters[username] = SanitizeRoster(r.Roster);
            }
            SaveToDisk();
            SaveColonistsToDisk();
            // Pipeline observability: confirms the report reached + was stored server-side (open with `kmh diag`).
            Diagnostics.ServerLog.Verbose(
                $"PlayerStats: stored colony report from {username} - wealth {r.Wealth}, colony '{r.ColonyName}', " +
                $"{(r.Roster?.Count ?? 0)} colonist(s), top '{r.TopColonistName}'");
        }

        // Bounded because the count comes from the client: a report claiming thousands of maps must not grow the stored file.
        private static List<Dto.SettlementReport> SanitizeSettlements(List<Dto.SettlementReport> src)
        {
            List<Dto.SettlementReport> outList = new List<Dto.SettlementReport>();
            if (src == null) return outList;
            foreach (Dto.SettlementReport s in src)
            {
                if (s == null) continue;
                if (outList.Count >= 32) break;
                outList.Add(new Dto.SettlementReport
                {
                    Name       = Cap(s.Name, 64),
                    Wealth     = Clamp(s.Wealth, 0, 1_000_000_000_000L),
                    Population = (int)Clamp(s.Population, 0, 100_000),
                });
            }
            return outList;
        }

        // One-line health summary of the standings pipeline for the `kmh diag` admin command.
        public static string DiagLine()
        {
            lock (_lock)
            {
                int withReport = 0, withTop = 0; long newest = 0;
                foreach (PlayerLeaderboardEntry e in _entries.Values)
                {
                    if (e.LastReportUtcTicks > 0) { withReport++; if (e.LastReportUtcTicks > newest) newest = e.LastReportUtcTicks; }
                    if (!string.IsNullOrEmpty(e.TopColonistName)) withTop++;
                }
                int roster = 0; foreach (List<ColonistEntry> l in _rosters.Values) roster += l?.Count ?? 0;
                string ago = newest > 0 ? $"{(int)Math.Max(0, (DateTime.UtcNow - new DateTime(newest, DateTimeKind.Utc)).TotalSeconds)}s ago" : "never";
                return $"players={_entries.Count}, with colony report={withReport}, with top colonist={withTop}, " +
                       $"roster colonists={roster}, colonist profiles={_colonists.Count}, last report {ago}";
            }
        }

        public static ColonistProfile GetColonist(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock) { return _colonists.TryGetValue(username, out ColonistProfile d) ? d : null; }
        }

        public static ColonistRosterSnapshot BuildColonistRoster()
        {
            ColonistRosterSnapshot snap = new ColonistRosterSnapshot();
            lock (_lock)
            {
                foreach (KeyValuePair<string, List<ColonistEntry>> kv in _rosters)
                {
                    string owner  = kv.Key;
                    string colony = _entries.TryGetValue(owner, out PlayerLeaderboardEntry pe) ? pe.ColonyName : "";
                    foreach (ColonistEntry c in kv.Value)
                        snap.Colonists.Add(new ColonistEntry
                        {
                            Owner = owner, Colony = colony ?? "", Name = c.Name, Title = c.Title,
                            Age = c.Age, Days = c.Days, Kills = c.Kills,
                            SkShooting = c.SkShooting, SkMelee = c.SkMelee, SkMedicine = c.SkMedicine,
                            SkCrafting = c.SkCrafting, SkConstruction = c.SkConstruction,
                        });
                }
            }
            return snap;
        }

        private static List<ColonistEntry> SanitizeRoster(List<ColonistEntry> list)
        {
            List<ColonistEntry> outList = new List<ColonistEntry>();
            if (list == null) return outList;
            int n = Math.Min(list.Count, 10);
            for (int i = 0; i < n; i++)
            {
                ColonistEntry c = list[i];
                if (c == null) continue;
                outList.Add(new ColonistEntry
                {
                    Name  = Cap(c.Name, 64),
                    Title = Cap(c.Title, 64),
                    Age   = (int)Clamp(c.Age, 0, 5000),
                    Days  = (int)Clamp(c.Days, 0, 2_000_000),
                    Kills = (int)Clamp(c.Kills, 0, 10_000_000),
                    SkShooting     = (int)Clamp(c.SkShooting, 0, 50),
                    SkMelee        = (int)Clamp(c.SkMelee, 0, 50),
                    SkMedicine     = (int)Clamp(c.SkMedicine, 0, 50),
                    SkCrafting     = (int)Clamp(c.SkCrafting, 0, 50),
                    SkConstruction = (int)Clamp(c.SkConstruction, 0, 50),
                });
            }
            return outList;
        }

        // Bounded so a modified client can't bloat storage or push absurd values onto a vanity card.
        private const int MaxListItems = 32;

        private static ColonistProfile Sanitize(ColonistProfile c)
        {
            c.Name          = Cap(c.Name, 80);
            c.Title         = Cap(c.Title, 80);
            c.GenderAge     = Cap(c.GenderAge, 80);
            c.Descriptor    = Cap(c.Descriptor, 80);
            c.Childhood     = Cap(c.Childhood, 120);
            c.Adulthood     = Cap(c.Adulthood, 120);
            c.Weapon        = Cap(c.Weapon, 80);
            c.WeaponQuality = Cap(c.WeaponQuality, 32);
            c.DaysInColony  = (int)Clamp(c.DaysInColony, 0, 2_000_000);

            c.HealthPct = (int)Clamp(c.HealthPct, 0, 1000);
            c.PainPct   = (int)Clamp(c.PainPct, 0, 1000);
            c.TotalKills     = (int)Clamp(c.TotalKills, 0, 10_000_000);
            c.HumanlikeKills = (int)Clamp(c.HumanlikeKills, 0, 10_000_000);
            c.MechanoidKills = (int)Clamp(c.MechanoidKills, 0, 10_000_000);
            c.AnimalKills    = (int)Clamp(c.AnimalKills, 0, 10_000_000);
            c.DamageTaken    = (int)Clamp(c.DamageTaken, 0, 100_000_000);

            c.Traits       = CapStrings(c.Traits, 60);
            c.Incapable    = CapStrings(c.Incapable, 60);
            c.Conditions   = CapStrings(c.Conditions, 80);
            c.RecentCombat = CapStrings(c.RecentCombat, 120);

            if (c.Skills != null)
            {
                if (c.Skills.Count > MaxListItems) c.Skills = c.Skills.GetRange(0, MaxListItems);
                c.Skills.RemoveAll(s => s == null);   // a modified client can send null entries; don't NRE on them
                foreach (ColonistSkill s in c.Skills) { s.Name = Cap(s.Name, 40); s.Level = (int)Clamp(s.Level, 0, 50); s.Passion = (int)Clamp(s.Passion, 0, 2); }
            }
            if (c.Capacities != null)
            {
                if (c.Capacities.Count > MaxListItems) c.Capacities = c.Capacities.GetRange(0, MaxListItems);
                c.Capacities.RemoveAll(cap => cap == null);
                foreach (ColonistCapacity cap in c.Capacities) { cap.Name = Cap(cap.Name, 40); cap.Pct = (int)Clamp(cap.Pct, 0, 1000); }
            }
            return c;
        }

        private static List<string> CapStrings(List<string> list, int maxLen)
        {
            if (list == null) return new List<string>();
            if (list.Count > MaxListItems) list = list.GetRange(0, MaxListItems);
            for (int i = 0; i < list.Count; i++) list[i] = Cap(list[i], maxLen);
            return list;
        }

        // A separate file so the roster JSON stays small.
        private class ColonistState { public Dictionary<string, ColonistProfile> Colonists { get; set; } = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase); }

        public static void LoadColonistsFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.ColonistsFile, out ColonistState s) || s?.Colonists == null) return;
            lock (_lock)
            {
                _colonists.Clear();
                foreach (KeyValuePair<string, ColonistProfile> kv in s.Colonists)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) _colonists[kv.Key] = kv.Value;
            }
            Diagnostics.ServerLog.Info($"PlayerStats: loaded {s.Colonists.Count} colonist profile(s)");
        }

        public static void SaveColonistsToDisk()
        {
            ColonistState s = new ColonistState();
            lock (_lock) foreach (KeyValuePair<string, ColonistProfile> kv in _colonists) s.Colonists[kv.Key] = kv.Value;
            JsonFileStore.Save(KmhDataPaths.ColonistsFile, s);
        }

        // Clones with guild + discord-link joined in live: affiliation is never persisted here, so it can't go stale.
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
                        LastSeenUtcTicks  = e.LastSeenUtcTicks,
                        ActiveSeconds     = e.ActiveSeconds,
                        ConnectedSeconds  = e.ConnectedSeconds,
                        SilverDonated     = e.SilverDonated,
                        SalesEarned       = e.SalesEarned,
                        PurchasesSpent    = e.PurchasesSpent,
                        QuestsCompleted   = e.QuestsCompleted,
                        QuestsPosted      = e.QuestsPosted,
                        MarketplaceSales  = e.MarketplaceSales,
                        SitesBuilt        = e.SitesBuilt,
                        WorkerXp          = e.WorkerXp,
                        ColonyName         = e.ColonyName,
                        ColonyAgeDays      = e.ColonyAgeDays,
                        TimePlayedHours    = e.TimePlayedHours,
                        Wealth             = e.Wealth,
                        Settlements        = SanitizeSettlements(e.Settlements),
                        Kills              = e.Kills,
                        TopColonistName       = e.TopColonistName,
                        TopColonistTitle      = e.TopColonistTitle,
                        TopColonistKills      = e.TopColonistKills,
                        LastReportUtcTicks = e.LastReportUtcTicks,
                        ContractsBounty  = e.ContractsBounty,
                        ContractsDeliver = e.ContractsDeliver,
                        ContractsHunt    = e.ContractsHunt,
                        ContractsDefend  = e.ContractsDefend,
                        ContractsFailed  = e.ContractsFailed,
                        ContractStreak   = e.ContractStreak,
                        ItemsSold        = e.ItemsSold,
                        ItemsBought      = e.ItemsBought,
                        LargestSale      = e.LargestSale,
                        Population       = e.Population,
                        KillsHumanlike   = e.KillsHumanlike,
                        KillsMechanoid   = e.KillsMechanoid,
                        KillsAnimal      = e.KillsAnimal,
                        RaidsSurvived    = e.RaidsSurvived,
                        PawnsLost        = e.PawnsLost,
                        DevelopmentScore = e.DevelopmentScore,
                        DefenseScore     = e.DefenseScore,
                    });
                }
            }
            // Joined outside the lock - GuildStore/LinkedAccountsStore/SiteStore hold their own, and these never nest.
            List<Sites.Dto.SiteEntry> sites = SitesForJoin();
            Dictionary<string, long> siteSilver = SiteSilverFrom(sites);
            Dictionary<string, int>  owned      = SitesOwnedByPlayer(sites, out Dictionary<string, int> outposts);
            Dictionary<string, long> workerXp   = WorkerXpByWorker(sites);
            foreach (PlayerLeaderboardEntry e in s.Entries)
            {
                e.GuildName         = Guilds.GuildStore.CurrentGuildOf(e.Username) ?? "";
                e.IsLinkedToDiscord = LinkedAccounts.LinkedAccountsStore.IsLinked(e.Username);
                e.SiteSilverProduced = siteSilver.TryGetValue(e.Username, out long v) ? v : 0;
                e.SitesOwned         = owned.TryGetValue(e.Username, out int n) ? n : 0;
                e.OutpostsHeld       = outposts.TryGetValue(e.Username, out int o) ? o : 0;
                e.WorkerXp           = workerXp.TryGetValue(e.Username, out long xp) ? xp : 0;
                // Server-side total, never the client's claim, and the same call the storyteller and audit-player use.
                e.KmhWealth          = KmhWealthOf(e.Username);
                e.EconomyScore       = EconomyScoreOf(e);   // last: the joins above are terms of it
                // Derived per snapshot: no persisted field exists for an import or hand-edited save to smuggle a staff role through.
                e.StaffRole          = Identity.StaffRegistry.RoleFor(e.Username);
            }
            return s;
        }

        // KMH value is optional to a standings snapshot: a failure here costs one column, never the whole board.
        private static long KmhWealthOf(string username)
        {
            try { return Treasury.TreasuryStore.PersonalOffMapValue(username); }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"PlayerStats: KMH wealth join failed for {username}: {ex.Message}");
                return 0;
            }
        }

        // Sites are optional to a standings snapshot: a failure here costs three joins, never the whole board.
        private static List<Sites.Dto.SiteEntry> SitesForJoin()
        {
            try { return Sites.SiteStore.AllForApi(); }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"PlayerStats: site join failed: {ex.Message}");
                return new List<Sites.Dto.SiteEntry>();
            }
        }

        // Live, so losing a site lowers it - which is why it is not SitesBuilt. A guild-held site counts for no individual.
        private static Dictionary<string, int> SitesOwnedByPlayer(List<Sites.Dto.SiteEntry> sites, out Dictionary<string, int> outposts)
        {
            try { return SitesOwnedFrom(sites, out outposts); }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"PlayerStats: sites-owned join failed: {ex.Message}");
                outposts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Pure: the counting rule, separated from where the sites come from, so the arithmetic can be tested.
        internal static Dictionary<string, int> SitesOwnedFrom(IEnumerable<Sites.Dto.SiteEntry> sites,
                                                               out Dictionary<string, int> outposts)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            outposts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (sites != null)
            {
                foreach (Sites.Dto.SiteEntry site in sites)
                {
                    string acct = Features.Sites.SiteOwnership.PayoutAccount(site);
                    if (string.IsNullOrEmpty(acct)) continue;
                    map.TryGetValue(acct, out int cur);
                    map[acct] = cur + 1;
                    if (string.IsNullOrEmpty(site.OutpostTemplate)) continue;
                    outposts.TryGetValue(acct, out int oc);
                    outposts[acct] = oc + 1;
                }
            }
            return map;
        }

        // XP belongs to the worker, never the site owner - or one owner absorbs five players' progress.
        private static Dictionary<string, long> WorkerXpByWorker(List<Sites.Dto.SiteEntry> sites)
        {
            try { return WorkerXpFrom(sites); }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"PlayerStats: worker-xp join failed: {ex.Message}");
                return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Pure: XP is keyed by the WORKER, never the site owner.
        internal static Dictionary<string, long> WorkerXpFrom(IEnumerable<Sites.Dto.SiteEntry> sites)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (sites != null)
            {
                foreach (Sites.Dto.SiteEntry site in sites)
                {
                    if (site.WorkerProgress == null) continue;
                    foreach (KeyValuePair<string, Sites.Dto.WorkerProgressDto> kv in site.WorkerProgress)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                        map.TryGetValue(kv.Key, out long cur);
                        map[kv.Key] = cur + (long)kv.Value.Xp;
                    }
                }
            }
            return map;
        }

        internal static Dictionary<string, long> SiteSilverFrom(IEnumerable<Sites.Dto.SiteEntry> sites)
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Sites.Dto.SiteEntry site in sites ?? new List<Sites.Dto.SiteEntry>())
                {
                    string acct = Features.Sites.SiteOwnership.PayoutAccount(site);
                    if (string.IsNullOrEmpty(acct)) continue;
                    map.TryGetValue(acct, out long cur);
                    map[acct] = cur + (long)site.TotalSilverGenerated;
                }
            }
            catch { /* sites optional - never break the snapshot */ }
            return map;
        }

        public static void AddSilverDonated(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            EnsurePlayer(username); // make sure the row exists first - a bump on an unrostered player is otherwise lost
            lock (_lock)
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                    e.SilverDonated = Math.Max(0, e.SilverDonated + delta); // never negative (withdraw nets it down)
            SaveToDisk();
        }

        public static void AddSalesEarned(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            EnsurePlayer(username);
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.SalesEarned += delta;
                    e.MarketplaceSales += 1;
                }
            }
            SaveToDisk();
        }

        public static void RecordContractCompleted(string username, string kind)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.QuestsCompleted += 1;
                    e.ContractStreak  += 1;
                    if      (kind == Quests.Dto.QuestEntry.KindBounty)      e.ContractsBounty++;
                    else if (kind == Quests.Dto.QuestEntry.KindDeliverItem) e.ContractsDeliver++;
                    else if (kind == Quests.Dto.QuestEntry.KindHunt)        e.ContractsHunt++;
                    else if (kind == Quests.Dto.QuestEntry.KindDefend)      e.ContractsDefend++;
                }
            }
            SaveToDisk();
        }

        public static void RecordContractFailed(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock)
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) { e.ContractsFailed += 1; e.ContractStreak = 0; }
            SaveToDisk();
        }

        // SalesEarned and the sale count arrive separately through AddSalesEarned.
        public static void RecordSale(string seller, int qty, long saleValue)
        {
            if (string.IsNullOrEmpty(seller) || qty <= 0) return;
            EnsurePlayer(seller);
            lock (_lock)
                if (_entries.TryGetValue(seller, out PlayerLeaderboardEntry e)) { e.ItemsSold += qty; if (saleValue > e.LargestSale) e.LargestSale = saleValue; }
            SaveToDisk();
        }

        public static void RecordPurchase(string buyer, int qty, long spent)
        {
            if (string.IsNullOrEmpty(buyer) || qty <= 0) return;
            EnsurePlayer(buyer);
            lock (_lock)
                if (_entries.TryGetValue(buyer, out PlayerLeaderboardEntry e)) { e.ItemsBought += qty; e.PurchasesSpent += Math.Max(0, spent); }
            SaveToDisk();
        }

        public static void BumpQuestsCompleted(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock)
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                    e.QuestsCompleted += 1;
            SaveToDisk();
        }

        // Only a new site counts: a capture is a Frontier record instead, and losing a site never decrements this.
        public static void BumpSitesBuilt(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock) { if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) e.SitesBuilt += 1; }
            SaveToDisk();
        }

        // Credited to whoever executed the capture, guild claims included - a guild does not act on its own.
        public static void BumpFrontierCaptures(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock) { if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) e.FrontierCaptures += 1; }
            SaveToDisk();
        }

        public static void BumpQuestsPosted(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock)
            {
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                {
                    e.QuestsPosted += 1;
                }
            }
            SaveToDisk();
        }

        // Derived per snapshot, never stored - WorkerXp is joined live, so a cached score would lag it.
        internal static long EconomyScoreOf(PlayerLeaderboardEntry e)
            => e.SilverDonated
             + e.SalesEarned        / 2
             + e.QuestsCompleted    * 100L
             // SitesBuilt, not SitesOwned: a score that fell when you sold a site would rank holding above achieving.
             + e.SitesBuilt         * 50L
             + e.WorkerXp           / 10L;
    }
}
