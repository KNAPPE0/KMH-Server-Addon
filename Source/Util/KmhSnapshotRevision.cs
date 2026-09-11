using System.Threading;

namespace KMHServerAddon.Util
{
    // Request replies and mutation pushes race across two transports; without this the client applies whichever lands last.
    internal static class KmhSnapshotRevision
    {
        private static long _next;

        // Take this INSIDE the lock the store reads its state under, or the number stops tracking build order.
        public static long Next() => Interlocked.Increment(ref _next);
    }
}
