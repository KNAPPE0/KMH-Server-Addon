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
        // Stable per-install id, minted once and kept, so external tooling can tell KMH instances apart on one host.
        public string ServerId          { get; set; } = "";
        // Highest recommended-defaults revision applied (see KmhDefaultsUpgrade). 0 = pre-1.2.0 file.
        public int    DefaultsRevision  { get; set; } = 0;

        // Build that last ran, captured before the stamp is refreshed so boot can detect an upgrade. Blank on a fresh server.
        public static string PreviousBuildVersion { get; private set; } = "";

        // This install's stable server id (set during ReconcileOnBoot).
        public static string InstanceId { get; private set; } = "";

        // Defaults revision loaded from disk; StampDefaultsRevision raises it after an upgrade pass.
        public static int  AppliedDefaultsRevision { get; private set; }

        // True when this boot found neither a meta stamp nor any irreplaceable data (brand-new server).
        public static bool IsFreshInstall { get; private set; }

        private static string _bootBuild = "";

        // Called once on boot, before stores load. Returns a human summary line for the log. Never throws.
        public static string ReconcileOnBoot()
        {
            try
            {
                string build = typeof(KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "?";
                _bootBuild = build;
                bool   had   = JsonFileStore.TryLoad(KmhDataPaths.MetaFile, out KmhDataMeta meta) && meta != null;
                if (had) PreviousBuildVersion = meta.LastBuildVersion ?? "";
                AppliedDefaultsRevision = had ? meta.DefaultsRevision : 0;

                // Keep the existing server id, or mint one now and preserve it on every future write.
                InstanceId = (had && !string.IsNullOrEmpty(meta.ServerId)) ? meta.ServerId : Guid.NewGuid().ToString("N");

                if (!had)
                {
                    // No stamp means new server or pre-stamp data; adopt it as current since earlier builds were additive-compatible.
                    bool hasData = HasExistingData();
                    IsFreshInstall = !hasData;
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

        // Raise the applied-defaults marker (never lowers) so the one-shot upgrade doesn't re-run every boot.
        public static void StampDefaultsRevision(int revision)
        {
            if (revision <= AppliedDefaultsRevision) return;
            AppliedDefaultsRevision = revision;
            Write(_bootBuild);
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
                    ServerId          = InstanceId,
                    DefaultsRevision  = AppliedDefaultsRevision,
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
