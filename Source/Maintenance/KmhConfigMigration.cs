using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Config schema is tracked apart from data schema, or a cosmetic config edit forces a one-way data upgrade.
    internal static class KmhConfigMigration
    {
        internal delegate int StepAction(List<string> report);

        internal readonly struct Step
        {
            public readonly int      ToVersion;
            public readonly string   Name;
            public readonly StepAction Run;
            public Step(int toVersion, string name, StepAction run) { ToVersion = toVersion; Name = name; Run = run; }
        }

        // Ordered by ToVersion. Add a step and bump KmhVersion.ConfigSchema together.
        private static readonly Step[] Steps =
        {
            new Step(2, "purge stuck world events", PurgeStuckWorldEvents),
            new Step(2, "remove obsolete config keys", PruneObsoleteConfigKeys),
            new Step(3, "economy config v3 (decimal price + drop reserved fields)", MigrateEconomyV3),
            // Runs after the economy migration above, which must read its old keys before they are pruned.
            new Step(3, "remove obsolete config keys (v3)", PruneObsoleteConfigKeys),
        };

        public static bool RanThisBoot { get; private set; }
        public static List<string> Report { get; } = new List<string>();

        public static void ApplyIfNeeded()
        {
            try
            {
                int applied = KmhDataMeta.AppliedConfigSchema;
                int target  = KmhVersion.ConfigSchema;

                if (applied > target)
                {
                    // Mirrors the data-side downgrade guard: an older binary must not "migrate" a newer layout backwards.
                    ServerLog.Error(
                        $"Config schema: on-disk config is schema v{applied} but this build understands v{target}. " +
                        "You are running an OLDER KMH than the one that wrote this config. No config migration will run.");
                    return;
                }
                if (applied == target) return;   // already current - idempotent no-op

                // Its own copy, because the boot backup exists only while BackupOnBoot is on and this rewrite is irreversible.
                if (Persistence.KmhDataBackup.TryCreate($"premigration-config-v{applied}", out string preDir, out string preErr))
                    ServerLog.Info($"Config schema: pre-migration backup at {System.IO.Path.GetFileName(preDir)}.");
                else
                    ServerLog.Warn($"Config schema: could not take a pre-migration backup ({preErr}). " +
                                   "Migrating anyway - restore from KMH-Data-Backups if the old values mattered.");

                foreach (Step s in Steps)
                {
                    if (s.ToVersion <= applied) continue;   // already past this step
                    int changed;
                    try { changed = s.Run(Report); }
                    catch (Exception ex)
                    {
                        // The stamp stays behind the failed step, so the next boot retries it rather than skipping it.
                        ServerLog.Error($"Config migration step '{s.Name}' failed, stopping: {ex}");
                        return;
                    }
                    if (changed > 0) Report.Add($"{s.Name}: {changed} change(s)");
                }

                KmhDataMeta.StampConfigSchema(target);
                RanThisBoot = Report.Count > 0;
                ServerLog.Info(Report.Count > 0
                    ? $"Config schema: migrated v{applied} -> v{target}. {string.Join("; ", Report)}"
                    : $"Config schema: stamped v{target} (nothing to migrate).");
            }
            catch (Exception ex) { ServerLog.Error($"Config migration failed: {ex}"); }
        }

        // Clears records already stranded on live servers by the zero-duration event bug; the code paths are fixed.
        private static int PurgeStuckWorldEvents(List<string> report)
        {
            int n = Features.World.WorldStore.PurgeEndlessEvents();
            if (n > 0) report.Add($"removed {n} world event(s) that had no end time and could never expire");
            return n;
        }

        // The pre-migration backup holds the originals of anything stripped here.
        private static int PruneObsoleteConfigKeys(List<string> report)
        {
            int total = 0, files = 0;
            // The same set the boot load-test covers, or a config lands in one list and not the other.
            foreach ((string _, string path, object defaults) in KmhConfigValidation.ConfigTargets())
            {
                files++;
                // The prune runs before the steps that convert these, so stripping them here loses the value they carry.
                ICollection<string> aliases =
                      defaults is Features.World.WorldConfig     ? Features.World.WorldConfig.LegacyAliasKeys
                    : defaults is Features.Economy.EconomyConfig ? Features.Economy.EconomyConfig.LegacyAliasKeys
                    : null;
                total += JsonFileStore.PruneUnknownFields(path, defaults, aliases);
            }
            if (total > 0) report.Add($"removed {total} obsolete config key(s) across {files} file(s)");
            return total;
        }

        // The file I/O runs under the migration journal, so a crash mid-write rolls back to the pre-migration backup.
        private static int MigrateEconomyV3(List<string> report)
        {
            string path = KmhDataPaths.EconomyConfigFile;
            if (!File.Exists(path)) return 0;

            string json;
            try { json = File.ReadAllText(path); } catch { return 0; }

            string outJson = KmhConfigFieldMigrations.MigrateEconomyV3(json, out int changed);
            if (changed > 0)
            {
                File.WriteAllText(path, outJson);
                report.Add($"economy config: cleaned {changed} field(s) (decimal price + dropped reserved keys)");
            }
            return changed;
        }
    }
}
