using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Persistence;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.PlayerStats
{
    // Roster of every player ever seen, persisted to KMH-Data/Players/PlayerStats.json and saved after each mutation
    // (cheap; the roster is small). Other features bump the stat fields (treasury -> SilverDonated, marketplace ->
    // SalesEarned, quests -> QuestsCompleted, ...). All access is under _lock (RWT's chat + login are background threads).
    internal static class PlayerStatsStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, PlayerLeaderboardEntry> _entries
            = new Dictionary<string, PlayerLeaderboardEntry>(StringComparer.OrdinalIgnoreCase);

        // Full top-colonist profiles (Bio/Health/Combat), kept OUT of the snapshot for size - served on demand.
        private static readonly Dictionary<string, ColonistProfile> _colonists
            = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase);

        // Each player's reported compact colonist roster, for the per-skill Colonist Records boards (in-memory;
        // rebuilds from reports after a restart).
        private static readonly Dictionary<string, List<ColonistEntry>> _rosters
            = new Dictionary<string, List<ColonistEntry>>(StringComparer.OrdinalIgnoreCase);

        // Server "wealth index": total reported colony wealth across every player. The World Engine scales quest
        // rewards to this so a thriving server pays bigger pots than a quiet one - a live read of RimWorld's own
        // economy, aggregated. Returns 0 before any colony report has landed.
        public static long TotalReportedWealth()
        {
            long total = 0;
            lock (_lock)
                foreach (PlayerLeaderboardEntry e in _entries.Values)
                    if (e != null && e.Wealth > 0) total += e.Wealth;
            return total;
        }

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

        // Apply a client's colony report (display-only vanity stats). Updates the roster row's colony fields and
        // stashes the full colonist profile for on-demand fetch. Values clamped non-negative so a bad client can't
        // push negatives, but otherwise trusted - this is cosmetic, not reward-gating.
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

        // Full colonist profile for a player, or null if none reported.
        public static ColonistProfile GetColonist(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock) { return _colonists.TryGetValue(username, out ColonistProfile d) ? d : null; }
        }

        // Flattened roster of every colony's reported colonists, each stamped with its owner + colony name.
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

        // Bound a reported roster: at most 10 colonists, strings capped, skills/age/days/kills clamped.
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

        // Bound a client-reported profile so a modified client can't bloat storage or push absurd values (it's a
        // display-only vanity card; just keep it sane). Strings capped, lists truncated, numbers clamped.
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

        // -- colonist persistence (separate file so the roster JSON stays small) --

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
                        ColonyName         = e.ColonyName,
                        ColonyAgeDays      = e.ColonyAgeDays,
                        TimePlayedHours    = e.TimePlayedHours,
                        Wealth             = e.Wealth,
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
            // join outside the lock - GuildStore/LinkedAccountsStore/SiteStore have their own locks, never nest
            Dictionary<string, long> siteSilver = SiteSilverByOwner();
            foreach (PlayerLeaderboardEntry e in s.Entries)
            {
                e.GuildName         = Guilds.GuildStore.CurrentGuildOf(e.Username) ?? "";
                e.IsLinkedToDiscord = LinkedAccounts.LinkedAccountsStore.IsLinked(e.Username);
                e.SiteSilverProduced = siteSilver.TryGetValue(e.Username, out long v) ? v : 0;
            }
            return s;
        }

        // Total silver each player's custom sites have generated, summed by owner (for Site Records).
        private static Dictionary<string, long> SiteSilverByOwner()
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Sites.Dto.SiteEntry site in Sites.SiteStore.AllForApi())
                {
                    if (site == null || string.IsNullOrEmpty(site.OwnerUsername)) continue;
                    map.TryGetValue(site.OwnerUsername, out long cur);
                    map[site.OwnerUsername] = cur + (long)site.TotalSilverGenerated;
                }
            }
            catch { /* sites optional - never break the snapshot */ }
            return map;
        }

        // Mutation entry points for other features (no-op now; consumers arrive when Treasury/Marketplace/Quest
        // handlers port)
        public static void AddSilverDonated(string username, long delta)
        {
            if (string.IsNullOrEmpty(username) || delta == 0) return;
            EnsurePlayer(username); // make sure the row exists first - a bump on an unrostered player is otherwise lost
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
            EnsurePlayer(username);
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

        // Contract completion: bumps total + the per-kind bucket + the consecutive-completion streak.
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
                    RecomputeEconomyScoreLocked(e);
                }
            }
            SaveToDisk();
        }

        // Contract failure (abandon / rejected proof): bumps the failure count and breaks the streak.
        public static void RecordContractFailed(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            EnsurePlayer(username);
            lock (_lock)
                if (_entries.TryGetValue(username, out PlayerLeaderboardEntry e)) { e.ContractsFailed += 1; e.ContractStreak = 0; }
            SaveToDisk();
        }

        // Trade detail on a completed marketplace sale (seller side; SalesEarned/count come via AddSalesEarned).
        public static void RecordSale(string seller, int qty, long saleValue)
        {
            if (string.IsNullOrEmpty(seller) || qty <= 0) return;
            EnsurePlayer(seller);
            lock (_lock)
                if (_entries.TryGetValue(seller, out PlayerLeaderboardEntry e)) { e.ItemsSold += qty; if (saleValue > e.LargestSale) e.LargestSale = saleValue; }
            SaveToDisk();
        }

        // Trade detail on a completed marketplace buy (buyer side).
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
