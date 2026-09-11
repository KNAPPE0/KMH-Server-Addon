using System.Collections.Generic;
using KMHServerAddon.Results;

namespace KMHServerAddon.Transactions
{
    internal static class KmhSettlementSelfTest
    {
        private sealed class MockMove : IKmhValueMove
        {
            public bool ReserveOk = true, DeliverOk = true;
            public KmhErrorCode ReserveErr = KmhErrorCode.InsufficientFunds, DeliverErr = KmhErrorCode.DeliveryFailed;
            public bool Reserved, Delivered, Refunded;

            public string System => "marketplace";
            public KmhTxType Type => KmhTxType.Purchase;
            public string Describe() => "mock move";
            public bool Reserve(out KmhErrorCode e) { e = ReserveOk ? KmhErrorCode.None : ReserveErr; if (ReserveOk) Reserved = true; return ReserveOk; }
            public bool Deliver(out KmhErrorCode e) { e = DeliverOk ? KmhErrorCode.None : DeliverErr; if (DeliverOk) Delivered = true; return DeliverOk; }
            public void Refund() { Refunded = true; }
        }

        private static KmhTransaction NewTx() => KmhTransaction.Create("p", "marketplace", KmhTxType.Purchase, "x");

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var m1 = new MockMove();
            var tx1 = NewTx();
            var o1 = KmhSettlement.RunCore(tx1, m1);
            r.Add(("Settlement: happy path confirms",
                o1.Ok && tx1.State == KmhTxState.Confirmed && m1.Reserved && m1.Delivered && !m1.Refunded, "reserve->deliver->confirm"));

            var m2 = new MockMove();
            var tx2 = NewTx();
            var o2 = KmhSettlement.RunCore(tx2, m2, () => KmhErrorCode.PermissionDenied);
            r.Add(("Settlement: policy refusal rejects",
                !o2.Ok && o2.Error == KmhErrorCode.PermissionDenied && tx2.State == KmhTxState.Rejected
                    && !m2.Reserved && o2.Message.Length > 0, "rejected before reserve"));

            var m3 = new MockMove { ReserveOk = false };
            var tx3 = NewTx();
            var o3 = KmhSettlement.RunCore(tx3, m3);
            r.Add(("Settlement: reserve failure rejects, no refund",
                !o3.Ok && o3.Error == KmhErrorCode.InsufficientFunds && tx3.State == KmhTxState.Rejected && !m3.Refunded, "no phantom refund"));

            var m4 = new MockMove { DeliverOk = false };
            var tx4 = NewTx();
            var o4 = KmhSettlement.RunCore(tx4, m4);
            r.Add(("Settlement: delivery failure refunds",
                !o4.Ok && m4.Reserved && !m4.Delivered && m4.Refunded && tx4.State == KmhTxState.Refunded, "reserved then refunded"));

            var thrower = new ThrowingMove();
            var tx5 = NewTx();
            var o5 = KmhSettlement.RunCore(tx5, thrower);
            r.Add(("Settlement: a throwing deliver still refunds",
                !o5.Ok && thrower.Refunded && tx5.State == KmhTxState.Refunded, "exception -> refund"));

            return r;
        }

        private sealed class ThrowingMove : IKmhValueMove
        {
            public bool Refunded;
            public string System => "marketplace";
            public KmhTxType Type => KmhTxType.Purchase;
            public string Describe() => "throwing move";
            public bool Reserve(out KmhErrorCode e) { e = KmhErrorCode.None; return true; }
            public bool Deliver(out KmhErrorCode e) { throw new System.Exception("boom"); }
            public void Refund() { Refunded = true; }
        }
    }
}
