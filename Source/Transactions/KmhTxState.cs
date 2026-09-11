using System.Collections.Generic;

namespace KMHServerAddon.Transactions
{
    // One lifecycle for every economic action, so recovery and rollback never face a bespoke half-state per feature.
    internal enum KmhTxState
    {
        Requested,   // client asked; nothing reserved yet
        Validating,  // server checking balance/inventory/permission
        Reserved,    // goods/silver escrowed out of the source, awaiting approval
        Approved,    // validated and cleared to deliver
        Delivered,   // handed to the destination (materialized in-colony / credited)
        Confirmed,   // destination acknowledged receipt; the happy-path terminal
        Rejected,    // failed validation before anything was reserved
        Refunded,    // reserved goods returned to the source
        Recovered,   // reconciled after a failure/rollback (e.g. re-credited or clawed back)
        Failed,      // an error left it incomplete; needs refund or recovery
        // Written down before the escrow is credited back, so a crash mid-refund resumes instead of guessing.
        Compensating,
    }

    // Decides which system owns delivery and refund for the transaction.
    internal enum KmhTxType
    {
        Deposit,
        Withdrawal,
        Purchase,
        Sale,
        Listing,
        Cancellation,
        Refund,
        AuctionBid,
        WantBoardFill,
        QuestReward,
        SiteOutput,
        GuildContribution,
        RecoveryDelivery,
    }

    // The one authority on legal transitions, kept beside the enum so a new state cannot be added without one.
    internal static class KmhTxStateMachine
    {
        private static readonly Dictionary<KmhTxState, KmhTxState[]> Allowed = new Dictionary<KmhTxState, KmhTxState[]>
        {
            [KmhTxState.Requested]  = new[] { KmhTxState.Validating, KmhTxState.Rejected, KmhTxState.Failed },
            [KmhTxState.Validating] = new[] { KmhTxState.Reserved, KmhTxState.Rejected, KmhTxState.Failed },
            [KmhTxState.Reserved]   = new[] { KmhTxState.Approved, KmhTxState.Rejected, KmhTxState.Compensating, KmhTxState.Refunded, KmhTxState.Failed },
            [KmhTxState.Approved]   = new[] { KmhTxState.Delivered, KmhTxState.Compensating, KmhTxState.Refunded, KmhTxState.Failed },
            [KmhTxState.Delivered]  = new[] { KmhTxState.Confirmed, KmhTxState.Recovered, KmhTxState.Failed },
            [KmhTxState.Failed]     = new[] { KmhTxState.Compensating, KmhTxState.Refunded, KmhTxState.Recovered },

            // Re-enterable on purpose: a crash here resumes the same credit, which the escrow's txn id deduplicates.
            [KmhTxState.Compensating] = new[] { KmhTxState.Compensating, KmhTxState.Refunded, KmhTxState.Failed },

            // Terminal: a refund or recovery is a NEW transaction pointing back, never a mutation of the original.
            [KmhTxState.Confirmed]  = new KmhTxState[0],
            [KmhTxState.Rejected]   = new KmhTxState[0],
            [KmhTxState.Refunded]   = new KmhTxState[0],
            [KmhTxState.Recovered]  = new KmhTxState[0],
        };

        public static bool IsTerminal(KmhTxState state)
            => Allowed.TryGetValue(state, out KmhTxState[] next) && next.Length == 0;

        public static bool CanTransition(KmhTxState from, KmhTxState to)
        {
            if (!Allowed.TryGetValue(from, out KmhTxState[] next)) return false;
            foreach (KmhTxState s in next) if (s == to) return true;
            return false;
        }
    }
}
