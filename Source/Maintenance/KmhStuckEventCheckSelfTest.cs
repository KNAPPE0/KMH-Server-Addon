using System;
using System.Collections.Generic;

namespace KMHServerAddon.Maintenance
{
    // A live run can only ever show one half, since a working sweeper clears the event before the uptime gate opens.
    internal static class KmhStuckEventCheckSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            TimeSpan cycle = KmhSmokeTest.SweepCycle;

            // A short boot or a restart loop has not given the sweep a chance yet.
            r.Add(("Sweep check: silent while too young to answer",
                   !KmhSmokeTest.StuckEventsAnswerable(TimeSpan.Zero)
                   && !KmhSmokeTest.StuckEventsAnswerable(cycle)
                   && !KmhSmokeTest.StuckEventsAnswerable(cycle + TimeSpan.FromSeconds(59)), ""));

            r.Add(("Sweep check: answers once a sweep could have run",
                   KmhSmokeTest.StuckEventsAnswerable(cycle + cycle)
                   && KmhSmokeTest.StuckEventsAnswerable(TimeSpan.FromHours(9)), ""));

            long now = DateTime.UtcNow.Ticks;

            // The uptime gate must not have neutered the check it guards.
            r.Add(("Sweep check: still catches a stuck event",
                   KmhSmokeTest.CountOverdue(new[] { now - TimeSpan.FromMinutes(30).Ticks }, now) == 1, ""));
            r.Add(("Sweep check: counts every stuck event",
                   KmhSmokeTest.CountOverdue(
                       new[] { now - TimeSpan.FromMinutes(30).Ticks, now - TimeSpan.FromHours(4).Ticks }, now) == 2, ""));

            r.Add(("Sweep check: live and just-expired are not stuck",
                   KmhSmokeTest.CountOverdue(
                       new[] { now + TimeSpan.FromMinutes(10).Ticks, now - TimeSpan.FromSeconds(30).Ticks }, now) == 0, ""));

            r.Add(("Sweep check: empty and null are clean",
                   KmhSmokeTest.CountOverdue(new long[0], now) == 0
                   && KmhSmokeTest.CountOverdue(null, now) == 0, ""));

            return r;
        }
    }
}
