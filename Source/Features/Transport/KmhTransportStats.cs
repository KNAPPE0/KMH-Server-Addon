using System.Collections.Generic;
using System.Threading;

namespace KMHServerAddon.Features.Transport
{
    // Interlocked rather than locked: touched on every frame in both directions.
    internal static class KmhTransportStats
    {
        private static long _framesIn, _bytesIn, _framesOut, _bytesOut, _rawOut, _compressedOut, _writes;
        private static long _queuePeakBytes, _slowPeerDrops, _writeFailures, _connections;

        public static void FrameIn(int wireBytes)
        {
            Interlocked.Increment(ref _framesIn);
            Interlocked.Add(ref _bytesIn, wireBytes);
        }

        // One write may carry several frames; rawBytes is their uncompressed cost.
        public static void Written(int frames, long wireBytes, long rawBytes, int compressedFrames)
        {
            Interlocked.Add(ref _framesOut, frames);
            Interlocked.Add(ref _bytesOut, wireBytes);
            Interlocked.Add(ref _rawOut, rawBytes);
            if (compressedFrames > 0) Interlocked.Add(ref _compressedOut, compressedFrames);
            Interlocked.Increment(ref _writes);
        }

        public static void QueueDepth(long bytes)
        {
            long seen = Interlocked.Read(ref _queuePeakBytes);
            while (bytes > seen)
            {
                long was = Interlocked.CompareExchange(ref _queuePeakBytes, bytes, seen);
                if (was == seen) break;
                seen = was;
            }
        }

        public static void SlowPeerDropped() => Interlocked.Increment(ref _slowPeerDrops);
        public static void WriteFailed() => Interlocked.Increment(ref _writeFailures);
        public static void Connected() => Interlocked.Increment(ref _connections);

        public static List<KeyValuePair<string, long>> Snapshot() => new List<KeyValuePair<string, long>>
        {
            new KeyValuePair<string, long>("frames_in", Interlocked.Read(ref _framesIn)),
            new KeyValuePair<string, long>("bytes_in", Interlocked.Read(ref _bytesIn)),
            new KeyValuePair<string, long>("frames_out", Interlocked.Read(ref _framesOut)),
            new KeyValuePair<string, long>("bytes_out", Interlocked.Read(ref _bytesOut)),
            new KeyValuePair<string, long>("bytes_out_uncompressed", Interlocked.Read(ref _rawOut)),
            new KeyValuePair<string, long>("frames_compressed", Interlocked.Read(ref _compressedOut)),
            new KeyValuePair<string, long>("socket_writes", Interlocked.Read(ref _writes)),
            new KeyValuePair<string, long>("send_queue_peak_bytes", Interlocked.Read(ref _queuePeakBytes)),
            new KeyValuePair<string, long>("slow_peers_dropped", Interlocked.Read(ref _slowPeerDrops)),
            new KeyValuePair<string, long>("write_failures", Interlocked.Read(ref _writeFailures)),
            new KeyValuePair<string, long>("connections_accepted", Interlocked.Read(ref _connections)),
        };

        internal static void ResetForTest()
        {
            _framesIn = _bytesIn = _framesOut = _bytesOut = _rawOut = _compressedOut = _writes = 0;
            _queuePeakBytes = _slowPeerDrops = _writeFailures = _connections = 0;
        }
    }
}
