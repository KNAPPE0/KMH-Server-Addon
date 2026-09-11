using System;
using System.Collections.Generic;

namespace KMHServerAddon.Transactions
{
    internal static class KmhTxSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            List<KmhTransaction> Mix(int finished, int open)
            {
                var l = new List<KmhTransaction>();
                for (int i = 0; i < finished; i++) l.Add(new KmhTransaction { Id = $"done{i}", State = KmhTxState.Confirmed });
                for (int i = 0; i < open; i++)     l.Add(new KmhTransaction { Id = $"open{i}", State = KmhTxState.Reserved });
                return l;
            }

            var replayIdx = new KmhTransactionIndex();
            long t0 = DateTime.UtcNow.Ticks;
            var settled = new KmhTransaction { Id = "done-x", RequestKey = "mkt.buy|alice|opX", State = KmhTxState.Confirmed };
            replayIdx.Load(new List<KmhTransaction> { settled });
            bool liveRowBlocks = replayIdx.IsDuplicate("mkt.buy|alice|opX", t0);

            var carried = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KmhTransaction d in KmhTxPrune.SelectForPrune(new List<KmhTransaction> { settled }, 0))
                carried[d.RequestKey] = t0;
            replayIdx.Load(new List<KmhTransaction>(), carried);
            bool prunedStillBlocks = replayIdx.IsDuplicate("mkt.buy|alice|opX", t0 + TimeSpan.FromHours(1).Ticks);
            bool expiresEventually = !replayIdx.IsDuplicate("mkt.buy|alice|opX", t0 + KmhTransactionIndex.KeyLifetime.Ticks + TimeSpan.FromMinutes(1).Ticks);

            r.Add(("Tx replay: a settled operation stays un-repeatable after its row is pruned",
                   liveRowBlocks && prunedStillBlocks,
                   liveRowBlocks && prunedStillBlocks ? "key outlives the row"
                       : $"liveRow={liveRowBlocks}, afterPrune={prunedStillBlocks} - PRUNING FREED A SPENT OPERATION KEY"));
            r.Add(("Tx replay: the key is retained for a defined lifetime, not forever",
                   expiresEventually, $"{KmhTransactionIndex.KeyLifetime.TotalHours}h"));

            var over = Mix(10, 3);
            int dropped = KmhTxPrune.PruneFinished(over, 4);
            int openLeft = 0; foreach (KmhTransaction t in over) if (!KmhTxPrune.IsFinished(t)) openLeft++;
            r.Add(("Tx retention: settled history is capped", dropped == 6 && over.Count == 7, $"dropped {dropped}, {over.Count} left"));
            r.Add(("Tx retention: unfinished transactions are NEVER dropped", openLeft == 3, $"{openLeft} of 3 open kept"));
            r.Add(("Tx retention: the newest settled ones survive",
                   over.Exists(t => t.Id == "done9") && !over.Exists(t => t.Id == "done0"), ""));

            var allOpen = Mix(0, 5);
            r.Add(("Tx retention: a list of only open work is untouched",
                   KmhTxPrune.PruneFinished(allOpen, 0) == 0 && allOpen.Count == 5, ""));
            r.Add(("Tx retention: under the cap nothing is dropped",
                   KmhTxPrune.PruneFinished(Mix(3, 1), 500) == 0, ""));
            r.Add(("Tx retention: null and negative caps are safe",
                   KmhTxPrune.PruneFinished(null, 5) == 0 && KmhTxPrune.PruneFinished(Mix(2, 0), -1) == 2, ""));
            r.Add(("Tx retention: only terminal states count as finished",
                   KmhTxPrune.IsFinished(new KmhTransaction { State = KmhTxState.Confirmed })
                   && !KmhTxPrune.IsFinished(new KmhTransaction { State = KmhTxState.Failed })
                   && !KmhTxPrune.IsFinished(null), "Failed is recoverable, not finished"));

            var happy = new[]
            {
                KmhTxState.Requested, KmhTxState.Validating, KmhTxState.Reserved,
                KmhTxState.Approved, KmhTxState.Delivered, KmhTxState.Confirmed,
            };
            bool pathOk = true;
            for (int i = 0; i + 1 < happy.Length; i++)
                if (!KmhTxStateMachine.CanTransition(happy[i], happy[i + 1])) pathOk = false;
            r.Add(("Tx: happy path is legal", pathOk, "Requested->...->Confirmed"));

            r.Add(("Tx: cannot skip validation",
                !KmhTxStateMachine.CanTransition(KmhTxState.Requested, KmhTxState.Delivered), "Requested->Delivered denied"));
            r.Add(("Tx: cannot un-confirm",
                !KmhTxStateMachine.CanTransition(KmhTxState.Confirmed, KmhTxState.Delivered), "Confirmed->Delivered denied"));

            bool terminalsClosed =
                KmhTxStateMachine.IsTerminal(KmhTxState.Confirmed) &&
                KmhTxStateMachine.IsTerminal(KmhTxState.Rejected) &&
                KmhTxStateMachine.IsTerminal(KmhTxState.Refunded) &&
                KmhTxStateMachine.IsTerminal(KmhTxState.Recovered) &&
                !KmhTxStateMachine.IsTerminal(KmhTxState.Failed);   // Failed can still be refunded/recovered
            r.Add(("Tx: terminal states closed, Failed open", terminalsClosed, "Confirmed/Rejected/Refunded/Recovered terminal"));

            r.Add(("Tx: failed can recover",
                KmhTxStateMachine.CanTransition(KmhTxState.Failed, KmhTxState.Recovered) &&
                KmhTxStateMachine.CanTransition(KmhTxState.Failed, KmhTxState.Refunded), "Failed->Recovered/Refunded"));

            var tx = KmhTransaction.Create("alice", "marketplace", KmhTxType.Purchase, "10x Silver");
            bool advanceOk = tx.Advance(KmhTxState.Validating) && !tx.Advance(KmhTxState.Confirmed) && tx.State == KmhTxState.Validating;
            r.Add(("Tx: Advance guards illegal jumps", advanceOk, $"state stayed {tx.State}"));

            var tx2 = KmhTransaction.Create("bob", "treasury", KmhTxType.Withdrawal, "5x Steel");
            tx2.Advance(KmhTxState.Validating); tx2.Advance(KmhTxState.Reserved); tx2.Advance(KmhTxState.Approved);
            tx2.Advance(KmhTxState.Delivered);  bool beforeConfirm = !tx2.DeliveryConfirmed;
            tx2.Advance(KmhTxState.Confirmed);
            r.Add(("Tx: Confirmed sets DeliveryConfirmed", beforeConfirm && tx2.DeliveryConfirmed, "flag flipped on Confirmed"));

            string a = KmhTransaction.NewId(), b = KmhTransaction.NewId(), c = KmhTransaction.NewId();
            bool idsOk = a != b && b != c && string.CompareOrdinal(a, b) < 0 && string.CompareOrdinal(b, c) < 0;
            r.Add(("Tx: ids unique and ordered", idsOk, $"{a} < {b} < {c}"));

            bool recovery =
                KmhTransactionRecovery.DecideForStuck(KmhTxState.Requested)  == KmhTxRecoveryAction.Reject    &&
                KmhTransactionRecovery.DecideForStuck(KmhTxState.Validating) == KmhTxRecoveryAction.Reject    &&
                KmhTransactionRecovery.DecideForStuck(KmhTxState.Reserved)   == KmhTxRecoveryAction.Refund    &&
                KmhTransactionRecovery.DecideForStuck(KmhTxState.Approved)   == KmhTxRecoveryAction.Refund    &&
                KmhTransactionRecovery.DecideForStuck(KmhTxState.Delivered)  == KmhTxRecoveryAction.Reconcile &&
                KmhTransactionRecovery.DecideForStuck(KmhTxState.Confirmed)  == KmhTxRecoveryAction.None;
            r.Add(("Tx: recovery follows escrow boundary", recovery, "reject/refund/reconcile/none"));

            var idx = new KmhTransactionIndex();
            var t1 = KmhTransaction.Create("alice", "marketplace", KmhTxType.Purchase, "x"); t1.RequestKey = "req-1";
            var t2 = KmhTransaction.Create("alice", "marketplace", KmhTxType.Purchase, "x"); t2.RequestKey = "req-1";
            bool first  = idx.Add(t1);
            bool dupRej = !idx.Add(t2);
            bool lookup = idx.Get(t1.Id) != null;
            bool isDup  = idx.IsDuplicate("req-1") && !idx.IsDuplicate("never-seen");
            r.Add(("Tx: index dedups + looks up", first && dupRej && lookup && isDup, "same key refused, id found"));

            var t3 = KmhTransaction.Create("bob", "treasury", KmhTxType.Deposit, "y"); t3.RequestKey = "req-2";
            idx.Add(t3);
            bool inPending = idx.Pending().Exists(t => t.Id == t3.Id);
            t3.Advance(KmhTxState.Rejected);
            bool outOfPending = !idx.Pending().Exists(t => t.Id == t3.Id);
            r.Add(("Tx: pending tracks non-terminal", inPending && outOfPending, "in while open, out when terminal"));

            bool perPlayer = idx.ForPlayer("alice").Count == 1 && idx.ForPlayer("bob").Count == 1;
            var idx2 = new KmhTransactionIndex();
            idx2.Load(new List<KmhTransaction> { t1, t3 });
            bool reload = idx2.Get(t1.Id) != null && idx2.IsDuplicate("req-1") && idx2.Count == 2;
            r.Add(("Tx: per-player filter + reload", perPlayer && reload, "filtered + rebuilt from disk shape"));

            return r;
        }
    }
}
