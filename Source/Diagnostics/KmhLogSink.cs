using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Diagnostics
{
    // Where a line is worth reading - a separate question from how important it is.
    internal enum KmhLogTo { Console = 1, File = 2, Both = 3 }

    // Economy threads only ever enqueue - a slow disk must not hold up a settlement - and the queue is bounded.
    internal static class KmhLogSink
    {
        private const int  MaxQueued      = 4096;
        private const long MaxFileBytes   = 8L * 1024 * 1024;
        private const int  MaxFiles       = 12;
        private const int  RetentionDays  = 14;
        private const int  DrainBatch     = 256;

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly object _fileLock = new object();
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private static Thread _drain;
        private static volatile bool _stopping;
        private static volatile bool _started;
        private static string _path;
        private static long _bytes;
        private static int _rollover;
        private static int _dropped;
        private static int _queued;
        private static bool _sinkBroken;
        private static long _lastBreakReportTicks;

        internal static Func<string> FailWriteForTest;

        public static bool Started => _started;
        public static string CurrentFile { get { lock (_fileLock) return _path ?? ""; } }
        public static int QueueDepth => Volatile.Read(ref _queued);
        public static int DroppedCount => Volatile.Read(ref _dropped);

        public static void Start()
        {
            lock (_fileLock)
            {
                if (_started) return;
                _started = true;
                _stopping = false;
            }
            try { OpenFile(); } catch (Exception ex) { BreakOnce(ex.Message); }
            _drain = new Thread(DrainLoop) { IsBackground = true, Name = "kmh-log-sink" };
            _drain.Start();
        }

        // Every diagnostic line arrives here from whatever thread produced it and returns immediately.
        public static void Write(string line)
        {
            if (!_started || _stopping || string.IsNullOrEmpty(line)) return;
            if (Volatile.Read(ref _queued) >= MaxQueued)
            {
                // Dropping a routine line beats stalling the thread that was settling an auction.
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queue.Enqueue(line);
            Interlocked.Increment(ref _queued);
            try { _wake.Set(); } catch { /* stopping */ }
        }

        private static void DrainLoop()
        {
            while (true)
            {
                bool stopping = _stopping;
                try { _wake.WaitOne(250); } catch { return; }
                if (PauseDrainForTest && !stopping) continue;
                // Until empty, not one batch per wake: a burst would otherwise trickle out at one batch per tick.
                while (!_queue.IsEmpty) DrainOnce();
                DrainOnce();   // once more, so a pending drop summary is written even with nothing queued
                if (stopping && _queue.IsEmpty) return;
            }
        }

        internal static volatile bool PauseDrainForTest;

        private static void DrainOnce()
        {
            if (PauseDrainForTest) return;
            StringBuilder sb = null;
            int taken = 0;
            while (taken < DrainBatch && _queue.TryDequeue(out string line))
            {
                Interlocked.Decrement(ref _queued);
                (sb ?? (sb = new StringBuilder())).AppendLine(line);
                taken++;
            }

            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                (sb ?? (sb = new StringBuilder())).AppendLine(
                    $"[{DateTime.UtcNow:O}] [sink] suppressed {dropped} diagnostic line(s) - the queue was full");

            if (sb == null || sb.Length == 0) return;
            try
            {
                string text = sb.ToString();
                lock (_fileLock)
                {
                    string injected = FailWriteForTest?.Invoke();
                    if (injected != null) throw new IOException(injected);
                    if (_path == null) OpenFile();
                    RollIfNeeded();
                    File.AppendAllText(_path, text);
                    // Bytes, not chars: the file is UTF-8, so a char count would roll late on non-ASCII lines.
                    _bytes += Encoding.UTF8.GetByteCount(text);
                    _sinkBroken = false;
                }
            }
            catch (Exception ex) { BreakOnce(ex.Message); }
        }

        // Never through ServerLog: a sink failure reported through the logger that failed is a loop.
        private static void BreakOnce(string why)
        {
            long now = DateTime.UtcNow.Ticks;
            bool say;
            lock (_fileLock)
            {
                say = !_sinkBroken || now - _lastBreakReportTicks > TimeSpan.TicksPerMinute * 5;
                _sinkBroken = true;
                if (say) _lastBreakReportTicks = now;
            }
            if (!say) return;
            try { Console.Error.WriteLine($"{Constants.LogPrefix} diagnostic file sink unavailable ({why}) - console logging continues."); }
            catch { }
        }

        private static void OpenFile()
        {
            string dir = KmhDataPaths.DebugDir;
            Directory.CreateDirectory(dir);
            Prune(dir);
            string name = $"KMH-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}"
                        + (_rollover > 0 ? $"-{_rollover:00}" : "") + ".log";
            _path  = Path.Combine(dir, name);
            _bytes = 0;
            File.AppendAllText(_path, Header());
        }

        private static string Header()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== KMH server diagnostic log ===");
            sb.AppendLine($"Build        : {KmhVersion.Build} ({KmhVersion.BuildTag})");
            sb.AppendLine($"Protocol     : v{KmhVersion.Protocol}");
            sb.AppendLine($"ConfigSchema : {KmhVersion.ConfigSchema}   DataSchema: {KmhVersion.DataSchema}");
            sb.AppendLine($"StartedUtc   : {DateTime.UtcNow:O}");
            sb.AppendLine("=================================");
            return sb.ToString();
        }

        private static void RollIfNeeded()
        {
            if (_bytes < MaxFileBytes) return;
            _rollover++;
            OpenFile();
        }

        // Cleanup must never stop KMH, so every failure here is swallowed deliberately.
        private static void Prune(string dir)
        {
            try
            {
                var files = new List<FileInfo>();
                foreach (string f in Directory.GetFiles(dir, "KMH-*.log")) files.Add(new FileInfo(f));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                for (int i = 0; i < files.Count; i++)
                {
                    // The active file is index 0 only after it exists; never delete what is being written now.
                    if (string.Equals(files[i].FullName, _path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (i < MaxFiles && files[i].LastWriteTimeUtc >= cutoff) continue;
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        // Bounded: a logger that cannot finish must not hold the process open.
        public static void Stop(TimeSpan timeout)
        {
            if (!_started) return;
            _stopping = true;
            try { _wake.Set(); } catch { }
            Thread t = _drain;
            if (t != null && t.IsAlive) { try { t.Join(timeout); } catch { } }
            DrainUntilEmpty(timeout);   // whatever the drain thread did not reach
            _started = false;
        }

        internal static void FlushForTest() => DrainUntilEmpty(TimeSpan.FromSeconds(5));

        // Bounded: a sink that cannot finish must not hold the caller, least of all the shutdown path.
        private static void DrainUntilEmpty(TimeSpan budget)
        {
            long deadline = DateTime.UtcNow.Ticks + budget.Ticks;
            do { DrainOnce(); } while (!_queue.IsEmpty && DateTime.UtcNow.Ticks < deadline);
        }

        internal static void ResetForTest()
        {
            _stopping = true;
            try { _wake.Set(); } catch { }
            Thread t = _drain;
            if (t != null && t.IsAlive) { try { t.Join(TimeSpan.FromSeconds(2)); } catch { } }
            while (_queue.TryDequeue(out _)) { }
            Volatile.Write(ref _queued, 0);
            Volatile.Write(ref _dropped, 0);
            lock (_fileLock) { _path = null; _bytes = 0; _rollover = 0; _sinkBroken = false; }
            _started = false;
            _stopping = false;
            _drain = null;
        }
    }
}
