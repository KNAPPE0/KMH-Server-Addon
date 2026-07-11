using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Release-safety knobs for auto backups and boot scans; migration backups are always forced.
    internal sealed class MaintenanceConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Friendly name for THIS server instance, shown to players + external tooling + Discord. Give each server on a
        // shared host a distinct name (e.g. "MoW S6 - 25554") so they're easy to tell apart. Blank -> "KMH Server".
        public string ServerName      { get; set; } = "KMH Server";

        // Snapshot all of KMH-Data into KMH-Data-Backups/ once per boot, then prune to BackupRetention copies. JSON
        // data is small and servers don't restart often, so this is cheap insurance against a bad save/wipe.
        public bool BackupOnBoot      { get; set; } = true;

        // How many timestamped backup folders to keep (oldest pruned first). 0 disables pruning (keep everything).
        public int  BackupRetention   { get; set; } = 12;

        // Validate every KMH JSON file on boot and log a clear summary; warn loudly on a corrupt irreplaceable file.
        public bool IntegrityScanOnBoot { get; set; } = true;

        // Prune KMH-Data/Snapshots/ folders older than this many days (0 = keep by age forever).
        public int  SnapshotRetentionDays { get; set; } = 14;

        // Cap snapshots kept per player (and per _server), newest first (0 = no count cap).
        public int  SnapshotMaxPerPlayer  { get; set; } = 200;

        private static MaintenanceConfig _current;
        public static MaintenanceConfig Current => _current ?? (_current = LoadOrDefault());

        public static MaintenanceConfig LoadOrDefault()
        {
            MaintenanceConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.MaintenanceConfigFile, out MaintenanceConfig loaded) && loaded != null
                ? loaded : new MaintenanceConfig();
            cfg.Clamp();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.MaintenanceConfigFile))
                JsonFileStore.Save(KmhDataPaths.MaintenanceConfigFile, new MaintenanceConfig());
        }

        public static void Reload() => _current = LoadOrDefault();

        private void Clamp()
        {
            if (BackupRetention < 0)   BackupRetention = 0;
            if (BackupRetention > 500) BackupRetention = 500;
            if (SnapshotRetentionDays < 0)    SnapshotRetentionDays = 0;
            if (SnapshotRetentionDays > 3650) SnapshotRetentionDays = 3650;
            if (SnapshotMaxPerPlayer < 0)     SnapshotMaxPerPlayer = 0;
            if (SnapshotMaxPerPlayer > 100000) SnapshotMaxPerPlayer = 100000;
        }
    }
}
