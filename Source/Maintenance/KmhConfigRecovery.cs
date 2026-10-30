using System;
using System.Collections.Generic;
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

        public static void RecoverFromLatestBackup(string name, string activePath, object typeSample)
        {
            List<string> copies = BackupCopiesOf(activePath);
            int pick = NewestThatLoads(copies.ConvertAll(SafeRead), typeSample);
            string backupCopy = pick >= 0 ? copies[pick] : null;

            switch (Decide(currentValid: false, haveBackup: copies.Count > 0, backupValid: backupCopy != null))
            {
                case Action.RestoreFromBackup:
                    string kept = KeepBroken(activePath);
                    if (RestoreFile(activePath, backupCopy, typeSample, out string err))
                        ServerLog.Warn($"Config recovery: {name} did not load, so it was restored from backup "
                                     + $"{BackupNameOf(backupCopy)}."
                                     + (kept != null ? $" Your broken file is kept beside it as {Path.GetFileName(kept)}." : ""));
                    else
                        ServerLog.Error($"Config recovery: {name} did not load and the backup restore failed ({err}). It loads safe defaults; fix it by hand.");
                    break;
                default:
                    ServerLog.Error($"Config recovery: {name} did not load and no backup has a copy that does. It runs on safe defaults "
                                  + "and the file is left as it is - fix it, or restore it from KMH-Data-Backups.");
                    break;
            }
        }

        // Newest first; the first copy that loads wins. This boot's own backup is the newest and holds the same broken file.
        internal static int NewestThatLoads(IList<string> jsonNewestFirst, object typeSample)
        {
            for (int i = 0; i < (jsonNewestFirst?.Count ?? 0); i++)
                if (!string.IsNullOrWhiteSpace(jsonNewestFirst[i]) && KmhConfigValidation.LoadTest(jsonNewestFirst[i], typeSample, out _)) return i;
            return -1;
        }

        // A copy of what the owner wrote, so a restore never silently throws away their edits.
        private static string KeepBroken(string activePath)
        {
            try
            {
                if (!File.Exists(activePath)) return null;
                string kept = activePath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(activePath, kept, overwrite: true);
                return kept;
            }
            catch { return null; }
        }

        private static string BackupNameOf(string copyPath)
        {
            string root = Path.GetFullPath(Persistence.KmhDataPaths.BackupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(copyPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(full);
            string rest = full.Substring(root.Length);
            int cut = rest.IndexOf(Path.DirectorySeparatorChar);
            return cut > 0 ? rest.Substring(0, cut) : rest;
        }

        private static List<string> BackupCopiesOf(string activePath)
        {
            var copies = new List<string>();
            try
            {
                var backups = Persistence.KmhDataBackup.List();
                if (backups == null) return copies;
                string rel = GetRelative(Persistence.KmhDataPaths.Folder, activePath);
                foreach (var b in backups)   // List() is newest-first
                {
                    string candidate = Path.Combine(Persistence.KmhDataPaths.BackupRoot, b.Name, rel);
                    if (File.Exists(candidate)) copies.Add(candidate);
                }
            }
            catch { }
            return copies;
        }

        internal static string GetRelative(string root, string full)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(full);
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f.Substring(r.Length) : Path.GetFileName(full);
        }

        private static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return ""; } }
    }
}
