using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordAdminStatus
    {
        internal static readonly TimeSpan Every = TimeSpan.FromSeconds(50);

        internal sealed class Facts
        {
            public int People, Simulated;
            public TimeSpan? Uptime;
            public string Build = "";
            public bool PersistenceOk = true;
            public string PersistenceDetail = "";
            public string Backup = "";
            public bool BackupOk = true;
            public string Global = "";
            public int OpenReports;
            public string Discord = "";
        }

        internal static async Task<bool> TickAsync(DiscordConfig cfg, DiscordLiveState state, DateTime now, IDiscordLiveSink sink, bool canEdit)
        {
            bool changed = false;
            if (cfg.PostStatus)
            {
                if (!canEdit) DiscordBridge.Once("status", "Discord: the live server status needs the bot's own gateway (coordinator mode cannot edit a message), so it is off.");
                else
                {
                    var window = DiscordDayWindow.For(now, cfg.LiveDayStartUtcHour, 24);
                    DiscordLiveSlot slot = state.Status;
                    changed |= await DiscordLive.TickSlotAsync(slot, cfg.AdminChannelId, now, window, Every, false,
                        live: () => new DiscordLivePost { Title = "Server status", Content = Compose(Gather(now), LiveHeader(window.End, now)) },
                        final: _ => DiscordLive.Finished(slot, FinalHeader(new DateTime(slot.WindowStartTicks, DateTimeKind.Utc),
                                                                           new DateTime(slot.WindowEndTicks, DateTimeKind.Utc),
                                                                           new DateTime(slot.LastUpdatedTicks, DateTimeKind.Utc))),
                        sink).ConfigureAwait(false);
                }
            }
            if (cfg.PostAlerts) await DiscordAlerts.TickAsync(cfg, now, sink).ConfigureAwait(false);
            return changed;
        }

        internal static string LiveHeader(DateTime windowEnd, DateTime now)
            => $"🖥️ **Server status** · live · updated every minute · final at {DiscordDayWindow.EndsAt(windowEnd, now)}";

        internal static string FinalHeader(DateTime windowStart, DateTime windowEnd, DateTime lastUpdated)
            => $"🖥️ **Server status** · final for {DiscordDayWindow.Label(windowStart, windowEnd)} · last updated {DiscordCompactText.Hhmm(lastUpdated)} UTC";

        internal static string Compose(Facts f, string header)
        {
            var lines = new List<string> { header };
            lines.Add($"👥 Online: {Count(f.People, "person", "people")} · {f.Simulated} simulated");
            lines.Add($"⏱️ Up {(f.Uptime.HasValue ? Duration(f.Uptime.Value) : "(unknown)")} · Colony Nexus {DiscordText.SafeName(f.Build)}");
            lines.Add(f.PersistenceOk ? "💾 Persistence: OK" : $"⚠️ Persistence: DEGRADED · {DiscordText.SafeName(f.PersistenceDetail)}");
            lines.Add((f.BackupOk ? "🗄️ Last backup: " : "⚠️ Last backup: ") + f.Backup);
            lines.Add("🌐 Global: " + f.Global);
            lines.Add($"📝 Open reports: {f.OpenReports}");
            lines.Add("🤖 Discord: " + f.Discord);
            return DiscordCompactText.Cap(string.Join("\n", lines), DiscordCompactText.MaxChars);
        }

        private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        internal static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
            if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
            return $"{t.Minutes}m";
        }

        internal static Facts Gather(DateTime now)
        {
            var f = new Facts { Build = $"{KmhVersion.Build} ({KmhVersion.BuildTag})" };

            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (!string.IsNullOrEmpty(u) && Simulation.SimulatedAccounts.IsSimulated(u)) f.Simulated++;
                else f.People++;
            }

            DateTime boot = Main_.BootstrapUtc;
            f.Uptime = boot == DateTime.MinValue ? (TimeSpan?)null : now - boot;

            f.PersistenceOk = !Maintenance.KmhMaintenanceGate.PersistenceDegraded && !Persistence.JsonFileStore.AnyStoreUnsaved;
            f.PersistenceDetail = Persistence.JsonFileStore.DescribeHealth();

            Persistence.KmhDataBackup.Outcome b = Persistence.KmhDataBackup.LastOutcome;
            if (b == null) f.Backup = "none since start-up";
            else
            {
                f.BackupOk = b.Ok;
                string when = $"{b.AtUtc.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} UTC ({DiscordText.SafeName(b.Reason)})";
                f.Backup = b.Ok ? when + " · OK" : when + " · FAILED: " + DiscordText.SafeName(Short(b.Detail));
            }

            f.Global = DescribeGlobal();

            try
            {
                foreach (Reports.StoredReport r in Reports.ReportStore.List())
                    if (r != null && r.StaffVisible && Reports.ReportStatuses.IsOpen(r.Status)) f.OpenReports++;
            }
            catch (Exception) { f.OpenReports = -1; }

            long failures = DiscordBridge.SendFailures;
            f.Discord = "connected · " + (failures == 0 ? "no send errors"
                : $"{failures} send error(s), last {DiscordCompactText.Hhmm(DiscordBridge.LastSendFailureUtc)} UTC");
            return f;
        }

        private static string DescribeGlobal()
        {
            try
            {
                if (!Global.GlobalConfig.Current.Enabled) return "off";
                if (!Global.GlobalSetupState.Current_.IsRegistered) return "on, not registered yet";
                DateTime ok = Global.KmhGlobalRegistrar.LastSuccessUtc;
                string beat = ok == default ? "no heartbeat yet" : $"last heartbeat {DiscordCompactText.Hhmm(ok)} UTC";
                return $"{Global.KmhGlobalRegistrar.State.ToString().ToLowerInvariant()} · {beat}";
            }
            catch (Exception) { return "unknown"; }
        }

        private static string Short(string s)
        {
            s = (s ?? "").Trim();
            return s.Length <= 160 ? s : s.Substring(0, 159) + "…";
        }
    }
}
