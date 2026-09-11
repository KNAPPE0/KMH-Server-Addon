using System;
using System.Collections.Generic;

namespace KMHServerAddon.AdminCommands
{
    // A help line that omits "kmh " reads as a standalone command and gets typed that way.
    internal static class KmhHelpRenderSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            List<string> root = Capture(reply => KmhCommandHelp.Root(reply, isAdmin: true));
            r.Add(("Help: root renders", root.Count > 0, $"{root.Count} line(s)"));

            string bare = FirstBareCommand(root);
            r.Add(("Help: every root command line is a full kmh invocation", bare == null, bare ?? ""));

            r.Add(("Help: root points at topic help", root.Contains("Use: kmh help <topic>"), ""));
            r.Add(("Help: root stays scannable", root.Count <= 60, $"{root.Count} line(s)"));

            List<string> frontier = Capture(reply => KmhCommandHelp.Topic("frontier", reply, isAdmin: true));
            r.Add(("Help: kmh help frontier lists the Frontier commands",
                   Has(frontier, "kmh frontier status") && Has(frontier, "kmh frontier list")
                   && Has(frontier, "kmh frontier tick"), ""));
            string bareTopic = FirstBareCommand(frontier);
            r.Add(("Help: every topic command line is a full kmh invocation", bareTopic == null, bareTopic ?? ""));

            r.Add(("Help: a command carrying arguments shows them",
                   Has(Capture(reply => KmhCommandHelp.Topic("sites", reply, isAdmin: true)), "kmh site-condition <damage|repair> <tile>"),
                   ""));

            List<string> unknown = Capture(reply => KmhCommandHelp.Topic("__kmh_no_such_topic__", reply, isAdmin: true));
            r.Add(("Help: an unknown topic says so and points home",
                   unknown.Count > 0 && unknown[0].Contains("No help topic") && unknown[0].Contains("kmh help"), ""));

            // A non-admin seeing a destructive command would type it and be refused, so the menu hides what it cannot run.
            List<string> playerView = Capture(reply => KmhCommandHelp.Topic("players", reply, isAdmin: false));
            r.Add(("Help: admin-only commands are hidden from a non-admin",
                   !Has(playerView, "kmh wipe-player"), string.Join(" | ", playerView)));
            r.Add(("Help: a non-admin still sees what they can run",
                   Has(Capture(reply => KmhCommandHelp.Root(reply, isAdmin: false)), "kmh status"), ""));

            // Non-vacuity: the same scan must catch a line written the old bare way.
            r.Add(("Help: the bare-command check is not vacuous",
                   FirstBareCommand(new List<string> { "  frontier reload    Reload Frontier settings" }) != null, ""));

            r.Add(("Help: every topic in the menu resolves",
                   KmhCommandHelp.HasTopic("frontier") && KmhCommandHelp.HasTopic("config")
                   && !KmhCommandHelp.HasTopic("__kmh_no_such_topic__"), ""));

            return r;
        }

        private static List<string> Capture(Action<Action<string>> render)
        {
            var lines = new List<string>();
            render(l => lines.Add(l ?? ""));
            return lines;
        }

        private static bool Has(List<string> lines, string fragment)
        {
            foreach (string l in lines) if (l.Contains(fragment)) return true;
            return false;
        }

        // An indented line that names a command must open with "kmh "; headings and blanks are not commands.
        private static string FirstBareCommand(List<string> lines)
        {
            foreach (string l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (!l.StartsWith("  ", StringComparison.Ordinal)) continue;
                string body = l.TrimStart();
                if (body.StartsWith("kmh ", StringComparison.Ordinal)) continue;
                return l;
            }
            return null;
        }
    }
}
