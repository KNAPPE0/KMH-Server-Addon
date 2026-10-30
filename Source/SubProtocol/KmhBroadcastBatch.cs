using System;
using System.Collections.Generic;

namespace KMHServerAddon.SubProtocol
{
    // Collapses repeat broadcasts of one kind inside a scope; scoped, not timed, so a lone action still sends at once.
    internal static class KmhBroadcastBatch
    {
        [ThreadStatic] private static Dictionary<string, Action> _pending;
        [ThreadStatic] private static int _depth;

        internal static bool Active => _depth > 0;

        // True when the caller's broadcast was taken over by the batch and must not also be sent now.
        public static bool Defer(string kind, Action flush)
        {
            if (_depth <= 0 || string.IsNullOrEmpty(kind) || flush == null) return false;
            (_pending ??= new Dictionary<string, Action>(StringComparer.Ordinal))[kind] = flush;
            return true;
        }

        public static IDisposable Open() { _depth++; return Scope.Instance; }

        private sealed class Scope : IDisposable
        {
            internal static readonly Scope Instance = new Scope();

            public void Dispose()
            {
                if (_depth > 0) _depth--;
                if (_depth > 0 || _pending == null || _pending.Count == 0) return;

                // Taken and cleared first: a flush that broadcasts again must not re-enter this same list.
                var due = new List<Action>(_pending.Values);
                _pending.Clear();
                foreach (Action a in due)
                {
                    try { a(); }
                    catch (Exception ex) { Diagnostics.ServerLog.Warn($"Broadcast batch: a deferred send failed: {ex.Message}"); }
                }
            }
        }

        internal static void ResetForTest() { _depth = 0; _pending?.Clear(); }

        internal static int PendingCountForTest => _pending?.Count ?? 0;
    }
}
