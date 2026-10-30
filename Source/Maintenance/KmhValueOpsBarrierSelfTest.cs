using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Each case holds a move open on its own thread on purpose: barriers must wait for every ingress, never hang.
    internal static class KmhValueOpsBarrierSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            (Thread worker, ManualResetEventSlim release) Hold(KmhIngress from)
            {
                var started = new ManualResetEventSlim(false);
                var release = new ManualResetEventSlim(false);
                var t = new Thread(() =>
                {
                    using (KmhValueOps.Scope op = KmhValueOps.Begin(from, out _)) { started.Set(); release.Wait(10000); }
                }) { IsBackground = true };
                t.Start();
                started.Wait(5000);
                return (t, release);
            }

            Check("Barrier: moves from Discord, the scheduler, chat and extensions are counted, and a closed gate refuses the next", () =>
            {
                var held = new List<(Thread, ManualResetEventSlim)>();
                foreach (KmhIngress from in new[] { KmhIngress.Discord, KmhIngress.Scheduler, KmhIngress.Sdk, KmhIngress.ClientChat })
                    held.Add(Hold(from));
                int counted = KmhValueOps.InFlight;
                bool refused;
                KmhMaintenanceGate.Enter(KmhMaintenanceReason.Backup);
                try
                {
                    using (KmhValueOps.Scope late = KmhValueOps.Begin(KmhIngress.Discord, out string why))
                        refused = late == null && !string.IsNullOrEmpty(why);
                }
                finally { KmhMaintenanceGate.Release(); }
                foreach ((Thread t, ManualResetEventSlim rel) in held) { rel.Set(); t.Join(5000); }
                bool drained = KmhValueOps.WaitForIdle(TimeSpan.FromSeconds(2));
                bool ok = counted >= 4 && refused && drained;
                return (ok, $"counted={counted} (4 held), refusedWhileClosed={refused}, drainedAfter={drained}");
            });

            Check("Barrier: a backup waits for a move already running, then copies", () =>
            {
                (Thread t, ManualResetEventSlim release) = Hold(KmhIngress.Discord);
                string dir = null, err = null;
                bool made = false;
                var backup = new Thread(() => made = KmhDataBackup.TryCreate("selftest-barrier", out dir, out err)) { IsBackground = true };
                try
                {
                    backup.Start();
                    bool waited = !backup.Join(400);
                    // While it waits the gate is closed, so nothing new starts writing into what it is about to copy.
                    bool closed = KmhMaintenanceGate.IsActive;
                    release.Set(); t.Join(5000);
                    bool finished = backup.Join(15000);
                    bool ok = waited && closed && finished && made && dir != null && Directory.Exists(dir);
                    return (ok, ok ? "waited for the purchase, then copied" : $"waited={waited}, gateClosed={closed}, finished={finished}, made={made} ({err})");
                }
                finally
                {
                    release.Set(); t.Join(5000); backup.Join(15000);
                    if (dir != null) try { Directory.Delete(dir, recursive: true); } catch { }
                }
            });

            Check("Barrier: a backup refuses rather than copy while a move outlasts its wait, and reopens the gate", () =>
            {
                TimeSpan had = KmhDataBackup.QuiesceWait;
                (Thread t, ManualResetEventSlim release) = Hold(KmhIngress.Sdk);
                bool made;
                string dir = null, err;
                try
                {
                    KmhDataBackup.QuiesceWait = TimeSpan.FromMilliseconds(200);
                    made = KmhDataBackup.TryCreate("selftest-barrier", out dir, out err);
                }
                finally
                {
                    KmhDataBackup.QuiesceWait = had;
                    release.Set(); t.Join(5000);
                    if (dir != null) try { Directory.Delete(dir, recursive: true); } catch { }
                }
                bool reopened = !KmhMaintenanceGate.IsActive;
                bool ok = !made && dir == null && err != null && err.Contains("still running") && reopened;
                return (ok, ok ? "refused, nothing copied, gate open again" : $"made={made}, error={err ?? "none"}, gateReopened={reopened}");
            });

            Check("Barrier: an economy reset purges nothing while a move runs, and finishes on its retry", () =>
            {
                string who = "selftest_barrier_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                TimeSpan had = Features.Economy.KmhEconomyReset.QuiesceWait;
                Features.Economy.KmhEconomyReset.ResetForTest();
                Features.Economy.EconomyResetStore.Forget(who);
                (Thread t, ManualResetEventSlim release) = (null, null);
                try
                {
                    if (!TreasuryStore.DepositSilver(who, 500, "selftest seed")) return (false, "seed");
                    (t, release) = Hold(KmhIngress.Scheduler);
                    Features.Economy.KmhEconomyReset.QuiesceWait = TimeSpan.FromMilliseconds(200);
                    if (!Features.Economy.KmhEconomyReset.Begin(who, "", Features.Economy.KmhResetScope.Player, out string why))
                        return (false, "begin: " + why);
                    Features.Economy.KmhEconomyReset.Run(who, "");
                    long during = TreasuryStore.GetPersonalSilver(who);
                    bool stalled = Features.Economy.KmhEconomyReset.StalledForTest && Features.Economy.KmhEconomyReset.InProgress;
                    release.Set(); t.Join(5000);
                    bool resumed = Features.Economy.KmhEconomyReset.ResumeStalled();
                    long after = TreasuryStore.GetPersonalSilver(who);
                    bool done = !Features.Economy.KmhEconomyReset.InProgress;
                    bool ok = during == 500 && stalled && resumed && after == 0 && done;
                    return (ok, ok ? "held off while the move ran, purged on the retry"
                                  : $"silverDuring={during}, stalled={stalled}, resumed={resumed}, silverAfter={after}, finished={done}"
                                    + (during != 500 ? " - PURGED UNDER A RUNNING MOVE" : ""));
                }
                finally
                {
                    release?.Set(); t?.Join(5000);
                    Features.Economy.KmhEconomyReset.QuiesceWait = had;
                    TreasuryStore.PurgeOwnerForTest(who);
                    Features.Economy.EconomyResetStore.Forget(who);
                    Features.Economy.KmhEconomyReset.ResetForTest();
                }
            });

            Check("Barrier: a mutating scheduler job is counted for its whole run", () =>
            {
                KmhScheduler.Loop loop = KmhScheduler.NewLoopForTest(TimeSpan.FromMilliseconds(20));
                var inJob = new ManualResetEventSlim(false);
                var release = new ManualResetEventSlim(false);
                int runs = 0;
                loop.Register("selftest-barrier-job", TimeSpan.FromHours(1),
                              () => { if (Interlocked.Increment(ref runs) == 1) { inJob.Set(); release.Wait(5000); } },
                              KmhJobKind.Mutating, TimeSpan.Zero);
                try
                {
                    loop.Start();
                    bool ran = inJob.Wait(5000);
                    bool waits = !KmhValueOps.WaitForIdle(TimeSpan.FromMilliseconds(200));
                    release.Set();
                    bool idle = KmhValueOps.WaitForIdle(TimeSpan.FromSeconds(2));
                    bool ok = ran && waits && idle;
                    return (ok, $"ran={ran}, barrierWaitedForIt={waits}, idleAfter={idle}");
                }
                finally { release.Set(); loop.StopAndDrain(TimeSpan.FromSeconds(2), park: false); }
            });

            Check("Barrier: an admin value command is counted while it runs, and a read-only one is not", () =>
            {
                int baseline = KmhValueOps.InFlight;
                int duringValue = -1, duringRead = -1;
                AdminCommands.KmhServerCommands.Dispatch(new[] { "cancel" }, true, "selftest", _ => duringValue = KmhValueOps.InFlight);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "orphans" }, true, "selftest", _ => { if (duringRead < 0) duringRead = KmhValueOps.InFlight; });
                bool ok = duringValue == baseline + 1 && duringRead == baseline && KmhValueOps.InFlight == baseline;
                return (ok, $"baseline={baseline} duringCancel={duringValue} duringListing={duringRead} after={KmhValueOps.InFlight}");
            });

            return r;
        }
    }
}
