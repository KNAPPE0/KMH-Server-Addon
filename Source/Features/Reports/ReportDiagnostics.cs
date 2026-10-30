using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Reports
{
    // Never a path on the owner's disk; every console line is redacted.
    internal static class ReportDiagnostics
    {
        // Global's copy keeps only warnings, errors and the reporter's lines, and names no other player.
        public static (string Full, string Forward) Build(ServerClient client, string user, DateTimeOffset now, JObject report = null)
        {
            JObject full = new JObject
            {
                ["server"]    = Section(Server),
                ["global"]    = Section(GlobalState),
                ["transport"] = Section(Transport),
                ["runtime"]   = Section(Runtime),
                ["reporter"]  = Section(() => Reporter(client, user)),
                ["captured_at"] = now.ToUniversalTime().ToString("u"),
            };
            // What this server validated, so Global need not trust the client's summary alone.
            if (report != null) full["report"] = report.DeepClone();

            List<string> console = ConsoleTail(ReportLimits.ConsoleLines);
            JObject forward = (JObject)full.DeepClone();
            full["console"] = new JArray(console.ToArray());
            ICollection<string> accounts = Identity.StaffRegistry.Accounts(TimeSpan.FromMinutes(1))?.Keys;
            forward["console"] = new JArray(console.FindAll(l => Forwardable(l, user))
                .ConvertAll(l => OthersHidden(Maintenance.KmhRedact.Addresses(l), user, accounts)).ToArray());
            return (Fit(full), Fit(forward));
        }

        // One section failing must not cost the report the others.
        private static JToken Section(Func<JObject> build)
        {
            try { return build(); }
            catch (Exception ex) { return new JObject { ["unavailable"] = ex.GetType().Name }; }
        }

        private static JObject Server()
        {
            Maintenance.KmhStatusExport.KmhStatus s = Maintenance.KmhStatusExport.Build();
            return new JObject
            {
                ["kmh_build"]            = KmhVersion.Build,
                ["build_tag"]            = KmhVersion.BuildTag,
                ["protocol"]             = KmhVersion.Protocol,
                ["config_schema"]        = KmhVersion.ConfigSchema,
                ["data_schema"]          = KmhVersion.DataSchema,
                ["rwt_version"]          = GlobalStatusProbe.RwtVersion(),
                ["uptime_seconds"]       = s.UptimeSeconds,
                ["players"]              = s.Players,
                ["verified_clients"]     = s.VerifiedClients,
                ["guilds"]               = s.Guilds,
                ["sites"]                = s.Sites,
                ["disabled_features"]    = string.Join(",", (s.DisabledFeatures ?? new List<string>()).ToArray()),
                ["persistence_degraded"] = Persistence.JsonFileStore.AnyStoreUnwritable,
                ["log_queue"]            = KmhLogSink.QueueDepth,
                ["log_dropped"]          = KmhLogSink.DroppedCount,
                ["in_flight_requests"]   = SubProtocol.KmhRouter.InFlightMutations,
            };
        }

        private static JObject GlobalState()
        {
            GlobalConfig cfg = GlobalConfig.Current;
            bool registered = GlobalSetupState.Current_.IsRegistered;
            return new JObject
            {
                ["state"]           = cfg.Enabled ? (registered ? "linked" : "setting up")
                                    : KmhGlobalBackend.Active ? (registered ? "private identity" : "registering") : "off",
                ["server_id"]       = KmhServerIdentity.Id ?? "",
                ["generation"]      = cfg.SigningGeneration,
                ["forward_reports"] = cfg.ForwardReports,
                ["last_error"]      = KmhGlobalRegistrar.LastErrorCode ?? "",
            };
        }

        private static JObject Transport()
        {
            Features.Transport.TransportConfig c = Features.Transport.TransportConfig.Current;
            JObject t = new JObject
            {
                ["api_enabled"]         = c.EnableKmhApiTransport,
                ["api_running"]         = Features.Transport.KmhApiServer.Running,
                ["api_connections"]     = Features.Transport.KmhApiServer.ConnectedCount,
                ["auth_required"]       = c.RequireKmhApiAuth,
                ["chat_fallback"]       = c.AllowChatTransportFallback,
                ["max_frame_kb"]        = c.Advanced.MaxFrameKb,
                ["max_inflate_kb"]      = Features.Transport.KmhApiServer.MaxInflateBytes / 1024,
            };
            foreach (KeyValuePair<string, long> kv in Features.Transport.KmhTransportStats.Snapshot()) t[kv.Key] = kv.Value;
            return t;
        }

        private static JObject Runtime()
        {
            Process p = Process.GetCurrentProcess();
            return new JObject
            {
                ["os"]               = RuntimeInformation.OSDescription,
                ["framework"]        = RuntimeInformation.FrameworkDescription,
                ["arch"]             = RuntimeInformation.ProcessArchitecture.ToString(),
                ["cpus"]             = Environment.ProcessorCount,
                ["working_set_mb"]   = p.WorkingSet64 / (1024 * 1024),
                ["gc_heap_mb"]       = GC.GetTotalMemory(false) / (1024 * 1024),
                ["gc_gen0"]          = GC.CollectionCount(0),
                ["gc_gen1"]          = GC.CollectionCount(1),
                ["gc_gen2"]          = GC.CollectionCount(2),
                ["threads"]          = p.Threads.Count,
                ["threadpool_queue"] = System.Threading.ThreadPool.PendingWorkItemCount,
                ["cpu_seconds"]      = (long)p.TotalProcessorTime.TotalSeconds,
            };
        }

        private static JObject Reporter(ServerClient client, string user)
            => new JObject
            {
                ["name"]           = user ?? "",
                ["global_id"]      = KmhVerifiedPlayers.IdFor(client) ?? "",
                ["on_api"]         = Features.Transport.KmhApiServer.IsConnected(client),
                ["compatible"]     = SubProtocol.KmhHandshakeHandler.IsCompatible(client),
            };

        private static List<string> ConsoleTail(int lines)
        {
            var outp = new List<string>();
            long from = Math.Max(0, KmhConsoleTail.NewestSequence - lines);
            foreach (KmhConsoleLine l in KmhConsoleTail.Since(from, lines))
                outp.Add(Maintenance.KmhRedact.Text(l.Text));
            return outp;
        }

        internal static bool Forwardable(string line, string user)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (line.IndexOf("] [warn] ", StringComparison.Ordinal) >= 0
                || line.IndexOf("] [error] ", StringComparison.Ordinal) >= 0) return true;
            return !string.IsNullOrEmpty(user) && WholeWords(line, user).Count > 0;
        }

        internal const string OtherPlayer = "[player]";

        // A reporter named like an ordinary word pulls in lines about others; who those others are stays here.
        internal static string OthersHidden(string line, string user, IEnumerable<string> accounts)
        {
            if (string.IsNullOrEmpty(line) || accounts == null) return line;
            List<(int Start, int End)> own = string.IsNullOrEmpty(user) ? new List<(int, int)>() : WholeWords(line, user);
            var hide = new List<(int Start, int End)>();
            foreach (string other in accounts)
                if (!string.IsNullOrEmpty(other) && !string.Equals(other, user, StringComparison.OrdinalIgnoreCase))
                    foreach (var w in WholeWords(line, other))
                        if (!own.Exists(o => w.Start < o.End && o.Start < w.End)) hide.Add(w);
            if (hide.Count == 0) return line;

            hide.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
            var sb = new System.Text.StringBuilder(line.Length);
            int at = 0;
            foreach (var (start, end) in hide)
            {
                if (end <= at) continue;
                if (start >= at) sb.Append(line, at, start - at).Append(OtherPlayer);
                at = end;
            }
            return sb.Append(line, at, line.Length - at).ToString();
        }

        // "Bob" inside "Bobby joined" is somebody else's line.
        private static List<(int Start, int End)> WholeWords(string line, string name)
        {
            var found = new List<(int, int)>();
            for (int i = line.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = line.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                int end = i + name.Length;
                bool before = i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_';
                bool after = end >= line.Length || !char.IsLetterOrDigit(line[end]) && line[end] != '_';
                if (before && after) found.Add((i, end));
            }
            return found;
        }

        // Global's ceiling: a console full of long lines is trimmed from the oldest end rather than refused there.
        private static string Fit(JObject o)
        {
            string text = o.ToString(Formatting.None);
            if (text.Length <= ReportLimits.MaxDiagnosticsChars || !(o["console"] is JArray lines)) return text;
            while (lines.Count > 0 && text.Length > ReportLimits.MaxDiagnosticsChars)
            {
                lines.RemoveAt(0);
                text = o.ToString(Formatting.None);
            }
            return text.Length <= ReportLimits.MaxDiagnosticsChars ? text : text.Substring(0, ReportLimits.MaxDiagnosticsChars);
        }
    }
}
