using System.Collections.Generic;

namespace KMHServerAddon.Transactions
{
    // A non-terminal transaction is never dropped: boot reconciliation is the only thing that can resolve it.
    internal static class KmhTxPrune
    {
        // A terminal row with an unconfirmed delivery is not finished - dropping it orphans the delivery from the operation that owes it.
        public static bool IsFinished(KmhTransaction t)
            => t != null && KmhTxStateMachine.IsTerminal(t.State) && !HasOutstandingDelivery(t);

        public static bool HasOutstandingDelivery(KmhTransaction t)
            => t != null && !string.IsNullOrEmpty(t.DeliveryId)
            && Features.Delivery.DeliveryStore.IsOwed(t.DeliveryId);

        // The rows PruneFinished would drop, so a caller can carry anything that must outlive them.
        public static List<KmhTransaction> SelectForPrune(List<KmhTransaction> records, int maxTerminal)
        {
            var doomed = new List<KmhTransaction>();
            if (records == null) return doomed;
            if (maxTerminal < 0) maxTerminal = 0;
            int finished = 0;
            foreach (KmhTransaction t in records) if (IsFinished(t)) finished++;
            int toDrop = finished - maxTerminal;
            if (toDrop <= 0) return doomed;
            foreach (KmhTransaction t in records)
            {
                if (doomed.Count >= toDrop) break;
                if (IsFinished(t)) doomed.Add(t);
            }
            return doomed;
        }

        // Mutates `records` in place, and relies on insertion order to decide which finished entries are oldest.
        public static int PruneFinished(List<KmhTransaction> records, int maxTerminal)
        {
            if (records == null) return 0;
            if (maxTerminal < 0) maxTerminal = 0;

            int finished = 0;
            foreach (KmhTransaction t in records) if (IsFinished(t)) finished++;

            int toDrop = finished - maxTerminal;
            if (toDrop <= 0) return 0;

            int removed = 0;
            for (int i = 0; i < records.Count && removed < toDrop; )
            {
                if (IsFinished(records[i])) { records.RemoveAt(i); removed++; }
                else i++;
            }
            return removed;
        }
    }
}
