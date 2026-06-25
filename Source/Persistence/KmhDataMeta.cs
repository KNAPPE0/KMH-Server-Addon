using System;
using System.IO;
using System.Linq;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Persistence
{
    // Data-format guard for KMH-Data: additive releases stay format 1, future reshapes backup first, downgrades refuse.
    internal sealed class KmhDataMeta
    {
        // Bump only for incompatible data rewrites, and add the matching RunMigrations() step.
        public const int CurrentFormat = 1;

        public int    DataFormatVersion { get; set; } = CurrentFormat;
        public string LastBuildVersion  { get; set; } = "";
        public string LastRunUtc        { get; set; } = "";

        // Called once on boot, before stores load. Returns a human summary line for the log. Never throws.
        public static string ReconcileOnBoot()
        {
            try
            {
                string build = typeof(KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "?";
                bool   had   = JsonFileStore.TryLoad(KmhDataPaths.MetaFile, out KmhDataMeta meta) && meta != null;

                if (!had)
                {
                    // No stamp means new server or pre-stamp data; adopt it as current since earlier builds were additive-compatible.
                    bool hasData = HasExistingData();
                    Write(build);
                    return hasData
                        ? $"Data format: adopted existing data as format v{CurrentFormat} (first run with the format stamp)."
                        : $"Data format: fresh KMH-Data, stamped format v{CurrentFormat}.";
                }

                if (meta.DataFormatVersion > CurrentFormat)
                {
                    // Newer KMH data detected; refuse downgrade to avoid corrupting data and allow re-upgrade recovery.
                    ServerLog.Error(
                        $"Data format: KMH-Data is format v{meta.DataFormatVersion} but this build only understands v{CurrentFormat}. " +
                        "You are running an OLDER KMH than the one that wrote this data. KMH will NOT modify it - " +
                        "upgrade the server binary again, or restore an older backup from KMH-Data-Backups.");
                    return $"Data format: DOWNGRADE detected (data v{meta.DataFormatVersion} > binary v{CurrentFormat}); left untouched.";
                }

                if (meta.DataFormatVersion < CurrentFormat)
                {
                    // A real migration. Always snapshot first - this is the one backup that isn't optional.
                    if (KmhDataBackup.TryCreate($"premigration-v{meta.DataFormatVersion}", out string dir, out string err))
                        ServerLog.Info($"Data format: pre-migration backup at {Path.GetFileName(dir)}.");
                    else
                        ServerLog.Warn($"Data format: could not take a pre-migration backup ({err}) - proceeding cautiously.");

                    int from = meta.DataFormatVersion;
                    RunMigrations(from, CurrentFormat);
                    Write(build);
                    return $"Data format: migrated v{from} -> v{CurrentFormat} (backed up first).";
                }

                // Same format - just refresh the run markers.
                Write(build);
                return $"Data format: v{CurrentFormat}, last run by build {(string.IsNullOrEmpty(meta.LastBuildVersion) ? "?" : meta.LastBuildVersion)}.";
            }
            catch (Exception ex)
            {
                return $"Data format: reconcile skipped ({ex.Message}).";
            }
        }

        // Step data forward by format; empty for now until an incompatible migration ships.
        private static void RunMigrations(int from, int to)
        {
            // if (from < 2) { ...reshape...; from = 2; }
        }

        private static void Write(string build)
        {
            try
            {
                JsonFileStore.Save(KmhDataPaths.MetaFile, new KmhDataMeta
                {
                    DataFormatVersion = CurrentFormat,
                    LastBuildVersion  = build,
                    LastRunUtc        = DateTime.UtcNow.ToString("o"),
                });
            }
            catch { /* stamp is advisory; a failed write just re-adopts next boot */ }
        }

        // Any irreplaceable runtime file already on disk means this isn't a brand-new server.
        private static bool HasExistingData()
        {
            try
            {
                return KmhDataPaths.KnownDataFiles.Any(f => !f.Regenerable && File.Exists(f.Path));
            }
            catch { return false; }
        }
    }
}
