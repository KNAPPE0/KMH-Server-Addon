using System;
using System.IO;
using System.Linq;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Persistence
{
    internal sealed class KmhDataMeta
    {
        // Bump only for incompatible data rewrites, and add the matching RunMigrations() step.
        public const int CurrentFormat = 1;

        public int    DataFormatVersion { get; set; } = CurrentFormat;
        public string LastBuildVersion  { get; set; } = "";
        public string LastRunUtc        { get; set; } = "";
        // Minted once and never reissued, so external tooling can tell two KMH instances on one host apart.
        public string ServerId          { get; set; } = "";
        public int    DefaultsRevision  { get; set; } = 0;   // 0 = pre-1.2.0 file
        // Separate from DataFormatVersion: config can migrate without rewriting stored data.
        public int    ConfigSchema      { get; set; } = 0;

        // Captured before the stamp is refreshed, or boot cannot tell that this build is an upgrade.
        public static string PreviousBuildVersion { get; private set; } = "";

        public static string InstanceId { get; private set; } = "";

        // The offline suite never runs ReconcileOnBoot, so identity-keyed code would otherwise run under a blank namespace production never has.
        internal static void SetInstanceIdForTest(string id) => InstanceId = id ?? "";

        public static int  AppliedDefaultsRevision { get; private set; }

        public static bool IsFreshInstall { get; private set; }

        private static string _bootBuild = "";

        // Runs before any store loads, and never throws: a boot must not be blocked by its own version stamp.
        public static string ReconcileOnBoot()
        {
            try
            {
                string build = typeof(KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "?";
                _bootBuild = build;
                bool   had   = JsonFileStore.TryLoad(KmhDataPaths.MetaFile, out KmhDataMeta meta) && meta != null;
                if (had) PreviousBuildVersion = meta.LastBuildVersion ?? "";
                AppliedDefaultsRevision = had ? meta.DefaultsRevision : 0;
                AppliedConfigSchema     = had ? meta.ConfigSchema : 0;

                InstanceId = (had && !string.IsNullOrEmpty(meta.ServerId)) ? meta.ServerId : Guid.NewGuid().ToString("N");

                if (!had)
                {
                    // No stamp means a new server or pre-stamp data, and every earlier build was additive-compatible.
                    bool hasData = HasExistingData();
                    IsFreshInstall = !hasData;
                    Write(build);
                    return hasData
                        ? $"Data format: adopted existing data as format v{CurrentFormat} (first run with the format stamp)."
                        : $"Data format: fresh KMH-Data, stamped format v{CurrentFormat}.";
                }

                if (meta.DataFormatVersion > CurrentFormat)
                {
                    // A one-way door: an older binary writing this data would silently drop what it cannot represent.
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

                Write(build);
                return $"Data format: v{CurrentFormat}, last run by build {(string.IsNullOrEmpty(meta.LastBuildVersion) ? "?" : meta.LastBuildVersion)}.";
            }
            catch (Exception ex)
            {
                return $"Data format: reconcile skipped ({ex.Message}).";
            }
        }

        // Empty until an incompatible migration ships; every change so far has been additive.
        private static void RunMigrations(int from, int to)
        {
        }

        // Never lowers, or the one-shot upgrade re-runs every boot and overwrites what the owner changed since.
        public static void StampDefaultsRevision(int revision)
        {
            if (revision <= AppliedDefaultsRevision) return;
            AppliedDefaultsRevision = revision;
            Write(_bootBuild);
        }

        // Raised only once KmhConfigMigration's steps succeed, so a failed run repeats rather than being skipped.
        public static int AppliedConfigSchema { get; private set; }

        public static void StampConfigSchema(int schema)
        {
            if (schema <= AppliedConfigSchema) return;
            AppliedConfigSchema = schema;
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
                    ConfigSchema      = AppliedConfigSchema,
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
