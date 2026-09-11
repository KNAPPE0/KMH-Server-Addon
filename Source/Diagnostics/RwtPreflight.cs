using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Diagnostics
{
    // RWT rewrites a nulled config back over itself each boot, so the crash loop never names the file that caused it.
    internal static class RwtPreflight
    {
        private const long LowDiskWarnBytes  = 512L * 1024 * 1024;
        private const long LowDiskErrorBytes =  64L * 1024 * 1024;
        private const long BigLogsWarnBytes  =   2L * 1024 * 1024 * 1024;

        private static string Root         => Master.MainPath ?? Directory.GetCurrentDirectory();
        private static string ConfigsDir   => Path.Combine(Root, "Configs");
        private static string LogsDir      => Path.Combine(Root, "Logs");
        private static string KnownGoodDir => Path.Combine(Persistence.KmhDataPaths.Folder, "RwtConfigBackup");

        internal enum ConfigState { Ok, Empty, Null, Unparseable }

        // Unparseable is kept distinct from Empty/Null because a half-written file may still hold the owner's values.
        internal static ConfigState Classify(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ConfigState.Empty;
            try
            {
                JToken token = JToken.Parse(text.Trim());
                return token == null || token.Type == JTokenType.Null ? ConfigState.Null : ConfigState.Ok;
            }
            catch { return ConfigState.Unparseable; }
        }

        internal static bool CarriesNoSettings(ConfigState state)
            => state == ConfigState.Empty || state == ConfigState.Null;

        internal static string Describe(ConfigState state)
        {
            switch (state)
            {
                case ConfigState.Empty:       return "empty (0 bytes)";
                case ConfigState.Null:        return "the single word \"null\"";
                case ConfigState.Unparseable: return "not valid JSON";
                default:                      return "fine";
            }
        }

        // RWT's startup failures name a type but never the file behind them, so the stack alone tells an owner nothing.
        public static string ExplainStartupFailure(Exception ex)
        {
            string s = ex?.ToString() ?? "";
            if (s.Contains("CheckIfShouldPrint"))
                return "one of RimWorld Together's config files loaded as nothing, so RWT's own logger hit a null "
                     + $"reference on the first line it tried to print. Look in {ConfigsDir} for a .json that is "
                     + "0 bytes or contains only \"null\".";
            if (s.Contains("SemaphoreFullException"))
                return "RimWorld Together could not parse one of its config files. Look in "
                     + $"{ConfigsDir} for a truncated or half-written .json.";
            return null;
        }

        public static void Run()
        {
            try { CheckDiskSpace(); }  catch (Exception ex) { ServerLog.Verbose($"Disk check skipped: {ex.Message}"); }
            try { CheckConfigs(); }    catch (Exception ex) { ServerLog.Verbose($"RWT config check skipped: {ex.Message}"); }
            try { CheckLogGrowth(); }  catch (Exception ex) { ServerLog.Verbose($"RWT log check skipped: {ex.Message}"); }
        }

        // A full disk is what truncates the configs in the first place, so catch it before the handoff.
        private static void CheckDiskSpace()
        {
            string root = Path.GetPathRoot(Path.GetFullPath(Root));
            if (string.IsNullOrEmpty(root)) return;
            DriveInfo drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            long free = drive.AvailableFreeSpace;
            if (free < LowDiskErrorBytes)
                ServerLog.Error($"Only {Bytes(free)} free on {drive.Name} - saves and configs WILL be corrupted on "
                              + "write. Free space before running the server.");
            else if (free < LowDiskWarnBytes)
                ServerLog.Warn($"Low disk space: {Bytes(free)} free on {drive.Name}. A full disk truncates RWT's "
                             + "config files, and RWT then fails to start on every restart.");
        }

        private static void CheckConfigs()
        {
            if (!Directory.Exists(ConfigsDir)) return;   // first boot: RWT creates them itself

            foreach (string path in Directory.GetFiles(ConfigsDir, "*.json"))
            {
                string name = Path.GetFileName(path);
                ConfigState state = Classify(SafeRead(path));

                if (state == ConfigState.Ok) { RememberGood(path); continue; }

                if (!CarriesNoSettings(state))
                {
                    ServerLog.Error($"RWT config {name} is {Describe(state)}. RimWorld Together will fail to start "
                                  + $"and keep failing until it is fixed. Repair or delete {path} - deleting makes "
                                  + "RWT write a fresh default.");
                    continue;
                }

                if (TryRestoreKnownGood(path))
                {
                    ServerLog.Warn($"RWT config {name} was {Describe(state)} - restored from KMH's last-good copy. "
                                 + "That is what a failed write leaves behind, so check free disk space.");
                    continue;
                }

                string moved = Quarantine(path);
                if (moved != null)
                    ServerLog.Warn($"RWT config {name} was {Describe(state)} and held no settings - moved it aside as "
                                 + $"{moved} so RWT regenerates a default. A failed write causes this; check disk space.");
                else
                    ServerLog.Error($"RWT config {name} is {Describe(state)} and RimWorld Together cannot start with "
                                  + $"it. Delete {path} and RWT will write a fresh default.");
            }
        }

        // RWT appends to its log with no pruning, so a restart loop is bounded only by the disk.
        private static void CheckLogGrowth()
        {
            if (!Directory.Exists(LogsDir)) return;
            long total = 0;
            foreach (string f in Directory.EnumerateFiles(LogsDir))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            if (total >= BigLogsWarnBytes)
                ServerLog.Warn($"RimWorld Together's Logs folder holds {Bytes(total)} ({LogsDir}). RWT never prunes it, "
                             + "so a crash loop can fill the disk - clear the old files.");
        }

        // Refreshed whenever a config parses, so there is something to restore from after a bad write.
        private static void RememberGood(string path)
        {
            try
            {
                Directory.CreateDirectory(KnownGoodDir);
                string dest = Path.Combine(KnownGoodDir, Path.GetFileName(path));
                if (File.Exists(dest) && File.ReadAllText(dest) == File.ReadAllText(path)) return;
                File.Copy(path, dest, overwrite: true);
            }
            catch { }
        }

        private static bool TryRestoreKnownGood(string path)
        {
            try
            {
                string src = Path.Combine(KnownGoodDir, Path.GetFileName(path));
                if (!File.Exists(src) || Classify(File.ReadAllText(src)) != ConfigState.Ok) return false;
                File.Copy(src, path, overwrite: true);
                return true;
            }
            catch { return false; }
        }

        // Only ever called for a file that carries no settings, so nothing is lost by moving it out of the way.
        private static string Quarantine(string path)
        {
            try
            {
                string dest = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(path, dest);
                return Path.GetFileName(dest);
            }
            catch { return null; }
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return null; }
        }

        internal static string Bytes(long n)
        {
            if (n >= 1L << 30) return $"{n / (double)(1L << 30):0.#} GB";
            if (n >= 1L << 20) return $"{n / (double)(1L << 20):0.#} MB";
            if (n >= 1L << 10) return $"{n / (double)(1L << 10):0.#} KB";
            return $"{n} bytes";
        }
    }
}
