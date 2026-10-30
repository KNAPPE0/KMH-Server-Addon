using System;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Order matters: flushing stores while the scheduler still settles auctions writes half-settled state.
    internal static class KmhShutdown
    {
        private static readonly object _lock = new object();
        private static bool _begun;

        public static bool InProgress => _begun;

        public static void Begin()
        {
            lock (_lock)
            {
                if (_begun) return;
                _begun = true;
            }

            // Admin and recovery stay privileged so the steps below are not gated against themselves.
            KmhMaintenanceGate.Enter(KmhMaintenanceReason.Shutdown);

            if (!KmhScheduler.StopAndDrain(TimeSpan.FromSeconds(10)))
                ServerLog.Warn("Shutdown: a scheduler job was still running after 10s - flushing anyway.");

            // The gate stops new moves; one admitted before it may be between its writes and must finish first.
            if (!KmhValueOps.WaitForIdle(TimeSpan.FromSeconds(10)))
                ServerLog.Warn($"Shutdown: {KmhValueOps.InFlight} value move(s) still running after 10s - flushing anyway.");

            // Before the config flush below, or a heartbeat in flight can persist a sequence after it.
            try
            {
                if (!Features.Global.KmhGlobalService.StopAndWait(TimeSpan.FromSeconds(10)))
                    ServerLog.Warn("Shutdown: a Global heartbeat was still in flight after 10s - flushing anyway.");
            }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: Global heartbeat stop failed: {ex.Message}"); }

            // Parked with it: the world chat poller talks to the same directory on its own timer.
            try { Features.Discord.KmhWorldChatDiscordBridge.Stop(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: world chat bridge stop failed: {ex.Message}"); }

            // The ledger holds entries no store snapshot contains.
            try { Persistence.TransactionLedger.Flush(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: ledger flush failed: {ex.Message}"); }

            try { ServerLog.Info($"Shutdown flush: {KmhDataFlush.FlushAll()} store(s) saved."); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: store flush failed: {ex.Message}"); }

            // Extensions after the flush, since they may still want to read state.
            try { Extensibility.ExtensionLoader.ShutdownAll(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: extension shutdown failed: {ex.Message}"); }

            // Its own loop would otherwise keep appending behind everything above.
            try { Persistence.TransactionLedger.Stop(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: ledger stop failed: {ex.Message}"); }

            // Last, and bounded: everything above is worth recording, but a stuck sink must not hold the exit.
            try
            {
                ServerLog.Info($"Shutdown complete - {Persistence.JsonFileStore.DescribeHealth()}");
                Diagnostics.KmhLogSink.Stop(TimeSpan.FromSeconds(5));
            }
            catch { /* exiting anyway */ }
        }

        internal static void ResetForTest()
        {
            lock (_lock)
            {
                if (!_begun) return;
                _begun = false;
            }
            KmhMaintenanceGate.Release();
            KmhScheduler.UnparkForTest();
        }
    }
}
