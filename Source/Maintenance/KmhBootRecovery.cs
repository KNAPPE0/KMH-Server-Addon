using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Whether boot may settle the value a crash left in flight; the passes themselves stay in Main, in order.
    internal static class KmhBootRecovery
    {
        // A store that came up empty because its file was damaged would take these credits, and restoring that
        // file - what DAMAGED.txt asks for - erases them with nothing left to replay. They wait, still recorded.
        public static bool ShouldRun()
        {
            if (!Persistence.JsonFileStore.DamageReported(out _)) return true;
            ServerLog.Error("Boot recovery SKIPPED - KMH-Data is damaged. Interrupted payouts and refunds stay recorded "
                          + "and are settled on the first boot after the damaged file is restored and DAMAGED.txt deleted.");
            return false;
        }
    }
}
