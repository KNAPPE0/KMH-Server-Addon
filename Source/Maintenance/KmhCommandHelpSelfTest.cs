using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace KMHServerAddon.Maintenance
{
    // The dispatch switch and the help table are two hand-maintained lists of the same thing, so they drift.
    internal static class KmhCommandHelpSelfTest
    {
        // Kept working for owners with them in a script, but deliberately absent from help, which shows one name each.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "migrationreport",      "hyphen-less form of migration-report" },
            { "transporttest",        "hyphen-less form of transport-test" },
            { "reset-player-economy", "former name of wipe-economy" },
            { "reload-discord",       "per-area form of `reload discord`" },
            { "reload-world",         "per-area form of `reload world`" },
            { "reload-features",      "per-area form of `reload features`" },
            { "reload-economy",       "per-area form of `reload economy`" },
            { "wq",                   "short form of worldquest" },
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            string src = ReadSource(Path.Combine("AdminCommands", "KmhServerCommands.cs"));
            if (src == null)
            {
                r.Add(("commands: the source is readable", true, "not asked - no source tree beside this build"));
                return r;
            }

            int dispatch = src.IndexOf("public static void Dispatch(", StringComparison.Ordinal);
            int end      = dispatch < 0 ? -1 : src.IndexOf("default:", dispatch, StringComparison.Ordinal);
            if (dispatch < 0 || end < 0)
            {
                r.Add(("commands: the dispatch switch is found", false, $"dispatch@{dispatch} end@{end}"));
                return r;
            }

            var commands = new List<string>();
            foreach (Match m in Regex.Matches(src.Substring(dispatch, end - dispatch), "case\\s+\"([a-z0-9\\-]+)\""))
                commands.Add(m.Groups[1].Value);
            r.Add(("commands: the dispatch switch was parsed", commands.Count > 20, commands.Count + " command(s)"));

            // Asked of the live help table rather than the source text, so the two cannot agree only by coincidence.
            var undocumented = new List<string>();
            foreach (string c in commands)
                if (c != "help" && !Aliases.ContainsKey(c) && !AdminCommands.KmhCommandHelp.Documents(c)) undocumented.Add(c);

            r.Add(("commands: every dispatched command appears in `kmh help`", undocumented.Count == 0,
                   undocumented.Count == 0 ? "" : "missing: " + string.Join(", ", undocumented)));

            var deadAliases = new List<string>();
            foreach (KeyValuePair<string, string> a in Aliases)
                if (!commands.Contains(a.Key)) deadAliases.Add(a.Key);
            r.Add(("commands: every documented alias is still dispatched", deadAliases.Count == 0,
                   deadAliases.Count == 0 ? Aliases.Count + " alias(es)" : "gone: " + string.Join(", ", deadAliases)));

            return r;
        }

        private static string ReadSource(string relative)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    string candidate = Path.Combine(dir, "Source", relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }
    }
}
