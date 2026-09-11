using System;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Ordered quiescence: the old shutdown flushed the stores while the scheduler was still settling auctions.
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

            // 1. No new ordinary mutation; admin and recovery stay privileged so the steps below are not gated against themselves.
            KmhMaintenanceGate.Enter(KmhMaintenanceReason.Shutdown);

            // 2. Park background work and wait for the job already running to finish.
            if (!KmhScheduler.StopAndDrain(TimeSpan.FromSeconds(10)))
                ServerLog.Warn("Shutdown: a scheduler job was still running after 10s - flushing anyway.");

            // 3. Durability work that must complete: the ledger holds entries no store snapshot contains.
            try { Persistence.TransactionLedger.Flush(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: ledger flush failed: {ex.Message}"); }

            // 4. Now nothing else is writing, so the snapshot on disk is the one in memory.
            try { ServerLog.Info($"Shutdown flush: {KmhDataFlush.FlushAll()} store(s) saved."); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: store flush failed: {ex.Message}"); }

            // 5. Extensions last: IKmhServerExtension declares Shutdown(), and they may still want to read state.
            try { Extensibility.ExtensionLoader.ShutdownAll(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: extension shutdown failed: {ex.Message}"); }

            // 6. Its own loop would otherwise keep appending behind everything above.
            try { Persistence.TransactionLedger.Stop(); }
            catch (Exception ex) { ServerLog.Warn($"Shutdown: ledger stop failed: {ex.Message}"); }

            // 7. Last, and bounded: everything above is worth recording, but a stuck sink must not hold the exit.
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
        }
    }
}
