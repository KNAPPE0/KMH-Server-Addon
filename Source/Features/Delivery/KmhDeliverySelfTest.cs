using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Features.Delivery
{
    // Transport success is not receipt: these pin the boundaries where value used to disappear, on either side of the send and across a restart.
    internal static class KmhDeliverySelfTest
    {
        private static string User() => "selftest_dlv_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // The offline runner never boots KmhDataMeta, so without this every id below is namespace-less - what the first check forbids.
            string bootedIdentity = KmhServerIdentity.Id;
            if (string.IsNullOrEmpty(bootedIdentity)) KmhDataMeta.SetInstanceIdForTest("selftestsrv");

            Check("Delivery: no delivery is issued before this data set has an identity", () =>
            {
                string had = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest("");
                OutboundDelivery blank = DeliveryStore.Owe(User(), 100, null, null, "");
                KmhDataMeta.SetInstanceIdForTest(had);
                return (blank == null, blank == null
                    ? "refused while the namespace is unknown"
                    : $"issued '{blank.Id}' - AN ID EVERY SERVER WOULD ALSO MINT");
            });

            Check("Delivery A: a delivery is owed before it is ever sent", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 250, null, null, "");
                bool owedNow = d != null && DeliveryStore.OwedFor(who).Count == 1;
                return (owedNow, owedNow ? "recorded before the socket is touched" : "NOT OWED BEFORE SEND");
            });

            Check("Delivery B: an unacknowledged delivery replays after a reconnect", () =>
            {
                string who = User();
                DeliveryStore.Owe(who, 400, null, null, "");
                int first = DeliveryStore.OwedFor(who).Count;
                int again = DeliveryStore.OwedFor(who).Count;   // a reconnect asks the same question
                return (first == 1 && again == 1, $"owed {first} then {again}");
            });

            Check("Delivery D: an acknowledged delivery stops being owed, and a repeat ack is harmless", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 300, null, null, "");
                bool acked  = DeliveryStore.Ack(who, d.Id);
                bool second = DeliveryStore.Ack(who, d.Id);   // the ack the client re-sends when its own ack was lost
                int owed = DeliveryStore.OwedFor(who).Count;
                bool ok = acked && !second && owed == 0;
                return (ok, ok ? "settled once, replayed ack ignored" : $"first={acked}, second={second}, owed={owed}");
            });

            Check("Delivery E: an owed delivery survives a restart", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 175, null, null, "");
                if (d == null) return (false, "could not owe");
                DeliveryStore.ClearForTest();          // the process going away
                DeliveryStore.LoadFromDisk();          // and coming back
                List<OutboundDelivery> owed = DeliveryStore.OwedFor(who);
                bool ok = owed.Count == 1 && owed[0].Silver == 175 && owed[0].Id == d.Id;
                return (ok, ok ? "same id and amount after reload" : $"owed={owed.Count} - AN OWED DELIVERY DID NOT SURVIVE A RESTART");
            });

            Check("Delivery F: ids are namespaced by this data set, so servers cannot collide", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 120, null, null, "");
                string prefix = KmhServerIdentity.Id + ":";
                bool namespaced = d != null && d.Id.StartsWith(prefix, StringComparison.Ordinal) && d.Id.Length > prefix.Length;
                bool foreign = DeliveryStore.Ack(who, "someotherserver:" + (d?.Id ?? "").Split(':')[1]);
                bool stillOwed = DeliveryStore.OwedFor(who).Count == 1;
                bool ok = namespaced && !foreign && stillOwed;
                return (ok, ok ? "id carries this data set's identity" : $"namespaced={namespaced}, foreignAccepted={foreign}, stillOwed={stillOwed}");
            });

            Check("Delivery: one player cannot acknowledge another's", () =>
            {
                string mine = User(), theirs = User();
                OutboundDelivery d = DeliveryStore.Owe(mine, 90, null, null, "");
                bool stolen = DeliveryStore.Ack(theirs, d.Id);
                bool stillOwed = DeliveryStore.OwedFor(mine).Count == 1;
                return (!stolen && stillOwed, !stolen && stillOwed
                    ? "ack is bound to the owner" : $"stolen={stolen}, stillOwed={stillOwed}");
            });

            Check("Delivery: a delivery that cannot be written down is not handed out", () =>
            {
                string who = User();
                OutboundDelivery d;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.DeliveryFile ? "injected: disk full" : null))
                    d = DeliveryStore.Owe(who, 500, null, null, "");
                bool nothingOwed = DeliveryStore.OwedFor(who).Count == 0;
                return (d == null && nothingOwed, d == null && nothingOwed
                    ? "refused, caller keeps the value" : $"record={(d == null ? "null" : d.Id)}, owed={!nothingOwed}");
            });

            Check("Link D: an economic commit with an unacked delivery is not finished", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 200, null, null);
                if (d == null) return (false, "could not open the operation");
                KmhTransaction t = null;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who)) if (x.DeliveryId == d.Id) t = x;
                bool linked = t != null && t.State == KmhTxState.Delivered;
                // Terminal-looking is not finished while the colony has not confirmed holding the value.
                bool notFinished = t != null && !KmhTxPrune.IsFinished(t);
                bool ok = linked && notFinished;
                return (ok, ok ? "Delivered, linked, and not prunable"
                              : $"state={t?.State.ToString() ?? "none"}, finished={!notFinished}");
            });

            Check("Link E/F: the ack finalises the operation, and only then may it be pruned", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 150, null, null);
                if (d == null) return (false, "could not open the operation");
                KmhTransaction t = null;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who)) if (x.DeliveryId == d.Id) t = x;
                bool heldBefore = t != null && !KmhTxPrune.IsFinished(t);

                DeliveryStore.Ack(who, d.Id);
                KmhTransactionRepository.ConfirmDelivered(d.Id);
                bool confirmed = t != null && t.State == KmhTxState.Confirmed;
                bool prunable  = t != null && KmhTxPrune.IsFinished(t);
                // A repeat of the whole settle must not move it anywhere else.
                KmhTransactionRepository.ConfirmDelivered(d.Id);
                bool stable = t != null && t.State == KmhTxState.Confirmed;
                bool ok = heldBefore && confirmed && prunable && stable;
                return (ok, ok ? "confirmed once on ack, then prunable"
                              : $"heldBefore={heldBefore}, confirmed={confirmed}, prunable={prunable}, stable={stable}");
            });

            Check("Link F: a settled operation is still retained while its delivery is owed", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 210, null, null);
                if (d == null) return (false, "could not open the operation");
                KmhTransaction t = null;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who)) if (x.DeliveryId == d.Id) t = x;
                if (t == null) return (false, "operation not linked");

                // A terminal state reached without the client confirming - dropping the row here orphans a delivery still being replayed.
                t.State = KmhTxState.Refunded;
                bool retained = !KmhTxPrune.IsFinished(t);
                DeliveryStore.Ack(who, d.Id);
                bool prunableAfterAck = KmhTxPrune.IsFinished(t);
                bool ok = retained && prunableAfterAck;
                return (ok, ok ? "kept while owed, prunable once settled"
                              : $"retainedWhileOwed={retained}, prunableAfterAck={prunableAfterAck}");
            });

            Check("Link: a delivery that cannot be recorded does not leave an operation half-open", () =>
            {
                string who = User();
                OutboundDelivery d;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.DeliveryFile ? "injected: disk full" : null))
                    d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 320, null, null);
                int stillOpen = 0;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who))
                    if (!KmhTxStateMachine.IsTerminal(x.State)) stillOpen++;
                return (d == null && stillOpen == 0, d == null && stillOpen == 0
                    ? "refused and the operation was compensated"
                    : $"delivery={(d == null ? "null" : d.Id)}, openOperations={stillOpen}");
            });

            Check("Delivery: an ack the disk refuses leaves the delivery owed", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 60, null, null, "");
                bool acked;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.DeliveryFile ? "injected: disk full" : null))
                    acked = DeliveryStore.Ack(who, d.Id);
                bool stillOwed = DeliveryStore.OwedFor(who).Count == 1;
                return (!acked && stillOwed, !acked && stillOwed
                    ? "still owed until the ack is durable" : $"acked={acked}, stillOwed={stillOwed}");
            });

            return r;
        }
    }
}
