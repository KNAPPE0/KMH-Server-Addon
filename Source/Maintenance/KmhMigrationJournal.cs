using System;
using System.IO;
using Newtonsoft.Json;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Each write is atomic, but a crash between them leaves a mixed set, so a journal short of Committed rolls all back.
    internal enum KmhMigrationStatus { Started, Committed }

    internal sealed class KmhMigrationJournal
    {
        [JsonProperty("id")]         public string Id            { get; set; } = "";
        [JsonProperty("started")]    public string StartedUtc    { get; set; } = "";
        [JsonProperty("updated")]    public string UpdatedUtc    { get; set; } = "";
        [JsonProperty("build")]      public string Build         { get; set; } = "";
        [JsonProperty("backup_dir")] public string BackupDirName { get; set; } = "";   // pre-migration boot backup
        [JsonProperty("status")]     public KmhMigrationStatus Status { get; set; } = KmhMigrationStatus.Started;

        private static string File => KmhDataPaths.MigrationJournalFile;

        // Only a journal that exists and never reached Committed means the migration was interrupted.
        public static bool NeedsRecovery(KmhMigrationJournal found)
            => found != null && found.Status != KmhMigrationStatus.Committed;

        // Opens a journal for a migration about to run, recording which backup to roll back to if it never commits.
        public static KmhMigrationJournal Begin(string backupDirName)
        {
            var j = new KmhMigrationJournal
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedUtc = DateTime.UtcNow.ToString("o"),
                UpdatedUtc = DateTime.UtcNow.ToString("o"),
                Build = KmhVersion.Build,
                BackupDirName = backupDirName ?? "",
                Status = KmhMigrationStatus.Started,
            };
            j.Save();
            return j;
        }

        public void Commit()
        {
            Status = KmhMigrationStatus.Committed;
            UpdatedUtc = DateTime.UtcNow.ToString("o");
            Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                JsonFileStore.Save(File, this);
            }
            catch (Exception ex) { ServerLog.Warn($"Migration journal: could not write ({ex.Message})."); }
        }

        public static KmhMigrationJournal Read()
            => JsonFileStore.TryLoad(File, out KmhMigrationJournal j) ? j : null;

        public static void Clear()
        {
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); }
            catch (Exception ex) { ServerLog.Warn($"Migration journal: could not clear ({ex.Message})."); }
        }
    }
}
