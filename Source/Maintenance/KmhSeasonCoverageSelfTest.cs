using System;
using System.Collections.Generic;
using System.Reflection;

namespace KMHServerAddon.Maintenance
{
    // A store defining ClearForNewSeason but never invoked carries last season's value into the new one.
    internal static class KmhSeasonCoverageSelfTest
    {
        // Treasury is wiped by ClearAllVaults instead, and must run LAST so a concurrent settlement can't re-credit it.
        private static readonly string[] Exempt = { "TreasuryStore" };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var covered = new HashSet<Type>();
            foreach (Action a in KmhSeasonReset.SeasonClearers())
                if (a?.Method?.DeclaringType != null) covered.Add(a.Method.DeclaringType);

            var missing = new List<string>();
            int total = 0;
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (Type t in typeof(KmhSeasonReset).Assembly.GetTypes())
            {
                if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue;                        // static classes
                if (t.GetMethod("ClearForNewSeason", Flags, null, Type.EmptyTypes, null) == null) continue;
                total++;
                if (covered.Contains(t) || Array.IndexOf(Exempt, t.Name) >= 0) continue;
                missing.Add(t.Name);
            }

            r.Add(("Season reset: every ClearForNewSeason store is wiped", missing.Count == 0,
                missing.Count == 0 ? $"{total} store(s)" : $"NOT WIPED: {string.Join(", ", missing)}"));

            // Only reset clears standings; wiring them into roll would make every "cumulative" label a lie.
            r.Add(("Season: a full reset clears standings, and only a full reset does",
                   covered.Contains(typeof(Features.PlayerStats.PlayerStatsStore)),
                   "standings must be in the reset clearers - roll only archives and advances"));

            MethodInfo roll = typeof(Features.Seasons.SeasonStore)
                .GetMethod("RollSeason", BindingFlags.Public | BindingFlags.Static);
            r.Add(("Season: rolling a season is a distinct operation from wiping the economy",
                   roll != null && typeof(KmhSeasonReset).GetMethod("Run", Flags) != null,
                   "roll archives + advances; reset backs up, rolls, then wipes"));
            return r;
        }
    }
}
