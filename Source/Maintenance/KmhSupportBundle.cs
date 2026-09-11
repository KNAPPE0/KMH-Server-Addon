using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Everything written passes through KmhRedact, because this archive is meant to be handed to a stranger.
    internal static class KmhSupportBundle
    {
        private const int MaxLogBytes = 512 * 1024;
        private const int MaxLogFiles = 3;
        private const int KeepBundles = 5;

        internal static string OutputDir => Path.Combine(KmhDataPaths.Folder, "Support");

        internal static bool Create(Action<string> reply)
        {
            try
            {
                PrimeKnownSecrets();

                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                Directory.CreateDirectory(OutputDir);
                string zipPath = Path.Combine(OutputDir, $"kmh-support-{stamp}.zip");

                using (FileStream fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    Write(zip, "README.txt", Readme());
                    Write(zip, "identity.txt", Identity());
                    Write(zip, "features.txt", FeatureState());
                    Write(zip, "economy.txt", EconomySummary());
                    AddConfigs(zip);
                    AddIfExists(zip, "status.json", KmhDataPaths.StatusFile);
                    Write(zip, "health.txt", Health());
                    AddIfExists(zip, "migration-latest.txt", KmhDataPaths.MigrationReportLatest);
                    AddLogs(zip);
                    AddKmhDiagnostics(zip);
                }

                PruneOldBundles(zipPath);

                long kb = new FileInfo(zipPath).Length / 1024;
                reply($"Support bundle written: {zipPath} ({kb} KB)");
                reply("Secrets are redacted. Review it before sharing if you like - it is plain text inside.");
                ServerLog.Info($"Support bundle created ({kb} KB).");
                return true;
            }
            catch (Exception ex)
            {
                reply($"Could not create the support bundle: {ex.Message}");
                ServerLog.Warn($"Support bundle failed: {ex.Message}");
                return false;
            }
        }

        // The questions a support reply always has to ask first: was it running, could it write, was it mid-reset.
        internal static string Health()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== KMH health ===");
            sb.AppendLine($"Readiness   : {KmhReadiness.Describe()}");
            sb.AppendLine($"Persistence : {Persistence.JsonFileStore.DescribeHealth()}");
            sb.AppendLine($"Maintenance : {KmhMaintenanceGate.Describe()}");
            sb.AppendLine($"Reset       : {(Features.Economy.KmhEconomyReset.InProgress ? "in progress (" + Features.Economy.KmhEconomyReset.ActivePhase + ")" : "idle")}");
            sb.AppendLine($"DataGen     : {Features.Economy.KmhEconomyReset.Generation}");
            sb.AppendLine($"Recovery    : {Features.Recovery.RecoveryStore.HeldCount} record(s) held");
            sb.AppendLine($"Diagnostics : {(Diagnostics.KmhLogSink.Started ? Path.GetFileName(Diagnostics.KmhLogSink.CurrentFile) : "sink not started")}"
                        + $", queued {Diagnostics.KmhLogSink.QueueDepth}, dropped {Diagnostics.KmhLogSink.DroppedCount}");
            sb.AppendLine($"GeneratedUtc: {DateTime.UtcNow:O}");
            return sb.ToString();
        }

        // Nothing else prunes these, and an owner reporting a series of problems would accumulate one per attempt.
        private static void PruneOldBundles(string keep)
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(OutputDir).GetFiles("kmh-support-*.zip"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = KeepBundles; i < files.Count; i++)
                    if (!string.Equals(files[i].FullName, keep, StringComparison.OrdinalIgnoreCase)) files[i].Delete();
            }
            catch (Exception ex) { ServerLog.Warn($"Support bundle: could not prune old bundles - {ex.Message}"); }
        }

        // Load the secrets this build knows so they can be scrubbed from log text too, not just from config keys.
        private static void PrimeKnownSecrets()
        {
            try { KmhRedact.RegisterSecret(Features.Discord.DiscordConfig.LoadOrDefault()?.BotToken); } catch { }
        }

        private static string Readme() =>
            "KMH support bundle\r\n\r\n"
          + "Diagnostic snapshot for reporting a KMH problem. Configs and logs here have been passed through KMH's\r\n"
          + "redaction: bot tokens, API tokens, webhooks, passwords and similar values are replaced with "
          + KmhRedact.Mask + ".\r\n\r\n"
          + "Not included: executables, backups, media cache, Workshop content, save data, full chat history.\r\n";

        private static string Identity()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"KMH build           : {KmhVersion.Build}");
            sb.AppendLine($"Protocol version    : {SubProtocol.KmhProtocol.CurrentVersion}");
            sb.AppendLine($"Config schema       : {KmhVersion.ConfigSchema}");
            sb.AppendLine($"Data schema         : {KmhVersion.DataSchema}");
            sb.AppendLine($"Capabilities        : {SubProtocol.KmhCapabilities.Manifest}");
            sb.AppendLine($"Server name         : {KmhServerIdentity.Name}");
            sb.AppendLine($"OS                  : {Environment.OSVersion}");
            sb.AppendLine($"64-bit process      : {Environment.Is64BitProcess}");
            sb.AppendLine($"Runtime             : {Environment.Version}");
            sb.AppendLine($"Processors          : {Environment.ProcessorCount}");
            try { sb.AppendLine($"Discord token source: {Features.Discord.DiscordTokenSource.Describe()}"); } catch { }
            return KmhRedact.Text(sb.ToString());
        }

        private static string FeatureState()
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("Features.json gates:");
                foreach (string d in Features.FeaturesConfig.Current.DisabledList()) sb.AppendLine($"  DISABLED {d}");
                sb.AppendLine("Subsystems:");
                sb.AppendLine($"  Frontier            : {Features.Frontier.FrontierConfig.Current.Enabled}");
                sb.AppendLine($"  Media resolver      : {Features.Media.MediaConfig.Current.ServerMediaResolverEnabled}");
                sb.AppendLine($"  Chat image previews : {Features.Chat.ChatConfig.Current.AllowImagePreviews}");
                sb.AppendLine($"  Staff badges        : {Features.Identity.StaffConfig.Current.ShowStaffBadges}");
                sb.AppendLine($"  Site archetypes     : {Features.Sites.SitesConfig.Current.ArchetypesEnabled}");
            }
            catch (Exception ex) { sb.AppendLine("  (unavailable: " + ex.Message + ")"); }
            return sb.ToString();
        }

        // The RESOLVED policy, because the granular fee/cooldown fields only apply under EconomyMode=Custom.
        private static string EconomySummary()
        {
            var sb = new StringBuilder();
            try
            {
                Features.Economy.EconomyPolicy p = Features.Economy.EconomyConfig.Current.ResolvePolicy();
                sb.AppendLine($"EconomyMode        : {p.Mode}");
                sb.AppendLine($"Deposit fee        : {p.DepositFeePct}%   cooldown {p.DepositCooldownSec}s");
                sb.AppendLine($"Withdraw fee       : {p.WithdrawFeePct}%   cooldown {p.WithdrawCooldownSec}s");
                sb.AppendLine($"Personal access    : {p.PersonalAccess}");
                sb.AppendLine($"Guild access       : {p.GuildAccess}");
                sb.AppendLine($"Blocked in raid    : {p.BlockDuringRaid}");
                sb.AppendLine($"Max silver / tx    : {Features.Economy.EconomyConfig.Current.MaxSilverDepositPerTx}");
                sb.AppendLine($"Max items / tx     : {Features.Economy.EconomyConfig.Current.MaxItemDepositQtyPerTx}");
            }
            catch (Exception ex) { sb.AppendLine("(unavailable: " + ex.Message + ")"); }
            return sb.ToString();
        }

        private static void AddConfigs(ZipArchive zip)
        {
            string dir = Path.Combine(KmhDataPaths.Folder, "Config");
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    string rel = "config/" + f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                    Write(zip, rel, KmhRedact.Json(File.ReadAllText(f)));
                }
                catch (Exception ex) { ServerLog.Warn($"Support bundle: skipped {Path.GetFileName(f)} - {ex.Message}"); }
            }
        }

        private static void AddLogs(ZipArchive zip)
        {
            try
            {
                string dir = Path.Combine(Master.MainPath ?? Directory.GetCurrentDirectory(), "Logs", "System");
                if (!Directory.Exists(dir)) return;

                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.txt"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

                for (int i = 0; i < files.Count && i < MaxLogFiles; i++)
                    Write(zip, "logs/" + files[i].Name, KmhRedact.Text(Tail(files[i].FullName, MaxLogBytes)));
            }
            catch (Exception ex) { ServerLog.Warn($"Support bundle: logs skipped - {ex.Message}"); }
        }

        // KMH's own diagnostics, which hold the routine detail the console never shows.
        private static void AddKmhDiagnostics(ZipArchive zip)
        {
            try
            {
                Diagnostics.KmhLogSink.FlushForTest();   // whatever is queued belongs in the bundle too
                string dir = KmhDataPaths.DebugDir;
                if (!Directory.Exists(dir)) return;

                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("KMH-*.log"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

                for (int i = 0; i < files.Count && i < MaxLogFiles; i++)
                    Write(zip, "kmh-diagnostics/" + files[i].Name, KmhRedact.Text(Tail(files[i].FullName, MaxLogBytes)));
            }
            catch (Exception ex) { ServerLog.Warn($"Support bundle: KMH diagnostics skipped - {ex.Message}"); }
        }

        private static string Tail(string path, int maxBytes)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
            using StreamReader sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        private static void AddIfExists(ZipArchive zip, string entry, string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string body = File.ReadAllText(path);
                Write(zip, entry, entry.EndsWith(".json", StringComparison.Ordinal) ? KmhRedact.Json(body) : KmhRedact.Text(body));
            }
            catch (Exception ex) { ServerLog.Warn($"Support bundle: skipped {entry} - {ex.Message}"); }
        }

        private static void Write(ZipArchive zip, string entry, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(entry, CompressionLevel.Optimal);
            using Stream s = e.Open();
            using StreamWriter w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(content ?? "");
        }
    }
}
