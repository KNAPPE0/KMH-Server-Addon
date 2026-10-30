using System;
using System.Collections.Generic;

namespace KMHServerAddon.Diagnostics
{
    // Counts errors by where they came from, so one source failing over and over can be told from scattered noise.
    internal static class KmhErrorTally
    {
        private const int MaxSources = 64, MaxPerSource = 50;

        private sealed class Source
        {
            public readonly Queue<DateTime> At = new Queue<DateTime>();
            public string Last = "";
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Source> _sources = new Dictionary<string, Source>(StringComparer.Ordinal);

        // "Persistence: save FAILED ..." comes from Persistence; a line with no short prefix is its own first words.
        internal static string SourceOf(string message)
        {
            string m = KmhLogText.OneLine(message ?? "", 400).Trim();
            int colon = m.IndexOf(':');
            if (colon > 0 && colon <= 48) return m.Substring(0, colon).Trim();
            return m.Length <= 32 ? m : m.Substring(0, 32).Trim();
        }

        public static void Note(string message, DateTime nowUtc)
        {
            string key = SourceOf(message);
            if (key.Length == 0) return;
            lock (_lock)
            {
                if (!_sources.TryGetValue(key, out Source s))
                {
                    if (_sources.Count >= MaxSources) _sources.Clear();
                    _sources[key] = s = new Source();
                }
                s.At.Enqueue(nowUtc);
                while (s.At.Count > MaxPerSource) s.At.Dequeue();
                s.Last = KmhLogText.OneLine(message ?? "", 400);
            }
        }

        public static List<(string Source, int Count, string Last)> Bursts(DateTime nowUtc, TimeSpan window, int threshold)
        {
            var hot = new List<(string, int, string)>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, Source> kv in _sources)
                {
                    while (kv.Value.At.Count > 0 && nowUtc - kv.Value.At.Peek() > window) kv.Value.At.Dequeue();
                    if (kv.Value.At.Count >= threshold) hot.Add((kv.Key, kv.Value.At.Count, kv.Value.Last));
                }
            }
            return hot;
        }

        internal static void ResetForTest() { lock (_lock) _sources.Clear(); }
    }
}
