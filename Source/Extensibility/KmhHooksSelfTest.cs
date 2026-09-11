using System;
using System.Collections.Generic;
using KMH.Sdk.Server.Hooks;

namespace KMHServerAddon.Extensibility
{
    internal static class KmhHooksSelfTest
    {
        private static Func<int, KmhHookVerdict> Allow      => _ => KmhHookVerdict.Allow;
        private static Func<int, KmhHookVerdict> DenyA      => _ => KmhHookVerdict.Deny("A");
        private static Func<int, KmhHookVerdict> DenyB      => _ => KmhHookVerdict.Deny("B");
        private static Func<int, KmhHookVerdict> Throws     => _ => throw new Exception("boom");

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("Hooks: none registered -> allow", !KmhHooks.Aggregate(Array.Empty<Func<int, KmhHookVerdict>>(), 0, "t", null).Denied, ""));
            r.Add(("Hooks: null set -> allow", !KmhHooks.Aggregate<int>(null, 0, "t", null).Denied, ""));

            r.Add(("Hooks: allow hook -> allow", !KmhHooks.Aggregate(new[] { Allow }, 0, "t", null).Denied, ""));

            var d = KmhHooks.Aggregate(new[] { Allow, DenyA, DenyB }, 0, "t", null);
            r.Add(("Hooks: first deny wins", d.Denied && d.Reason == "A", d.Reason));

            string reported = null;
            var afterThrow = KmhHooks.Aggregate(new[] { Throws, DenyB }, 0, "throw-probe", m => reported = m);
            r.Add(("Hooks: throw = allow, keeps going", afterThrow.Denied && afterThrow.Reason == "B" && reported != null, reported ?? ""));

            r.Add(("Hooks: lone throw -> allow", !KmhHooks.Aggregate(new[] { Throws }, 0, "t", _ => { }).Denied, ""));

            // The visibility hook runs once per listing per viewer, so a down extension used to print one line each.
            string floodName = "flood-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int lines = 0;
            for (int i = 0; i < 50; i++) KmhHooks.Aggregate(new[] { Throws }, 0, floodName, _ => lines++);
            r.Add(("Hooks: a failing extension does not flood the console", lines == 1, $"{lines} line(s) from 50 failures"));

            // Suppressed failures are counted, not discarded: an owner has to be able to see the scale of an outage.
            string tallyName = "tally-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string last = null;
            KmhHooks.Aggregate(new[] { Throws }, 0, tallyName, m => last = m);           // reports, opens the window
            for (int i = 0; i < 3; i++) KmhHooks.Aggregate(new[] { Throws }, 0, tallyName, m => last = m);  // suppressed
            KmhHooks.ResetErrorWindowForTest(tallyName);                                  // the minute elapses
            KmhHooks.Aggregate(new[] { Throws }, 0, tallyName, m => last = m);
            r.Add(("Hooks: the suppressed count is reported when logging resumes",
                   last != null && last.Contains("+3 more"), last ?? "(nothing reported)"));

            r.Add(("Hooks: blank deny reason safe", KmhHookVerdict.Deny("  ").Reason.Length > 0, ""));

            return r;
        }
    }
}
