using System;
using System.Collections.Generic;
using System.Reflection;

namespace KMHServerAddon.Maintenance
{
    // A store absent from FlushAll is skipped exactly when an owner is persisting before a backup or shutdown.
    internal static class KmhFlushCoverageSelfTest
    {
        private static readonly string[] Exempt = { "WeatherDefCache" };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            HashSet<Type> covered = KmhDataFlush.CoveredTypes();
            var missing = new List<string>();
            int persisters = 0;

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (Type t in typeof(KmhDataFlush).Assembly.GetTypes())
            {
                if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue;          // static classes only
                if (t.GetMethod("SaveToDisk", Flags, null, Type.EmptyTypes, null) == null) continue;
                if (t.GetMethod("LoadFromDisk", Flags, null, Type.EmptyTypes, null) == null) continue;
                persisters++;
                if (covered.Contains(t)) continue;
                if (Array.IndexOf(Exempt, t.Name) >= 0) continue;
                missing.Add(t.Name);
            }

            r.Add(("Flush coverage: every persisted store is force-flushed", missing.Count == 0,
                missing.Count == 0 ? $"{persisters} store(s)" : $"NOT FLUSHED: {string.Join(", ", missing)}"));
            return r;
        }
    }
}
