using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.Features.Reports
{
    // Best effort with backoff: the report on disk is what matters, and Global never gets it twice.
    internal static class ReportForwarder
    {
        public const string Path = "/api/v1/servers/reports";
        private const int PerPass = 5;
        private const int MaxAttempts = 24;
        private static int _sending;

        // Disclosure decides whether Global may get anything; the owner's switch whether reports are among it.
        public static bool CanForward(out string why)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            if (cfg == null || !cfg.ForwardReports) { why = "Global.ForwardReports is off"; return false; }
            return KmhGlobalDisclosure.MayPublish(Path, out why);
        }

        public static void Nudge() => Tick();

        // A test that just queued a report waits for the pass its Nudge started, so the two never race.
        internal static bool WaitIdleForTest(TimeSpan max)
        {
            DateTime until = DateTime.UtcNow + max;
            while (Volatile.Read(ref _sending) != 0)
            {
                if (DateTime.UtcNow > until) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        // One pass in flight, off the scheduler's loop, so a slow Global never holds up the expiry sweep.
        public static void Tick()
        {
            if (Maintenance.KmhShutdown.InProgress) return;
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try { await PassAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { ServerLog.Verbose($"Reports: forward pass failed ({ex.GetType().Name})."); }
                finally { Volatile.Write(ref _sending, 0); }
            });
        }

        internal static async Task<int> PassAsync(DateTimeOffset now, CancellationToken ct)
        {
            List<string> ids = ReportStore.PendingIds();
            if (ids.Count == 0) return 0;
            bool can = CanForward(out _);
            int sent = 0, tried = 0;
            foreach (string id in ids)
            {
                if (tried >= PerPass) break;
                StoredReport r = ReportStore.Load(id);
                if (r == null || r.ForwardState != ReportForwardStates.Pending) continue;
                // The player kept it from the developers: nothing here sends it, whatever the queue says.
                if (!r.WantsDevelopers)
                {
                    r.ForwardState = ReportForwardStates.Off;
                    r.LastError = "the player did not send it to the Colony Nexus developers";
                    ReportStore.Update(r);
                    continue;
                }
                if (!can)
                {
                    // Turned off: what was waiting stays here rather than leaving later. Other reasons just wait.
                    if (GlobalConfig.Current?.ForwardReports != true)
                    {
                        r.ForwardState = ReportForwardStates.Off;
                        r.LastError = "Global.ForwardReports was turned off";
                        ReportStore.Update(r);
                    }
                    continue;
                }
                if (r.NextAttemptUnix > now.ToUnixTimeSeconds()) continue;

                tried++;
                GlobalCallResult res = await SendAsync(r, now, ct).ConfigureAwait(false);
                if (Settle(r, res, now)) sent++;
                ReportStore.Update(r);
                // Relayed, never shown here: once Global holds it this server keeps no copy.
                if (r.ForwardState == ReportForwardStates.Sent && !r.StaffVisible) ReportStore.Delete(r.ReportId);
            }
            return sent;
        }

        internal static ReportBody BodyFor(StoredReport r, DateTimeOffset now)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            byte[] shot = ReportStore.Screenshot(r);
            ReportBody b = new ReportBody
            {
                ServerId          = KmhServerIdentity.Id ?? "",
                Generation        = cfg.SigningGeneration,
                IssuedAtUnix      = now.ToUnixTimeSeconds(),
                Nonce             = Guid.NewGuid().ToString("N"),
                ReportId          = r.ReportId,
                Kind              = ReportKinds.ForGlobal(r.Kind),
                Title             = r.Title,
                Body              = r.Body,
                Reporter          = r.Reporter,
                ReporterGlobalId  = r.ReporterGlobalId,
                ClientVersion     = r.ClientVersion,
                CreatedAtUnix     = r.CreatedAtUnix,
                ClientDiagnostics = r.ClientDiagnostics,
                ServerDiagnostics = r.ForwardDiagnostics,
                Screenshot        = shot == null ? "" : Convert.ToBase64String(shot),
            };
            b.Signature = KmhServerKey.Sign(b.Canonical());
            return b;
        }

        private static async Task<GlobalCallResult> SendAsync(StoredReport r, DateTimeOffset now, CancellationToken ct)
        {
            ReportBody body = BodyFor(r, now);
            if (string.IsNullOrEmpty(body.Signature)) return GlobalCallResult.Offline("this server has no signing key yet");
            return await new KmhGlobalClient(GlobalConfig.Current.BaseUrl).PostAsync(Path, body, null, ct).ConfigureAwait(false);
        }

        // A refusal no retry can change is final; anything else backs off.
        internal static bool Settle(StoredReport r, GlobalCallResult res, DateTimeOffset now)
        {
            r.ForwardAttempts++;
            if (res != null && res.Ok)
            {
                r.ForwardState = ReportForwardStates.Sent;
                r.LastError = "";
                r.ForwardedId = (string)res.Body?["report_id"] ?? "";
                ServerLog.Info($"Report {ReportText.Short(r.ReportId)} sent to Colony Nexus Global.");
                return true;
            }

            string code = string.IsNullOrEmpty(res?.ErrorCode) ? "no answer" : res.ErrorCode;
            r.LastError = code;
            bool final = res != null && !res.Unreachable
                         && (res.Status == 400 || res.Status == 403 || res.Status == 404 || res.Status == 413);
            if (final || r.ForwardAttempts >= MaxAttempts)
            {
                r.ForwardState = ReportForwardStates.Failed;
                ServerLog.Warn($"Report {ReportText.Short(r.ReportId)} could not be sent to Colony Nexus Global ({code}). "
                             + "It stays on this server - 'kmh reports show " + ReportText.Short(r.ReportId) + "'.");
                return false;
            }

            // 2, 4, 8 ... minutes, at most six hours: an outage costs a few requests, not one a minute.
            long wait = Math.Min(6L * 3600, 60L << Math.Min(r.ForwardAttempts, 9));
            r.NextAttemptUnix = now.ToUnixTimeSeconds() + wait;
            return false;
        }

        internal static int Requeue()
        {
            if (!CanForward(out _)) return 0;
            int n = 0;
            foreach (StoredReport r in ReportStore.List())
            {
                if (r.ForwardState != ReportForwardStates.Failed && r.ForwardState != ReportForwardStates.Off) continue;
                if (!r.WantsDevelopers) continue;
                r.ForwardState = ReportForwardStates.Pending;
                r.ForwardAttempts = 0;
                r.NextAttemptUnix = 0;
                if (ReportStore.Update(r)) n++;
            }
            if (n > 0) Nudge();
            return n;
        }
    }
}
