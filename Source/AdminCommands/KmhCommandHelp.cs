using System;
using System.Collections.Generic;

namespace KMHServerAddon.AdminCommands
{
    // One table behind both root and topic help, or the two drift apart the moment a command is added.
    internal static class KmhCommandHelp
    {
        internal sealed class Entry
        {
            public string Topic;
            public string Command;      // always the full invocation, so it can be copied straight out
            public string Args = "";
            public string Desc;
            public bool   AdminOnly = true;
            public bool   Featured;

            public string Invocation => string.IsNullOrEmpty(Args) ? Command : Command + " " + Args;
        }

        private static readonly (string Topic, string Title)[] Topics =
        {
            ("server",   "Server"),
            ("frontier", "Frontier"),
            ("world",    "World"),
            ("sites",    "Sites"),
            ("economy",  "Economy"),
            ("players",  "Players"),
            ("config",   "Config"),
            ("backup",   "Backup"),
            ("check",    "Checks"),
            ("fix",      "Fix stuck data"),
            ("advanced", "Advanced"),
        };

        private static readonly Entry[] All =
        {
            new Entry { Topic = "server", Command = "kmh status",     Desc = "Server and KMH health report", AdminOnly = false, Featured = true },
            new Entry { Topic = "server", Command = "kmh diag",       Desc = "Data-pipeline counts and file sizes" },
            new Entry { Topic = "server", Command = "kmh transport",  Desc = "KMH API transport status" },
            new Entry { Topic = "server", Command = "kmh extensions", Desc = "List loaded extensions" },
            new Entry { Topic = "server", Command = "kmh export",     Desc = "Write status.json for dashboards" },
            new Entry { Topic = "server", Command = "kmh save",       Desc = "Flush every store to disk now" },

            new Entry { Topic = "frontier", Command = "kmh frontier status", Desc = "Director state, budget and next action", Featured = true },
            new Entry { Topic = "frontier", Command = "kmh frontier list",   Desc = "Outposts and active operations" },
            new Entry { Topic = "frontier", Command = "kmh frontier place",  Args = "<tile>", Desc = "Establish an outpost on one tile" },
            new Entry { Topic = "frontier", Command = "kmh frontier tick",   Desc = "Run one Director pass now" },

            new Entry { Topic = "world", Command = "kmh event",      Args = "<type>", Desc = "Fire a world event", Featured = true },
            new Entry { Topic = "world", Command = "kmh worldquest", Args = "<sub>",  Desc = "Create, list or end a global quest" },
            new Entry { Topic = "world", Command = "kmh season",     Args = "<roll|reset>", Desc = "Archive leaders or wipe the season" },

            new Entry { Topic = "sites", Command = "kmh site-catalog",   Args = "[reset]", Desc = "What KMH thinks each item is", Featured = true },
            new Entry { Topic = "sites", Command = "kmh site-condition", Args = "<damage|repair> <tile> [points]", Desc = "Adjust a site's condition by hand" },
            new Entry { Topic = "sites", Command = "kmh roadworks",      Args = "<list|show|cancel>", Desc = "Inspect or cancel road projects" },

            new Entry { Topic = "economy", Command = "kmh inspect",       Args = "<subsystem>", Desc = "Dump live entries with their ids", Featured = true },
            new Entry { Topic = "economy", Command = "kmh ledger",        Args = "[user] [count]", Desc = "Recent economy audit trail" },
            new Entry { Topic = "economy", Command = "kmh history",       Args = "<domain>", Desc = "Past events for one domain" },
            new Entry { Topic = "economy", Command = "kmh catalog",       Args = "<def> [value|unpin]", Desc = "Pin an item's server value" },
            new Entry { Topic = "economy", Command = "kmh policy",        Args = "<set|reset>", Desc = "Tune economy policy" },
            new Entry { Topic = "economy", Command = "kmh contributions", Args = "<guild>", Desc = "Guild contribution breakdown" },
            new Entry { Topic = "economy", Command = "kmh drain-house",   Args = "<user>", Desc = "Move the house pool to a player" },

            new Entry { Topic = "players", Command = "kmh audit-player",   Args = "<user>", Desc = "Everything KMH knows about a player", Featured = true },
            new Entry { Topic = "players", Command = "kmh give-silver",    Args = "<user> <amount>", Desc = "Grant silver" },
            new Entry { Topic = "players", Command = "kmh treasury-reset", Args = "<user|all>", Desc = "Clear a personal treasury" },
            new Entry { Topic = "players", Command = "kmh reset-preview",  Args = "<user>", Desc = "Preview what a save-reset clears" },
            new Entry { Topic = "players", Command = "kmh wipe-economy",   Args = "<user> <dry|confirm>", Desc = "Clear a player's economy" },
            new Entry { Topic = "players", Command = "kmh wipe-player",    Args = "<user> <dry|confirm>", Desc = "Wipe a player entirely" },
            new Entry { Topic = "players", Command = "kmh purge",          Args = "<what> <user> <dry|confirm>", Desc = "Purge one player's listings or sites" },
            new Entry { Topic = "players", Command = "kmh reset-cleanup",  Args = "<user> <dry|confirm>", Desc = "Full rollback-abuse fix" },
            new Entry { Topic = "players", Command = "kmh remove-player-guilds", Args = "<user> <dry|confirm>", Desc = "Remove a player from every guild" },
            new Entry { Topic = "players", Command = "kmh unlink-player", Args = "<user> <dry|confirm>", Desc = "Unlink a player's Discord account" },

            new Entry { Topic = "config", Command = "kmh config",        Args = "[area] [all]", Desc = "Show settings changed from default", Featured = true },
            new Entry { Topic = "config", Command = "kmh config reload", Args = "<area>", Desc = "Reload one config area", Featured = true },
            new Entry { Topic = "config", Command = "kmh reload",        Args = "<area>", Desc = "Reload one config area" },
            new Entry { Topic = "config", Command = "kmh maintenance",   Desc = "Maintenance-mode gate" },
            new Entry { Topic = "config", Command = "kmh migration-report", Desc = "Last data-migration report" },
            new Entry { Topic = "config", Command = "kmh enforce",       Args = "<on|off|status|publish>", Desc = "Lock players' Mod Options" },

            new Entry { Topic = "backup", Command = "kmh backup",   Args = "[reason]", Desc = "Snapshot KMH-Data", Featured = true },
            new Entry { Topic = "backup", Command = "kmh backups",  Desc = "List snapshots" },
            new Entry { Topic = "backup", Command = "kmh restore",  Args = "<name|latest>", Desc = "Roll back on next restart" },
            new Entry { Topic = "backup", Command = "kmh snapshot-player", Args = "<user>", Desc = "Snapshot one player" },
            new Entry { Topic = "backup", Command = "kmh snapshot-server", Desc = "Snapshot the whole server" },
            new Entry { Topic = "backup", Command = "kmh snapshot-all",    Desc = "Snapshot every player and the server" },
            new Entry { Topic = "backup", Command = "kmh snapshot-verify", Args = "<folder>", Desc = "Checksum and validate a snapshot" },
            new Entry { Topic = "backup", Command = "kmh restore-preview-player", Args = "<folder>", Desc = "Preview what a player restore changes" },

            new Entry { Topic = "check", Command = "kmh verify",    Desc = "Read-only integrity scan", Featured = true },
            new Entry { Topic = "check", Command = "kmh smoketest", Desc = "Is this server healthy right now", Featured = true },
            new Entry { Topic = "check", Command = "kmh audit",     Desc = "Advisory anti-cheat scan" },
            new Entry { Topic = "check", Command = "kmh validate",  Args = "[area]", Desc = "Pre-flight configs and pools" },
            new Entry { Topic = "check", Command = "kmh transport-test", Desc = "API DoS-guard self-check" },

            new Entry { Topic = "fix", Command = "kmh recover",  Args = "<list|retry|refund|drop>", Desc = "Triage parked value", Featured = true },
            new Entry { Topic = "fix", Command = "kmh cancel",   Args = "<kind> <id>", Desc = "Refund and remove one entry" },
            new Entry { Topic = "fix", Command = "kmh repush",   Args = "[area]", Desc = "Re-push snapshots to clients" },
            new Entry { Topic = "fix", Command = "kmh reload-marketplace", Desc = "Reload Marketplace.json into the store" },
            new Entry { Topic = "fix", Command = "kmh rebuild-standings", Desc = "Reload and re-push standings" },
            new Entry { Topic = "fix", Command = "kmh rebuild-player",    Args = "<user>", Desc = "Rebuild one player's derived data" },

            new Entry { Topic = "advanced", Command = "kmh selftest",       Desc = "Does this build honour its contracts" },
            new Entry { Topic = "advanced", Command = "kmh support-bundle", Desc = "One shareable zip for a bug report" },
        };

        public static IEnumerable<Entry> ForTopic(string topic)
        {
            foreach (Entry e in All)
                if (string.Equals(e.Topic, topic, StringComparison.OrdinalIgnoreCase)) yield return e;
        }

        // Asked by the drift guard, which enumerates the dispatch switch and expects every case to be reachable here.
        public static bool Documents(string subcommand)
        {
            if (string.IsNullOrEmpty(subcommand)) return false;
            string want = "kmh " + subcommand;
            foreach (Entry e in All)
            {
                if (string.Equals(e.Command, want, StringComparison.OrdinalIgnoreCase)) return true;
                if (e.Command.StartsWith(want + " ", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool HasTopic(string topic)
        {
            foreach ((string t, string _) in Topics)
                if (string.Equals(t, topic, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void Root(Action<string> reply, bool isAdmin)
        {
            reply("KMH commands");
            foreach ((string topic, string title) in Topics)
            {
                List<Entry> shown = Visible(topic, isAdmin, featuredOnly: true);
                if (shown.Count == 0) continue;
                reply("");
                reply(title);
                foreach (Entry e in shown) reply(Line(e));
            }
            reply("");
            reply("Use: kmh help <topic>");
            reply("Topics: " + string.Join(", ", TopicNames(isAdmin)));
        }

        public static void Topic(string topic, Action<string> reply, bool isAdmin)
        {
            List<Entry> shown = Visible(topic, isAdmin, featuredOnly: false);
            if (shown.Count == 0) { reply($"No help topic '{topic}'. Use: kmh help"); return; }

            reply($"{TitleOf(topic)} commands");
            foreach (Entry e in shown) reply(Line(e));
            reply("");
            reply("Use: kmh help");
        }

        // Padded so the descriptions line up, but never so wide that a long invocation is truncated.
        private static string Line(Entry e)
        {
            string inv = e.Invocation;
            return inv.Length >= 34 ? $"  {inv}  {e.Desc}" : $"  {inv.PadRight(34)}{e.Desc}";
        }

        private static List<Entry> Visible(string topic, bool isAdmin, bool featuredOnly)
        {
            var outp = new List<Entry>();
            foreach (Entry e in ForTopic(topic))
            {
                if (e.AdminOnly && !isAdmin) continue;
                if (featuredOnly && !e.Featured) continue;
                outp.Add(e);
            }
            return outp;
        }

        private static List<string> TopicNames(bool isAdmin)
        {
            var outp = new List<string>();
            foreach ((string t, string _) in Topics)
                if (Visible(t, isAdmin, featuredOnly: false).Count > 0) outp.Add(t);
            return outp;
        }

        private static string TitleOf(string topic)
        {
            foreach ((string t, string title) in Topics)
                if (string.Equals(t, topic, StringComparison.OrdinalIgnoreCase)) return title;
            return topic;
        }
    }
}
