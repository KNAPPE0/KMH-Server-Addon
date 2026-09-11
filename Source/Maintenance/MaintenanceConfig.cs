using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    internal enum KmhConsoleLevel { Info, Warn, Error }

    // Release-safety knobs for auto backups and boot scans; migration backups are always forced.
    internal sealed class MaintenanceConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Blank means use the RWT server's own name, so a fresh install does not report a generic one beside it.
        public string ServerName      { get; set; } = "";

        // JSON data is small and restarts are rare, so a per-boot snapshot is cheap insurance against a bad wipe.
        public bool BackupOnBoot      { get; set; } = true;

        // How many timestamped backup folders to keep (oldest pruned first). 0 disables pruning (keep everything).
        public int  BackupRetention   { get; set; } = 12;

        // Validate every KMH JSON file on boot and log a clear summary; warn loudly on a corrupt irreplaceable file.
        public bool IntegrityScanOnBoot { get; set; } = true;

        // Prune KMH-Data/Snapshots/ folders older than this many days (0 = keep by age forever).
        public int  SnapshotRetentionDays { get; set; } = 14;

        // Cap snapshots kept per player (and per _server), newest first (0 = no count cap).
        public int  SnapshotMaxPerPlayer  { get; set; } = 200;

        // A refusal is recoverable; a deposit the player was told succeeded and that vanishes on restart is not.
        public bool FreezeEconomyOnPersistenceFailure { get; set; } = true;

        // Terminal only - all | warn | error | quiet. The diagnostic file always gets every line regardless.
        public string ConsoleLogLevel { get; set; } = "all";

        public bool ConsoleAllows(KmhConsoleLevel level)
        {
            switch ((ConsoleLogLevel ?? "all").Trim().ToLowerInvariant())
            {
                case "quiet": return false;
                case "error": return level == KmhConsoleLevel.Error;
                case "warn":  return level != KmhConsoleLevel.Info;
                default:      return true;
            }
        }

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
