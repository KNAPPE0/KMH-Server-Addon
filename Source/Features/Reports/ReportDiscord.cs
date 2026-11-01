using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Discord;

namespace KMHServerAddon.Features.Reports
{
    // Every word is the player's or the client's, so each is made unable to ping, format or open a new line.
    internal static class ReportDiscord
    {
        internal const int ExcerptChars = 300;
        internal const int TitleChars = 200;

        // The last report a caller asked to post, so a test sees the handler's own choice before the bridge's.
        internal static string LastAskedId = "";

        public static void Post(StoredReport r)
        {
            LastAskedId = r?.ReportId ?? "";
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!ShouldPost(r, cfg)) return;
                DiscordBridge.PostToChannel(cfg.AdminChannelId, Text(r));
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: report post failed: {ex.Message}"); }
        }

        internal static bool ShouldPost(StoredReport r, DiscordConfig cfg)
            => r != null && r.StaffVisible && cfg != null && cfg.IsEnabled && cfg.PostReportEvents && cfg.AdminChannelId != 0;

        internal static string Text(StoredReport r)
        {
            string reference = ReportText.Short(r.ReportId);
            var head = new List<string> { KindHeading(r.Kind), DiscordText.SafeName(AreaLabel(r.Area)) };
            if (r.Kind == ReportKinds.Bug)
            {
                if (!string.IsNullOrEmpty(r.Severity) && SeverityLabel(r.Severity) != "not said") head.Add(SeverityLabel(r.Severity));
                if (!string.IsNullOrEmpty(r.Frequency) && FrequencyLabel(r.Frequency) != "not said") head.Add(FrequencyLabel(r.Frequency));
            }
            string title = DiscordText.SafeName(Cap(r.Title, TitleChars));
            string excerpt = string.IsNullOrWhiteSpace(r.Body) ? "*(no description)*" : DiscordText.SafeName(Cap(r.Body, ExcerptChars));
            string client = DiscordText.SafeName(string.IsNullOrEmpty(r.ClientVersion) ? "?" : r.ClientVersion);

            var lines = new List<string>
            {
                string.Join(" · ", head),
                $"**{title}** — {excerpt}",
                $"-# from **{DiscordText.SafeName(r.Reporter)}** · client {client} · server {DiscordText.SafeName($"{KmhVersion.Display} {KmhVersion.BuildTag}")}"
                + $" · read it with `kmh reports show {reference}`",
            };
            if (r.WantsDevelopers)
                lines.Add(r.ForwardState == ReportForwardStates.Off
                    ? "-# Also meant for the Colony Nexus developers - not sent, because forwarding is off."
                    : "-# Also on its way to the Colony Nexus developers.");
            return string.Join("\n", lines);
        }

        private static string KindHeading(string kind)
            => kind == ReportKinds.Suggestion ? "💡 **Suggestion**" : kind == ReportKinds.Question ? "❓ **Question**" : "🐞 **Bug report**";

        internal static string AreaLabel(string area)
        {
            switch (area)
            {
                case "joining":      return "Joining & server browser";
                case "want_board":   return "Want Board";
                case "world_events": return "World events";
                case "standings":    return "Standings & leaderboards";
                case "":
                case null:           return "General";
                default:             return char.ToUpperInvariant(area[0]) + area.Substring(1).Replace('_', ' ');
            }
        }

        internal static string SeverityLabel(string s)
            => s == ReportSeverities.Blocks ? "Blocks them" : s == ReportSeverities.Annoying ? "Annoying" : s == ReportSeverities.Minor ? "Minor" : "not said";

        internal static string FrequencyLabel(string s)
            => s == ReportFrequencies.Always ? "Every time" : s == ReportFrequencies.Sometimes ? "Sometimes" : s == ReportFrequencies.Once ? "Once" : "not said";

        private static string Cap(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
