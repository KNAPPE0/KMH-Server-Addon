using System;
using System.Collections.Generic;

namespace KMHServerAddon.Diagnostics
{
    // Sequences are monotonic per process, so a reader asks for what came after N rather than re-reading the window.
    internal static class KmhConsoleTail
    {
        internal const int Capacity     = 600;
        internal const int MaxLineChars = 400;

        private static readonly object _lock = new object();
        private static readonly string[] _lines = new string[Capacity];
        private static readonly long[]   _seqs  = new long[Capacity];
        private static long _next = 1;
        private static int  _count;
        private static int  _head;

        public static long NewestSequence { get { lock (_lock) return _next - 1; } }

        public static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string one = KmhLogText.OneLine(line, MaxLineChars);
            lock (_lock)
            {
                int slot = (_head + _count) % Capacity;
                if (_count == Capacity) { slot = _head; _head = (_head + 1) % Capacity; }
                else _count++;
                _lines[slot] = one;
                _seqs[slot]  = _next++;
            }
        }

        // Oldest-first and bounded, so a reader that has been away for an hour gets a page rather than everything.
        public static List<KmhConsoleLine> Since(long afterSequence, int max)
        {
            var outList = new List<KmhConsoleLine>();
            if (max <= 0) return outList;
            lock (_lock)
            {
                for (int i = 0; i < _count && outList.Count < max; i++)
                {
                    int slot = (_head + i) % Capacity;
                    if (_seqs[slot] > afterSequence) outList.Add(new KmhConsoleLine(_seqs[slot], _lines[slot]));
                }
            }
            return outList;
        }

        // A reader asking from zero after a restart would otherwise be told the window is empty, not that it moved.
        public static long OldestSequence
        {
            get { lock (_lock) return _count == 0 ? 0 : _seqs[_head]; }
        }

        internal static void ResetForTest()
        {
            lock (_lock)
            {
                Array.Clear(_lines, 0, Capacity);
                Array.Clear(_seqs, 0, Capacity);
                _next = 1; _count = 0; _head = 0;
            }
        }
    }

    internal readonly struct KmhConsoleLine
    {
        public readonly long   Sequence;
        public readonly string Text;
        public KmhConsoleLine(long sequence, string text) { Sequence = sequence; Text = text; }
    }
}
