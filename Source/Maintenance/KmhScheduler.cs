using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // A job that throws is logged and skipped, so it can never stop the loop or starve another job.
    internal static class KmhScheduler
    {
        // Resolution, not a job interval. Jobs fire on the first pass at or after they come due.
        private static readonly TimeSpan Resolution = TimeSpan.FromSeconds(15);

        internal sealed class Job
        {
            public string   Name;
            public TimeSpan Interval;
            public Action   Tick;
            public long     NextDueUtcTicks;
            public long     LastRunUtcTicks;
            public int      Runs;
            public int      Failures;
            public string   LastError = "";
        }

        private static readonly object _lock = new object();
        private static readonly List<Job> _jobs = new List<Job>();
        private static CancellationTokenSource _cts;

        // Idempotent by name, so re-registering replaces a job rather than doubling it.
        public static void Register(string name, TimeSpan interval, Action tick, TimeSpan? firstDelay = null)
        {
            if (string.IsNullOrEmpty(name) || tick == null || interval <= TimeSpan.Zero) return;
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                _jobs.RemoveAll(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
                _jobs.Add(new Job
                {
                    Name = name,
                    Interval = interval,
                    Tick = tick,
                    NextDueUtcTicks = now + (firstDelay ?? interval).Ticks,
                });
            }
            Start();
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
            }
            Task.Run(() => RunLoop(_cts.Token));
            ServerLog.Verbose($"Scheduler started (resolution {Resolution.TotalSeconds:F0}s)");
        }

        public static void Stop()
        {
            lock (_lock) { _cts?.Cancel(); _cts = null; }
        }

        private static int _busy;

        public static bool IsBusy => System.Threading.Volatile.Read(ref _busy) > 0;

        // Cancelling only stops the next job; shutdown must wait, or the final flush misses what the running one mutates after it.
        public static bool StopAndDrain(TimeSpan timeout)
        {
            Stop();
            long deadline = DateTime.UtcNow.Ticks + timeout.Ticks;
            while (IsBusy && DateTime.UtcNow.Ticks < deadline) System.Threading.Thread.Sleep(25);
            return !IsBusy;
        }

        // Pure: is this job due? Split out so the scheduling rule is testable without waiting on wall-clock time.
        internal static bool IsDue(Job job, long nowTicks) => job != null && nowTicks >= job.NextDueUtcTicks;

        // Anchored to now, so an overrun or a suspended server does not come back owing a burst of catch-up runs.
        internal static long NextDue(Job job, long nowTicks)
            => job == null ? nowTicks : nowTicks + job.Interval.Ticks;

        private static async Task RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(Resolution, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }

                long now = DateTime.UtcNow.Ticks;
                List<Job> due = new List<Job>();
                lock (_lock)
                    foreach (Job j in _jobs) if (IsDue(j, now)) due.Add(j);

                foreach (Job j in due)
                {
                    if (ct.IsCancellationRequested) return;
                    System.Threading.Interlocked.Increment(ref _busy);
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
                    finally { System.Threading.Interlocked.Decrement(ref _busy); }
                    j.LastRunUtcTicks = DateTime.UtcNow.Ticks;
                    j.NextDueUtcTicks = NextDue(j, j.LastRunUtcTicks);
                }
            }
        }

        // For 'kmh status': what runs, how often, and whether it is failing.
        public static List<(string Name, int Runs, int Failures, string LastError, int SecondsSinceRun)> Describe()
        {
            var outp = new List<(string, int, int, string, int)>();
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
                foreach (Job j in _jobs)
                    outp.Add((j.Name, j.Runs, j.Failures, j.LastError,
                              j.LastRunUtcTicks == 0 ? -1 : (int)((now - j.LastRunUtcTicks) / TimeSpan.TicksPerSecond)));
            return outp;
        }
    }
}
