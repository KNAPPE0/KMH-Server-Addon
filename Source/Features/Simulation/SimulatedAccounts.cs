using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Simulation
{
    // Written by the owner or the playtest population tool, never by a client, so nobody can mark a real player simulated.
    internal sealed class SimulatedAccountsFile
    {
        public int SchemaVersion { get; set; } = 1;

        // Off the published boards (in-game, image, Discord, website) while on; their play is still real.
        public bool ExcludeFromRankings { get; set; } = true;

        public List<string> Accounts { get; set; } = new List<string>();
    }

    // Test accounts run by the population simulator: real RWT and Colony Nexus sessions, always shown as simulated.
    internal static class SimulatedAccounts
    {
        public const string Role = "sim";
        public const string BadgeLabel = "SIM";
        public const string BadgeColor = "929bab";

        public static string FilePath => Path.Combine(KmhDataPaths.Folder, "Simulation", "accounts.json");

        private static readonly object _lock = new object();
        private static HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _exclude = true;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static DateTime _checkedAt = DateTime.MinValue;
        private static Func<SimulatedAccountsFile> _sourceForTest;

        // Checked at most every few seconds, so the simulator can add an account without a reload.
        private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(5);

        private static void Refresh()
        {
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_sourceForTest == null && now - _checkedAt < RecheckEvery) return;
                _checkedAt = now;
            }

            SimulatedAccountsFile file = null;
            DateTime stamp = DateTime.MinValue;
            if (_sourceForTest != null) file = _sourceForTest();
            else
            {
                try
                {
                    string path = FilePath;
                    if (File.Exists(path))
                    {
                        stamp = File.GetLastWriteTimeUtc(path);
                        lock (_lock) if (stamp == _fileStamp) return;
                        if (!JsonFileStore.TryLoad(path, out file)) file = null;
                    }
                }
                catch { file = null; }
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in file?.Accounts ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
            lock (_lock)
            {
                _names = names;
                _exclude = file?.ExcludeFromRankings ?? true;
                _fileStamp = stamp;
            }
        }

        public static void Reload() { lock (_lock) { _checkedAt = DateTime.MinValue; _fileStamp = DateTime.MinValue; } }

        public static bool IsSimulated(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            Refresh();
            // Untrimmed: RWT keeps "bot1 " and "bot1" as two accounts, and only the listed one is simulated.
            lock (_lock) return _names.Contains(username);
        }

        // Gameplay that scales with players (world quests, Frontier) counts people only: nobody plays a simulated colony.
        public static bool IsPerson(string username) => !string.IsNullOrWhiteSpace(username) && !IsSimulated(username);

        // A session past the handshake but not yet logged in has no name ("?"): it is nobody yet, not a person.
        internal static int People(IEnumerable<string> names) => names?.Count(n => n != "?" && IsPerson(n)) ?? 0;

        internal static List<string> RwtOnline()
        {
            var rwt = new List<string>();
            try
            {
                foreach (ServerClient c in Network.ServerClients.Keys)
                    if (c?.IsVerified == true) rwt.Add(c.GetData<UserFile>()?.Username ?? "?");
            }
            catch { }
            return rwt;
        }

        public static bool ExcludedFromRankings(string username)
        {
            if (!IsSimulated(username)) return false;
            lock (_lock) return _exclude;
        }

        public static bool ExcludesFromRankings { get { Refresh(); lock (_lock) return _exclude; } }

        public static List<string> All()
        {
            Refresh();
            lock (_lock) return _names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The SIM badge is disclosure, not staff styling, so it rides along even when staff badges are off.
        public static string WithBadge(string staffBadges)
        {
            if (All().Count == 0) return staffBadges ?? "";
            string sim = $"{Role}:{BadgeLabel}:{BadgeColor}";
            return string.IsNullOrEmpty(staffBadges) ? sim : staffBadges + ";" + sim;
        }

        // Appended last: a client keeps the last role it reads for a name, so a simulated account never reads as staff.
        public static string WithRoles(string staffRoles)
        {
            List<string> names = All();
            if (names.Count == 0) return staffRoles ?? "";
            string sims = string.Join(";", names.Select(n => n + ":" + Role));
            return string.IsNullOrEmpty(staffRoles) ? sims : staffRoles + ";" + sims;
        }

        internal static IDisposable UseForTest(SimulatedAccountsFile file)
        {
            lock (_lock) _sourceForTest = () => file;
            return new RestoreSource();
        }

        private sealed class RestoreSource : IDisposable
        {
            public void Dispose()
            {
                lock (_lock) _sourceForTest = null;
                Reload();
            }
        }

        public static void Report(Action<string> reply)
        {
            List<string> rwt = RwtOnline();
            HashSet<string> linked = PlayerStats.PlayerStatsStore.OnlineNow();

            int rwtSim = rwt.Count(IsSimulated), linkedSim = linked.Count(IsSimulated), pending = rwt.Count(n => n == "?");
            List<string> names = All();
            reply("=== Simulated players ===");
            reply($"  registry: {names.Count} account(s) in KMH-Data/Simulation/accounts.json; "
                + (ExcludesFromRankings ? "kept off the leaderboards" : "RANKED on the leaderboards"));
            reply($"  RWT online:          {rwt.Count,3}  ({People(rwt)} people, {rwtSim} simulated"
                + (pending > 0 ? $", {pending} still logging in)" : ")"));
            reply($"  Colony Nexus linked: {linked.Count,3}  ({People(linked)} people, {linkedSim} simulated)");
            List<string> unlinked = rwt.Where(n => !linked.Contains(n)).ToList();
            if (unlinked.Count > 0)
                reply($"  in RWT but not linked to Colony Nexus: {string.Join(", ", unlinked.Take(12))}{(unlinked.Count > 12 ? ", ..." : "")}");
            if (names.Count == 0) reply("  No simulated accounts are registered, so every session here is a person.");
        }
    }
}
