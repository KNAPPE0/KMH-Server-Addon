using System;
using System.IO;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Never regenerates defaults over a corrupt config: that would wipe owner settings still recoverable by hand.
    internal static class KmhConfigRecovery
    {
        internal enum Action { None, RestoreFromBackup, WarnManual }

        // Pure decision so it can be unit-tested apart from any file I/O.
        public static Action Decide(bool currentValid, bool haveBackup, bool backupValid)
        {
            if (currentValid) return Action.None;
            if (haveBackup && backupValid) return Action.RestoreFromBackup;
            return Action.WarnManual;
        }

        // The backup is load-tested first, so a recovery can never swap one bad file for another.
        public static bool RestoreFile(string activePath, string backupPath, object typeSample, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(backupPath)) { error = "no backup copy"; return false; }
                string backupJson = File.ReadAllText(backupPath);
                if (!KmhConfigValidation.LoadTest(backupJson, typeSample, out error)) return false;

                Directory.CreateDirectory(Path.GetDirectoryName(activePath));
                string tmp = activePath + ".restore.tmp";
                File.WriteAllText(tmp, backupJson);
                File.Move(tmp, activePath, overwrite: true);   // atomic swap - active file is never half-written
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Attempts recovery for a single failed config from its copy inside the latest boot backup. Logs what it did.
        public static void RecoverFromLatestBackup(string name, string activePath, object typeSample)
        {
            string backupCopy = LatestBackupCopyOf(activePath);
            bool haveBackup   = backupCopy != null && File.Exists(backupCopy);
            bool backupValid  = haveBackup
                && KmhConfigValidation.LoadTest(SafeRead(backupCopy), typeSample, out _);

            switch (Decide(currentValid: false, haveBackup, backupValid))
            {
                case Action.RestoreFromBackup:
                    if (RestoreFile(activePath, backupCopy, typeSample, out string err))
                        ServerLog.Info($"Config recovery: restored {name} from the boot backup (its migrated copy failed the load-test).");
                    else
                        ServerLog.Error($"Config recovery: {name} failed load-test and the backup restore failed ({err}). It loads safe defaults; fix it by hand.");
                    break;
                default:
                    ServerLog.Error($"Config recovery: {name} failed load-test and no clean backup exists. It loads safe defaults; restore from KMH-Data-Backups if the values mattered.");
                    break;
            }
        }

        // The same config's path inside the newest boot backup directory, or null if none.
        private static string LatestBackupCopyOf(string activePath)
        {
            try
            {
                var backups = Persistence.KmhDataBackup.List();
                if (backups == null || backups.Count == 0) return null;
                string rel = GetRelative(Persistence.KmhDataPaths.Folder, activePath);
                foreach (var b in backups)   // List() is newest-first
                {
                    string candidate = Path.Combine(Persistence.KmhDataPaths.BackupRoot, b.Name, rel);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        private static string GetRelative(string root, string full)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(full);
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f.Substring(r.Length) : Path.GetFileName(full);
        }

        private static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return ""; } }
    }
}
