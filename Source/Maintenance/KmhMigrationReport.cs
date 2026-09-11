using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Persisted so an owner can review a migration afterwards rather than catching it scroll past in the log.
    internal static class KmhMigrationReport
    {
        internal struct Inputs
        {
            public string TimestampUtc;
            public string Build;
            public bool   FreshInstall;
            public int    ConfigSchemaFrom, ConfigSchemaTo, DataSchema;
            public int    FieldsBackfilled;
            public int    DefaultsChanged, DefaultsPreserved;
            public List<string> Steps;   // per-step lines from KmhConfigMigration
        }

        // Deterministic: identical inputs produce identical text, so a re-run report can be diffed.
        public static List<string> Build(Inputs i)
        {
            var lines = new List<string>
            {
                $"KMH migration report - {i.TimestampUtc}",
                $"  build: {i.Build}",
                i.FreshInstall
                    ? "  install: fresh (configs generated with current defaults)"
                    : "  install: upgraded from existing data",
                $"  config schema: v{i.ConfigSchemaFrom} -> v{i.ConfigSchemaTo}",
                $"  data schema:   v{i.DataSchema}",
            };

            if (i.FieldsBackfilled > 0)
                lines.Add($"  backfilled {i.FieldsBackfilled} missing field(s) with safe defaults (owner values kept)");
            if (i.DefaultsChanged > 0 || i.DefaultsPreserved > 0)
                lines.Add($"  defaults: {i.DefaultsChanged} updated to current, {i.DefaultsPreserved} kept as owner-set");

            if (i.Steps != null && i.Steps.Count > 0)
            {
                lines.Add("  migration steps:");
                foreach (string s in i.Steps) lines.Add($"    - {s}");
            }
            else if (i.ConfigSchemaFrom != i.ConfigSchemaTo)
            {
                lines.Add("  migration steps: none needed for this boot");
            }

            if (i.FieldsBackfilled == 0 && i.DefaultsChanged == 0 && (i.Steps == null || i.Steps.Count == 0)
                && i.ConfigSchemaFrom == i.ConfigSchemaTo && !i.FreshInstall)
                lines.Add("  no changes: config already current");

            lines.Add("  originals preserved in KMH-Data-Backups (boot backup).");
            return lines;
        }

        // Did this boot actually do anything? Boot writes a report every time, so this decides what is worth keeping.
        internal static bool AnyChange(Inputs i)
            => i.FreshInstall
            || i.ConfigSchemaFrom != i.ConfigSchemaTo
            || i.FieldsBackfilled  > 0
            || i.DefaultsChanged   > 0
            || i.DefaultsPreserved > 0
            || (i.Steps != null && i.Steps.Count > 0);

        // Never throws, because a failed report must not stop the server.
        public static void Write(Inputs inputs)
        {
            try
            {
                List<string> lines = Build(inputs);
                Directory.CreateDirectory(KmhDataPaths.MigrationsDir);
                string body = string.Join(Environment.NewLine, lines);
                File.WriteAllText(KmhDataPaths.MigrationReportLatest, body);

                // Boot calls this unconditionally, so archiving every time would file one "no changes" copy per restart.
                if (!AnyChange(inputs)) return;
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.WriteAllText(Path.Combine(KmhDataPaths.MigrationsDir, $"migration-{stamp}.txt"), body);
            }
            catch (Exception ex) { ServerLog.Warn($"Migration report: could not write ({ex.Message})."); }
        }

        public static List<string> ReadLatest()
        {
            try
            {
                if (File.Exists(KmhDataPaths.MigrationReportLatest))
                    return new List<string>(File.ReadAllLines(KmhDataPaths.MigrationReportLatest));
            }
            catch (Exception ex) { ServerLog.Warn($"Migration report: could not read ({ex.Message})."); }
            return new List<string> { "No migration report found." };
        }
    }
}
