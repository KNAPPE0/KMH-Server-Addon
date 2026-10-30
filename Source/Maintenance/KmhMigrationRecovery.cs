using System;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Runs before the new migration and before any economy system initializes, or a mixed config set reaches them.
    internal static class KmhMigrationRecovery
    {
        public static void RecoverIfIncomplete()
        {
            KmhMigrationJournal journal = KmhMigrationJournal.Read();
            if (!KmhMigrationJournal.NeedsRecovery(journal)) { KmhMigrationJournal.Clear(); return; }

            string backupRoot = string.IsNullOrEmpty(journal.BackupDirName)
                ? null : Path.Combine(KmhDataPaths.BackupRoot, journal.BackupDirName);

            if (backupRoot == null || !Directory.Exists(backupRoot))
            {
                ServerLog.Error($"Migration recovery: previous migration ({journal.Id}) did not commit, but its backup " +
                                $"'{journal.BackupDirName}' is missing. Configs left as-is; review KMH-Data-Backups.");
                KmhMigrationJournal.Clear();
                return;
            }

            int restored = RestoreConfigsFrom(backupRoot);
            ServerLog.Warn($"Migration recovery: previous migration ({journal.Id}) was interrupted - rolled {restored} config(s) " +
                           $"back to the pre-migration backup '{journal.BackupDirName}'.");
            KmhMigrationJournal.Clear();
        }

        // Only a backup that load-tests clean is restored, so recovery never swaps one bad file for another.
        private static int RestoreConfigsFrom(string backupRoot)
        {
            int n = 0;
            foreach ((string name, string activePath, object sample) in KmhConfigValidation.ConfigTargets())
            {
                string rel = KmhConfigRecovery.GetRelative(KmhDataPaths.Folder, activePath);
                string backupCopy = Path.Combine(backupRoot, rel);
                if (!File.Exists(backupCopy)) continue;
                if (KmhConfigRecovery.RestoreFile(activePath, backupCopy, sample, out string err)) n++;
                else ServerLog.Warn($"Migration recovery: could not restore {name} ({err}).");
            }
            return n;
        }
    }
}
