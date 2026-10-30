using System;
using System.Threading;

namespace KMHServerAddon.Maintenance
{
    // A reset, backup or shutdown closes the gate then waits for these, so no move from any ingress lands inside it.
    internal static class KmhValueOps
    {
        private static int _inFlight;
        [ThreadStatic] private static int _mine;

        public static int InFlight => Volatile.Read(ref _inFlight);

        // Counted before admission is read, so a barrier that just closed the gate waits for this move or refuses it.
        public static Scope Begin(KmhIngress from, out string refusal)
        {
            Enter();
            if (KmhAdmission.AllowsValueMutation(from, out refusal)) return new Scope();
            Leave();
            return null;
        }

        public static Scope BeginFeature(KmhIngress from, string feature, out string refusal)
        {
            Enter();
            if (KmhAdmission.AllowsPlayerFeature(from, feature, out refusal)) return new Scope();
            Leave();
            return null;
        }

        internal static void Enter() { Interlocked.Increment(ref _inFlight); _mine++; }
        internal static void Leave() { _mine--; Interlocked.Decrement(ref _inFlight); }

        // Bounded so a stuck move cannot hold a barrier; the caller's own moves are skipped or it would wait on itself.
        public static bool WaitForIdle(TimeSpan max)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref _inFlight) - _mine > 0)
            {
                if (sw.Elapsed >= max) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        // Null when admitted moves outlast max: the gate reopens and the caller must not go on over a live write.
        public static Barrier Quiesce(KmhMaintenanceReason reason, TimeSpan max, out string refusal)
        {
            refusal = null;
            KmhMaintenanceGate.Enter(reason);
            if (WaitForIdle(max)) return new Barrier();
            KmhMaintenanceGate.Release();
            refusal = $"{InFlight - _mine} value move(s) were still running after {max.TotalSeconds:0}s";
            return null;
        }

        // Disposed on the thread that began it: every scope wraps a synchronous move, never an await.
        internal sealed class Scope : IDisposable
        {
            private int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Leave(); }
        }

        internal sealed class Barrier : IDisposable
        {
            private int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) KmhMaintenanceGate.Release(); }
        }
    }
}
