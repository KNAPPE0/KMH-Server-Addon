using System;
using System.Collections.Generic;

namespace KMHServerAddon.SubProtocol
{
    internal static class KmhBroadcastBatchSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { KmhBroadcastBatch.ResetForTest(); (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { KmhBroadcastBatch.ResetForTest(); }
            }

            Check("Broadcast batch: ten requests for one kind collapse to one send", () =>
            {
                int sent = 0;
                using (KmhBroadcastBatch.Open())
                    for (int i = 0; i < 10; i++)
                        if (!KmhBroadcastBatch.Defer("kmh.sites", () => sent++)) sent++;
                return (sent == 1, $"{sent} send(s) for 10 requests");
            });

            Check("Broadcast batch: different kinds each still send once", () =>
            {
                int sites = 0, market = 0;
                using (KmhBroadcastBatch.Open())
                    for (int i = 0; i < 5; i++)
                    {
                        if (!KmhBroadcastBatch.Defer("kmh.sites", () => sites++)) sites++;
                        if (!KmhBroadcastBatch.Defer("kmh.market", () => market++)) market++;
                    }
                return (sites == 1 && market == 1, $"sites {sites}, market {market}");
            });

            // Outside a batch nothing is delayed, or a single action would wait on a scope that never opens.
            Check("Broadcast batch: a lone broadcast is not deferred", () =>
            {
                bool deferred = KmhBroadcastBatch.Defer("kmh.sites", () => { });
                return (!deferred && !KmhBroadcastBatch.Active, deferred ? "it was held with no scope open" : "sent immediately");
            });

            Check("Broadcast batch: nesting sends once, at the outermost close", () =>
            {
                int sent = 0;
                using (KmhBroadcastBatch.Open())
                {
                    KmhBroadcastBatch.Defer("kmh.sites", () => sent++);
                    using (KmhBroadcastBatch.Open()) KmhBroadcastBatch.Defer("kmh.sites", () => sent++);
                    if (sent != 0) return (false, "an inner scope flushed early");
                }
                return (sent == 1, $"{sent} send(s)");
            });

            Check("Broadcast batch: one failing send does not lose the others", () =>
            {
                int good = 0;
                using (KmhBroadcastBatch.Open())
                {
                    KmhBroadcastBatch.Defer("kmh.bad", () => throw new InvalidOperationException("boom"));
                    KmhBroadcastBatch.Defer("kmh.good", () => good++);
                }
                return (good == 1 && !KmhBroadcastBatch.Active, $"good {good}, depth clear {!KmhBroadcastBatch.Active}");
            });

            Check("Broadcast batch: the scope leaves nothing pending behind it", () =>
            {
                using (KmhBroadcastBatch.Open()) KmhBroadcastBatch.Defer("kmh.sites", () => { });
                return (KmhBroadcastBatch.PendingCountForTest == 0, $"{KmhBroadcastBatch.PendingCountForTest} left");
            });

            return r;
        }
    }
}
