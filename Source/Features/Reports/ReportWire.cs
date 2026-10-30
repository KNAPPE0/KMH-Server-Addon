using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Features.Global;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Reports
{
    internal static class ReportLimits
    {
        public const int MaxIdChars          = 64;
        public const int MaxTitleChars       = 120;
        public const int MaxBodyChars        = 8000;
        public const int MaxVersionChars     = 64;
        // Global's own ceilings, so nothing this server forwards is cut there instead of here.
        public const int MaxDiagnosticsChars = 192 * 1024;
        public const int MaxScreenshotBytes  = 512 * 1024;
        public const int ConsoleLines        = 200;

        public const int BurstPerWindow  = 3;
        public const int BurstWindowSec  = 10 * 60;
        public const int PerDay          = 15;

        public const int KeepCount = 300;
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);
    }

    // "question" is newer than Global, which still knows only the first two; the summary carries the true kind.
    internal static class ReportKinds
    {
        public const string Bug = "bug", Suggestion = "suggestion", Question = "question";
        public static bool IsKnown(string kind) => kind == Bug || kind == Suggestion || kind == Question;
        public static string ForGlobal(string kind) => kind == Question ? Suggestion : kind;
    }

    internal static class ReportAreas
    {
        public const string General = "general";

        public static readonly string[] All =
        {
            General, "joining", "marketplace", "auctions", "want_board", "treasury", "mail", "chat", "guilds", "quests",
            "world_events", "sites", "roadworks", "frontier", "standings", "performance", "other",
        };

        public static string Normalize(string area) => Array.IndexOf(All, area ?? "") >= 0 ? area : General;
    }

    // Both describe a bug; anything else, or a word this build does not know, is left empty.
    internal static class ReportSeverities
    {
        public const string Blocks = "blocks", Annoying = "annoying", Minor = "minor";
        public static string Normalize(string kind, string s) => kind == ReportKinds.Bug && (s == Blocks || s == Annoying || s == Minor) ? s : "";
    }

    internal static class ReportFrequencies
    {
        public const string Always = "always", Sometimes = "sometimes", Once = "once";
        public static string Normalize(string kind, string s) => kind == ReportKinds.Bug && (s == Always || s == Sometimes || s == Once) ? s : "";
    }

    // Stored as "server,developers"; empty is a client that did not say, which has always meant both.
    internal static class ReportDestinations
    {
        public const string Server = "server", Developers = "developers";

        public static string FromWire(List<string> wire)
        {
            if (wire == null) return "";
            bool server = wire.Contains(Server), developers = wire.Contains(Developers);
            return Csv(server, developers);
        }

        public static bool ShowsStaff(string csv) => string.IsNullOrEmpty(csv) || Has(csv, Server);
        public static bool WantsDevelopers(string csv) => string.IsNullOrEmpty(csv) || Has(csv, Developers);

        public static string Union(string a, string b)
            => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) ? "" : Csv(Has(a, Server) || Has(b, Server), Has(a, Developers) || Has(b, Developers));

        private static bool Has(string csv, string d) => Array.IndexOf(csv.Split(','), d) >= 0;

        private static string Csv(bool server, bool developers)
            => server && developers ? Server + "," + Developers : server ? Server : developers ? Developers : "";
    }

    internal static class ReportStatuses
    {
        public const string New = "new", Acknowledged = "acknowledged", InProgress = "in_progress", Fixed = "fixed",
                            WontFix = "wont_fix", Closed = "closed";

        public static readonly string[] All = { New, Acknowledged, InProgress, Fixed, WontFix, Closed };

        public static bool IsOpen(string status) => status != Fixed && status != WontFix && status != Closed;

        public static string Parse(string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace("'", "").Replace(" ", "_");
            switch (w)
            {
                case "ack": case "acked": return Acknowledged;
                case "progress": case "inprogress": case "working": return InProgress;
                case "wontfix": case "won_t_fix": return WontFix;
                case "done": case "resolved": return Fixed;
                default: return Array.IndexOf(All, w) >= 0 ? w : "";
            }
        }
    }

    internal static class ReportDevelopers
    {
        public const string Forwarding = "forwarding", NotForwarded = "not_forwarded";
    }

    // The envelope's op id is the report id.
    internal sealed class ReportSubmit
    {
        [JsonProperty("report_id")]          public string ReportId { get; set; } = "";
        [JsonProperty("kind")]               public string Kind { get; set; } = "";
        [JsonProperty("title")]              public string Title { get; set; } = "";
        [JsonProperty("body")]               public string Body { get; set; } = "";
        [JsonProperty("client_version")]     public string ClientVersion { get; set; } = "";
        [JsonProperty("created_at")]         public long   CreatedAtUnix { get; set; }
        [JsonProperty("client_diagnostics")] public string ClientDiagnostics { get; set; } = "";
        [JsonProperty("screenshot")]         public string Screenshot { get; set; } = "";
        // Null from a client older than reports_v 2.
        [JsonProperty("destinations")]       public List<string> Destinations { get; set; }
        [JsonProperty("area")]               public string Area { get; set; } = "";
        [JsonProperty("severity")]           public string Severity { get; set; } = "";
        [JsonProperty("frequency")]          public string Frequency { get; set; } = "";
    }

    // The reason is a word the client translates; message is the English fallback.
    internal sealed class ReportResultWire
    {
        [JsonProperty("report_id")]  public string ReportId { get; set; } = "";
        [JsonProperty("ok")]         public bool   Ok { get; set; }
        [JsonProperty("reason")]     public string Reason { get; set; } = "";
        [JsonProperty("forwarding")] public string Forwarding { get; set; } = "";
        [JsonProperty("message")]    public string Message { get; set; } = "";
        [JsonProperty("developers")] public string Developers { get; set; } = "";
        [JsonProperty("reference")]  public string Reference { get; set; } = "";
    }

    // The screenshot sits beside it as <id>.jpg or .png.
    internal sealed class StoredReport
    {
        public string ReportId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Reporter { get; set; } = "";
        public string ReporterGlobalId { get; set; } = "";
        public string ClientVersion { get; set; } = "";
        public long   CreatedAtUnix { get; set; }
        public long   ReceivedAtUnix { get; set; }
        public string ClientDiagnostics { get; set; } = "";
        public string ServerDiagnostics { get; set; } = "";
        // Global's copy: warnings, errors and the reporter's own console lines only.
        public string ForwardDiagnostics { get; set; } = "";
        public string ScreenshotFile { get; set; } = "";

        public string ForwardState { get; set; } = ReportForwardStates.Off;
        public int    ForwardAttempts { get; set; }
        public long   NextAttemptUnix { get; set; }
        public string ForwardedId { get; set; } = "";
        public string LastError { get; set; } = "";

        public string Destinations { get; set; } = "";
        public string Area { get; set; } = ReportAreas.General;
        public string Severity { get; set; } = "";
        public string Frequency { get; set; } = "";
        public string Status { get; set; } = ReportStatuses.New;
        public string StaffNote { get; set; } = "";
        public long   StatusAtUnix { get; set; }

        [JsonIgnore] public bool StaffVisible    => ReportDestinations.ShowsStaff(Destinations);
        [JsonIgnore] public bool WantsDevelopers => ReportDestinations.WantsDevelopers(Destinations);
        [JsonIgnore] public string StatusOrNew   => string.IsNullOrEmpty(Status) ? ReportStatuses.New : Status;
    }

    internal static class ReportForwardStates
    {
        public const string Off = "off", Pending = "pending", Sent = "sent", Failed = "failed";
    }

    // server -> Global: POST /api/v1/servers/reports, signed with this server's key like a heartbeat.
    internal sealed class ReportBody
    {
        [JsonProperty("server_id")]          public string ServerId { get; set; } = "";
        [JsonProperty("generation")]         public int    Generation { get; set; } = 1;
        [JsonProperty("issued_at")]          public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]              public string Nonce { get; set; } = "";
        [JsonProperty("report_id")]          public string ReportId { get; set; } = "";
        [JsonProperty("kind")]               public string Kind { get; set; } = "";
        [JsonProperty("title")]              public string Title { get; set; } = "";
        [JsonProperty("body")]               public string Body { get; set; } = "";
        [JsonProperty("reporter")]           public string Reporter { get; set; } = "";
        [JsonProperty("reporter_global_id")] public string ReporterGlobalId { get; set; } = "";
        [JsonProperty("client_version")]     public string ClientVersion { get; set; } = "";
        [JsonProperty("created_at")]         public long   CreatedAtUnix { get; set; }
        [JsonProperty("client_diagnostics")] public string ClientDiagnostics { get; set; } = "";
        [JsonProperty("server_diagnostics")] public string ServerDiagnostics { get; set; } = "";
        [JsonProperty("screenshot")]         public string Screenshot { get; set; } = "";
        [JsonProperty("signature")]          public string Signature { get; set; } = "";

        // Global's ServerReportRequest builds these same bytes: the three large fields by their hashes.
        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.Report)
            .Add(ServerId)
            .Add(Generation)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(ReportId)
            .Add(Kind)
            .Add(Title)
            .Add(Body)
            .Add(Reporter)
            .Add(ReporterGlobalId)
            .Add(ClientVersion)
            .Add(CreatedAtUnix)
            .Add(ReportText.Sha256Hex(ClientDiagnostics))
            .Add(ReportText.Sha256Hex(ServerDiagnostics))
            .Add(ReportText.Sha256Hex(Screenshot))
            .Build();
    }

    internal static class ReportText
    {
        public static string Sha256Hex(string s)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""));
            StringBuilder sb = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Global's rule, so a report reads the same in 'kmh reports' and on the dashboard.
        public static string Clean(string s, int maxChars, bool multiline)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(Math.Min(s.Length, maxChars));
            for (int i = 0; i < s.Length && b.Length < maxChars; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        if (b.Length + 2 > maxChars) break;
                        b.Append(c).Append(s[++i]);
                    }
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                if (c == '\t') { b.Append(multiline ? '\t' : ' '); continue; }
                if (c == '\n') { b.Append(multiline ? '\n' : ' '); continue; }
                if (c == '\r') { if (!multiline) b.Append(' '); continue; }
                if (char.IsControl(c)) continue;
                b.Append(c);
            }
            return b.ToString().Trim();
        }

        public static bool IsReportId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > ReportLimits.MaxIdChars) return false;
            foreach (char c in id)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_'))
                    return false;
            return true;
        }

        public static string ImageExtension(byte[] bytes)
        {
            if (bytes == null) return "";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return ".png";
            return "";
        }

        public static string Short(string id) => string.IsNullOrEmpty(id) ? "" : id.Length <= 8 ? id : id.Substring(0, 8);
    }
}
