using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Declared per job, not checked inside each: jobs that forgot to ask admission moved state through a restore.
    internal enum KmhJobKind
    {
        // Moves value or authoritative world state - skipped while maintenance holds the economy.
        Mutating,
        // Saves, prunes, exports and the persistence retry - these are how maintenance ENDS, so they always run.
        Infrastructure,
    }

    // A job that throws is logged and skipped, so it can never stop the loop or starve another job.
    internal static class KmhScheduler
    {
        // Resolution, not a job interval. Jobs fire on the first pass at or after they come due.
        private static readonly TimeSpan Resolution = TimeSpan.FromSeconds(15);

        internal sealed class Job
        {
            public string     Name;
            public TimeSpan   Interval;
            public Action     Tick;
            public KmhJobKind Kind;
            public int        Deferred;
            public long     NextDueUtcTicks;
            public long     LastRunUtcTicks;
            public int      Runs;
            public int      Failures;
            public string   LastError = "";
        }

        // One loop at a time, ever: a start while the previous loop is still finishing its job waits for it, and a
        // drained loop stays parked so a late Register cannot run jobs behind the final save.
        internal sealed class Loop
        {
            private readonly object _lock = new object();
            private readonly List<Job> _jobs = new List<Job>();
            private readonly TimeSpan _resolution;
            private CancellationTokenSource _cts;
            private Task _task = Task.CompletedTask;
            private bool _parked;
            private int _busy;

            public Loop(TimeSpan resolution) { _resolution = resolution; }

            // Runs between the loop's cancellation check and its job: the window a drain must not slip through.
            internal Action BeforeJobForTest;

            public bool IsBusy => Volatile.Read(ref _busy) > 0;
            public bool Parked { get { lock (_lock) return _parked; } }

            public void Register(string name, TimeSpan interval, Action tick, KmhJobKind kind, TimeSpan? firstDelay = null)
            {
                if (string.IsNullOrEmpty(name) || tick == null || interval <= TimeSpan.Zero) return;
                long now = DateTime.UtcNow.Ticks;
                lock (_lock)
                {
                    _jobs.RemoveAll(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
                    _jobs.Add(new Job { Name = name, Interval = interval, Tick = tick, Kind = kind, NextDueUtcTicks = now + (firstDelay ?? interval).Ticks });
                }
                Start();
            }

            public bool Start()
            {
                lock (_lock)
                {
                    if (_cts != null || _parked) return false;
                    CancellationTokenSource cts = new CancellationTokenSource();
                    _cts = cts;
                    Task previous = _task;
                    _task = Task.Run(async () =>
                    {
                        try { await previous.ConfigureAwait(false); } catch (Exception) { }
                        await RunLoop(cts.Token).ConfigureAwait(false);
                    });
                    return true;
                }
            }

            public void Stop()
            {
                lock (_lock) { _cts?.Cancel(); _cts = null; }
            }

            // Cancelling only stops the next job; the loop is waited for, so the job it was running has finished and
            // no other can start. Parked afterwards: shutdown flushes next, and nothing may run behind that flush.
            public bool StopAndDrain(TimeSpan timeout, bool park)
            {
                Task task;
                lock (_lock)
                {
                    if (park) _parked = true;
                    _cts?.Cancel();
                    _cts = null;
                    task = _task;
                }
                try { return task.Wait(timeout); }
                catch (AggregateException) { return true; }
            }

            public void Unpark() { lock (_lock) _parked = false; }

            public List<Job> Snapshot() { lock (_lock) return new List<Job>(_jobs); }

            private async Task RunLoop(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(_resolution, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    long now = DateTime.UtcNow.Ticks;
                    List<Job> due = new List<Job>();
                    lock (_lock)
                        foreach (Job j in _jobs) if (IsDue(j, now)) due.Add(j);

                    foreach (Job j in due)
                    {
                        if (ct.IsCancellationRequested) return;
                        BeforeJobForTest?.Invoke();
                        // A mutating job is counted as a value move for its whole run, so a reset or backup waits for it.
                        KmhValueOps.Scope op = null;
                        string refusal = null;
                        if (j.Kind == KmhJobKind.Mutating ? (op = KmhValueOps.Begin(KmhIngress.Scheduler, out refusal)) == null
                                                          : !MayRun(j, out refusal))
                        {
                            j.Deferred++;
                            j.NextDueUtcTicks = NextDue(j, DateTime.UtcNow.Ticks);
                            ServerLog.Verbose($"Scheduler job '{j.Name}' deferred - {refusal}");
                            continue;
                        }
                        Interlocked.Increment(ref _busy);
                        try
                        {
                            j.Tick();
                            j.Runs++;
                            j.LastError = "";
                        }
                        catch (Exception ex)
                        {
                            j.Failures++;
                            j.LastError = ex.GetType().Name + ": " + ex.Message;
                            ServerLog.Error($"Scheduler job '{j.Name}' threw", ex);
                        }
                        finally { op?.Dispose(); Interlocked.Decrement(ref _busy); }
                        j.LastRunUtcTicks = DateTime.UtcNow.Ticks;
                        j.NextDueUtcTicks = NextDue(j, j.LastRunUtcTicks);
                    }
                }
            }
        }

        private static readonly Loop Default = new Loop(Resolution);

        internal static Loop NewLoopForTest(TimeSpan resolution) => new Loop(resolution);

        // Idempotent by name, so re-registering replaces a job rather than doubling it.
        public static void Register(string name, TimeSpan interval, Action tick, KmhJobKind kind, TimeSpan? firstDelay = null)
            => Default.Register(name, interval, tick, kind, firstDelay);

        public static void Start()
        {
            if (Default.Start()) ServerLog.Verbose($"Scheduler started (resolution {Resolution.TotalSeconds:F0}s)");
        }

        public static void Stop() => Default.Stop();

        public static bool IsBusy => Default.IsBusy;

        public static bool StopAndDrain(TimeSpan timeout) => Default.StopAndDrain(timeout, park: true);

        internal static void UnparkForTest() => Default.Unpark();

        internal static bool IsDue(Job job, long nowTicks) => job != null && nowTicks >= job.NextDueUtcTicks;

        // Anchored to now, so an overrun or a suspended server does not come back owing a burst of catch-up runs.
        internal static long NextDue(Job job, long nowTicks)
            => job == null ? nowTicks : nowTicks + job.Interval.Ticks;

        // A due timer is not permission to move value: wait for the next pass rather than pay through a restore.
        internal static bool MayRun(Job job, out string refusal)
        {
            refusal = null;
            if (job == null) return false;
            if (job.Kind != KmhJobKind.Mutating) return true;
            return KmhAdmission.AllowsValueMutation(KmhIngress.Scheduler, out refusal);
        }

        public static List<(string Name, int Runs, int Failures, string LastError, int SecondsSinceRun)> Describe()
        {
            var outp = new List<(string, int, int, string, int)>();
            long now = DateTime.UtcNow.Ticks;
            foreach (Job j in Default.Snapshot())
                outp.Add((j.Name, j.Runs, j.Failures, j.LastError,
                          j.LastRunUtcTicks == 0 ? -1 : (int)((now - j.LastRunUtcTicks) / TimeSpan.TicksPerSecond)));
            return outp;
        }
    }
}
