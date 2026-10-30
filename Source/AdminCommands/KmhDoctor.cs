using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.AdminCommands
{
    internal enum DoctorLevel { Ok, Info, Warning, Problem }

    internal readonly struct DoctorFinding
    {
        public readonly DoctorLevel Level;
        public readonly string Text;
        public readonly string Fix;
        public DoctorFinding(DoctorLevel level, string text, string fix = "") { Level = level; Text = text; Fix = fix; }
    }

    // Read-only by design: it reports every check worst first with its fix, and changes nothing.
    internal static class KmhDoctor
    {
        public static List<DoctorFinding> Collect()
        {
            List<DoctorFinding> found = new List<DoctorFinding>();
            void Add(DoctorLevel l, string text, string fix = "") => found.Add(new DoctorFinding(l, text, fix));

            if (!Maintenance.KmhReadiness.IsReady)
                Add(DoctorLevel.Problem, "Colony Nexus is not serving players: " + Maintenance.KmhReadiness.Describe(),
                    "Read the boot log above this line; 'kmh status' shows the same state.");
            if (Persistence.JsonFileStore.DamageReported(out string damage))
                Add(DoctorLevel.Problem, "A KMH-Data file is damaged: " + damage, "Restore it with 'kmh backups' and 'kmh restore <name>'.");

            try
            {
                foreach (Maintenance.KmhValidationIssue i in Maintenance.KmhConfigValidator.ValidateFiles())
                    Add(i.Severity == Maintenance.KmhValidationSeverity.Error ? DoctorLevel.Problem
                        : i.Severity == Maintenance.KmhValidationSeverity.Warning ? DoctorLevel.Warning : DoctorLevel.Info,
                        i.Where + ": " + i.Message, "Edit it in KMH-Data/Config, then 'kmh reload'.");
            }
            catch (Exception ex) { Add(DoctorLevel.Warning, "Config check could not run: " + ex.Message); }

            try
            {
                var byFile = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                foreach ((string file, string key) in Maintenance.KmhConfigKeyScan.Unknown())
                {
                    if (!byFile.TryGetValue(file, out List<string> keys)) byFile[file] = keys = new List<string>();
                    keys.Add(key);
                }
                foreach (KeyValuePair<string, List<string>> kv in byFile)
                    Add(DoctorLevel.Warning,
                        $"Config/{kv.Key}.json has {kv.Value.Count} setting(s) Colony Nexus does not recognise, so they do nothing: "
                        + string.Join(", ", kv.Value.GetRange(0, Math.Min(6, kv.Value.Count))) + (kv.Value.Count > 6 ? ", ..." : ""),
                        $"Usually a typo. Compare with 'kmh config {kv.Key.ToLowerInvariant()} all' and Config/README.txt.");
            }
            catch (Exception ex) { Add(DoctorLevel.Info, "Unknown-setting scan could not run: " + ex.Message); }

            Features.Transport.TransportConfig t = Features.Transport.TransportConfig.Current;
            if (Features.Transport.KmhApiServer.StartRefusal is string refused)
                Add(DoctorLevel.Problem, "The Colony Nexus API transport refused to start: " + refused,
                    "Fix Config/Transport.json, then restart the server. Players use the slower chat transport until then.");
            else if (t.EnableKmhApiTransport && !Features.Transport.KmhApiServer.Running && Maintenance.KmhReadiness.IsReady)
                Add(DoctorLevel.Warning, $"The Colony Nexus API port {t.KmhApiPort} is not listening, so players use the slower chat transport.",
                    "Check that nothing else uses the port, or set KmhApiPort in Config/Transport.json.");

            GlobalConfig g = GlobalConfig.Current;
            foreach (GlobalFinding f in GlobalConfigCheck.Inspect(g, GlobalSetupState.Current_.IsRegistered))
                Add(f.Level == GlobalFindingLevel.Blocker ? DoctorLevel.Problem
                    : f.Level == GlobalFindingLevel.Warn ? DoctorLevel.Warning : DoctorLevel.Info, f.Message, f.Action);

            // Federation activity waits for Global; past 7 days Global no longer counts it, and the queue is bounded.
            long oldest = Features.Global.Federation.FederationOutbox.OldestPendingUnix;
            long ageHours = oldest > 0 ? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - oldest) / 3600 : 0;
            if (ageHours >= 5 * 24)
                Add(DoctorLevel.Warning, $"Guild Federation activity has waited {ageHours / 24} day(s) to reach Colony Nexus Global; "
                    + "Global does not count reports older than 7 days.", "'kmh federation status' shows why it is not getting through.");
            long lost = Features.Global.Federation.FederationOutbox.LostTotal;
            if (lost > 0)
                Add(DoctorLevel.Info, $"{lost} Guild Federation activity report(s) from this server were never counted (queue limit, too old, or the link ended).",
                    "'kmh federation status' breaks them down.");

            if (Maintenance.KmhConfigMigration.RanThisBoot)
                Add(DoctorLevel.Info, $"Settings were upgraded to config schema {KmhVersion.ConfigSchema} this boot; your values were kept.",
                    "'kmh migration-report' lists exactly what moved.");

            found.Sort((a, b) => b.Level.CompareTo(a.Level));
            return found;
        }

        public static string Summary(List<DoctorFinding> found)
        {
            int problems = found.FindAll(f => f.Level == DoctorLevel.Problem).Count;
            int warnings = found.FindAll(f => f.Level == DoctorLevel.Warning).Count;
            if (problems == 0 && warnings == 0) return "Health: all checks passed.";
            return $"Health: {problems} problem(s), {warnings} warning(s). Run 'kmh doctor' for what to do.";
        }

        public static void Run(Action<string> reply)
        {
            List<DoctorFinding> found = Collect();
            reply("=== Colony Nexus doctor: " + KmhVersion.Summary + " ===");
            reply(Summary(found));
            if (found.Count == 0) { reply("  Nothing to fix."); return; }
            foreach (DoctorFinding f in found)
            {
                string tag = f.Level == DoctorLevel.Problem ? "PROBLEM" : f.Level == DoctorLevel.Warning ? "WARNING" : "INFO";
                reply($"  [{tag}] {f.Text}");
                if (f.Fix.Length > 0) reply($"           -> {f.Fix}");
            }
        }

        // Once, at the end of boot, so the owner sees the verdict without asking for it.
        public static void LogBootSummary()
        {
            try
            {
                List<DoctorFinding> found = Collect();
                string line = Summary(found);
                if (found.Exists(f => f.Level == DoctorLevel.Problem)) Diagnostics.ServerLog.Warn(line);
                else Diagnostics.ServerLog.Info(line);
            }
            catch (Exception ex) { Diagnostics.ServerLog.Verbose("Health summary skipped: " + ex.Message); }
        }
    }
}
