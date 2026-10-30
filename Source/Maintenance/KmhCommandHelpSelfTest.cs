using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            Routes(r);

            string src = ReadSource(Path.Combine("AdminCommands", "KmhServerCommands.cs"));
            if (src == null)
            {
                r.Add(Diagnostics.KmhSelfTestResult.Skipped("commands: the source is readable", "no source tree beside this build"));
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

            // The first word can match while help lists only some subcommands, so subcommands are compared both ways too.
            foreach ((string cmd, string file) in Dispatchers)
            {
                string name = $"commands: `kmh {cmd}` help names exactly the subcommands its dispatcher handles";
                string code = ReadSource(Path.Combine("AdminCommands", file));
                if (code == null) { r.Add(Diagnostics.KmhSelfTestResult.Skipped(name, "no source tree beside this build")); continue; }
                List<string> handled = SubcommandCases(code);
                List<string> listed = AdminCommands.KmhCommandHelp.SubcommandsOf(cmd);
                List<string> unlisted = handled.FindAll(s => !listed.Contains(s));
                List<string> phantom = listed.FindAll(s => !handled.Contains(s));
                r.Add((name, handled.Count > 0 && listed.Count > 0 && unlisted.Count == 0 && phantom.Count == 0,
                       $"handled [{string.Join(",", handled)}] listed [{string.Join(",", listed)}]"
                       + (unlisted.Count > 0 ? " - missing from help: " + string.Join(",", unlisted) : "")
                       + (phantom.Count > 0 ? " - in help but not handled: " + string.Join(",", phantom) : "")));
            }

            return r;
        }

        private static readonly (string Command, string File)[] Dispatchers =
        {
            ("global",     "KmhGlobalCommands.cs"),
            ("federation", "KmhFederationCommands.cs"),
            ("recover",    "KmhRecoveryCommands.cs"),
            ("reports",    "KmhReportCommands.cs"),
            ("guild",      "KmhGuildCommands.cs"),
            ("sites",      "KmhSitesCommands.cs"),
        };

        // Also via `kmh inspect`: re-reading its own subsystem word as the subcommand makes repair and inspect unreachable.
        private static void Routes(List<(string Name, bool Ok, string Detail)> r)
        {
            string owner = "selftest_cmd_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string guild = "selftest cmd " + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why))
                { r.Add(("commands: guild and sites routes reach every branch", false, "guild: " + why)); return; }
                string Said(params string[] args)
                {
                    var lines = new List<string>();
                    AdminCommands.KmhServerCommands.Dispatch(args, true, "selftest", lines.Add);
                    return string.Join(" | ", lines);
                }
                var got = new List<(string Form, string Want, string Said)>
                {
                    ("kmh guild inspect",          $"=== Guild '{guild}' ===", Said(new[] { "guild", "inspect" }.Concat(guild.Split(' ')).ToArray())),
                    ("kmh inspect guild inspect",  $"=== Guild '{guild}' ===", Said(new[] { "inspect", "guild", "inspect" }.Concat(guild.Split(' ')).ToArray())),
                    ("kmh guild repair",           "Guild membership",         Said("guild", "repair")),
                    ("kmh inspect guilds repair",  "Guild membership",         Said("inspect", "guilds", "repair")),
                    ("kmh guild",                  "=== Guilds ===",           Said("guild")),
                    ("kmh sites inspect",          $"Sites involving '{owner}'", Said("sites", "inspect", owner)),
                    ("kmh inspect sites inspect",  $"Sites involving '{owner}'", Said("inspect", "sites", "inspect", owner)),
                    ("kmh sites",                  "=== Sites (",              Said("sites")),
                };
                var missed = got.FindAll(g => g.Said.IndexOf(g.Want, StringComparison.Ordinal) < 0);
                r.Add(("commands: guild and sites routes reach every branch, at the top level and through `kmh inspect`", missed.Count == 0,
                       missed.Count == 0 ? $"{got.Count} form(s)" : string.Join("; ", missed.ConvertAll(m => $"{m.Form} said '{m.Said}'"))));

                // Both forms `kmh help` lists for one reload; the featured one must not be read as a config area name.
                string viaConfig = Said("config", "reload", "economy"), direct = Said("reload", "economy");
                r.Add(("commands: `kmh config reload <area>` does what `kmh reload <area>` does",
                       viaConfig.Length > 0 && viaConfig == direct && viaConfig.IndexOf("No config area", StringComparison.Ordinal) < 0,
                       $"config reload said '{viaConfig}'; reload said '{direct}'"));
            }
            finally { Features.Guilds.GuildStore.Leave(owner, out _); }
        }

        // The case labels of the first switch in Run, up to its default.
        internal static List<string> SubcommandCases(string code)
        {
            var outp = new List<string>();
            int run = code.IndexOf("public static void Run(", StringComparison.Ordinal);
            int sw  = run < 0 ? -1 : code.IndexOf("switch (", run, StringComparison.Ordinal);
            int end = sw < 0 ? -1 : code.IndexOf("default:", sw, StringComparison.Ordinal);
            if (end < 0) return outp;
            foreach (Match m in Regex.Matches(code.Substring(sw, end - sw), "case\\s+\"([a-z0-9\\-]+)\"\\s*:"))
                if (!outp.Contains(m.Groups[1].Value)) outp.Add(m.Groups[1].Value);
            return outp;
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
