using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Release-safety knobs for auto backups and boot scans; migration backups are always forced.
    internal sealed class MaintenanceConfig
    {
        // Snapshot all of KMH-Data into KMH-Data-Backups/ once per boot, then prune to BackupRetention copies. JSON
        // data is small and servers don't restart often, so this is cheap insurance against a bad save/wipe.
        public bool BackupOnBoot      { get; set; } = true;

        // How many timestamped backup folders to keep (oldest pruned first). 0 disables pruning (keep everything).
        public int  BackupRetention   { get; set; } = 12;

        // Validate every KMH JSON file on boot and log a clear summary; warn loudly on a corrupt irreplaceable file.
        public bool IntegrityScanOnBoot { get; set; } = true;

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
        }
    }
}
