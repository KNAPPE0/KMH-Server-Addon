using System;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Reports
{
    // The player is told whether a report was only kept or also queued for Global, so "sent" never means more.
    internal static class ReportHandler
    {
        private static readonly object _rateLock = new object();
        private static readonly Util.KmhRateWindow _burst = new Util.KmhRateWindow();
        private static readonly Util.KmhRateWindow _daily = new Util.KmhRateWindow();

        // Reasons the client translates. Anything else it shows as the English message.
        internal const string ReasonInvalid = "invalid", ReasonRateLimited = "rate_limited", ReasonStorage = "storage";

        // The hello's reports_v: this server reads destinations, area, severity, frequency and the question kind.
        internal const int WireVersion = 2;

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ReportSubmit, OnSubmit);
            Schedule(Maintenance.KmhScheduler.Register);
        }

        internal delegate void JobRegistrar(string name, TimeSpan interval, Action tick, Maintenance.KmhJobKind kind, TimeSpan? firstDelay);

        internal static void Schedule(JobRegistrar register)
        {
            register("report-forward", TimeSpan.FromMinutes(1), ReportForwarder.Tick, Maintenance.KmhJobKind.Infrastructure, TimeSpan.FromSeconds(30));
            // Retention keeps running on a server that stops receiving reports.
            register("report-prune", TimeSpan.FromHours(24), PruneTick, Maintenance.KmhJobKind.Infrastructure, TimeSpan.FromMinutes(10));
        }

        internal static void PruneTick()
        {
            int gone = ReportStore.Prune(DateTimeOffset.UtcNow);
            if (gone > 0) ServerLog.Info($"Reports: {gone} report(s) past the retention limit were removed.");
        }

        // In the hello, so the dialog says where a report goes before it is written.
        public static string Mode => !FeaturesConfig.Current.Reports ? "off" : ReportForwarder.CanForward(out _) ? "global" : "local";

        private static void OnSubmit(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user) || !KmhRouter.IsLive(client)) return;
            ReportSubmit s = env?.DataAs<ReportSubmit>();
            string id = s?.ReportId ?? "";

            ReportResultWire reply = Accept(client, user, s, DateTimeOffset.UtcNow);
            reply.ReportId = id;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ReportResult, reply);
        }

        internal static ReportResultWire Accept(ServerClient client, string user, ReportSubmit s, DateTimeOffset now)
        {
            string id = s?.ReportId ?? "";
            string title = ReportText.Clean(s?.Title, ReportLimits.MaxTitleChars, multiline: false);
            if (s == null || !ReportText.IsReportId(id) || !ReportKinds.IsKnown(s.Kind) || title.Length == 0)
                return Refuse(ReasonInvalid, "That report is missing a kind or a title.");

            string destinations = ReportDestinations.FromWire(s.Destinations);
            bool staff = ReportDestinations.ShowsStaff(destinations);
            bool developers = ReportDestinations.WantsDevelopers(destinations);

            // A resend after a lost answer is the same report: answered as it was, never stored twice.
            StoredReport held = ReportStore.Load(id);
            if (held != null)
            {
                if (!string.Equals(held.Reporter, user, StringComparison.OrdinalIgnoreCase))
                    return Refuse(ReasonInvalid, "That report id is already taken.");
                if (s.Destinations != null) Widen(held, destinations);
                return ResultFor(held);
            }

            // Only the player's own choice sends a report to Global, and only the owner's switch lets it leave.
            bool forward = developers && ReportForwarder.CanForward(out _);
            if (!staff && !forward)
                return new ReportResultWire { Ok = true, Forwarding = "local", Developers = ReportDevelopers.NotForwarded, Reference = ReportText.Short(id) };

            if (!AllowRate(user, now))
                return Refuse(ReasonRateLimited, "You have sent several reports in a short time. Try again in a few minutes.");

            string kind = s.Kind;
            string area = ReportAreas.Normalize(s.Area);
            string severity = ReportSeverities.Normalize(kind, s.Severity);
            string frequency = ReportFrequencies.Normalize(kind, s.Frequency);
            byte[] screenshot = DecodeScreenshot(s.Screenshot);
            var report = new JObject
            {
                ["kind"] = kind, ["area"] = area, ["severity"] = severity, ["frequency"] = frequency,
                ["destinations"] = string.IsNullOrEmpty(destinations) ? "server,developers" : destinations,
            };
            var (fullDiag, forwardDiag) = ReportDiagnostics.Build(client, user, now, report);

            StoredReport r = new StoredReport
            {
                ReportId           = id,
                Kind               = kind,
                Title              = title,
                Body               = ReportText.Clean(s.Body, ReportLimits.MaxBodyChars, multiline: true),
                Reporter           = user,
                ReporterGlobalId   = Global.KmhVerifiedPlayers.IdFor(client) ?? "",
                ClientVersion      = ReportText.Clean(s.ClientVersion, ReportLimits.MaxVersionChars, multiline: false),
                CreatedAtUnix      = s.CreatedAtUnix,
                ReceivedAtUnix     = now.ToUnixTimeSeconds(),
                ClientDiagnostics  = ReportText.Clean(s.ClientDiagnostics, ReportLimits.MaxDiagnosticsChars, multiline: true),
                ServerDiagnostics  = fullDiag,
                ForwardDiagnostics = forwardDiag,
                ForwardState       = forward ? ReportForwardStates.Pending : ReportForwardStates.Off,
                Destinations       = destinations,
                Area               = area,
                Severity           = severity,
                Frequency          = frequency,
                Status             = ReportStatuses.New,
                StatusAtUnix       = now.ToUnixTimeSeconds(),
            };

            if (!ReportStore.Add(r, screenshot))
                return Refuse(ReasonStorage, "The server could not save your report. Tell the server owner.");

            ServerLog.Info(staff
                ? $"Report {ReportText.Short(id)}: {r.Kind} ({r.Area}) from {user} - \"{r.Title}\""
                  + (screenshot != null ? " (with a screenshot)" : "")
                  + (forward ? ", also queued for the Colony Nexus developers." : ", kept on this server ('kmh reports').")
                : $"Report {ReportText.Short(id)} from {user}: relayed to the Colony Nexus developers only, not shown here.");
            if (staff) ReportDiscord.Post(r);
            if (forward) ReportForwarder.Nudge();
            return ResultFor(r);
        }

        // A resend that adds a destination the first copy lacked: shown to staff from now, or queued for Global if allowed.
        private static void Widen(StoredReport held, string destinations)
        {
            bool wasStaff = held.StaffVisible;
            string widened = ReportDestinations.Union(held.Destinations, destinations);
            bool changed = widened != held.Destinations;
            held.Destinations = widened;
            if (ReportDestinations.WantsDevelopers(destinations) && held.ForwardState == ReportForwardStates.Off && ReportForwarder.CanForward(out _))
            {
                held.ForwardState = ReportForwardStates.Pending;
                held.ForwardAttempts = 0;
                held.NextAttemptUnix = 0;
                changed = true;
            }
            if (!changed || !ReportStore.Update(held)) return;
            if (!wasStaff && held.StaffVisible) ReportDiscord.Post(held);
            if (held.ForwardState == ReportForwardStates.Pending) ReportForwarder.Nudge();
        }

        internal static ReportResultWire ResultFor(StoredReport r)
            => new ReportResultWire
            {
                Ok = true,
                Forwarding = r.ForwardState == ReportForwardStates.Off ? "local" : "global",
                Developers = !r.WantsDevelopers ? ""
                           : r.ForwardState == ReportForwardStates.Pending || r.ForwardState == ReportForwardStates.Sent
                               ? ReportDevelopers.Forwarding : ReportDevelopers.NotForwarded,
                Reference = ReportText.Short(r.ReportId),
            };

        private static ReportResultWire Refuse(string reason, string message)
            => new ReportResultWire { Ok = false, Reason = reason, Message = message };

        private static bool AllowRate(string user, DateTimeOffset now)
        {
            long ticks = now.UtcTicks;
            lock (_rateLock)
                return _burst.Allow(user, ticks, ReportLimits.BurstPerWindow, ReportLimits.BurstWindowSec)
                    && _daily.Allow(user, ticks, ReportLimits.PerDay, 24 * 60 * 60);
        }

        // A bad image is dropped and the report kept.
        private static byte[] DecodeScreenshot(string base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length > (ReportLimits.MaxScreenshotBytes + 2) / 3 * 4) return null;
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                return bytes.Length <= ReportLimits.MaxScreenshotBytes && ReportText.ImageExtension(bytes).Length > 0 ? bytes : null;
            }
            catch (FormatException) { return null; }
        }

        internal static void ResetForTest()
        {
            lock (_rateLock) { _burst.Clear(); _daily.Clear(); }
        }
    }
}
