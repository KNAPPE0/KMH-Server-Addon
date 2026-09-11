using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Discord
{
    // One Discord message carries several KMH lines because chat is batched, so a removal edits rather than deletes it.
    internal static class KmhChatRelayIndex
    {
        private const int MaxBatches  = 300;
        private const int MaxInbound  = 2000;

        internal sealed class Batch
        {
            public ulong        DiscordMessageId;
            public List<long>   KmhIds  = new List<long>();
            public List<string> Lines   = new List<string>();
        }

        private static readonly object _lock = new object();
        private static readonly Queue<Batch> _batches = new Queue<Batch>();
        private static readonly Dictionary<ulong, long> _inbound = new Dictionary<ulong, long>();
        private static readonly Queue<ulong> _inboundOrder = new Queue<ulong>();

        public static void Clear()
        {
            lock (_lock) { _batches.Clear(); _inbound.Clear(); _inboundOrder.Clear(); }
        }

        public static void RecordBatch(ulong discordMessageId, List<long> kmhIds, List<string> lines)
        {
            if (discordMessageId == 0 || kmhIds == null || lines == null || kmhIds.Count == 0) return;
            lock (_lock)
            {
                while (_batches.Count >= MaxBatches) _batches.Dequeue();
                _batches.Enqueue(new Batch
                {
                    DiscordMessageId = discordMessageId,
                    KmhIds = new List<long>(kmhIds),
                    Lines  = new List<string>(lines),
                });
            }
        }

        // False also covers a line that has aged out of the index, which is not an error.
        public static bool TryRedactOutbound(long kmhId, string replacement, out ulong discordMessageId, out string newText)
        {
            discordMessageId = 0; newText = null;
            lock (_lock)
            {
                foreach (Batch b in _batches)
                {
                    int i = b.KmhIds.IndexOf(kmhId);
                    if (i < 0 || i >= b.Lines.Count) continue;
                    // Already redacted, so re-editing would churn Discord for no change.
                    if (b.Lines[i] == replacement) return false;
                    b.Lines[i] = replacement;
                    discordMessageId = b.DiscordMessageId;
                    newText = string.Join("\n", b.Lines.ToArray());
                    return true;
                }
            }
            return false;
        }

        public static void RecordInbound(ulong discordMessageId, long kmhId)
        {
            if (discordMessageId == 0 || kmhId <= 0) return;
            lock (_lock)
            {
                while (_inboundOrder.Count >= MaxInbound)
                {
                    ulong oldest = _inboundOrder.Dequeue();
                    _inbound.Remove(oldest);
                }
                if (_inbound.ContainsKey(discordMessageId)) return;   // never queue the same key twice
                _inbound[discordMessageId] = kmhId;
                _inboundOrder.Enqueue(discordMessageId);
            }
        }

        // Does not consume, because the message may still be deleted after a late preview attaches to it.
        public static bool TryPeekInbound(ulong discordMessageId, out long kmhId)
        {
            lock (_lock) return _inbound.TryGetValue(discordMessageId, out kmhId);
        }

        // Consumes the mapping, or a recycled message id could later remove an unrelated KMH line.
        public static bool TryTakeInbound(ulong discordMessageId, out long kmhId)
        {
            lock (_lock)
            {
                if (!_inbound.TryGetValue(discordMessageId, out kmhId)) return false;
                _inbound.Remove(discordMessageId);
                return true;
            }
        }

        internal static int BatchCountForTest { get { lock (_lock) return _batches.Count; } }
        internal static int InboundCountForTest { get { lock (_lock) return _inbound.Count; } }
    }
}
