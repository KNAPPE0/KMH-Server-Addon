using System;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // Completes setup once the browser approval lands, so the owner need not rerun 'kmh global setup'.
    internal static class KmhGlobalSetupCompleter
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

        private static CancellationTokenSource _cts;
        private static Task _loop;

        public static bool Running => _loop != null && !_loop.IsCompleted;

        // Other stages need the owner to start setup, or a grant a restart has already lost.
        public static bool ShouldWaitFor(GlobalSetupState state, long nowUnix)
        {
            if (state == null || state.IsOwnerRegistered) return false;
            if (state.Current != SetupStage.OwnerLinkPending) return false;
            if (state.PendingLinkId.Length == 0) return false;
            return state.PendingLinkExpiresUnix <= 0 || nowUnix < state.PendingLinkExpiresUnix;
        }

        public static void Start()
        {
            Stop();
            GlobalSetupState state = GlobalSetupState.Current_;
            if (!ShouldWaitFor(state, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return;

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => WaitAsync(_cts.Token));

            long left = state.PendingLinkExpiresUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ServerLog.Info(left > 0
                ? $"Global: waiting for the owner to approve the link code - it lapses in {Minutes(left)}. "
                + "Approve it in your browser and this server registers itself; nothing else to run."
                : "Global: waiting for the owner to approve the link code. Approve it in your browser and this "
                + "server registers itself; nothing else to run.");
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch (Exception) { }
            _cts = null;
            _loop = null;
        }

        private static string Minutes(long seconds) => $"{Math.Max(1, seconds / 60 + 1)} minute(s)";

        private static async Task WaitAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                GlobalSetupState state = GlobalSetupState.Current_;
                if (state.IsOwnerRegistered) return;

                // The console may have moved setup on, or the link may have lapsed during the wait.
                if (!ShouldWaitFor(state, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                {
                    if (state.Current == SetupStage.OwnerLinkPending)
                        ServerLog.Info("Global: the link code lapsed before it was approved. "
                                     + "Run 'kmh global setup new' for a fresh one.");
                    return;
                }

                try { await StepAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // A Global outage must not end the wait: the code is good until it expires.
                    ServerLog.Verbose($"Global: setup poll failed ({ex.GetType().Name}); still waiting.");
                }
            }
        }

        private static async Task StepAsync(CancellationToken ct)
        {
            if (!await KmhGlobalRegistration.SetupGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) return;
            try
            {
                using (CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    call.CancelAfter(CallTimeout);

                    KmhGlobalRegistration.Outcome polled =
                        await KmhGlobalRegistration.PollOwnerLinkAsync(call.Token).ConfigureAwait(false);

                    // Not approved yet is the normal answer; no log line every poll.
                    if (!polled.Ok) return;

                    KmhGlobalRegistration.Outcome registered =
                        await KmhGlobalRegistration.RegisterAsync(call.Token).ConfigureAwait(false);
                    if (!registered.Ok)
                    {
                        ServerLog.Warn($"Global: the owner approved the link but registration was refused: "
                                     + $"{registered.Detail}"
                                     + (string.IsNullOrEmpty(registered.ErrorCode) ? "" : $" ({registered.ErrorCode})")
                                     + ". Run 'kmh global setup' to retry.");
                        return;
                    }

                    GlobalConfig cfg = GlobalConfig.Current;
                    if (!cfg.Enabled) { cfg.Enabled = true; cfg.Save(); }
                    ServerLog.Success("Global: owner approval received - this server registered itself and is now "
                                    + "listed. No further setup is needed.");
                }
            }
            finally { KmhGlobalRegistration.SetupGate.Release(); }

            // Outside the gate: the service restart stops this completer and needs the lock this method holds.
            KmhGlobalService.Stop();
            KmhGlobalService.Start();
        }
    }
}
