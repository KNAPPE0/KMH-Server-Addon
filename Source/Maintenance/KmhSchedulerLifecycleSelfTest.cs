using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KMHServerAddon.Maintenance
{
    // Each check runs on its own fast loop, never the server's.
    internal static class KmhSchedulerLifecycleSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool, string)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw {ex.GetType().Name}: {ex.Message}")); }
            }

            Check("scheduler: stopping and starting fast never runs a job twice at once", () =>
            {
                KmhScheduler.Loop loop = KmhScheduler.NewLoopForTest(TimeSpan.FromMilliseconds(2));
                int running = 0, peak = 0, runs = 0;
                loop.Register("overlap-probe", TimeSpan.FromTicks(1), () =>
                {
                    int now = Interlocked.Increment(ref running);
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                    Thread.Sleep(40);
                    Interlocked.Decrement(ref running);
                    Interlocked.Increment(ref runs);
                }, KmhJobKind.Infrastructure, TimeSpan.Zero);
                // Restart mid-way through the 40 ms job (Windows timer ticks ~16 ms); a non-waiting loop would run it twice.
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(20 + i % 3 * 5);
                    loop.Stop();
                    loop.Start();
                }
                Thread.Sleep(50);
                bool drained = loop.StopAndDrain(TimeSpan.FromSeconds(5), park: true);
                bool ok = drained && peak == 1 && runs > 0;
                return (ok, $"{runs} run(s), at most {peak} at once, drained={drained}");
            });

            Check("scheduler: a drain waits for the job about to start, and nothing runs after it", () =>
            {
                KmhScheduler.Loop loop = KmhScheduler.NewLoopForTest(TimeSpan.FromMilliseconds(2));
                long finished = 0, startedAfter = 0, drainedAt = 0;
                int fired = 0;
                // The drain lands between the loop's cancellation check and the job, where a job could start after the flush.
                Task<bool> drain = null;
                loop.BeforeJobForTest = () =>
                {
                    if (Interlocked.Exchange(ref fired, 1) != 0) return;
                    drain = Task.Run(() =>
                    {
                        bool ok = loop.StopAndDrain(TimeSpan.FromSeconds(5), park: true);
                        Interlocked.Exchange(ref drainedAt, Stopwatch.GetTimestamp());
                        return ok;
                    });
                    Thread.Sleep(100);
                };
                loop.Register("drain-probe", TimeSpan.FromTicks(1), () =>
                {
                    if (Volatile.Read(ref drainedAt) != 0) Interlocked.Increment(ref startedAfter);
                    Thread.Sleep(50);
                    Interlocked.Exchange(ref finished, Stopwatch.GetTimestamp());
                }, KmhJobKind.Infrastructure, TimeSpan.Zero);
                Stopwatch wait = Stopwatch.StartNew();
                while (drain == null && wait.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
                if (drain == null) return (false, "the loop never reached a job");
                bool drained = drain.Wait(TimeSpan.FromSeconds(10)) && drain.Result;
                Thread.Sleep(50);
                bool ok = drained && startedAfter == 0 && finished != 0 && finished <= Volatile.Read(ref drainedAt);
                return (ok, $"drained={drained}, job finished before the drain returned: {finished != 0 && finished <= drainedAt}, started after: {startedAfter}");
            });

            Check("scheduler: a drained loop stays parked when a job is registered late, and restarts only when released", () =>
            {
                KmhScheduler.Loop loop = KmhScheduler.NewLoopForTest(TimeSpan.FromMilliseconds(2));
                loop.Register("idle", TimeSpan.FromHours(1), () => { }, KmhJobKind.Infrastructure);
                bool drained = loop.StopAndDrain(TimeSpan.FromSeconds(5), park: true);
                int late = 0;
                loop.Register("late", TimeSpan.FromTicks(1), () => Interlocked.Increment(ref late), KmhJobKind.Infrastructure, TimeSpan.Zero);
                Thread.Sleep(60);
                bool stayedParked = late == 0 && loop.Parked && !loop.Start();
                loop.Unpark();
                bool restarted = loop.Start();
                Stopwatch wait = Stopwatch.StartNew();
                while (Volatile.Read(ref late) == 0 && wait.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
                loop.StopAndDrain(TimeSpan.FromSeconds(5), park: true);
                bool ok = drained && stayedParked && restarted && late > 0;
                return (ok, $"drained={drained}, parked held={stayedParked}, restarted={restarted}, late runs after release={late}");
            });

            return r;
        }
    }
}
