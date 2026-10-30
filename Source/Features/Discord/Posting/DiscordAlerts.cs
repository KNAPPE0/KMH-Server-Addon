using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // Once per episode (with reminders) and on clearing, at most MaxPerHour for the whole channel.
    internal static class DiscordAlerts
    {
        internal const int MaxPerHour = 10;
        internal const int ErrorBurst = 5;
        internal static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan GlobalLostAfter = TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan Reminder = TimeSpan.FromHours(6);

        // What the server looks like right now; gathered live, or built by a self-test.
        internal sealed class Inputs
        {
            public bool PersistenceDegraded;
            public string PersistenceDetail = "";
            public DateTime BackupFailedUtc = DateTime.MinValue;
            public string BackupError = "";
            public bool GlobalExpected;
            public DateTime GlobalLastOkUtc = DateTime.MinValue;
            public TimeSpan Uptime;
            public bool ReadinessFailed;
            public string ReadinessReason = "";
            public List<(string Source, int Count, string Last)> ErrorBursts = new List<(string, int, string)>();
        }

        // Episodes and the rate limit; one instance lives for the process, a self-test makes its own.
        internal sealed class Memory
        {
            public readonly Dictionary<string, DateTime> LastSent = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            public readonly List<DateTime> SentTimes = new List<DateTime>();
            public bool PersistenceOpen, GlobalOpen, ReadinessSent;
            public DateTime BackupAlertedUtc = DateTime.MinValue;
            public int Suppressed;

            // A key re-alerts only after its gap, and nothing at all past the hourly budget.
            public bool Allow(string key, DateTime now, TimeSpan gap)
            {
                if (LastSent.TryGetValue(key, out DateTime last) && now - last < gap) return false;
                SentTimes.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
                if (SentTimes.Count >= MaxPerHour) { Suppressed++; return false; }
                LastSent[key] = now;
                SentTimes.Add(now);
                return true;
            }
        }

        private static readonly Memory Live = new Memory();

        internal static List<string> Evaluate(Inputs i, Memory m, DateTime now)
        {
            var outp = new List<string>();
            void Say(string key, TimeSpan gap, string text) { if (m.Allow(key, now, gap)) outp.Add(text); }

            if (i.PersistenceDegraded)
            {
                Say("persistence", Reminder, (m.PersistenceOpen ? "⚠️ **Persistence still degraded** · " : "⚠️ **Persistence degraded** · ")
                    + Clip(i.PersistenceDetail) + " - value moves are refused until KMH-Data can be written again.");
                m.PersistenceOpen = true;
            }
            else if (m.PersistenceOpen)
            {
                m.PersistenceOpen = false;
                Say("persistence-ok", TimeSpan.Zero, "✅ **Persistence recovered** · every store is being written again.");
            }

            if (i.BackupFailedUtc > m.BackupAlertedUtc)
            {
                m.BackupAlertedUtc = i.BackupFailedUtc;
                Say("backup", TimeSpan.FromHours(1), $"⚠️ **Backup failed** at {DiscordCompactText.Hhmm(i.BackupFailedUtc)} UTC · {Clip(i.BackupError)}");
            }

            bool lost = i.GlobalExpected
                     && (i.GlobalLastOkUtc != DateTime.MinValue ? now - i.GlobalLastOkUtc > GlobalLostAfter : i.Uptime > GlobalLostAfter);
            if (lost)
            {
                string since = i.GlobalLastOkUtc == DateTime.MinValue ? "no heartbeat since start-up"
                             : $"last heartbeat {DiscordCompactText.Hhmm(i.GlobalLastOkUtc)} UTC";
                Say("global", Reminder, $"⚠️ **Colony Nexus Global link lost** · {since}. The game server is unaffected; the listing and federation wait.");
                m.GlobalOpen = true;
            }
            else if (m.GlobalOpen && i.GlobalExpected)
            {
                m.GlobalOpen = false;
                Say("global-ok", TimeSpan.Zero, "✅ **Colony Nexus Global link back** · heartbeats are answered again.");
            }
            else if (!i.GlobalExpected) m.GlobalOpen = false;

            if (i.ReadinessFailed && !m.ReadinessSent)
            {
                m.ReadinessSent = true;
                Say("readiness", TimeSpan.FromDays(1), $"🛑 **Colony Nexus is not operational** · {Clip(i.ReadinessReason)}. Player value moves are refused; fix the cause and restart.");
            }

            foreach ((string source, int count, string last) in i.ErrorBursts ?? new List<(string, int, string)>())
                Say("errors:" + source, TimeSpan.FromHours(1),
                    $"⚠️ **{count} errors from {DiscordText.SafeName(source)}** in {ErrorWindow.TotalMinutes:0} min · latest: {Clip(last)}");
            return outp;
        }

        private static string Clip(string s)
        {
            s = DiscordText.SafeName((s ?? "").Trim());
            return s.Length <= 300 ? s : s.Substring(0, 299) + "…";
        }

        internal static Inputs Gather(DateTime now)
        {
            var i = new Inputs
            {
                PersistenceDegraded = Maintenance.KmhMaintenanceGate.PersistenceDegraded,
                PersistenceDetail = Persistence.JsonFileStore.DescribeHealth(),
                ReadinessFailed = Maintenance.KmhReadiness.State == Maintenance.KmhReadyState.Failed,
                ReadinessReason = Maintenance.KmhReadiness.Reason,
                Uptime = Main_.BootstrapUtc == DateTime.MinValue ? TimeSpan.Zero : now - Main_.BootstrapUtc,
                ErrorBursts = KmhErrorTally.Bursts(now, ErrorWindow, ErrorBurst),
            };
            Persistence.KmhDataBackup.Outcome b = Persistence.KmhDataBackup.LastOutcome;
            if (b != null && !b.Ok) { i.BackupFailedUtc = b.AtUtc; i.BackupError = b.Detail; }
            i.GlobalExpected = GlobalExpected();
            DateTime ok = Global.KmhGlobalRegistrar.LastSuccessUtc;
            i.GlobalLastOkUtc = ok == default ? DateTime.MinValue : ok;
            return i;
        }

        // Only a server that is meant to be listed can lose its link; a revoked one has been told why already.
        internal static bool GlobalExpected()
        {
            try
            {
                return Global.GlobalConfig.Current.Enabled && Global.GlobalSetupState.Current_.IsRegistered
                    && !Global.KmhGlobalRegistrar.IsRevoked && Global.KmhGlobalRegistrar.State != Global.GlobalLinkState.Disabled;
            }
            catch (Exception) { return false; }
        }

        // Role ids only, as everywhere else: a name could be any role anyone made.
        internal static List<ulong> PingRoles(DiscordConfig cfg)
        {
            var ids = new List<ulong>();
            if (cfg == null || !cfg.AlertPingModerators) return ids;
            foreach (string s in cfg.Roles?.Moderators ?? Array.Empty<string>())
            {
                ulong id = DiscordConfig.ParseId(s);
                if (id != 0 && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        internal static string WithPing(string text, List<ulong> roles)
        {
            if (roles == null || roles.Count == 0) return text;
            var sb = new System.Text.StringBuilder();
            foreach (ulong r in roles) sb.Append("<@&").Append(r).Append("> ");
            return sb.Append(text).ToString();
        }

        internal static async Task TickAsync(DiscordConfig cfg, DateTime now, IDiscordLiveSink sink)
        {
            List<string> alerts;
            int suppressed;
            lock (Live)
            {
                alerts = Evaluate(Gather(now), Live, now);
                suppressed = Live.Suppressed;
                Live.Suppressed = 0;
            }
            if (suppressed > 0) ServerLog.Warn($"Discord: {suppressed} alert(s) held back - the Admin channel already had {MaxPerHour} this hour");
            List<ulong> roles = PingRoles(cfg);
            foreach (string a in alerts)
            {
                ServerLog.Diag("Discord: alert - " + a);
                await sink.PostAsync(cfg.AdminChannelId, WithPing(a, roles), roles).ConfigureAwait(false);
            }
        }
    }
}
