using System;
using KMHServerAddon.Results;

namespace KMHServerAddon.Transactions
{
    internal readonly struct KmhSettlementOutcome
    {
        public readonly bool         Ok;
        public readonly string       TransactionId;
        public readonly KmhErrorCode Error;
        public KmhSettlementOutcome(bool ok, string txId, KmhErrorCode error) { Ok = ok; TransactionId = txId; Error = error; }

        public string Message => Ok ? "" : KmhErrorText.SafeMessage(Error);
    }

    // Before adopting: a crash between Reserve and Deliver leaves a move recovery classifies as Refund but never performs.
    internal static class KmhSettlement
    {
        // None = allowed; any other code refuses with that reason.
        public delegate KmhErrorCode PolicyCheck();

        // Persists nothing: the caller records the transaction around this.
        public static KmhSettlementOutcome RunCore(KmhTransaction tx, IKmhValueMove move, PolicyCheck policy = null)
        {
            if (tx == null || move == null) return Fail(tx, KmhErrorCode.InvalidState);

            tx.Advance(KmhTxState.Validating);
            KmhErrorCode policyResult = policy != null ? policy() : KmhErrorCode.None;
            if (policyResult != KmhErrorCode.None) { tx.Advance(KmhTxState.Rejected); return Fail(tx, policyResult); }

            if (!SafeReserve(move, out KmhErrorCode reserveErr)) { tx.Advance(KmhTxState.Rejected); return Fail(tx, reserveErr); }
            tx.Advance(KmhTxState.Reserved);
            tx.Advance(KmhTxState.Approved);

            if (!SafeDeliver(move, out KmhErrorCode deliverErr))
            {
                SafeRefund(move);
                tx.Advance(KmhTxState.Refunded);
                return Fail(tx, deliverErr == KmhErrorCode.None ? KmhErrorCode.DeliveryFailed : deliverErr);
            }
            tx.Advance(KmhTxState.Delivered);
            tx.Advance(KmhTxState.Confirmed);
            return new KmhSettlementOutcome(true, tx.Id, KmhErrorCode.None);
        }

        public static KmhSettlementOutcome Run(string player, string guild, string requestKey, IKmhValueMove move, PolicyCheck policy = null)
        {
            if (move == null) return new KmhSettlementOutcome(false, "", KmhErrorCode.InvalidState);
            if (!string.IsNullOrEmpty(requestKey) && KmhTransactionRepository.IsDuplicate(requestKey))
                return new KmhSettlementOutcome(false, "", KmhErrorCode.DuplicateRequest);

            KmhTransaction tx = KmhTransaction.Create(player, move.System, move.Type, move.Describe());
            tx.Guild = guild ?? "";
            tx.RequestKey = requestKey ?? "";
            KmhTransactionRepository.Add(tx);

            KmhSettlementOutcome outcome = RunCore(tx, move, policy);
            KmhTransactionRepository.Update(tx);
            return outcome;
        }

        private static bool SafeReserve(IKmhValueMove move, out KmhErrorCode error)
        {
            error = KmhErrorCode.None;
            try { return move.Reserve(out error); }
            catch { error = KmhErrorCode.InvalidState; return false; }
        }

        private static bool SafeDeliver(IKmhValueMove move, out KmhErrorCode error)
        {
            error = KmhErrorCode.None;
            try { return move.Deliver(out error); }
            catch { error = KmhErrorCode.DeliveryFailed; return false; }
        }

        private static void SafeRefund(IKmhValueMove move) { try { move.Refund(); } catch { } }

        private static KmhSettlementOutcome Fail(KmhTransaction tx, KmhErrorCode code)
            => new KmhSettlementOutcome(false, tx?.Id ?? "", code);
    }
}
