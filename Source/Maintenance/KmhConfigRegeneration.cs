using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Tunable configs are replaced with current defaults, not migrated field by field, so every server shares one baseline.
    internal static class KmhConfigRegeneration
    {
        // Identity and credentials, never "settings". Regenerating Global orphans the directory registration and
        // re-registers as a new server; Staff locks the owner out of in-game settings; Discord discards a token.
        private static readonly string[] Kept = { "Global", "Staff", "Discord" };

        internal static bool IsKept(string name) => Array.IndexOf(Kept, name) >= 0;

        // A regenerated file an owner cannot look up is a setting reset with no explanation.
        internal static bool Describes(string name)
        {
            foreach ((string n, string _) in Descriptions)
                if (string.Equals(n, name, StringComparison.Ordinal)) return true;
            return false;
        }

        public static readonly List<string> Regenerated = new List<string>();
        public static readonly List<string> Preserved   = new List<string>();

        private static Func<string, string> _readBack;

        // A write that reads back differently is a real storage failure, but a working disk cannot produce one on demand.
        internal static IDisposable ReadBackForTest(Func<string, string> reader)
        {
            _readBack = reader;
            return new RestoreReadBack();
        }

        private sealed class RestoreReadBack : IDisposable { public void Dispose() { _readBack = null; } }

        private static string ReadBack(string path) => _readBack?.Invoke(path) ?? File.ReadAllText(path);

        public static (int Changed, string Failure) RegenerateAll(List<string> report)
        {
            Regenerated.Clear();
            Preserved.Clear();
            var failed = new List<string>();
            int expected = 0;

            foreach ((string name, string path, object sample) in KmhConfigValidation.ConfigTargets())
            {
                if (IsKept(name)) { Preserved.Add(name); continue; }
                expected++;
                try
                {
                    // Enforcement's default file carries its _readme, which the bare sample lacks.
                    object toWrite = name == "Enforcement" ? Features.Enforcement.EnforcementConfig.BuildDefaultFile() : sample;
                    if (!JsonFileStore.Save(path, toWrite))
                    {
                        failed.Add($"{name} (write refused - check free space and permissions)");
                        continue;
                    }
                    // Read back from disk: a partly rewritten set stamped as the new schema never migrates again.
                    if (!KmhConfigValidation.LoadTest(ReadBack(path), sample, out string err))
                    {
                        failed.Add($"{name} ({err})");
                        continue;
                    }
                    Regenerated.Add(name);
                }
                catch (Exception ex) { failed.Add($"{name} ({ex.GetType().Name}: {ex.Message})"); }
            }

            WriteReadme();
            report.Add($"regenerated and verified {Regenerated.Count}/{expected} config(s) from defaults; kept {Preserved.Count}");
            return failed.Count == 0
                ? (Regenerated.Count, null)
                : (Regenerated.Count, $"{failed.Count} of {expected} config(s) did not survive a write-and-read-back: "
                                    + string.Join(", ", failed));
        }

        // Warn level: an owner who tuned these should learn it from the console, not from a player.
        public static void Announce()
        {
            if (Regenerated.Count == 0) return;
            ServerLog.Warn("Config: v1.3.1 REGENERATED every tunable config from the current defaults. Your previous "
                         + "files are in the pre-migration backup under KMH-Data-Backups.");
            ServerLog.Warn($"Config: regenerated - {string.Join(", ", Regenerated.ToArray())}");
            ServerLog.Warn($"Config: kept untouched (identity and credentials) - {string.Join(", ", Preserved.ToArray())}");
            ServerLog.Warn($"Config: what each file does is written to {KmhDataPaths.ConfigReadmeFile}. "
                         + "Economy runs on EconomyMode=Balanced unless you change it.");
        }

        // Rewritten only when this build's text differs, so an unchanged file is never touched.
        public static void RefreshReadme()
        {
            try
            {
                string now = File.Exists(KmhDataPaths.ConfigReadmeFile) ? File.ReadAllText(KmhDataPaths.ConfigReadmeFile) : null;
                if (now != ReadmeText()) WriteReadme();
            }
            catch (Exception ex) { ServerLog.Warn($"Config: could not refresh the config README ({ex.GetType().Name})."); }
        }

        private static void WriteReadme()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KmhDataPaths.ConfigReadmeFile));
                File.WriteAllText(KmhDataPaths.ConfigReadmeFile, ReadmeText());
            }
            catch (Exception ex) { ServerLog.Warn($"Config: could not write the config README ({ex.GetType().Name})."); }
        }

        private static bool HasPreMigrationBackup()
        {
            try { return Directory.Exists(KmhDataPaths.BackupRoot) && Directory.GetDirectories(KmhDataPaths.BackupRoot, "*premigration-config*").Length > 0; }
            catch { return false; }
        }

        internal static string ReadmeText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Colony Nexus configuration - what each file is for");
            sb.AppendLine("==================================================");
            sb.AppendLine();
            sb.AppendLine("Every file here is plain JSON. Edit it and run 'kmh reload <area>' to apply it without a");
            sb.AppendLine("restart, or edit it in game from Advanced Tools > Server Settings (owner only).");
            sb.AppendLine("Run 'kmh config' to see every setting and its current value.");
            sb.AppendLine();
            // Only a server that was upgraded has earlier files to point at; a new one never did.
            if (HasPreMigrationBackup())
            {
                sb.AppendLine("v1.3.1 regenerated these files from defaults rather than upgrading them field by field.");
                sb.AppendLine("Your previous files are kept in KMH-Data-Backups under a 'premigration-config' folder.");
                sb.AppendLine();
            }

            foreach ((string name, string what) in Descriptions)
                sb.AppendLine($"{name,-12} {what}");

            sb.AppendLine();
            sb.AppendLine("Economy modes");
            sb.AppendLine("-------------");
            sb.AppendLine("Balanced   (default) Deposit and withdraw anywhere, 1% fee each way, 5s cooldowns.");
            sb.AppendLine("Localized  Deposits at your colony, guild withdrawals need a guild hall. No fees.");
            sb.AppendLine("Hardcore   Caravan-only access, 2% fees, blocked during raids and hostile events.");
            sb.AppendLine("Standard   The older, looser ruleset kept for servers that preferred it.");
            sb.AppendLine("Custom     The \"Custom\" block in Economy.json is read verbatim; presets are ignored.");
            sb.AppendLine();
            sb.AppendLine("Everything under Economy.json > Custom applies ONLY when EconomyMode is Custom. Two");
            sb.AppendLine("settings are NOT in that block and apply in every mode, because they are switches rather");
            sb.AppendLine("than tuning: DisablePersonalTreasury / DisableGuildTreasury turn a treasury off outright,");
            sb.AppendLine("and Treasury.AccessFeeSilver prices opening a vault.");
            sb.AppendLine();
            sb.AppendLine("Identity and credentials are never regenerated: Global.json, Staff.json and");
            sb.AppendLine("Discord/DiscordConfig.json keep whatever they already held.");
            sb.AppendLine();
            sb.AppendLine("Run 'kmh doctor' after editing: it names any setting Colony Nexus does not recognise (usually a typo,");
            sb.AppendLine("which is otherwise ignored silently) and any value it had to correct.");
            return sb.ToString();
        }

        private static readonly (string Name, string What)[] Descriptions =
        {
            ("Economy",     "Treasury access, fees and cooldowns. Start with EconomyMode; see below."),
            ("Features",    "Master on/off switches for whole systems (chat, mail, treasury, sites...)."),
            ("Chat",        "In-game chat: image previews, allowed image hosts, size caps."),
            ("Media",       "Server-side image/video conversion for clients that cannot decode a format."),
            ("Mail",        "Player-to-player mail, attachments and the optional postage fee."),
            ("Federation",  "Guild Federation on this server: local bonus ceilings, objective rewards, activity reports."),
            ("Mods",        "RimWorldVersion (players on any other version are refused: 1.6 by default, set 1.7 for a 1.7 server), and which mods are required or optional."),
            ("Sites",       "Off-map sites: what can be built, what they cost and what they produce."),
            ("Frontier",    "Frontier expansion: claim rules, site caps and budgets."),
            ("Quests",      "The player-posted quest board: how many, how long, bounty limits."),
            ("Reputation",  "Player standing and how it is earned."),
            ("World",       "The world engine: server-run events and server-generated world quests."),
            ("Enforcement", "Config Enforcement: locks joining players' mod settings to this server's profile."),
            ("Transport",   "Listening port and bind address for the Colony Nexus API (both need a restart); limits and timeouts are in Advanced."),
            ("Maintenance", "Backups, log verbosity and the persistence-failure freeze."),
            ("Global",      "Colony Nexus Global: directory settings and the Listing block players see. What Global assigned is in Identity/global-state.json. NOT regenerated."),
            ("Staff",       "Owners, admins and moderators, and each role's badge under Badges. NOT regenerated."),
            ("Discord",     "Discord bridge and bot token. NOT regenerated."),
        };
    }
}
