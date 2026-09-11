using System;
using System.Collections.Generic;

namespace KMHServerAddon.Security
{
    internal static class KmhSeenGuardSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            long t0 = 0;
            long min = TimeSpan.FromMinutes(1).Ticks;

            var g = new KmhSeenGuard(TimeSpan.FromMinutes(10));
            bool firstNew = g.MarkIfNew("a", t0);
            bool dupSame  = !g.MarkIfNew("a", t0 + min);            // 1 min later, inside 10-min window -> duplicate
            r.Add(("SeenGuard: new then duplicate", firstNew && dupSame, "first new, repeat suppressed"));

            bool newAfterWindow = g.MarkIfNew("a", t0 + TimeSpan.FromMinutes(11).Ticks);   // past window -> new again
            r.Add(("SeenGuard: expires after window", newAfterWindow, "re-usable after 10 min"));

            var g2 = new KmhSeenGuard(TimeSpan.FromMinutes(10), pruneAt: 64);
            for (int i = 0; i < 200; i++) g2.MarkIfNew("old-" + i, t0);
            long twentyLater = t0 + TimeSpan.FromMinutes(20).Ticks;
            bool recentNew = g2.MarkIfNew("recent", twentyLater);
            bool recentDup = !g2.MarkIfNew("recent", twentyLater + min);
            bool pruned    = g2.Count < 50;
            r.Add(("SeenGuard: prune keeps recent ids", recentNew && recentDup && pruned, $"no replay hole after prune ({g2.Count} kept)"));

            bool emptyOk = g.MarkIfNew("", t0) && g.MarkIfNew("", t0);
            r.Add(("SeenGuard: empty key never suppressed", emptyOk, "no key = always allowed"));

            return r;
        }
    }
}
