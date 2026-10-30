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
                    return KmhTxRecoveryAction.Reject;
                case KmhTxState.Reserved:
                case KmhTxState.Approved:
                case KmhTxState.Compensating:
                    return KmhTxRecoveryAction.Refund;
                case KmhTxState.Delivered:
                case KmhTxState.Failed:
                    return KmhTxRecoveryAction.Reconcile;
                default:
                    return KmhTxRecoveryAction.None;
            }
        }
    }
}
