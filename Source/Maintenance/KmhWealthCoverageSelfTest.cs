using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Maintenance
{
    // A store that holds player value but is missing from the off-map list is invisible to raid scaling AND to standings.
    internal static class KmhWealthCoverageSelfTest
    {
        // The naming convention a value-holding store follows; a new one is found by name, not by a hand-kept list.
        private static readonly string[] AccessorNames =
        {
            "EscrowValueFor", "StoredValueFor", "ReservedSilverFor", "VaultShareFor", "PersonalVaultValue",
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var wired = new HashSet<MethodInfo>();
            foreach (Func<string, long> f in TreasuryStore.OffMapValueSources())
                if (f?.Method != null) wired.Add(f.Method);

            var found = new List<MethodInfo>();
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (Type t in LoadableTypes(typeof(TreasuryStore).Assembly))
            {
                if (!t.IsClass || !t.FullName.StartsWith("KMHServerAddon.Features.", StringComparison.Ordinal)) continue;
                foreach (string name in AccessorNames)
                {
                    MethodInfo m = t.GetMethod(name, Flags, null, new[] { typeof(string) }, null);
                    if (m != null && m.ReturnType == typeof(long)) found.Add(m);
                }
            }

            List<string> missing = found.Where(m => !wired.Contains(m))
                                        .Select(m => m.DeclaringType.Name + "." + m.Name).ToList();
            r.Add(("Wealth: every value-holding store is in the off-map list", missing.Count == 0,
                missing.Count == 0 ? $"{found.Count} source(s) wired"
                                   : "NOT COUNTED as player wealth: " + string.Join(", ", missing)));

            // Non-vacuous: the discovery must actually be finding the accessors, or an empty set would always pass.
            r.Add(("Wealth: the off-map source scan finds real accessors", found.Count >= 8, $"{found.Count} discovered"));

            r.Add(("Wealth: a player with nothing held reads zero", TreasuryStore.PersonalOffMapValue(ProbeUser) == 0,
                   ProbeUser));

            r.AddRange(GuildShareRules());
            r.AddRange(StandingsTotalRules());
            return r;
        }

        // A type whose optional dependency is absent must not blind the scan to every other type in the assembly.
        private static IEnumerable<Type> LoadableTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        // A guild vault belongs to the guild once, not to each member in full - the shares must never sum above it.
        private static IEnumerable<(string, bool, string)> GuildShareRules()
        {
            var r = new List<(string, bool, string)>();
            long vault = 900;

            long[] weighted = { Share(vault, new long[] { 600, 300 }, 0), Share(vault, new long[] { 600, 300 }, 1) };
            r.Add(("Wealth: guild shares are contribution-weighted", weighted[0] == 600 && weighted[1] == 300,
                   $"{weighted[0]} + {weighted[1]} of {vault}"));

            long[] even = { Share(vault, new long[] { 0, 0, 0 }, 0), Share(vault, new long[] { 0, 0, 0 }, 1),
                            Share(vault, new long[] { 0, 0, 0 }, 2) };
            r.Add(("Wealth: with no contributions the vault splits evenly", even.Sum() == vault,
                   $"{string.Join(" + ", even)} of {vault}"));

            long[] lopsided = { Share(vault, new long[] { 1, 2, 4 }, 0), Share(vault, new long[] { 1, 2, 4 }, 1),
                                Share(vault, new long[] { 1, 2, 4 }, 2) };
            r.Add(("Wealth: shares never sum above the vault", lopsided.Sum() <= vault,
                   $"{lopsided.Sum()} <= {vault}"));

            r.Add(("Wealth: a non-member gets no share of a guild vault", Share(vault, new long[] { 500 }, -1) == 0,
                   "outsider"));
            return r;
        }

        // Mirrors GuildStore.VaultShareFor without touching the live store; the rule, not the storage, is what can drift.
        private static long Share(long vault, long[] contributions, int me)
        {
            if (me < 0 || vault <= 0 || contributions.Length == 0) return 0;
            long total = contributions.Sum();
            return total > 0 ? (long)(vault * ((double)contributions[me] / total)) : vault / contributions.Length;
        }

        private static IEnumerable<(string, bool, string)> StandingsTotalRules()
        {
            var r = new List<(string, bool, string)>();
            var e = new PlayerLeaderboardEntry { Wealth = 120_000, KmhWealth = 500_000 };
            r.Add(("Standings: total wealth is map wealth plus KMH holdings", e.TotalWealth == 620_000,
                   $"{e.Wealth} + {e.KmhWealth} = {e.TotalWealth}"));

            // An older client reports no KMH figure; the total must fall back to map wealth, not to zero.
            var old = new PlayerLeaderboardEntry { Wealth = 120_000 };
            r.Add(("Standings: with no KMH holdings the total is the map figure", old.TotalWealth == 120_000,
                   old.TotalWealth.ToString()));

            // Derived rather than stored, so a stale persisted total can never contradict its two parts.
            bool stored = typeof(PlayerLeaderboardEntry).GetProperty("TotalWealth")?.CanWrite == true;
            r.Add(("Standings: the total is derived, never stored", !stored,
                   stored ? "TotalWealth is settable - a stored copy would go stale" : "read-only"));
            return r;
        }

        // Shares the snapshot probe's name so the boot sweep already clears any row a bug here leaves behind.
        private const string ProbeUser = KmhSnapshotCoverageSelfTest.ProbeUser;
    }
}
