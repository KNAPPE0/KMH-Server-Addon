using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.PlayerStats
{
    // All access is under _lock: RWT's chat and login run on background threads.
    internal static class PlayerStatsStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, PlayerLeaderboardEntry> _entries
            = new Dictionary<string, PlayerLeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

        // Kept out of the snapshot for size and served on demand.
        private static readonly Dictionary<string, ColonistProfile> _colonists
            = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase);

        // Saved in Colonists.json, so Colonist Records and the season's Deadliest Colonist survive a restart.
        private static readonly Dictionary<string, RosterState> _rosters
            = new Dictionary<string, RosterState>(StringComparer.OrdinalIgnoreCase);

        // Oldest first. Keyed by (player, save, colonist): the pawn id where the client sends one, else the name.
        private static readonly List<FallenRecord> _fallen = new List<FallenRecord>();
        internal const int MaxFallenPerPlayer = 25;
        internal const int MaxRoster = 10;

        // The save and death count a roster came from, which is what tells a death from a colonist pushed out of the top ten.
        internal sealed class RosterState
        {
            public string SaveId { get; set; } = "";
            public int PawnsLost { get; set; }
            public List<ColonistEntry> Colonists { get; set; } = new List<ColonistEntry>();
        }

        internal sealed class FallenRecord
        {
            public string Username { get; set; } = "";
            public string SaveId { get; set; } = "";
            public int ColonyDay { get; set; }
            public long DiedUtcTicks { get; set; }
            public ColonistEntry Colonist { get; set; } = new ColonistEntry();
        }

        // Separate from BuildSnapshot, which clones every row and joins the sites three times.
        public static int PlayerCount { get { lock (_lock) return _entries.Count; } }

        // Client-reported, so only shown (status, admin report); quest rewards scale to server-owned values instead.
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
                _statsPending++;
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
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) { e.LastSeenUtcTicks = DateTime.UtcNow.Ticks; _statsPending++; }
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

        public static HashSet<string> OnlineNow()
        {
            lock (_lock) return new HashSet<string>(_sessions.Keys, StringComparer.OrdinalIgnoreCase);
        }

        // Rolled on every heartbeat, so a crash loses a minute of credit, not a night.
        public static long RollSession(string username, bool ending)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(username, out long anchor)) return 0;
                long now = DateTime.UtcNow.Ticks;
                long credited = Rollable(anchor, now);
                if (credited > 0 && _entries.TryGetValue(username, out PlayerLeaderboardEntry e))
                { e.ConnectedSeconds += credited; _statsPending++; }
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

        internal static void SetConnectedSecondsForTest(string username, long seconds)
        {
            EnsurePlayer(username);
            lock (_lock) if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) e.ConnectedSeconds = seconds;
        }

        // first_seen survives reconnects; only a season reset clears the row.
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
            if (added) MarkStats();
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
                Diagnostics.ServerLog.Diag($"PlayerStats: loaded {state.Entries.Count} entries from disk");
            }
        }

        public static void ClearForNewSeason()
        {
            lock (_lock)
            {
                _entries.Clear(); _colonists.Clear(); _rosters.Clear(); _fallen.Clear();
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
                removed = _entries.Remove(username) | _colonists.Remove(username) | _rosters.Remove(username)
                        | _fallen.RemoveAll(f => string.Equals(f.Username, username, StringComparison.OrdinalIgnoreCase)) > 0;
                // Or their next heartbeat re-adds them and credits time from before the removal.
                _sessions.Remove(username);
            }
            if (removed) { SaveToDisk(); SaveColonistsToDisk(); }
            return removed;
        }

        // Display stats, not value: a change marks the file and the "playerstats-save" job writes it, so a crash costs 30 s.
        private static long _statsPending, _colonistsPending;
        internal static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

        private static void MarkStats() { lock (_lock) _statsPending++; }
        private static void MarkColonists() { lock (_lock) _colonistsPending++; }

        internal static (long Stats, long Colonists) PendingWrites { get { lock (_lock) return (_statsPending, _colonistsPending); } }

        public static void SaveIfDirty()
        {
            (long stats, long colonists) = PendingWrites;
            if (stats > 0) SaveToDisk();
            if (colonists > 0) SaveColonistsToDisk();
        }

        // Snapshot under lock, write outside it - disk I/O must not block concurrent mutations.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long pending, seq;
            lock (_lock)
            {
                state.Entries = KmhDeepCopy.OfList(_entries.Values);
                pending = _statsPending; _statsPending = 0;
                seq = JsonFileStore.NextSequence();
            }
            bool ok = JsonFileStore.Save(KmhDataPaths.PlayerStatsFile, state, seq);
            Written(KmhDataPaths.PlayerStatsFile, ok, pending, ref _statsPending);
            return ok;
        }

        // A failed write keeps the file marked, so the next tick retries it.
        private static void Written(string path, bool ok, long pending, ref long counter)
        {
            if (ok) { JsonFileStore.NoteCoalesced(path, pending - 1); return; }
            lock (_lock) counter += Math.Max(1, pending);
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
                ApplyRosterLocked(username, r, DateTime.UtcNow.Ticks);
            }
            MarkStats();
            MarkColonists();
            Diagnostics.ServerLog.Verbose(
                $"PlayerStats: stored colony report from {username} - wealth {r.Wealth}, colony '{r.ColonyName}', " +
                $"{(r.Roster?.Count ?? 0)} colonist(s), top '{r.TopColonistName}'");
        }

        private static void ApplyRosterLocked(string username, ColonyReport r, long nowTicks)
        {
            List<ColonistEntry> roster = SanitizeRoster(r.Roster);
            string saveId = Cap(r.SaveId, 64);
            int lost = (int)Clamp(r.PawnsLost, 0, 1_000_000);
            _rosters.TryGetValue(username, out RosterState before);
            string colony = _entries.TryGetValue(username, out PlayerLeaderboardEntry e) ? e.ColonyName ?? "" : "";

            // Back in a later roster of the same save: alive after all, or raised from the dead.
            _fallen.RemoveAll(f => SameSave(f, username, saveId) && f.Colonist != null && roster.Exists(c => SameColonist(f.Colonist, c)));

            foreach (ColonistEntry dead in FallenBetween(before, saveId, lost, roster))
            {
                _fallen.RemoveAll(f => SameSave(f, username, saveId) && f.Colonist != null && SameColonist(f.Colonist, dead));
                ColonistEntry last = KmhDeepCopy.Of(dead);
                last.Owner = username; last.Colony = colony;
                _fallen.Add(new FallenRecord
                {
                    Username = username, SaveId = saveId, ColonyDay = (int)Clamp(r.ColonyAgeDays, 0, 2_000_000),
                    DiedUtcTicks = nowTicks, Colonist = last,
                });
            }
            TrimFallenLocked(username);
            _rosters[username] = new RosterState { SaveId = saveId, PawnsLost = lost, Colonists = roster };
        }

        private static bool SameSave(FallenRecord f, string username, string saveId)
            => f != null && string.Equals(f.Username, username, StringComparison.OrdinalIgnoreCase) && string.Equals(f.SaveId, saveId, StringComparison.Ordinal);

        // By pawn id when both carry one; by name otherwise, which is all an older client or record has.
        internal static bool SameColonist(ColonistEntry a, ColonistEntry b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.Id) && !string.IsNullOrEmpty(b.Id)) return string.Equals(a.Id, b.Id, StringComparison.Ordinal);
            return string.Equals(a.Name ?? "", b.Name ?? "", StringComparison.Ordinal);
        }

        private static void TrimFallenLocked(string username)
        {
            int mine = 0;
            for (int i = _fallen.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_fallen[i].Username, username, StringComparison.OrdinalIgnoreCase)) continue;
                if (++mine > MaxFallenPerPlayer) _fallen.RemoveAt(i);
            }
        }

        // Gone while the same save's pawns_lost rose; not when a full top ten could have outranked them, or more left than died.
        internal static List<ColonistEntry> FallenBetween(RosterState before, string saveId, int pawnsLost, List<ColonistEntry> now)
        {
            var gone = new List<ColonistEntry>();
            if (before?.Colonists == null || now == null || string.IsNullOrEmpty(saveId)
                || !string.Equals(before.SaveId, saveId, StringComparison.Ordinal)) return gone;
            int deaths = pawnsLost - before.PawnsLost;
            if (deaths <= 0) return gone;

            ColonistEntry floor = null;
            foreach (ColonistEntry c in now)
            {
                if (c == null) continue;
                if (floor == null || RankCompare(c, floor) < 0) floor = c;
            }
            bool full = now.Count >= MaxRoster;
            foreach (ColonistEntry c in before.Colonists)
            {
                if (c == null || string.IsNullOrEmpty(c.Name) || now.Exists(n => SameColonist(c, n))) continue;
                if (full && floor != null && RankCompare(c, floor) <= 0) continue;
                gone.Add(c);
            }
            return gone.Count <= deaths ? gone : new List<ColonistEntry>();
        }

        // Kills and days only grow, so a colonist that outranked the new roster's last place cannot have been pushed out of it.
        private static int RankCompare(ColonistEntry a, ColonistEntry b)
        {
            int byKills = a.Kills.CompareTo(b.Kills);
            return byKills != 0 ? byKills : a.Days.CompareTo(b.Days);
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
                int roster = 0; foreach (RosterState l in _rosters.Values) roster += l?.Colonists?.Count ?? 0;
                string ago = newest > 0 ? $"{(int)Math.Max(0, (DateTime.UtcNow - new DateTime(newest, DateTimeKind.Utc)).TotalSeconds)}s ago" : "never";
                return $"players={_entries.Count}, with colony report={withReport}, with top colonist={withTop}, " +
                       $"roster colonists={roster}, fallen={_fallen.Count}, colonist profiles={_colonists.Count}, last report {ago}";
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
                foreach (KeyValuePair<string, RosterState> kv in _rosters)
                {
                    string owner  = kv.Key;
                    string colony = _entries.TryGetValue(owner, out PlayerLeaderboardEntry pe) ? pe.ColonyName : "";
                    foreach (ColonistEntry c in kv.Value?.Colonists ?? new List<ColonistEntry>())
                        snap.Colonists.Add(new ColonistEntry
                        {
                            Owner = owner, Colony = colony ?? "", Id = c.Id ?? "", Name = c.Name, Title = c.Title,
                            Age = c.Age, Days = c.Days, Kills = c.Kills,
                            SkShooting = c.SkShooting, SkMelee = c.SkMelee, SkMedicine = c.SkMedicine,
                            SkCrafting = c.SkCrafting, SkConstruction = c.SkConstruction,
                        });
                }
                for (int i = _fallen.Count - 1; i >= 0; i--)
                {
                    FallenRecord f = _fallen[i];
                    ColonistEntry c = KmhDeepCopy.Of(f.Colonist) ?? new ColonistEntry();
                    c.Owner = f.Username;
                    snap.Fallen.Add(new FallenColonistEntry { Colonist = c, ColonyDay = f.ColonyDay, DiedUtcTicks = f.DiedUtcTicks });
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
                    Id    = Cap(c.Id, 64),
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

        // A separate file so the roster JSON stays small. Files from before rosters were kept have neither list and load empty.
        internal class ColonistState
        {
            public Dictionary<string, ColonistProfile> Colonists { get; set; } = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, RosterState> Rosters { get; set; } = new Dictionary<string, RosterState>(StringComparer.OrdinalIgnoreCase);
            public List<FallenRecord> Fallen { get; set; } = new List<FallenRecord>();
        }

        // A hand-edited or damaged file gets the same caps a report does.
        internal static ColonistState Restore(ColonistState s)
        {
            var clean = new ColonistState();
            if (s == null) return clean;
            foreach (KeyValuePair<string, ColonistProfile> kv in s.Colonists ?? new Dictionary<string, ColonistProfile>())
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) clean.Colonists[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, RosterState> kv in s.Rosters ?? new Dictionary<string, RosterState>())
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    clean.Rosters[kv.Key] = new RosterState
                    {
                        SaveId = Cap(kv.Value.SaveId, 64), PawnsLost = (int)Clamp(kv.Value.PawnsLost, 0, 1_000_000),
                        Colonists = SanitizeRoster(kv.Value.Colonists),
                    };
            var perPlayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<FallenRecord> fallen = s.Fallen ?? new List<FallenRecord>();
            for (int i = fallen.Count - 1; i >= 0; i--)
            {
                FallenRecord f = fallen[i];
                if (f?.Colonist == null || string.IsNullOrEmpty(f.Username) || string.IsNullOrEmpty(f.Colonist.Name)) continue;
                perPlayer.TryGetValue(f.Username, out int n);
                if (n >= MaxFallenPerPlayer) continue;
                perPlayer[f.Username] = n + 1;
                clean.Fallen.Insert(0, f);
            }
            return clean;
        }

        public static void LoadColonistsFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.ColonistsFile, out ColonistState loaded) || loaded == null) return;
            ColonistState s;
            lock (_lock) s = InstallInto(loaded, _colonists, _rosters, _fallen);
            Diagnostics.ServerLog.Diag($"PlayerStats: loaded {s.Colonists.Count} colonist profile(s), {s.Rosters.Count} roster(s), {s.Fallen.Count} fallen");
        }

        // Takes the collections, not the live fields, so a self-test can run the real load and save without touching them.
        internal static ColonistState InstallInto(ColonistState loaded, Dictionary<string, ColonistProfile> colonists,
                                                  Dictionary<string, RosterState> rosters, List<FallenRecord> fallen)
        {
            ColonistState s = Restore(loaded);
            colonists.Clear();
            foreach (KeyValuePair<string, ColonistProfile> kv in s.Colonists) colonists[kv.Key] = kv.Value;
            rosters.Clear();
            foreach (KeyValuePair<string, RosterState> kv in s.Rosters) rosters[kv.Key] = kv.Value;
            fallen.Clear();
            fallen.AddRange(s.Fallen);
            return s;
        }

        internal static ColonistState CaptureFrom(Dictionary<string, ColonistProfile> colonists,
                                                  Dictionary<string, RosterState> rosters, List<FallenRecord> fallen)
        {
            ColonistState s = new ColonistState();
            foreach (KeyValuePair<string, ColonistProfile> kv in colonists) s.Colonists[kv.Key] = KmhDeepCopy.Of(kv.Value);
            foreach (KeyValuePair<string, RosterState> kv in rosters) s.Rosters[kv.Key] = KmhDeepCopy.Of(kv.Value);
            s.Fallen = KmhDeepCopy.OfList(fallen);
            return s;
        }

        public static bool SaveColonistsToDisk()
        {
            ColonistState s;
            long pending, seq;
            lock (_lock)
            {
                s = CaptureFrom(_colonists, _rosters, _fallen);
                pending = _colonistsPending; _colonistsPending = 0;
                seq = JsonFileStore.NextSequence();
            }
            bool ok = JsonFileStore.Save(KmhDataPaths.ColonistsFile, s, seq);
            Written(KmhDataPaths.ColonistsFile, ok, pending, ref _colonistsPending);
            return ok;
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
                        FrontierCaptures  = e.FrontierCaptures,
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
                // Disclosure outranks a staff badge: a simulated account always reads as one.
                if (Simulation.SimulatedAccounts.IsSimulated(e.Username)) e.StaffRole = Simulation.SimulatedAccounts.Role;
            }
            return s;
        }

        // KMH value is optional to a standings snapshot: a failure here costs one column, never the whole board.
        private static long KmhWealthOf(string username)
        {
            try { return Treasury.TreasuryStore.PersonalOffMapValue(username); }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"PlayerStats: Colony Nexus wealth join failed for {username}: {ex.Message}");
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
                        // Summed per ACCOUNT: the key names a colonist now, and a player's site XP is one figure.
                        string acct = Sites.SiteWorkerKey.AccountOf(kv.Key);
                        map.TryGetValue(acct, out long cur);
                        map[acct] = cur + (long)kv.Value.Xp;
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

        // Creates the row first, or a bump on a player not yet rostered is lost.
        private static void UpdateEntry(string username, Action<PlayerLeaderboardEntry> change)
        {
            EnsurePlayer(username);
            lock (_lock)
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) change(e);
            MarkStats();
        }

        public static void AddSilverDonated(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            // Withdrawals net it down, never below zero.
            UpdateEntry(username, e => e.SilverDonated = Math.Max(0, e.SilverDonated + delta));
        }

        public static void AddSalesEarned(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            UpdateEntry(username, e => { e.SalesEarned += delta; e.MarketplaceSales += 1; });
        }

        public static void RecordContractCompleted(string username, string kind)
        {
            if (string.IsNullOrEmpty(username)) return;
            UpdateEntry(username, e =>
            {
                e.QuestsCompleted += 1;
                e.ContractStreak  += 1;
                if      (kind == Quests.Dto.QuestEntry.KindBounty)      e.ContractsBounty++;
                else if (kind == Quests.Dto.QuestEntry.KindDeliverItem) e.ContractsDeliver++;
                else if (kind == Quests.Dto.QuestEntry.KindHunt)        e.ContractsHunt++;
                else if (kind == Quests.Dto.QuestEntry.KindDefend)      e.ContractsDefend++;
            });
        }

        public static void RecordContractFailed(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            UpdateEntry(username, e => { e.ContractsFailed += 1; e.ContractStreak = 0; });
        }

        // SalesEarned and the sale count arrive separately through AddSalesEarned.
        public static void RecordSale(string seller, int qty, long saleValue)
        {
            if (string.IsNullOrEmpty(seller) || qty <= 0) return;
            UpdateEntry(seller, e => { e.ItemsSold += qty; if (saleValue > e.LargestSale) e.LargestSale = saleValue; });
        }

        public static void RecordPurchase(string buyer, int qty, long spent)
        {
            if (string.IsNullOrEmpty(buyer) || qty <= 0) return;
            UpdateEntry(buyer, e => { e.ItemsBought += qty; e.PurchasesSpent += Math.Max(0, spent); });
        }

        public static void BumpQuestsCompleted(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            UpdateEntry(username, e => e.QuestsCompleted += 1);
        }

        // Only a new site counts: a capture is a Frontier record instead, and losing a site never decrements this.
        public static void BumpSitesBuilt(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            UpdateEntry(username, e => e.SitesBuilt += 1);
        }

        // Credited to whoever executed the capture, guild claims included - a guild does not act on its own.
        public static void BumpFrontierCaptures(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            UpdateEntry(username, e => e.FrontierCaptures += 1);
        }

        public static void BumpQuestsPosted(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            UpdateEntry(username, e => e.QuestsPosted += 1);
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
