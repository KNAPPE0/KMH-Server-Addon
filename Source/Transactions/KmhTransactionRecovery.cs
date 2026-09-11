namespace KMHServerAddon.Transactions
{
    // The decision follows the escrow boundary: nothing reserved, reject; reserved, refund; delivered, a human decides.
    internal enum KmhTxRecoveryAction { None, Reject, Refund, Reconcile }

    internal static class KmhTransactionRecovery
    {
        public static KmhTxRecoveryAction DecideForStuck(KmhTxState state)
        {
            switch (state)
            {
                case KmhTxState.Requested:
                case KmhTxState.Validating:
                    return KmhTxRecoveryAction.Reject;   // nothing reserved; safe to abandon
                case KmhTxState.Reserved:
                case KmhTxState.Approved:
                case KmhTxState.Compensating:
                    return KmhTxRecoveryAction.Refund;   // value escrowed, never delivered -> give it back
                case KmhTxState.Delivered:
                case KmhTxState.Failed:
                    return KmhTxRecoveryAction.Reconcile; // delivered-but-unconfirmed or errored -> needs a resolution pass
                default:
                    return KmhTxRecoveryAction.None;      // terminal states are settled
            }
        }
    }
}
