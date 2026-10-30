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

        // Null means the journal is not on disk, and a migration that runs without one has no crash barrier at all.
        public static KmhMigrationJournal Begin(string backupDirName, out string error)
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
            if (!j.Save(out error)) return null;

            // Read back: a write that returned true but left nothing readable is the case recovery cannot survive.
            KmhMigrationJournal back = Read();
            if (back == null || !string.Equals(back.Id, j.Id, StringComparison.Ordinal))
            {
                error = "the journal did not read back as written";
                return null;
            }
            return j;
        }

        public bool Commit(out string error)
        {
            Status = KmhMigrationStatus.Committed;
            UpdatedUtc = DateTime.UtcNow.ToString("o");
            return Save(out error);
        }

        private bool Save(out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                if (JsonFileStore.Save(File, this)) return true;
                error = "the persistence layer refused the write";
            }
            catch (Exception ex) { error = ex.Message; }
            ServerLog.Warn($"Migration journal: could not write ({error}).");
            return false;
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
