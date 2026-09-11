using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // A file missing from KnownDataFiles is never integrity-checked and is left out of server snapshots.
    internal static class KmhDataFileCoverageSelfTest
    {
        // Exempt because the scan JToken-parses whole documents, and these are markers or non-JSON reports.
        private static readonly Dictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MetaFile"]              = "the data-format stamp itself, read before the scan runs",
            ["StatusFile"]            = "liveness heartbeat, rewritten every ~60s",
            ["SnapshotRequestFile"]   = "one-line marker consumed on the sweep",
            ["MigrationReportLatest"] = "plain text report, not JSON",
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var results = new List<(string, bool, string)>();

            HashSet<string> known = new HashSet<string>(
                KmhDataPaths.KnownDataFiles.Select(f => f.Path ?? ""), StringComparer.OrdinalIgnoreCase);

            // By shape, not name: a "...File" convention missed MigrationReportLatest.
            PropertyInfo[] props = typeof(KmhDataPaths)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();

            // Separator-terminated, or the sibling KMH-Data-Backups folder matches as a prefix of KMH-Data.
            string folder;
            try
            {
                folder = Path.GetFullPath(KmhDataPaths.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            }
            catch (Exception ex) { results.Add(("Data files: resolve KMH-Data", false, ex.Message)); return results; }

            var uncovered = new List<string>();
            var considered = new List<string>();
            foreach (PropertyInfo p in props)
            {
                string path;
                try { path = (string)p.GetValue(null); } catch { continue; }
                if (string.IsNullOrEmpty(path)) continue;

                string full;
                try { full = Path.GetFullPath(path); } catch { continue; }
                if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;  // outside KMH-Data
                if (!Path.HasExtension(full)) continue;                                      // a directory, not a file

                considered.Add(p.Name);
                if (Exempt.ContainsKey(p.Name) || known.Contains(path)) continue;
                uncovered.Add(p.Name);
            }

            results.Add(("Data files: paths discovered", considered.Count > 0, $"{considered.Count} file path(s) under KMH-Data"));

            results.Add(("Data files: every KMH-Data file is scanned or exempt",
                uncovered.Count == 0,
                uncovered.Count == 0
                    ? $"{considered.Count} path(s), {KmhDataPaths.KnownDataFiles.Count} in catalog"
                    : "NOT scanned or exempt: " + string.Join(", ", uncovered)));

            // A stale exemption is its own drift: it must still name a path the sweep actually reaches.
            var staleExemptions = Exempt.Keys.Where(k => !considered.Contains(k)).ToList();
            results.Add(("Data files: no stale exemptions", staleExemptions.Count == 0,
                staleExemptions.Count == 0 ? $"{Exempt.Count} exemption(s) all live"
                                           : "gone: " + string.Join(", ", staleExemptions)));

            // Duplicate paths would make the scan report the same file twice and double-count a corruption.
            var dupes = KmhDataPaths.KnownDataFiles.GroupBy(f => f.Path ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            results.Add(("Data files: catalog has no duplicate paths", dupes.Count == 0,
                dupes.Count == 0 ? "unique" : string.Join(", ", dupes.Select(Path.GetFileName))));

            return results;
        }
    }
}
