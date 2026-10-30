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
        // Separate from DataFormatVersion: config can migrate without rewriting stored data.
        public int    ConfigSchema      { get; set; } = 0;

        // Captured before the stamp is refreshed, or boot cannot tell that this build is an upgrade.
        public static string PreviousBuildVersion { get; private set; } = "";

        public static string InstanceId { get; private set; } = "";

        // The offline suite never runs ReconcileOnBoot; without this, identity-keyed code runs under a blank namespace.
        internal static void SetInstanceIdForTest(string id) => InstanceId = id ?? "";

        public static bool IsFreshInstall { get; private set; }

        public static bool IdentityUnreadable { get; private set; }

        private static bool QuarantinedCopyExists()
        {
            try
            {
                return Directory.Exists(KmhDataPaths.Folder)
                    && Directory.GetFiles(KmhDataPaths.Folder, Path.GetFileName(KmhDataPaths.MetaFile) + ".corrupt-*").Length > 0;
            }
            catch { return false; }
        }

        internal static void ResetIdentityForTest(string id, int schema)
        { InstanceId = id ?? ""; AppliedConfigSchema = schema; IdentityUnreadable = false; }

        private static string _bootBuild = "";

        // Derived from the signing key when one can be made; a random id otherwise.
        internal static string NewInstanceId()
        {
            try
            {
                if (Features.Global.KmhServerKey.Load() != null)
                {
                    string derived = KmhServerIdentity.DeriveId(Features.Global.KmhServerKey.PublicKey);
                    if (derived.Length == 32) return derived;
                }
            }
            catch (Exception) { }
            return Guid.NewGuid().ToString("N");
        }

        // A new server starts on the current config schema (nothing to migrate or back up); unstamped data is schema 0.
        internal static int StartingConfigSchema(bool stamped, bool hasData, int stampedSchema)
            => stamped ? stampedSchema : hasData ? 0 : KmhVersion.ConfigSchema;

        // Runs before any store loads, and never throws: a boot must not be blocked by its own version stamp.
        public static string ReconcileOnBoot()
        {
            try
            {
                string build = typeof(KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "?";
                _bootBuild = build;
                // Read before TryLoad, which moves an unreadable file aside and makes this boot look like a first run.
                bool   present = File.Exists(KmhDataPaths.MetaFile) || QuarantinedCopyExists();
                bool   had     = JsonFileStore.TryLoad(KmhDataPaths.MetaFile, out KmhDataMeta meta) && meta != null;

                // Unreadable is not "new": a fresh id breaks every owed delivery and the Global registration, and
                // schema 0 regenerates every config from defaults. Nothing is reissued; the owner restores the file.
                string registered = (Features.Global.GlobalConfig.Current?.RegisteredServerId ?? "").Trim();
                if (!had && present && (HasExistingData() || registered.Length > 0))
                {
                    IdentityUnreadable = true;
                    InstanceId = registered;
                    AppliedConfigSchema = KmhVersion.ConfigSchema;
                    ServerLog.Error("Data format: .kmh-meta.json (this server's identity and config schema) could not be read. "
                        + "Colony Nexus has NOT issued a new identity or regenerated any config. Restore .kmh-meta.json from the newest "
                        + "backup in KMH-Data-Backups, delete any .kmh-meta.json.corrupt-* beside it, and restart.");
                    Maintenance.KmhReadiness.Failed("the server identity file .kmh-meta.json is unreadable - restore it from KMH-Data-Backups");
                    return "Data format: .kmh-meta.json UNREADABLE - identity kept, nothing regenerated, Colony Nexus not operational.";
                }

                if (had) PreviousBuildVersion = meta.LastBuildVersion ?? "";
                AppliedConfigSchema = had ? meta.ConfigSchema : 0;

                InstanceId = (had && !string.IsNullOrEmpty(meta.ServerId)) ? meta.ServerId : NewInstanceId();

                if (!had)
                {
                    // No stamp means a new server or pre-stamp data, and every earlier build was additive-compatible.
                    bool hasData = HasExistingData();
                    IsFreshInstall = !hasData;
                    AppliedConfigSchema = StartingConfigSchema(false, hasData, 0);
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
                        "You are running an OLDER Colony Nexus than the one that wrote this data. Colony Nexus will NOT modify it - " +
                        "upgrade the server binary again, or restore an older backup from KMH-Data-Backups.");
                    return $"Data format: DOWNGRADE detected (data v{meta.DataFormatVersion} > binary v{CurrentFormat}); left untouched.";
                }

                if (meta.DataFormatVersion < CurrentFormat)
                {
                    int from = meta.DataFormatVersion;
                    // Migration is a one-way rewrite, so without this backup KMH refuses rather than risk it.
                    bool backedUp = KmhDataBackup.TryCreate($"premigration-v{from}", out string dir, out string err);
                    if (backedUp) ServerLog.Info($"Data format: pre-migration backup at {Path.GetFileName(dir)}.");
                    else if (!TakeForceMigrationToken())
                    {
                        ServerLog.Error(
                            $"Data format: REFUSING to migrate v{from} -> v{CurrentFormat} - the pre-migration backup "
                          + $"failed ({err}). Your data is untouched. Fix the backup problem and restart, or create "
                          + $"'{Path.GetFileName(KmhDataPaths.ForceMigrationFile)}' in KMH-Data to migrate without one.");
                        Maintenance.KmhReadiness.Failed($"data migration v{from}->v{CurrentFormat} needs a backup it could not take");
                        return $"Data format: migration v{from} -> v{CurrentFormat} REFUSED (no pre-migration backup).";
                    }
                    else
                        ServerLog.Error($"Data format: migrating v{from} -> v{CurrentFormat} WITHOUT a backup ({err}) - "
                                      + "forced by the owner. There is nothing to roll back to if this goes wrong.");

                    RunMigrations(from, CurrentFormat);
                    Write(build);
                    return backedUp
                        ? $"Data format: migrated v{from} -> v{CurrentFormat} (backed up first)."
                        : $"Data format: migrated v{from} -> v{CurrentFormat} (FORCED, no pre-migration backup).";
                }

                Write(build);
                return $"Data format: v{CurrentFormat}, last run by build {(string.IsNullOrEmpty(meta.LastBuildVersion) ? "?" : meta.LastBuildVersion)}.";
            }
            catch (Exception ex)
            {
                return $"Data format: reconcile skipped ({ex.Message}).";
            }
        }

        // Consumed, not just read: an override left armed would silently permit the next unsafe migration too.
        internal static bool TakeForceMigrationToken()
        {
            try
            {
                if (!File.Exists(KmhDataPaths.ForceMigrationFile)) return false;
                File.Delete(KmhDataPaths.ForceMigrationFile);
                return true;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Data format: found a force-migration token but could not consume it ({ex.Message}) - ignoring it.");
                return false;
            }
        }

        // Empty until an incompatible migration ships; every change so far has been additive.
        private static void RunMigrations(int from, int to)
        {
        }

        // Raised only once KmhConfigMigration's steps succeed, so a failed run repeats rather than being skipped.
        public static int AppliedConfigSchema { get; private set; }

        // False means the stamp is memory-only: the next boot would see the old schema and migrate a second time,
        // which for a destructive config schema means resetting settings the owner just put back.
        public static bool StampConfigSchema(int schema, out string error)
        {
            error = null;
            if (schema <= AppliedConfigSchema) return true;
            int previous = AppliedConfigSchema;
            AppliedConfigSchema = schema;
            if (!Write(_bootBuild, out error)) { AppliedConfigSchema = previous; return false; }

            if (!JsonFileStore.TryLoad(KmhDataPaths.MetaFile, out KmhDataMeta back) || back == null
                || back.ConfigSchema < schema)
            {
                AppliedConfigSchema = previous;
                error = "the schema stamp did not read back as written";
                return false;
            }
            return true;
        }

        private static void Write(string build) => Write(build, out _);

        private static bool Write(string build, out string error)
        {
            error = null;
            try
            {
                if (JsonFileStore.Save(KmhDataPaths.MetaFile, new KmhDataMeta
                {
                    DataFormatVersion = CurrentFormat,
                    LastBuildVersion  = build,
                    LastRunUtc        = DateTime.UtcNow.ToString("o"),
                    ServerId          = InstanceId,
                    ConfigSchema      = AppliedConfigSchema,
                })) return true;
                error = "the persistence layer refused the write";
            }
            catch (Exception ex) { error = ex.Message; }
            return false;
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
