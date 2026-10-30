using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Interest-gated world state leaves every other player's map stale, and an uncreated folder cannot be written to.
    internal static class KmhWorldStateCoverageSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            r.AddRange(DirectoryCoverage());
            r.AddRange(IntegrityExpectations());
            r.AddRange(DeliveryPaths());
            return r;
        }

        private static IEnumerable<(string, bool, string)> DirectoryCoverage()
        {
            var r = new List<(string, bool, string)>();
            var dirs = new HashSet<string>(KmhDataPaths.RequiredDirectories(), StringComparer.OrdinalIgnoreCase);

            var uncovered = new List<string>();
            foreach (KmhDataPaths.DataFile f in KmhDataPaths.KnownDataFiles)
            {
                string dir = Path.GetDirectoryName(f.Path);
                if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir)) uncovered.Add(f.Label);
            }
            r.Add(("Data: every known data file's folder is created at boot", uncovered.Count == 0,
                uncovered.Count == 0 ? $"{dirs.Count} folder(s) created"
                                     : "NO FOLDER CREATED FOR: " + string.Join(", ", uncovered)));

            // Non-vacuous: an empty catalog or an empty folder set would pass the check above trivially.
            r.Add(("Data: the folder scan finds a real catalog", KmhDataPaths.KnownDataFiles.Count >= 40 && dirs.Count >= 20,
                   $"{KmhDataPaths.KnownDataFiles.Count} file(s), {dirs.Count} folder(s)"));
            return r;
        }

        private static IEnumerable<(string, bool, string)> IntegrityExpectations()
        {
            var r = new List<(string, bool, string)>();
            var byLabel = KmhDataPaths.KnownDataFiles.ToDictionary(f => f.Label, f => f);

            // Absence of these MEANS something; creating them to reach a zero count would destroy that meaning.
            foreach (string label in new[] { "Migrations/Journal", "Discord/GuildRoles" })
                r.Add(($"Integrity: '{label}' absent is reported as normal, not as damage",
                       byLabel.TryGetValue(label, out KmhDataPaths.DataFile f) && f.AbsentIsNormal, label));

            // Policies is the exception: nothing generates it, so a server whose owner never wrote one has no file.
            var configsMarkedOptional = KmhDataPaths.KnownDataFiles
                .Where(f => f.Label.StartsWith("Config/", StringComparison.Ordinal) && f.AbsentIsNormal
                            && f.Label != "Config/Policies")
                .Select(f => f.Label).ToList();
            r.Add(("Integrity: no owner config counts as optional-absent", configsMarkedOptional.Count == 0,
                configsMarkedOptional.Count == 0 ? "every Config/ file is required"
                                                 : "WRONGLY OPTIONAL: " + string.Join(", ", configsMarkedOptional)));
            return r;
        }

        // Read from IL because which router method a handler calls cannot be observed without a live network.
        private static IEnumerable<(string, bool, string)> DeliveryPaths()
        {
            var r = new List<(string, bool, string)>();
            foreach ((string type, string method) in new[]
                     {
                         ("KMHServerAddon.Features.Sites.SiteHandler", "BroadcastSnapshot"),
                         ("KMHServerAddon.Features.Roadworks.RoadworksHandler", "BroadcastSnapshot"),
                     })
            {
                // One hop deep: the entry may hand off to a private batching sender; the rule is where the send goes.
                List<string> calls = CallsMadeBy(type, method);
                foreach (string callee in new List<string>(calls))
                    if (callee.EndsWith("Now", StringComparison.Ordinal)) calls.AddRange(CallsMadeBy(type, callee));
                bool reachesEveryone = calls.Contains("BroadcastToVerified");
                r.Add(($"World state: {type.Split('.').Last()}.{method} reaches every verified client",
                       reachesEveryone,
                       calls.Count == 0 ? "could not read the method body" : string.Join(", ", calls)));
            }
            return r;
        }

        private static List<string> CallsMadeBy(string typeName, string methodName)
            => KmhIlReader.CallsMadeBy(typeName, methodName, "Broadcast");
    }
}
