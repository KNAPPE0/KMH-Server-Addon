using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Reports;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.AdminCommands
{
    // Readable here whether or not a report went further; one sent only to the developers is not.
    internal static class KmhReportCommands
    {
        private const int ListRows = 20;
        internal const int MaxNoteChars = 500;

        public static void Run(string[] args, Action<string> reply)
        {
            string sub = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            string arg = args != null && args.Length > 2 ? args[2] : "";
            switch (sub)
            {
                case "list":    List(arg, reply); break;
                case "show":    Show(arg, reply); break;
                case "status":  Status(args, reply); break;
                case "delete":  Delete(arg, reply); break;
                case "forward": Forward(arg, reply); break;
                case "retry":
                    int n = ReportForwarder.Requeue();
                    reply(n > 0 ? $"{n} report(s) queued for Colony Nexus Global again."
                                : ReportForwarder.CanForward(out string why)
                                    ? "Nothing to retry - every report was sent, is still waiting its turn, or was not meant for the developers."
                                    : $"Nothing was queued: {why}.");
                    break;
                default:
                    reply("Usage: kmh reports [list [open|all|bug|suggestion|question] | show <ref> | status <ref> <state> [note] | "
                        + "delete <ref> | retry | forward <on|off>]");
                    break;
            }
        }

        // Players already connected learn the change when they rejoin.
        private static void Forward(string value, Action<string> reply)
        {
            Features.Global.GlobalConfig cfg = Features.Global.GlobalConfig.Current;
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v != "on" && v != "off")
            {
                reply($"Forwarding to Colony Nexus Global is {(cfg.ForwardReports ? "ON" : "off")}. Usage: kmh reports forward <on|off>");
                return;
            }
            bool on = v == "on";
            if (cfg.ForwardReports == on) { reply($"Forwarding is already {(on ? "on" : "off")}."); return; }

            cfg.ForwardReports = on;
            bool written = cfg.Save();
            if (on)
            {
                bool ready = ReportForwarder.CanForward(out string why);
                reply("Reports players send to the Colony Nexus developers now go on to Colony Nexus Global"
                    + (ready ? "." : $" - but not yet: {why}.")
                    + " Reports already kept here stay here; 'kmh reports retry' sends those too.");
            }
            else reply("New reports stay on this server only. Copies Colony Nexus Global already holds stay there.");
            if (!written) reply("KMH-Data could not be written - this lasts until the next restart.");
        }

        private static void List(string filter, Action<string> reply)
        {
            string f = string.IsNullOrWhiteSpace(filter) ? "open" : filter.Trim().ToLowerInvariant();
            if (f != "open" && f != "all" && !ReportKinds.IsKnown(f))
            {
                reply("Usage: kmh reports list [open|all|bug|suggestion|question]");
                return;
            }
            List<StoredReport> staff = Visible();
            List<StoredReport> rows = staff.FindAll(r => f == "all" || (f == "open" ? ReportStatuses.IsOpen(r.StatusOrNew) : r.Kind == f));
            bool forward = ReportForwarder.CanForward(out string why);
            reply($"=== Player reports === {rows.Count} {(f == "all" ? "" : f + " ")}of {staff.Count} on this server · "
                + (forward ? "those meant for the developers go on to Colony Nexus Global" : $"kept here only ({why})"));
            if (!Features.FeaturesConfig.Current.Reports)
                reply("Reports are switched off (Features.json Reports=false): players cannot send new ones.");
            if (rows.Count == 0)
            {
                reply(staff.Count == 0 ? "  None yet. Players send them with the Report button in the Colony Nexus tab."
                                       : "  None match. 'kmh reports list all' shows every one.");
                return;
            }

            for (int i = 0; i < rows.Count && i < ListRows; i++)
            {
                StoredReport r = rows[i];
                string when = DateTimeOffset.FromUnixTimeSeconds(r.ReceivedAtUnix).UtcDateTime.ToString("MM-dd HH:mm");
                string tag = r.Area + (string.IsNullOrEmpty(r.Severity) ? "" : "/" + r.Severity);
                reply($"  {when}  {ReportText.Short(r.ReportId)}  {r.Kind,-10} {r.StatusOrNew,-12} {tag,-22} {r.Reporter}: {r.Title}");
            }
            if (rows.Count > ListRows) reply($"  ... and {rows.Count - ListRows} older. 'kmh reports show <ref>' reads one.");
            else reply("  'kmh reports show <ref>' reads one; the first few letters of the reference are enough.");
            if (f == "open" && rows.Count < staff.Count) reply($"  {staff.Count - rows.Count} closed - 'kmh reports list all' shows them too.");
        }

        private static void Show(string idOrPrefix, Action<string> reply)
        {
            StoredReport r = Find(idOrPrefix, reply);
            if (r == null) return;
            reply($"=== Report {r.ReportId} ===");
            reply($"  {r.Kind} from {r.Reporter}{(string.IsNullOrEmpty(r.ReporterGlobalId) ? "" : " (" + r.ReporterGlobalId + ")")}"
                + $", client {(string.IsNullOrEmpty(r.ClientVersion) ? "?" : r.ClientVersion)}");
            reply($"  received {DateTimeOffset.FromUnixTimeSeconds(r.ReceivedAtUnix).UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
            reply($"  area: {r.Area}   severity: {Said(r.Severity)}   how often: {Said(r.Frequency)}");
            reply($"  sent to: {(string.IsNullOrEmpty(r.Destinations) ? "server,developers (client did not say)" : r.Destinations)}");
            reply($"  status: {r.StatusOrNew}"
                + (r.StatusAtUnix > 0 ? $" since {DateTimeOffset.FromUnixTimeSeconds(r.StatusAtUnix).UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "")
                + (string.IsNullOrEmpty(r.StaffNote) ? "" : $"   note: {r.StaffNote}"));
            string versions = ClientVersions(r.ClientDiagnostics);
            if (versions.Length > 0) reply("  client: " + versions);
            reply($"  title: {r.Title}");
            foreach (string line in (string.IsNullOrEmpty(r.Body) ? "(no description)" : r.Body).Split('\n'))
                reply("  | " + line);
            reply($"  diagnostics: {r.ClientDiagnostics.Length:N0} chars from the game, {r.ServerDiagnostics.Length:N0} from this server"
                + (string.IsNullOrEmpty(r.ScreenshotFile) ? ", no screenshot" : ", screenshot " + r.ScreenshotFile));
            reply($"  Colony Nexus Global: {State(r)}"
                + (r.ForwardState == ReportForwardStates.Sent && !string.IsNullOrEmpty(r.ForwardedId) ? $" as {r.ForwardedId}" : "")
                + (string.IsNullOrEmpty(r.LastError) ? "" : $" (last answer: {r.LastError})"));
            reply($"  everything else: KMH-Data/Reports/{r.ReportId}.json   triage: kmh reports status {ReportText.Short(r.ReportId)} <state> [note]");
        }

        private static void Status(string[] args, Action<string> reply)
        {
            string usage = "Usage: kmh reports status <ref> <" + string.Join("|", ReportStatuses.All) + "> [note]";
            if (args == null || args.Length < 4) { reply(usage); return; }
            int noteAt = 4;
            string state = ReportStatuses.Parse(args[3]);
            // "won't fix" and "in progress" arrive as two words.
            if (state.Length == 0 && args.Length > 4 && (state = ReportStatuses.Parse(args[3] + " " + args[4])).Length > 0) noteAt = 5;
            if (state.Length == 0) { reply($"'{args[3]}' is not a state. " + usage); return; }
            StoredReport r = Find(args[2], reply);
            if (r == null) return;

            string note = args.Length > noteAt ? string.Join(" ", args, noteAt, args.Length - noteAt) : null;
            string before = r.StatusOrNew;
            r.Status = state;
            r.StatusAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (note != null) r.StaffNote = ReportText.Clean(note, MaxNoteChars, multiline: false);
            if (!ReportStore.Update(r)) { reply("The report could not be written - nothing changed."); return; }
            reply($"Report {ReportText.Short(r.ReportId)}: {before} -> {state}"
                + (note != null ? $" (note: {r.StaffNote})" : string.IsNullOrEmpty(r.StaffNote) ? "" : $" (note kept: {r.StaffNote})"));
        }

        private static void Delete(string idOrPrefix, Action<string> reply)
        {
            StoredReport r = Find(idOrPrefix, reply);
            if (r == null) return;
            reply(ReportStore.Delete(r.ReportId)
                ? $"Deleted report {ReportText.Short(r.ReportId)} from this server. A copy Colony Nexus Global already holds stays there."
                : "Nothing was deleted - the files could not be removed.");
        }

        private static string State(StoredReport r) => r.ForwardState switch
        {
            ReportForwardStates.Sent    => "sent",
            ReportForwardStates.Pending => "queued",
            ReportForwardStates.Failed  => "failed",
            _                           => r.WantsDevelopers ? "local" : "not meant for the developers",
        };

        private static string Said(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        internal static string ClientVersions(string clientDiagnostics)
        {
            if (string.IsNullOrEmpty(clientDiagnostics)) return "";
            try
            {
                if (!(JObject.Parse(clientDiagnostics)["summary"] is JObject s)) return "";
                var parts = new List<string>();
                void Add(string label, string key) { string v = (string)s[key]; if (!string.IsNullOrEmpty(v)) parts.Add(label + " " + v); }
                Add("Colony Nexus", "kmh_version");
                Add("build", "build");
                Add("protocol", "protocol");
                Add("RimWorld", "rimworld");
                Add("RimWorld Together", "rimworld_together");
                Add("fingerprint", "fingerprint");
                return string.Join(", ", parts.ToArray());
            }
            catch { return ""; }
        }

        // Relayed reports are the developers' alone; staff never see them here.
        private static List<StoredReport> Visible() => ReportStore.List().FindAll(r => r.StaffVisible);

        private static StoredReport Find(string idOrPrefix, Action<string> reply)
        {
            string p = (idOrPrefix ?? "").Trim();
            if (p.Length < 4) { reply("Give at least the first four characters of the report reference ('kmh reports' lists them)."); return null; }
            StoredReport match = null;
            foreach (StoredReport r in Visible())
            {
                if (!r.ReportId.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) { reply($"More than one report starts with '{p}' - give more of the reference."); return null; }
                match = r;
            }
            if (match == null) reply($"No report starts with '{p}'.");
            return match;
        }
    }
}
