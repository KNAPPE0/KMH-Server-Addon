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
            return results;
        }

        private static void Report(List<(string, bool, string)> r)
        {
            // An upgrade with changes lists them and never says "no changes".
            var changed = KmhMigrationReport.Build(new KmhMigrationReport.Inputs
            {
                TimestampUtc = "T", Build = "1.3.0", FreshInstall = false,
                ConfigSchemaFrom = 1, ConfigSchemaTo = 2, DataSchema = 1,
                FieldsBackfilled = 3, DefaultsChanged = 2, DefaultsPreserved = 1,
                Steps = new List<string> { "removed 4 stuck world event(s)" },
            });
            bool ok1 = changed.Exists(l => l.Contains("v1 -> v2"))
                    && changed.Exists(l => l.Contains("backfilled 3"))
                    && changed.Exists(l => l.Contains("removed 4 stuck"))
                    && !changed.Exists(l => l.Contains("no changes"));
            r.Add(("Migration report: upgrade lists changes", ok1, $"{changed.Count} lines"));

            // A no-op boot (already current) says so and lists no steps.
            var noop = KmhMigrationReport.Build(new KmhMigrationReport.Inputs
            {
                TimestampUtc = "T", Build = "1.3.0", FreshInstall = false,
                ConfigSchemaFrom = 2, ConfigSchemaTo = 2, DataSchema = 1,
                FieldsBackfilled = 0, DefaultsChanged = 0, DefaultsPreserved = 0, Steps = null,
            });
            r.Add(("Migration report: no-op says no changes",
                noop.Exists(l => l.Contains("no changes")) && !noop.Exists(l => l.Contains("backfilled")),
                $"{noop.Count} lines"));

            // Fresh install is labelled as such, not as an upgrade.
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

            // Unknown members are tolerated (prune removes them; they must not fail the load-test).
            bool extra = KmhConfigValidation.LoadTest("{ \"SchemaVersion\": 1, \"SomeOldKey\": 5 }", sample, out _);
            r.Add(("Validation: unknown members tolerated", extra, "extra key ignored"));

            Recovery(r, sample);
            Journal(r);
            Semantics(r);
        }

        private static void Semantics(List<(string, bool, string)> r)
        {
            // A clean economy config produces no issues.
            var ok = KmhConfigValidator.CheckEconomy(5, 0, 0, 100_000_000);
            r.Add(("Semantics: clean economy has no issues", ok.Count == 0, "no issues"));

            // An out-of-range tax is an Error; a very high (in-range) tax is a Warning.
            var badTax = KmhConfigValidator.CheckEconomy(150, 0, 0, 100_000_000);
            var highTax = KmhConfigValidator.CheckEconomy(60, 0, 0, 100_000_000);
            r.Add(("Semantics: tax range checked",
                badTax.Exists(i => i.Severity == KmhValidationSeverity.Error && i.Where.Contains("MarketplaceTax"))
                && highTax.Exists(i => i.Severity == KmhValidationSeverity.Warning),
                "150% error, 60% warning"));

            // Negative deposit cap is an Error.
            var negCap = KmhConfigValidator.CheckEconomy(5, 0, 0, -1);
            r.Add(("Semantics: negative deposit cap errors",
                negCap.Exists(i => i.Severity == KmhValidationSeverity.Error && i.Where.Contains("MaxSilverDeposit")), "flagged"));

            // Invalid port errors; per-IP over total warns (the cross-field rule).
            var badPort = KmhConfigValidator.CheckTransport(true, 99999, 200, 6);
            var badPerIp = KmhConfigValidator.CheckTransport(true, 5099, 50, 100);
            r.Add(("Semantics: transport port + per-IP coherence",
                badPort.Exists(i => i.Severity == KmhValidationSeverity.Error && i.Where.Contains("KmhApiPort"))
                && badPerIp.Exists(i => i.Severity == KmhValidationSeverity.Warning && i.Where.Contains("MaxConnectionsPerIp")),
                "port error, per-IP warning"));

            // A valid transport config produces at most an Info (API off), never an Error.
            var goodTp = KmhConfigValidator.CheckTransport(true, 5099, 200, 6);
            r.Add(("Semantics: valid transport has no error",
                !goodTp.Exists(i => i.Severity == KmhValidationSeverity.Error), "no error"));
        }

        private static void Journal(List<(string, bool, string)> r)
        {
            // Pure decision: an incomplete journal (never Committed) needs recovery; a committed or absent one does not.
            bool decide =
                !KmhMigrationJournal.NeedsRecovery(null) &&
                KmhMigrationJournal.NeedsRecovery(new KmhMigrationJournal { Status = KmhMigrationStatus.Started }) &&
                !KmhMigrationJournal.NeedsRecovery(new KmhMigrationJournal { Status = KmhMigrationStatus.Committed });
            r.Add(("Journal: needs-recovery decision", decide, "started recovers, committed/absent do not"));

            // Round-trip: a journal survives serialization with its status and backup reference intact.
            var j = new KmhMigrationJournal { Id = "abc", Build = "1.3.0", BackupDirName = "20260720-x", Status = KmhMigrationStatus.Started };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(j);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<KmhMigrationJournal>(json);
            bool roundTrip = back.Id == "abc" && back.BackupDirName == "20260720-x" && back.Status == KmhMigrationStatus.Started;
            r.Add(("Journal: round-trips", roundTrip, "id/backup/status preserved"));

            // Commit moves it to a terminal status that no longer needs recovery.
            var j2 = new KmhMigrationJournal { Status = KmhMigrationStatus.Started };
            j2.Status = KmhMigrationStatus.Committed;
            r.Add(("Journal: commit is terminal", !KmhMigrationJournal.NeedsRecovery(j2), "committed = no recovery"));
        }

        private static void Recovery(List<(string, bool, string)> r, Features.FeaturesConfig sample)
        {
            bool decide =
                KmhConfigRecovery.Decide(true,  true,  true)  == KmhConfigRecovery.Action.None            &&  // already valid
                KmhConfigRecovery.Decide(false, true,  true)  == KmhConfigRecovery.Action.RestoreFromBackup && // clean backup
                KmhConfigRecovery.Decide(false, true,  false) == KmhConfigRecovery.Action.WarnManual       &&  // backup also bad
                KmhConfigRecovery.Decide(false, false, false) == KmhConfigRecovery.Action.WarnManual;          // no backup
            r.Add(("Recovery: decision matrix", decide, "restore only from a clean backup"));

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kmh_rec_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string active = System.IO.Path.Combine(dir, "Features.json");
                System.IO.File.WriteAllText(active, "{ \"SchemaVersion\": \"corrupt\" }");   // fails load-test

                // A clean backup is restored over the corrupt active file.
                string goodBak = System.IO.Path.Combine(dir, "good.json");
                System.IO.File.WriteAllText(goodBak, "{ \"SchemaVersion\": 1, \"Treasury\": true }");
                bool restored = KmhConfigRecovery.RestoreFile(active, goodBak, sample, out _);
                bool activeNowValid = KmhConfigValidation.LoadTest(System.IO.File.ReadAllText(active), sample, out _);
                r.Add(("Recovery: restores a clean backup", restored && activeNowValid, "corrupt active replaced"));

                // A backup that is ALSO corrupt must be refused - never swap one bad file for another.
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

            // The shipped templates carry a "_readme" that is on no DTO, so pruning deleted it on the first boot of a new install.
            string noted = "{ \"SchemaVersion\": 1, \"_readme\": \"how to configure this\", \"Treasury\": true, \"ObsoleteKey\": 1 }";
            string keptNotes = Persistence.JsonFileStore.PruneUnknownJson(noted, defaults, null, out var removedNotes);
            bool notesKept = keptNotes.Contains("_readme") && !keptNotes.Contains("ObsoleteKey")
                             && removedNotes.Count == 1;
            r.Add(("Migration: an owner note survives the prune, an obsolete setting does not",
                   notesKept, $"removed {removedNotes.Count}: {string.Join(",", removedNotes)}"));
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

            // Idempotency: a second pass over the already-clean list must remove nothing.
            int again = events.RemoveAll(WorldStore.IsEndless);
            r.Add(("Migration: purge is idempotent", again == 0 && events.Count == 2,
                $"second pass removed {again}"));
        }
    }
}
