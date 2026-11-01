using System.Collections.Generic;
using KMHServerAddon.Features.World;
using KMHServerAddon.Features.World.Dto;

namespace KMHServerAddon.Maintenance
{
    // Synthetic in-memory data only, never the live stores, so the non-mutating smoke test can run it.
    internal static class KmhMigrationSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var results = new List<(string, bool, string)>();
            WorldEventPurge(results);
            ConfigPrune(results);
            Report(results);
            ActivityWindowMinutes(results);
            return results;
        }

        // World.json is minutes everywhere; the one hours key left must carry the owner's value over, not the default.
        private static void ActivityWindowMinutes(List<(string, bool, string)> r)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kmh_aw_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string path = System.IO.Path.Combine(dir, "World.json");
                Newtonsoft.Json.Linq.JToken Scaling() =>
                    Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path)).SelectToken("WorldQuests.Scaling");

                // What boot leaves behind: the owner's 24 hours, and the backfilled default beside it.
                System.IO.File.WriteAllText(path,
                    "{ \"WorldQuests\": { \"Scaling\": { \"ActivityWindowHours\": 24, \"ActivityWindowMinutes\": 2880, \"MinActivePlayers\": 1 } } }");
                var report = new List<string>();
                (int changed, string fail) = KmhConfigMigration.ActivityWindowToMinutesIn(path, report);
                Newtonsoft.Json.Linq.JToken s = Scaling();
                r.Add(("Migration: the owner's activity window in hours becomes the same window in minutes",
                       fail == null && changed == 1 && (long?)s?["ActivityWindowMinutes"] == 1440 && s?["ActivityWindowHours"] == null
                       && (long?)s?["MinActivePlayers"] == 1 && report.Count == 1,
                       fail ?? s?.ToString(Newtonsoft.Json.Formatting.None)));

                (int again, string fail2) = KmhConfigMigration.ActivityWindowToMinutesIn(path, new List<string>());
                r.Add(("Migration: converting the activity window twice changes nothing",
                       fail2 == null && again == 0 && (long?)Scaling()?["ActivityWindowMinutes"] == 1440,
                       fail2 ?? $"second pass changed {again}"));

                var loaded = Persistence.JsonFileStore.TryLoad(path, out Features.World.WorldConfig cfg) && cfg != null;
                if (loaded) Features.World.WorldConfig.ClampForTest(cfg);
                r.Add(("Migration: the converted World.json loads with the owner's window",
                       loaded && cfg.WorldQuests.Scaling.ActivityWindowMinutes == 1440, loaded ? "1440 minutes" : "did not load"));

                System.IO.File.WriteAllText(path, "{ \"WorldQuests\": { \"Scaling\": { \"ActivityWindowHours\": null } } }");
                (int dropped, string fail3) = KmhConfigMigration.ActivityWindowToMinutesIn(path, new List<string>());
                r.Add(("Migration: an unreadable hours value is dropped and the default stands",
                       fail3 == null && dropped == 1 && Scaling()?["ActivityWindowHours"] == null && Scaling()?["ActivityWindowMinutes"] == null,
                       fail3 ?? Scaling()?.ToString(Newtonsoft.Json.Formatting.None)));

                System.IO.File.WriteAllText(path, "{ \"WorldQuests\": { \"Scaling\": { \"ActivityWindowMinutes\": 600 } } }");
                (int none, string fail4) = KmhConfigMigration.ActivityWindowToMinutesIn(path, new List<string>());
                r.Add(("Migration: a World.json already in minutes is left alone",
                       fail4 == null && none == 0 && (long?)Scaling()?["ActivityWindowMinutes"] == 600, fail4 ?? "untouched"));

                System.IO.File.WriteAllText(path, "{ truncated");
                (int _, string fail5) = KmhConfigMigration.ActivityWindowToMinutesIn(path, new List<string>());
                r.Add(("Migration: an unreadable World.json fails the step rather than being skipped",
                       fail5 != null, fail5 ?? "(reported success)"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }

            r.Add(("Migration: the activity window step is registered at the current config schema",
                   KmhVersion.ConfigSchema >= 6 && KmhConfigMigration.HasStep(KmhConfigMigration.ActivityWindowToMinutes),
                   $"config schema {KmhVersion.ConfigSchema}"));

            // An upgrade from v5 keeps its settings and must not be told it will lose them.
            r.Add(("Migration: only an upgrade that regenerates configs is refused for that reason",
                   KmhConfigMigration.RegeneratesFrom(3) && !KmhConfigMigration.RegeneratesFrom(5),
                   $"from v3: {KmhConfigMigration.RegeneratesFrom(3)}, from v5: {KmhConfigMigration.RegeneratesFrom(5)}"));
        }

        private static void Report(List<(string, bool, string)> r)
        {
            var changed = KmhMigrationReport.Build(new KmhMigrationReport.Inputs
            {
                TimestampUtc = "T", Build = "1.3.0", FreshInstall = false,
                ConfigSchemaFrom = 1, ConfigSchemaTo = 2, DataSchema = 1,
                FieldsBackfilled = 3, Regenerated = 13, Kept = 3,
                Steps = new List<string> { "removed 4 stuck world event(s)" },
            });
            bool ok1 = changed.Exists(l => l.Contains("v1 -> v2"))
                    && changed.Exists(l => l.Contains("backfilled 3"))
                    && changed.Exists(l => l.Contains("regenerated 13"))
                    && changed.Exists(l => l.Contains("removed 4 stuck"))
                    && !changed.Exists(l => l.Contains("no changes"));
            r.Add(("Migration report: upgrade lists changes", ok1, $"{changed.Count} lines"));

            var noop = KmhMigrationReport.Build(new KmhMigrationReport.Inputs
            {
                TimestampUtc = "T", Build = "1.3.0", FreshInstall = false,
                ConfigSchemaFrom = 2, ConfigSchemaTo = 2, DataSchema = 1,
                FieldsBackfilled = 0, Regenerated = 0, Kept = 0, Steps = null,
            });
            r.Add(("Migration report: no-op says no changes",
                noop.Exists(l => l.Contains("no changes")) && !noop.Exists(l => l.Contains("backfilled")),
                $"{noop.Count} lines"));

            var fresh = KmhMigrationReport.Build(new KmhMigrationReport.Inputs
            {
                TimestampUtc = "T", Build = "1.3.0", FreshInstall = true,
                ConfigSchemaFrom = 0, ConfigSchemaTo = 2, DataSchema = 1, Steps = null,
            });
            r.Add(("Migration report: fresh install labelled",
                fresh.Exists(l => l.Contains("fresh")) && !fresh.Exists(l => l.Contains("upgraded from existing")),
                "fresh labelled"));

            // Only a boot that changed something earns a timestamped copy, or restarts fill Migrations/ forever.
            bool archiving =
                !KmhMigrationReport.AnyChange(new KmhMigrationReport.Inputs
                    { ConfigSchemaFrom = 2, ConfigSchemaTo = 2, Steps = null })
                && KmhMigrationReport.AnyChange(new KmhMigrationReport.Inputs
                    { ConfigSchemaFrom = 1, ConfigSchemaTo = 2, Steps = null })
                && KmhMigrationReport.AnyChange(new KmhMigrationReport.Inputs
                    { ConfigSchemaFrom = 2, ConfigSchemaTo = 2, FieldsBackfilled = 1, Steps = null });
            r.Add(("Migration report: only a boot that changed something is archived", archiving,
                "quiet boot skipped; schema change and backfill kept"));

            ConfigLoadTest(r);
        }

        private static void ConfigLoadTest(List<(string, bool, string)> r)
        {
            var sample = new Features.FeaturesConfig();

            bool good = KmhConfigValidation.LoadTest("{ \"SchemaVersion\": 1, \"Treasury\": true, \"Wealth\": false }", sample, out _);
            r.Add(("Validation: good config load-tests OK", good, "loads into class"));

            // A type mismatch is valid JSON, so a parse-only check would pass it and lose the owner's setting.
            bool typeBad = !KmhConfigValidation.LoadTest("{ \"SchemaVersion\": \"not-a-number\" }", sample, out string e1);
            r.Add(("Validation: type mismatch fails", typeBad, e1));

            bool malformed = !KmhConfigValidation.LoadTest("{ not json ", sample, out _);
            r.Add(("Validation: malformed json fails", malformed, "rejected"));

            bool empty = !KmhConfigValidation.LoadTest("   ", sample, out _);
            r.Add(("Validation: empty fails", empty, "rejected"));

            // Prune owns unknown members, so they must not fail the load-test.
            bool extra = KmhConfigValidation.LoadTest("{ \"SchemaVersion\": 1, \"SomeOldKey\": 5 }", sample, out _);
            r.Add(("Validation: unknown members tolerated", extra, "extra key ignored"));

            Recovery(r, sample);
            Journal(r);
            Semantics(r);
        }

        private static void Semantics(List<(string, bool, string)> r)
        {
            var ok = KmhConfigValidator.CheckEconomy(5, 0, 0, 100_000_000);
            r.Add(("Semantics: clean economy has no issues", ok.Count == 0, "no issues"));

            var badTax = KmhConfigValidator.CheckEconomy(150, 0, 0, 100_000_000);
            var highTax = KmhConfigValidator.CheckEconomy(60, 0, 0, 100_000_000);
            r.Add(("Semantics: tax range checked",
                badTax.Exists(i => i.Severity == KmhValidationSeverity.Error && i.Where.Contains("Marketplace.TaxPercent"))
                && highTax.Exists(i => i.Severity == KmhValidationSeverity.Warning),
                "150% error, 60% warning"));

            // A missing key falls back to the always-valid class default, so a file never read would report clean.
            var missing = new List<string>();
            foreach ((string label, string path, object sample) in KmhConfigValidation.ConfigTargets())
            {
                Newtonsoft.Json.Linq.JObject shape =
                    Newtonsoft.Json.Linq.JObject.FromObject(sample);
                foreach (string key in KmhConfigValidator.KeysReadFrom(label))
                    if (shape.SelectToken(key) == null) missing.Add($"{label}.{key}");
            }
            r.Add(("Semantics: every key the validator reads exists in that config", missing.Count == 0,
                   missing.Count == 0 ? "no silently-defaulted reads" : string.Join(", ", missing)));

            var negCap = KmhConfigValidator.CheckEconomy(5, 0, 0, -1);
            r.Add(("Semantics: negative deposit cap errors",
                negCap.Exists(i => i.Severity == KmhValidationSeverity.Error && i.Where.Contains("MaxSilverDeposit")), "flagged"));

            var badPort = KmhConfigValidator.CheckTransport(true, 99999, 200, 6);
            var badPerIp = KmhConfigValidator.CheckTransport(true, 5099, 50, 100);
            r.Add(("Semantics: transport port + per-IP coherence",
                badPort.Exists(i => i.Severity == KmhValidationSeverity.Error && i.Where.Contains("KmhApiPort"))
                && badPerIp.Exists(i => i.Severity == KmhValidationSeverity.Warning && i.Where.Contains("MaxConnectionsPerIp")),
                "port error, per-IP warning"));

            var goodTp = KmhConfigValidator.CheckTransport(true, 5099, 200, 6);
            r.Add(("Semantics: valid transport has no error",
                !goodTp.Exists(i => i.Severity == KmhValidationSeverity.Error), "no error"));

            CrossFile(r);
            SchemaVersions(r);
        }

        // Every upgrade path keys on SchemaVersion, so a config class without one can never migrate.
        private static void SchemaVersions(List<(string, bool, string)> r)
        {
            var missing = new List<string>();
            foreach ((string name, _, object sample) in KmhConfigValidation.ConfigTargets())
                if (sample?.GetType().GetProperty("SchemaVersion") == null) missing.Add(name);

            r.Add(("config: every config class carries a SchemaVersion", missing.Count == 0,
                   missing.Count == 0 ? $"{System.Linq.Enumerable.Count(KmhConfigValidation.ConfigTargets())} config(s)"
                                      : "missing: " + string.Join(", ", missing)));

            // The default is generated from the type; this guards the readme wrapper added around it.
            Newtonsoft.Json.Linq.JObject doc = Features.Enforcement.EnforcementConfig.BuildDefaultFile();
            var absent = new List<string>();
            const System.Reflection.BindingFlags Serialized =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            foreach (var p in typeof(Features.Enforcement.EnforcementConfig).GetProperties(Serialized))
                if (p.CanRead && p.CanWrite && doc[p.Name] == null) absent.Add(p.Name);

            r.Add(("config: the generated Enforcement default matches its class", absent.Count == 0,
                   absent.Count == 0 ? "every property present" : "missing: " + string.Join(", ", absent)));
        }

        // Each file can be individually valid and still contradict the next; only a check that reads both sees it.
        private static void CrossFile(List<(string, bool, string)> r)
        {
            string[] hosts = { "discordapp.com", "i.imgur.com" };
            var chat  = new Features.Chat.ChatConfig();
            var media = new Features.Media.MediaConfig();

            var shipped = KmhConfigValidator.CheckCrossFile(
                chat.AllowImagePreviews, chat.MaxImageBytes, chat.ImageHostAllowList,
                media.Images.ResolverEnabled, media.Images.MaxSourceBytes, media.Images.MaxOutputBytes,
                media.Fetch.SourceHostAllowList, chat.AllowVideoLinks, media.Video.ServerEnabled);
            r.Add(("Semantics: the shipped defaults contradict nothing", shipped.Count == 0,
                   shipped.Count == 0 ? "clean" : string.Join(" | ", shipped.ConvertAll(i => i.ToString()))));

            var wasted = KmhConfigValidator.CheckCrossFile(false, 32L << 20, hosts, true, 12L << 20, 8L << 20,
                                                           hosts, false, false);
            r.Add(("Semantics: a resolver whose output nobody can see is reported",
                   wasted.Exists(i => i.Where == "Media/Chat"), Detail(wasted)));

            var tooBig = KmhConfigValidator.CheckCrossFile(true, 4L << 20, hosts, true, 12L << 20, 8L << 20,
                                                           hosts, false, false);
            r.Add(("Semantics: an output cap above the chat cap is reported",
                   tooBig.Exists(i => i.Where.Contains("MaxOutputBytes")), Detail(tooBig)));

            var unreachable = KmhConfigValidator.CheckCrossFile(true, 32L << 20, hosts, true, 4L << 20, 8L << 20,
                                                               hosts, false, false);
            r.Add(("Semantics: an output cap the source cap can never reach is reported",
                   unreachable.Exists(i => i.Where.Contains("MaxSourceBytes")), Detail(unreachable)));

            var hostGap = KmhConfigValidator.CheckCrossFile(true, 32L << 20, hosts, true, 12L << 20, 8L << 20,
                                                           new[] { "discordapp.com" }, false, false);
            bool named = hostGap.Exists(i => i.Where.Contains("SourceHostAllowList")
                                          && i.Message.Contains("i.imgur.com"));
            r.Add(("Semantics: a host chat accepts but the resolver cannot fetch is named", named, Detail(hostGap)));

            // An empty resolver list means "any https host", so there is no gap to report - only the openness.
            var openList = KmhConfigValidator.CheckCrossFile(true, 32L << 20, hosts, true, 12L << 20, 8L << 20,
                                                             new string[0], false, false);
            bool openHandled = !openList.Exists(i => i.Message.Contains("cannot be fetched"))
                            && openList.Exists(i => i.Where.Contains("SourceHostAllowList")
                                                 && i.Severity == KmhValidationSeverity.Info);
            r.Add(("Semantics: an empty resolver allowlist is read as unrestricted, not as a host gap",
                   openHandled, Detail(openList)));

            var videos = KmhConfigValidator.CheckCrossFile(true, 32L << 20, hosts, true, 12L << 20, 8L << 20,
                                                          hosts, true, false);
            r.Add(("Semantics: video links with no video server are explained, not flagged as broken",
                   videos.Exists(i => i.Where.Contains("AllowVideoLinks")
                                   && i.Severity == KmhValidationSeverity.Info), Detail(videos)));

            var off = KmhConfigValidator.CheckCrossFile(true, 4L << 20, hosts, false, 4L << 20, 8L << 20,
                                                        new[] { "discordapp.com" }, false, false);
            r.Add(("Semantics: a resolver that is off raises none of the resolver's findings", off.Count == 0,
                   Detail(off)));
        }

        private static string Detail(List<KmhValidationIssue> issues)
            => issues.Count == 0 ? "(none)" : string.Join(" | ", issues.ConvertAll(i => i.ToString()));

        private static void Journal(List<(string, bool, string)> r)
        {
            bool decide =
                !KmhMigrationJournal.NeedsRecovery(null) &&
                KmhMigrationJournal.NeedsRecovery(new KmhMigrationJournal { Status = KmhMigrationStatus.Started }) &&
                !KmhMigrationJournal.NeedsRecovery(new KmhMigrationJournal { Status = KmhMigrationStatus.Committed });
            r.Add(("Journal: needs-recovery decision", decide, "started recovers, committed/absent do not"));

            var j = new KmhMigrationJournal { Id = "abc", Build = "1.3.0", BackupDirName = "20260720-x", Status = KmhMigrationStatus.Started };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(j);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<KmhMigrationJournal>(json);
            bool roundTrip = back.Id == "abc" && back.BackupDirName == "20260720-x" && back.Status == KmhMigrationStatus.Started;
            r.Add(("Journal: round-trips", roundTrip, "id/backup/status preserved"));

            var j2 = new KmhMigrationJournal { Status = KmhMigrationStatus.Started };
            j2.Status = KmhMigrationStatus.Committed;
            r.Add(("Journal: commit is terminal", !KmhMigrationJournal.NeedsRecovery(j2), "committed = no recovery"));

            Transaction(r);
        }

        // The migration rewrites every tunable file from defaults; each guard here keeps that upgrade reversible.
        private static void Transaction(List<(string, bool, string)> r)
        {
            string journalPath = Persistence.KmhDataPaths.MigrationJournalFile;

            KmhMigrationJournal refused;
            using (Persistence.JsonFileStore.FailWritesForTest(p => p == journalPath ? "injected: disk full" : null))
                refused = KmhMigrationJournal.Begin("premigration-config-v3", out _);
            r.Add(("Journal: a journal that cannot be written blocks the migration", refused == null,
                   refused == null ? "Begin returned null" : "Begin handed back a journal that is not on disk"));

            // Non-vacuous: the same call has to succeed when the disk does, or the check above proves nothing.
            try
            {
                KmhMigrationJournal opened = KmhMigrationJournal.Begin("selftest-not-a-real-backup", out string err);
                r.Add(("Journal: a writable journal opens and reads back", opened != null, err ?? "opened"));
            }
            finally { KmhMigrationJournal.Clear(); }

            int before = Persistence.KmhDataMeta.AppliedConfigSchema;
            bool stamped;
            using (Persistence.JsonFileStore.FailWritesForTest(
                       p => p == Persistence.KmhDataPaths.MetaFile ? "injected: disk full" : null))
                stamped = Persistence.KmhDataMeta.StampConfigSchema(before + 1, out _);
            // A stamp only in memory makes the NEXT boot migrate again and reset the settings a second time.
            r.Add(("Schema: a stamp that cannot be persisted fails and does not advance in memory",
                   !stamped && Persistence.KmhDataMeta.AppliedConfigSchema == before,
                   $"stamped={stamped} applied={Persistence.KmhDataMeta.AppliedConfigSchema} (was {before})"));

            r.Add(("Schema: re-stamping a schema already applied is a no-op, not a failure",
                   before <= 0 || Persistence.KmhDataMeta.StampConfigSchema(before, out _), $"v{before}"));

            var ran = new List<string>();
            var steps = new List<KmhConfigMigration.Step>
            {
                new KmhConfigMigration.Step(2, "already applied", _ => { ran.Add("skipped"); return (1, null); }),
                new KmhConfigMigration.Step(3, "fine",            _ => { ran.Add("fine"); return (1, null); }),
                new KmhConfigMigration.Step(4, "refuses",         _ => { ran.Add("refuses"); return (0, "disk full"); }),
                new KmhConfigMigration.Step(5, "never reached",   _ => { ran.Add("after"); return (1, null); }),
            };
            string stepFail = KmhConfigMigration.RunSteps(steps, 2, new List<string>());
            r.Add(("Migration: a step that fails stops the run and is reported, not swallowed",
                   stepFail != null && stepFail.Contains("refuses") && stepFail.Contains("disk full")
                   && !ran.Contains("skipped") && !ran.Contains("after"),
                   $"{stepFail ?? "(reported success)"} | ran: {string.Join(",", ran)}"));

            var threw = new List<KmhConfigMigration.Step>
            {
                new KmhConfigMigration.Step(3, "throws", _ => throw new System.IO.IOException("no space")),
                new KmhConfigMigration.Step(4, "after",  _ => { ran.Add("after-throw"); return (1, null); }),
            };
            string thrownFail = KmhConfigMigration.RunSteps(threw, 2, new List<string>());
            r.Add(("Migration: a step that throws is a failure, not a logged warning",
                   thrownFail != null && thrownFail.Contains("throws") && !ran.Contains("after-throw"),
                   thrownFail ?? "(reported success)"));

            // Non-vacuous: the same runner has to return success when nothing fails.
            r.Add(("Migration: a clean run reports success",
                   KmhConfigMigration.RunSteps(
                       new List<KmhConfigMigration.Step>
                       { new KmhConfigMigration.Step(3, "fine", _ => (1, null)) }, 2, new List<string>()) == null,
                   "no failure reported"));

            UpgradeBanner(r);
            DoctorAfterUpgrade(r);

            var names =new List<string> { "20260101-a", "20260102-b", "20260103-premigration-config-v3", "20260104-d" };
            List<string> unpinned = Persistence.KmhDataBackup.PruneCandidates(names, 1);
            Persistence.KmhDataBackup.Pin("20260103-premigration-config-v3");
            List<string> pinned = Persistence.KmhDataBackup.PruneCandidates(names, 1);
            Persistence.KmhDataBackup.Unpin("20260103-premigration-config-v3");
            List<string> released = Persistence.KmhDataBackup.PruneCandidates(names, 1);

            // Retention prunes on every backup creation, so the rollback point has to outrank it while it is in use.
            r.Add(("Backup: retention cannot prune the backup a migration is relying on",
                   unpinned.Contains("20260103-premigration-config-v3")
                   && !pinned.Contains("20260103-premigration-config-v3")
                   && pinned.Count == unpinned.Count - 1
                   && released.Contains("20260103-premigration-config-v3"),
                   $"unpinned={unpinned.Count} pinned={pinned.Count} released={released.Count}"));

            // A restore queued for the next boot names a backup the boot backup's own pruning could delete.
            string marker = Persistence.KmhDataPaths.RestoreRequestFile;
            string had = System.IO.File.Exists(marker) ? System.IO.File.ReadAllText(marker) : null;
            bool queuedKept;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker));
                System.IO.File.WriteAllText(marker, "20260101-a");
                queuedKept = !Persistence.KmhDataBackup.PruneCandidates(names, 1).Contains("20260101-a");
            }
            finally
            {
                if (had == null) System.IO.File.Delete(marker); else System.IO.File.WriteAllText(marker, had);
            }
            bool releasedAfter = Persistence.KmhDataBackup.PruneCandidates(names, 1).Contains("20260101-a");
            r.Add(("Backup: retention cannot prune the backup queued for restore at the next boot",
                   queuedKept && releasedAfter, $"kept while queued={queuedKept}, prunable once the queue is gone={releasedAfter}"));
        }

        // Owners follow the banner's upgrade instructions, so a stale claim there is a real hazard.
        private static void UpgradeBanner(List<(string, bool, string)> r)
        {
            string text = string.Join("\n", KmhWhatsNewBanner.LinesForTest);
            bool claimsV2 = text.Contains("protocol stays at v2") || text.Contains("v1.2.x client still connects");
            r.Add(($"Banner: does not promise old clients still connect while protocol is v{SubProtocol.KmhProtocol.CurrentVersion}",
                   SubProtocol.KmhProtocol.CurrentVersion <= 2 || !claimsV2,
                   claimsV2 ? "STALE: the banner still advertises protocol v2" : "consistent"));

            bool regenerates = KmhConfigMigration.RegeneratesConfigs;
            bool claimsKept = text.Contains("migrated in place") || text.Contains("values you set are kept");
            r.Add(("Banner: does not promise settings are kept while the migration regenerates them",
                   !regenerates || !claimsKept,
                   claimsKept ? "STALE: the banner still says configs are migrated in place" : "consistent"));

            r.Add(("Banner: names the pre-migration backup an owner has to restore from",
                   !regenerates || text.Contains("premigration-config"), "restore path named"));

            // An owner already on v1.3.1 keeps their configs, so taking v1.3.1.1 must not tell them they were regenerated.
            string patch = string.Join("\n", KmhWhatsNewBanner.LinesFor("1.3.1.0"));
            string fromOld = string.Join("\n", KmhWhatsNewBanner.LinesFor("1.3.0.0"));
            bool patchWarns = patch.Contains("REGENERATED");
            bool oldWarns = fromOld.Contains("REGENERATED") && !fromOld.Contains("unchanged");
            string title = KmhWhatsNewBanner.Title("1.3.1.0");
            r.Add(("Banner: an update within v1.3.1 skips the regeneration warning that one from v1.3.0 still gets",
                   !patchWarns && patch.Contains("unchanged") && oldWarns && title.Contains("to " + KmhVersion.Display + "."),
                   $"within v1.3.1 warns={patchWarns}, from v1.3.0 warns={oldWarns}, title '{title.Trim()}'"));
        }

        // The doctor follows the banner: an upgrade that replaced the owner's values must not say they were kept.
        private static void DoctorAfterUpgrade(List<(string, bool, string)> r)
        {
            var steps = new List<KmhConfigMigration.Step> { new KmhConfigMigration.Step(KmhVersion.ConfigSchema, "selftest step", _ => (1, null)) };
            int applied = Persistence.KmhDataMeta.AppliedConfigSchema;
            string Says(int from)
            {
                // Stamped at the applied schema, a no-op, so the runner's meta is untouched.
                string fail = KmhConfigMigration.ApplySteps(
                    new KmhConfigMigrationPlan(true, from, applied, $"20261029-000000-premigration-config-v{from}", null), steps);
                if (fail != null) return "apply failed: " + fail;
                return string.Join(" | ", AdminCommands.KmhDoctor.Collect()
                    .FindAll(d => d.Text.Contains($"config schema {KmhVersion.ConfigSchema} this boot"))
                    .ConvertAll(d => d.Text + " -> " + d.Fix));
            }

            string fromV3, fromV5;
            using (KmhConfigMigration.KeepBootStateForTest())
            {
                fromV3 = Says(3);
                fromV5 = Says(5);
            }
            r.Add(("Doctor: an upgrade that regenerated configs says they were reset and names the backup holding the originals",
                   fromV3.Contains("reset to the v1.3.1 defaults") && fromV3.Contains("'20261029-000000-premigration-config-v3'")
                   && !fromV3.Contains("were kept"), fromV3));
            r.Add(("Doctor: an upgrade that did not regenerate still says the values were kept",
                   fromV5.Contains("your values were kept") && !fromV5.Contains("reset"), fromV5));
        }

        private static void Recovery(List<(string, bool, string)> r, Features.FeaturesConfig sample)
        {
            bool decide =
                KmhConfigRecovery.Decide(true,  true,  true)  == KmhConfigRecovery.Action.None            &&
                KmhConfigRecovery.Decide(false, true,  true)  == KmhConfigRecovery.Action.RestoreFromBackup &&
                KmhConfigRecovery.Decide(false, true,  false) == KmhConfigRecovery.Action.WarnManual       &&
                KmhConfigRecovery.Decide(false, false, false) == KmhConfigRecovery.Action.WarnManual;
            r.Add(("Recovery: decision matrix", decide, "restore only from a clean backup"));

            // This boot's own backup is the newest and holds the same broken file, so the newest copy is not the answer.
            var copies = new List<string> { "{ \"SchemaVersion\": \"broken\" }", "", "{ \"SchemaVersion\": 1, \"Treasury\": false }", "{ \"SchemaVersion\": 1 }" };
            int pick = KmhConfigRecovery.NewestThatLoads(copies, sample);
            int none = KmhConfigRecovery.NewestThatLoads(new List<string> { "{ \"SchemaVersion\": \"broken\" }" }, sample);
            r.Add(("Recovery: takes the newest backup copy that loads, past this boot's copy of the same broken file",
                   pick == 2 && none == -1, $"picked {pick}, all-broken {none}"));

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kmh_rec_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string active = System.IO.Path.Combine(dir, "Features.json");
                System.IO.File.WriteAllText(active, "{ \"SchemaVersion\": \"corrupt\" }");

                string goodBak = System.IO.Path.Combine(dir, "good.json");
                System.IO.File.WriteAllText(goodBak, "{ \"SchemaVersion\": 1, \"Treasury\": true }");
                bool restored = KmhConfigRecovery.RestoreFile(active, goodBak, sample, out _);
                bool activeNowValid = KmhConfigValidation.LoadTest(System.IO.File.ReadAllText(active), sample, out _);
                r.Add(("Recovery: restores a clean backup", restored && activeNowValid, "corrupt active replaced"));

                System.IO.File.WriteAllText(active, "{ \"SchemaVersion\": \"corrupt\" }");
                string badBak = System.IO.Path.Combine(dir, "bad.json");
                System.IO.File.WriteAllText(badBak, "{ \"SchemaVersion\": \"alsobad\" }");
                bool refused = !KmhConfigRecovery.RestoreFile(active, badBak, sample, out _);
                bool activeUnchanged = System.IO.File.ReadAllText(active).Contains("corrupt");
                r.Add(("Recovery: refuses a corrupt backup", refused && activeUnchanged, "active left as-is"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        private static void ConfigPrune(List<(string, bool, string)> r)
        {
            string json = "{ \"SchemaVersion\": 1, \"Treasury\": true, \"Wealth\": true, \"ObsoleteKey\": 1, \"AliasKey\": 2 }";
            var defaults = new Features.FeaturesConfig();

            string once = Persistence.JsonFileStore.PruneUnknownJson(json, defaults, new[] { "AliasKey" }, out var removed);
            bool ok1 = removed.Count == 1 && !once.Contains("ObsoleteKey")
                       && once.Contains("Treasury") && once.Contains("Wealth") && once.Contains("AliasKey");
            r.Add(("Migration: prune obsolete config keys", ok1, $"removed {removed.Count}, kept real keys + alias"));

            Persistence.JsonFileStore.PruneUnknownJson(once, defaults, new[] { "AliasKey" }, out var again);
            r.Add(("Migration: prune is idempotent", again.Count == 0, $"second pass removed {again.Count}"));

            // Shipped templates carry a "_readme" on no DTO, which pruning would delete on a new install's first boot.
            string noted = "{ \"SchemaVersion\": 1, \"_readme\": \"how to configure this\", \"Treasury\": true, \"ObsoleteKey\": 1 }";
            string keptNotes = Persistence.JsonFileStore.PruneUnknownJson(noted, defaults, null, out var removedNotes);
            bool notesKept = keptNotes.Contains("_readme") && !keptNotes.Contains("ObsoleteKey")
                             && removedNotes.Count == 1;
            r.Add(("Migration: an owner note survives the prune, an obsolete setting does not",
                   notesKept, $"removed {removedNotes.Count}: {string.Join(",", removedNotes)}"));

            // A v0/v1 server's flat Global.json: the v2 prune runs before the v7 tidy that moves these, so it must keep them.
            string legacy = "{ \"SchemaVersion\": 1, \"Enabled\": true, \"Description\": \"Our server\", \"Region\": \"NA\", \"Tags\": [\"vanilla\"], "
                          + "\"RegisteredServerId\": \"d89d16b84a25498198b9ad22f53b20ae\", \"SigningGeneration\": 2, \"LastSequence\": 52682, "
                          + "\"LastMetadataRevision\": 80, \"Retired\": 1 }";
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kmh-prune-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "Global.json");
                System.IO.File.WriteAllText(file, legacy);
                var report = new List<string>();
                KmhConfigMigration.PruneObsoleteConfigKeysIn(new[] { ("Global", file, (object)new Features.Global.GlobalConfig()) }, report);
                var doc = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(file));
                bool retiredGone = doc["Retired"] == null;
                Persistence.JsonFileStore.ApplyMoves(doc, Features.Global.GlobalConfig.Moves);
                var g = Newtonsoft.Json.JsonConvert.DeserializeObject<Features.Global.GlobalConfig>(doc.ToString());
                bool listingAndIdentityKept = g.Listing?.Description == "Our server" && g.Listing.Region == "NA" && g.Listing.Tags?.Length == 1
                    && g.RegisteredServerId == "d89d16b84a25498198b9ad22f53b20ae" && g.SigningGeneration == 2 && g.LastSequence == 52682
                    && g.LastMetadataRevision == 80 && retiredGone;
                r.Add(("Migration: the v2 prune keeps what the v7 tidy moves - a v0/v1 server's listing and Global identity",
                       listingAndIdentityKept, $"{string.Join("; ", report)}; listing '{g.Listing?.Description}', id '{g.RegisteredServerId}'"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        private static void WorldEventPurge(List<(string, bool, string)> r)
        {
            long now = System.DateTime.UtcNow.Ticks;
            long hour = System.TimeSpan.TicksPerHour;

            // Includes a live event the normal sweep owns, so the migration must leave it alone.
            var events = new List<WorldEventDto>
            {
                new WorldEventDto { Type = "world_weather", Title = "stuck eclipse", EndsUtcTicks = 0 },
                new WorldEventDto { Type = "house_stipend", Title = "fired stipend", EndsUtcTicks = 0 },
                new WorldEventDto { Type = "market_boom",   Title = "live boom",     EndsUtcTicks = now + hour },
                new WorldEventDto { Type = "market_crash",  Title = "expiring soon", EndsUtcTicks = now + 60 },
            };

            int removed = events.RemoveAll(WorldStore.IsEndless);
            bool keptLive = events.Exists(e => e.Title == "live boom") && events.Exists(e => e.Title == "expiring soon");
            bool droppedStuck = !events.Exists(e => e.EndsUtcTicks <= 0);

            r.Add(("Migration: purge endless events",
                removed == 2 && keptLive && droppedStuck && events.Count == 2,
                $"removed {removed} stuck, kept {events.Count} timed"));

            int again = events.RemoveAll(WorldStore.IsEndless);
            r.Add(("Migration: purge is idempotent", again == 0 && events.Count == 2,
                $"second pass removed {again}"));
        }
    }
}
