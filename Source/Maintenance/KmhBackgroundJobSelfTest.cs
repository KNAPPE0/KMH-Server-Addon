using System;
using System.Collections.Generic;
using System.Reflection;
using KMHServerAddon.Features.Economy;

namespace KMHServerAddon.Maintenance
{
    // A value-moving job misfiled as Infrastructure runs mid-restore and can move silver under the restore.
    internal static class KmhBackgroundJobSelfTest
    {
        // Named here so a new job cannot join the Mutating side without someone reading this list.
        private static readonly string[] MutatingJobs = { "world-engine", "frontier-director" };

        private static readonly (string Name, string Why)[] InfrastructureJobs =
        {
            ("persistence-retry",  "retrying an unwritable store is how a persistence freeze ENDS"),
            ("expiry-sweep",       "its snapshot/prune/export duties must survive a freeze; the value half asks admission itself"),
            ("chat-save",          "saving what is already in memory moves no value"),
            ("playerstats-save",   "writes display stats already in memory; moves no value"),
            ("video-cache-sweep",  "a file cache, not authoritative state"),
            ("history-prune",      "trims history rows, moves no value"),
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Type jobType = typeof(KmhScheduler).GetNestedType("Job", BindingFlags.NonPublic);
            MethodInfo mayRun = typeof(KmhScheduler).GetMethod("MayRun",
                BindingFlags.NonPublic | BindingFlags.Static);

            object NewJob(string name, KmhJobKind kind)
            {
                object j = Activator.CreateInstance(jobType);
                jobType.GetField("Name").SetValue(j, name);
                jobType.GetField("Kind").SetValue(j, kind);
                return j;
            }
            bool Allows(object job)
            {
                object[] args = { job, null };
                return (bool)mayRun.Invoke(null, args);
            }

            Check("Jobs: the scheduler itself refuses a value-moving job during maintenance", () =>
            {
                if (jobType == null || mayRun == null) return (false, "KmhScheduler.Job/MayRun not found");
                object mut = NewJob("probe-mutating", KmhJobKind.Mutating);
                object inf = NewJob("probe-infra",    KmhJobKind.Infrastructure);

                bool mutOpen = Allows(mut), infOpen = Allows(inf);
                KmhMaintenanceGate.Enter(KmhMaintenanceReason.Restore);
                bool mutBarred, infStillRuns;
                try { mutBarred = !Allows(mut); infStillRuns = Allows(inf); }
                finally { KmhMaintenanceGate.Release(); }
                bool mutOpenAgain = Allows(mut);

                bool ok = mutOpen && infOpen && mutBarred && infStillRuns && mutOpenAgain;
                return (ok, ok ? "mutating jobs wait for the gate; infrastructure jobs never do"
                              : $"openBefore={mutOpen}, infra={infOpen}, barred={mutBarred}, "
                                + $"infraDuring={infStillRuns}, openAfter={mutOpenAgain}");
            });

            Check("Jobs: a persistence freeze stops value-moving jobs too", () =>
            {
                if (jobType == null || mayRun == null) return (false, "KmhScheduler.Job/MayRun not found");
                object mut = NewJob("probe-mutating", KmhJobKind.Mutating);
                object inf = NewJob("probe-infra",    KmhJobKind.Infrastructure);
                bool before = Allows(mut);
                KmhMaintenanceGate.SetPersistenceDegraded(true);
                bool barred, retryStillRuns;
                try { barred = !Allows(mut); retryStillRuns = Allows(inf); }
                finally { KmhMaintenanceGate.SetPersistenceDegraded(false); }
                bool ok = before && barred && retryStillRuns;
                return (ok, ok ? "a degraded disk stops value moves but not the retry that clears it"
                              : $"before={before}, barredWhileDegraded={barred}, retryRan={retryStillRuns}");
            });

            Check("Jobs: every registered job declares which side it is on", () =>
            {
                // The compiler already forces the argument; this catches a job registered under a name nobody classified.
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in MutatingJobs) known.Add(s);
                foreach ((string n, string _) in InfrastructureJobs) known.Add(n);

                var unlisted = new List<string>();
                foreach ((string name, int _, int __, string ___, int ____) in KmhScheduler.Describe())
                    if (!known.Contains(name)) unlisted.Add(name);

                return (unlisted.Count == 0,
                        unlisted.Count == 0 ? $"{known.Count} job(s) classified, none unlisted"
                                            : "not classified: " + string.Join(", ", unlisted));
            });

            Check("Jobs: the world engine and the frontier director are on the value-moving side", () =>
            {
                // Both move authoritative state (quest reserves, outposts), so neither may run while the economy is held.
                bool engine = Array.IndexOf(MutatingJobs, "world-engine") >= 0;
                bool director = Array.IndexOf(MutatingJobs, "frontier-director") >= 0;
                List<string> engineCalls = KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Features.World.WorldEngine", "Start", "Register");
                bool registered = engineCalls.Count > 0;
                bool ok = engine && director && registered;
                return (ok, ok ? "both declared Mutating at their registration site"
                              : $"engineListed={engine}, directorListed={director}, engineRegisters={registered}");
            });

            Check("Jobs: a value-moving job deferred by the gate is rescheduled, not dropped", () =>
            {
                if (jobType == null) return (false, "KmhScheduler.Job not found");
                MethodInfo nextDue = typeof(KmhScheduler).GetMethod("NextDue",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo isDue = typeof(KmhScheduler).GetMethod("IsDue",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (nextDue == null || isDue == null) return (false, "NextDue/IsDue not found");

                object j = NewJob("probe-mutating", KmhJobKind.Mutating);
                jobType.GetField("Interval").SetValue(j, TimeSpan.FromMinutes(1));
                long now = DateTime.UtcNow.Ticks;
                jobType.GetField("NextDueUtcTicks").SetValue(j, now - TimeSpan.FromMinutes(5).Ticks);

                bool dueBefore = (bool)isDue.Invoke(null, new object[] { j, now });
                long after = (long)nextDue.Invoke(null, new object[] { j, now });
                jobType.GetField("NextDueUtcTicks").SetValue(j, after);
                bool notDueNow = !(bool)isDue.Invoke(null, new object[] { j, now });
                bool dueLater = (bool)isDue.Invoke(null,
                    new object[] { j, now + TimeSpan.FromMinutes(2).Ticks });

                bool ok = dueBefore && notDueNow && dueLater;
                return (ok, ok ? "a deferred job comes due again one interval later, and never owes a catch-up burst"
                              : $"dueBefore={dueBefore}, notDueNow={notDueNow}, dueLater={dueLater}");
            });

            Check("Jobs: an abandoned publish transaction is swept without waiting for the next one", () =>
            {
                // Tick() only forwards to Tick(long); the steps are wired in the overload that takes the clock.
                List<string> steps = KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Maintenance.ExpirySweeper", "Tick", 1, "Purge");
                bool swept = steps.Contains("PurgeExpired");
                return (swept, swept ? "the expiry sweep drops expired publish staging every pass"
                                     : "nothing sweeps an abandoned publish transaction between publishes");
            });

            Check("Jobs: a season wipe closes the window for every value-moving job at once", () =>
            {
                if (jobType == null || mayRun == null) return (false, "KmhScheduler.Job/MayRun not found");
                object mut = NewJob("probe-mutating", KmhJobKind.Mutating);
                KmhEconomyReset.ResetForTest();
                bool openBefore = Allows(mut);

                // The same gate the season reset holds across its destructive window.
                KmhMaintenanceGate.Enter(KmhMaintenanceReason.OwnerMaintenance);
                bool barred;
                try { barred = !Allows(mut); }
                finally { KmhMaintenanceGate.Release(); }

                KmhEconomyReset.ResetForTest();
                bool ok = openBefore && barred;
                return (ok, ok ? "expiry, auctions, wants, mail, sites, world and frontier all wait out the wipe"
                              : $"openBefore={openBefore}, barredDuringWipe={barred}");
            });

            return r;
        }
    }
}
