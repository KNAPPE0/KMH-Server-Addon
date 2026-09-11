using System;

namespace KMHServerAddon.Maintenance
{
    // Why maintenance is active, so the block message can name a cause and telemetry can tell them apart.
    internal enum KmhMaintenanceReason { None, Migration, Restore, Backup, DataRepair, TransactionReconcile, OwnerMaintenance, Shutdown, PersistenceFailure }

    // Blocks value moves while KMH rewrites files underneath the economy, so a deposit cannot land mid-restore.
    internal static class KmhMaintenanceGate
    {
        private static readonly object _lock = new object();

        // Derived from the two independent conditions rather than stored, so they can't drift apart.
        public static bool IsActive => _depth > 0 || FrozenForPersistence;

        public static KmhMaintenanceReason Reason
            => _depth > 0             ? _maintenanceReason
             : FrozenForPersistence   ? KmhMaintenanceReason.PersistenceFailure
             : KmhMaintenanceReason.None;

        // Re-entrant by count, or a restore inside a migration releases the gate early; volatile for lock-free reads.
        private static volatile int _depth;
        private static KmhMaintenanceReason _maintenanceReason = KmhMaintenanceReason.None;
        private static volatile bool _persistenceDegraded;

        // Owner-overridable: KMH would rather refuse a deposit than take one it cannot write down.
        private static bool FrozenForPersistence
            => _persistenceDegraded && MaintenanceConfig.Current.FreezeEconomyOnPersistenceFailure;

        public static bool PersistenceDegraded => _persistenceDegraded;

        // A condition rather than a scope, so it is not depth-counted and clears only when a save succeeds again.
        public static void SetPersistenceDegraded(bool degraded)
        {
            lock (_lock) { _persistenceDegraded = degraded; }
        }

        public static void Enter(KmhMaintenanceReason reason)
        {
            lock (_lock) { _depth++; if (_maintenanceReason == KmhMaintenanceReason.None) _maintenanceReason = reason; }
        }

        public static void Release()
        {
            lock (_lock)
            {
                if (_depth > 0) _depth--;
                if (_depth == 0) _maintenanceReason = KmhMaintenanceReason.None;
            }
        }

        // Always releases, even on exception, or a failed restore freezes the economy permanently.
        public static void Run(KmhMaintenanceReason reason, Action action)
        {
            Enter(reason);
            try { action(); }
            finally { Release(); }
        }

        // Reads and infrastructure kinds stay allowed, so players can still see state while the economy is frozen.
        public static bool WouldBlock(string kind)
            => !string.IsNullOrEmpty(kind) && KmhMutationClass.IsMutation(kind);

        public static bool ShouldBlock(string kind) => IsActive && WouldBlock(kind);

        // For callers with no protocol kind (the SDK); admin commands are deliberately NOT gated - the operator needs to move value.
        public static bool BlocksValueMoves => IsActive;

        public static string Describe() => IsActive ? $"maintenance active ({Reason})" : "not in maintenance";
    }
}
