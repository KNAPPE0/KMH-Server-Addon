using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    internal readonly struct KmhConfigMigrationPlan
    {
        public readonly bool   Needed;
        public readonly int    From;
        public readonly int    To;
        public readonly string BackupName;
        public readonly string Blocked;      // null when the migration may proceed

        public KmhConfigMigrationPlan(bool needed, int from, int to, string backupName, string blocked)
        { Needed = needed; From = from; To = to; BackupName = backupName; Blocked = blocked; }

        public static KmhConfigMigrationPlan NotNeeded => new KmhConfigMigrationPlan(false, 0, 0, null, null);
    }

    // Config schema is tracked apart from data schema, or a cosmetic config edit forces a one-way data upgrade.
    internal static class KmhConfigMigration
    {
        internal delegate (int Changed, string Failure) StepAction(List<string> report);

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
            // Deliberately a rewrite, not an upgrade: every server lands on one reviewed baseline.
            new Step(4, "regenerate every tunable config from v1.3.1 defaults", KmhConfigRegeneration.RegenerateAll),
            new Step(5, "split Connected into GuildFederation and ConnectedTreasury", SplitConnectedFeature),
            new Step(5, "keep Colony Nexus Global off where its owner turned it off", KeepGlobalOffWhereDisabled),
            new Step(6, "count the world quest activity window in minutes", ActivityWindowToMinutes),
            new Step(7, "group listing, transport and badge settings, and keep Global's identity beside its key", TidyOwnerConfigs),
        };

        // Derived from the step list rather than restated, so upgrade documentation cannot outlive the step itself.
        internal static bool RegeneratesConfigs
            => System.Array.Exists(Steps, s => s.Run == (StepAction)KmhConfigRegeneration.RegenerateAll);

        public static bool RanThisBoot { get; private set; }
        // This boot replaced the owner's tunable values; BackupThisBoot holds the originals.
        public static bool RegeneratedThisBoot { get; private set; }
        public static string BackupThisBoot { get; private set; }
        public static List<string> Report { get; } = new List<string>();

        // The schema-4 step rewrites every tunable config from defaults, so only a verified backup keeps it reversible.
        public static KmhConfigMigrationPlan Prepare()
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
                        "You are running an OLDER Colony Nexus than the one that wrote this config. No config migration will run.");
                    return KmhConfigMigrationPlan.NotNeeded;
                }
                if (applied == target) return KmhConfigMigrationPlan.NotNeeded;

                // Its own copy, because the boot backup exists only while BackupOnBoot is on and this rewrite is irreversible.
                if (!Persistence.KmhDataBackup.TryCreate($"premigration-config-v{applied}", out string preDir, out string preErr))
                    return Block(applied, target, $"no pre-migration backup could be taken ({preErr})");

                string name = System.IO.Path.GetFileName(preDir);
                if (!Persistence.KmhDataBackup.Verify(preDir, out string verifyErr))
                    return Block(applied, target, $"the pre-migration backup '{name}' did not verify ({verifyErr})");

                ServerLog.Info($"Config schema: verified pre-migration backup at {name}.");
                return new KmhConfigMigrationPlan(true, applied, target, name, null);
            }
            catch (Exception ex) { return Block(0, 0, $"the migration could not be prepared ({ex.Message})"); }
        }

        private static KmhConfigMigrationPlan Block(int from, int to, string why)
        {
            string what = RegeneratesFrom(from) ? "rewrites every tunable config from defaults" : "rewrites config files";
            ServerLog.Error($"Config schema: REFUSING to migrate v{from} -> v{to} - {why}. Configs are untouched. "
                          + $"This migration {what}, so it will not run without a "
                          + "restore point. Free up space, fix permissions on KMH-Data-Backups, and restart.");
            return new KmhConfigMigrationPlan(true, from, to, null, why);
        }

        internal static bool RegeneratesFrom(int from)
            => System.Array.Exists(Steps, s => s.ToVersion > from && s.Run == (StepAction)KmhConfigRegeneration.RegenerateAll);

        internal static bool HasStep(StepAction run) => System.Array.Exists(Steps, s => s.Run == run);

        // The first failure stops the run: later steps would write over a half-migrated set and the stamp call it current.
        internal static string RunSteps(IEnumerable<Step> steps, int from, List<string> report)
        {
            foreach (Step s in steps)
            {
                if (s.ToVersion <= from) continue;
                int changed;
                string failure;
                try { (changed, failure) = s.Run(report); }
                catch (Exception ex) { failure = ex.ToString(); changed = 0; }

                if (failure != null)
                {
                    // The stamp stays behind the failed step, so the next boot retries it rather than skipping it.
                    ServerLog.Error($"Config migration step '{s.Name}' failed, stopping: {failure}");
                    return $"step '{s.Name}' failed: {failure}";
                }
                if (changed > 0) report.Add($"{s.Name}: {changed} change(s)");
            }
            return null;
        }

        // Null means the migration completed and the new schema is on disk; anything else is the reason it did not.
        // Main runs this under the migration journal, so a crash mid-write rolls back to the pre-migration backup.
        public static string Apply(KmhConfigMigrationPlan plan) => ApplySteps(plan, Steps);

        // Not an Apply overload: the migration harness finds Apply by name.
        internal static string ApplySteps(KmhConfigMigrationPlan plan, IEnumerable<Step> steps)
        {
            try
            {
                string stepFailure = RunSteps(steps, plan.From, Report);
                if (stepFailure != null) return stepFailure;

                // Last, and only if it persists: an unrecorded stamp makes the next boot reset the settings again.
                if (!KmhDataMeta.StampConfigSchema(plan.To, out string stampErr))
                    return $"the new config schema v{plan.To} could not be recorded ({stampErr})";

                RanThisBoot = Report.Count > 0;
                RegeneratedThisBoot = RegeneratesFrom(plan.From);
                BackupThisBoot = plan.BackupName;
                ServerLog.Info(Report.Count > 0
                    ? $"Config schema: migrated v{plan.From} -> v{plan.To}. {string.Join("; ", Report)}"
                    : $"Config schema: stamped v{plan.To} (nothing to migrate).");
                return null;
            }
            catch (Exception ex) { return $"the migration threw ({ex.Message})"; }
        }

        // Apply sets the boot-wide state the doctor reads, so a test that runs it puts that state back.
        internal static IDisposable KeepBootStateForTest() => new BootState();

        private sealed class BootState : IDisposable
        {
            private readonly bool _ran = RanThisBoot, _regenerated = RegeneratedThisBoot;
            private readonly string _backup = BackupThisBoot;
            private readonly List<string> _report = new List<string>(Report);

            public void Dispose()
            {
                RanThisBoot = _ran; RegeneratedThisBoot = _regenerated; BackupThisBoot = _backup;
                Report.Clear(); Report.AddRange(_report);
            }
        }

        // Clears world events the zero-duration bug stranded in existing data; nothing creates new ones.
        private static (int, string) PurgeStuckWorldEvents(List<string> report)
        {
            int n = Features.World.WorldStore.PurgeEndlessEvents();
            if (n > 0) report.Add($"removed {n} world event(s) that had no end time and could never expire");
            return (n, null);
        }

        // The pre-migration backup holds the originals of anything stripped here.
        // The same set the boot load-test covers, or a config lands in one list and not the other.
        private static (int, string) PruneObsoleteConfigKeys(List<string> report)
            => PruneObsoleteConfigKeysIn(KmhConfigValidation.ConfigTargets(), report);

        internal static (int, string) PruneObsoleteConfigKeysIn(IEnumerable<(string name, string path, object sample)> targets, List<string> report)
        {
            int total = 0, files = 0;
            foreach ((string name, string path, object defaults) in targets)
            {
                files++;
                total += JsonFileStore.PruneUnknownFields(path, defaults, KeptForLaterSteps(name));
            }
            if (total > 0) report.Add($"removed {total} obsolete config key(s) across {files} file(s)");
            return (total, null);
        }

        // Read again by the v7 tidy, which moves them; pruned first, a server migrating from v0 or v1 loses its identity and listing.
        internal static readonly string[] GlobalIdentityKeys =
            { "RegisteredServerId", "LastSequence", "LastMetadataRevision", "SigningGeneration", "PublicListingAcknowledgedUtc" };

        // By ConfigTargets name.
        internal static ICollection<string> KeptForLaterSteps(string config)
        {
            IReadOnlyList<(string From, string To)> moves =
                config == "Global"    ? Features.Global.GlobalConfig.Moves :
                config == "Transport" ? Features.Transport.TransportConfig.Moves :
                config == "Staff"     ? Features.Identity.StaffConfig.Moves : null;
            if (moves == null) return null;
            HashSet<string> keep = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string from, string _) in moves) keep.Add(from.Split('.')[0]);
            if (config == "Global") keep.UnionWith(GlobalIdentityKeys);
            return keep;
        }

        // Federation defaults on, but an old "Connected": true must not enable cross-server silver.
        internal static (int, string) SplitConnectedFeature(List<string> report)
            => SplitConnectedFeatureIn(KmhDataPaths.FeaturesConfigFile, report);

        internal static (int, string) SplitConnectedFeatureIn(string path, List<string> report)
        {
            if (!File.Exists(path)) return (0, null);
            Newtonsoft.Json.Linq.JObject o;
            try { o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return (0, $"Features.json could not be read ({ex.Message})"); }

            bool wasConnected = o["Connected"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)o["Connected"];
            int changed = 0;
            if (o.Remove("Connected")) changed++;
            if (o["GuildFederation"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean || !(bool)o["GuildFederation"])
            { o["GuildFederation"] = true; changed++; }
            if (o["ConnectedTreasury"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean || (bool)o["ConnectedTreasury"])
            { o["ConnectedTreasury"] = false; changed++; }
            if (changed == 0) return (0, null);

            if (!JsonFileStore.Save(path, o)) return (0, "Features.json could not be written");
            Features.FeaturesConfig.Reload();
            report.Add(wasConnected
                ? "Guild Federation is on; Connected Treasury was on and is now off - cross-server silver is disabled in v1.3.1"
                : "Guild Federation is on; Connected Treasury stays off");
            return (changed, null);
        }

        // Boot backfill has already written ActivityWindowMinutes at its default, so the owner's hours win while present.
        internal static (int, string) ActivityWindowToMinutes(List<string> report)
        {
            (int changed, string failure) = ActivityWindowToMinutesIn(KmhDataPaths.WorldConfigFile, report);
            if (changed > 0) Features.World.WorldConfig.Reload();
            return (changed, failure);
        }

        internal static (int, string) ActivityWindowToMinutesIn(string path, List<string> report)
        {
            if (!File.Exists(path)) return (0, null);
            Newtonsoft.Json.Linq.JObject o;
            try { o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return (0, $"World.json could not be read ({ex.Message})"); }

            if (!(o.SelectToken("WorldQuests.Scaling") is Newtonsoft.Json.Linq.JObject scaling)) return (0, null);
            Newtonsoft.Json.Linq.JToken hours = scaling["ActivityWindowHours"];
            if (hours == null) return (0, null);

            long minutes = 0;
            try { minutes = (long)Math.Round((double)hours * 60); } catch { }
            if (minutes > 0) scaling["ActivityWindowMinutes"] = minutes;
            scaling.Remove("ActivityWindowHours");

            if (!JsonFileStore.Save(path, o)) return (0, "World.json could not be written");
            report.Add(minutes > 0
                ? $"World.json: WorldQuests.Scaling.ActivityWindowHours {hours} became ActivityWindowMinutes {minutes}"
                : "World.json: an unreadable ActivityWindowHours was dropped; ActivityWindowMinutes keeps its default");
            return (1, null);
        }

        // A registered owner who switched Global off chose silence; the new backend identity must not undo that.
        internal static (int, string) KeepGlobalOffWhereDisabled(List<string> report)
        {
            Features.Global.GlobalConfig cfg = Features.Global.GlobalConfig.Current;
            if (cfg == null || cfg.Enabled || !cfg.BackendIdentity) return (0, null);
            if (!Features.Global.GlobalSetupState.Current_.IsOwnerRegistered) return (0, null);
            cfg.BackendIdentity = false;
            if (!cfg.Save()) return (0, "Global.json could not be written");
            report.Add("Colony Nexus Global stays off on this server, as its owner left it");
            return (1, null);
        }

        // Loaders already read every old key, so this only reshapes files and moves Global's identity, then verifies both.
        internal static (int, string) TidyOwnerConfigs(List<string> report)
        {
            int changed = 0;
            (int n, string failure) = Tidy("Global.json", KmhDataPaths.GlobalConfigFile, Features.Global.GlobalConfig.Moves,
                GlobalIdentityKeys,
                () =>
                {
                    Features.Global.GlobalConfig.Reload();
                    Features.Global.GlobalConfig cfg = Features.Global.GlobalConfig.Current;
                    string id = cfg.RegisteredServerId ?? "";
                    int generation = cfg.SigningGeneration;
                    if (!cfg.Save()) return "Global.json or Identity/global-state.json could not be written";
                    // The identity has to read back exactly, or a restart registers this server as a new one.
                    if (!JsonFileStore.TryLoad(KmhDataPaths.GlobalStateFile, out Features.Global.GlobalRuntimeState state) || state == null
                        || (state.RegisteredServerId ?? "") != id || state.SigningGeneration != generation)
                        return "Identity/global-state.json did not read back with the registered server id and generation";
                    return null;
                }, report);
            if (failure != null) return (changed, failure);
            changed += n;

            (n, failure) = Tidy("Transport.json", KmhDataPaths.TransportConfigFile, Features.Transport.TransportConfig.Moves, null, () =>
            {
                Features.Transport.TransportConfig.Reload();
                return JsonFileStore.Save(KmhDataPaths.TransportConfigFile, Features.Transport.TransportConfig.Current)
                    ? null : "Transport.json could not be written";
            }, report);
            if (failure != null) return (changed, failure);
            changed += n;

            (n, failure) = Tidy("Staff.json", KmhDataPaths.StaffConfigFile, Features.Identity.StaffConfig.Moves, null, () =>
            {
                Features.Identity.StaffConfig.Reload();
                return JsonFileStore.Save(KmhDataPaths.StaffConfigFile, Features.Identity.StaffConfig.Current)
                    ? null : "Staff.json could not be written";
            }, report);
            if (failure != null) return (changed, failure);
            return (changed + n, null);
        }

        private static (int, string) Tidy(string label, string path, IReadOnlyList<(string From, string To)> moves, string[] alsoGone,
                                          Func<string> rewrite, List<string> report)
        {
            if (!File.Exists(path)) return (0, null);
            Newtonsoft.Json.Linq.JObject before;
            try { before = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return (0, $"{label} could not be read ({ex.Message})"); }

            List<string> old = new List<string>();
            foreach ((string from, string to) in moves) if (before.SelectToken(from) != null) old.Add(from + " -> " + to);
            List<string> gone = new List<string>();
            foreach (string key in alsoGone ?? Array.Empty<string>()) if (before[key] != null) gone.Add(key);
            if (old.Count == 0 && gone.Count == 0) return (0, null);

            string failure = rewrite();
            if (failure != null) return (0, failure);

            Newtonsoft.Json.Linq.JObject after;
            try { after = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return (0, $"{label} could not be read back ({ex.Message})"); }
            foreach ((string from, string _) in moves)
                if (after.SelectToken(from) != null) return (0, $"{label} still has {from} after the rewrite");
            foreach (string key in alsoGone ?? Array.Empty<string>())
                if (after[key] != null) return (0, $"{label} still has {key} after the rewrite");

            if (old.Count > 0) report.Add($"{label}: kept your values and moved {old.Count} setting(s): {string.Join(", ", old)}");
            if (gone.Count > 0) report.Add($"{label}: {string.Join(", ", gone)} now live in Identity/global-state.json (or were unused)");
            return (1, null);
        }
    }
}
