using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Recovery.Dto;

namespace KMHServerAddon.Features.Recovery
{
    // Held records are never dropped: they still hold real value awaiting triage.
    internal static class RecoveryPrune
    {
        public const string StatusHeld       = "held";
        public const string StatusDelivering = "delivering";
        public const string StatusResolved   = "resolved";

        public static bool IsHeld(RecoveryRecord r)
            => r != null && string.Equals(r.Status, StatusHeld, StringComparison.OrdinalIgnoreCase);

        // Written before the value moves and cleared after, so this state at boot means interrupted: neither re-deliverable nor forgettable.
        public static bool IsDelivering(RecoveryRecord r)
            => r != null && string.Equals(r.Status, StatusDelivering, StringComparison.OrdinalIgnoreCase);

        // Drops from the front, so "keep the newest" holds only while the list stays in oldest-first order.
        public static int PruneResolved(List<RecoveryRecord> records, int maxResolved)
        {
            if (records == null) return 0;
            if (maxResolved < 0) maxResolved = 0;

            // Only finished records are prunable: a delivering one is the sole evidence that value may or may not have arrived.
            int resolved = 0;
            foreach (RecoveryRecord r in records) if (IsPrunable(r)) resolved++;

            int toDrop = resolved - maxResolved;
            if (toDrop <= 0) return 0;

            int removed = 0;
            for (int i = 0; i < records.Count && removed < toDrop; )
            {
                if (IsPrunable(records[i])) { records.RemoveAt(i); removed++; }
                else i++;
            }
            return removed;
        }

        private static bool IsPrunable(RecoveryRecord r) => r != null && !IsHeld(r) && !IsDelivering(r);
    }
}
