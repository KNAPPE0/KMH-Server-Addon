using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    internal enum KmhReadyState { Starting = 0, Recovering = 1, Ready = 2, Failed = 3 }

    // Handlers register long before their stores load; without this a half-failed boot serves an unread treasury and saves that emptiness.
    internal static class KmhReadiness
    {
        private static volatile KmhReadyState _state = KmhReadyState.Starting;
        private static string _why = "";

        public static KmhReadyState State => _state;
        public static bool IsReady => _state == KmhReadyState.Ready;
        public static string Reason => _why ?? "";

        public static string Describe()
            => _state == KmhReadyState.Ready ? "ready"
             : _state == KmhReadyState.Failed ? $"start-up failed ({Reason})"
             : _state == KmhReadyState.Recovering ? "still recovering from the last run"
             : "still starting up";

        public static void Recovering()
        {
            if (_state != KmhReadyState.Starting) return;
            _state = KmhReadyState.Recovering;
            ServerLog.Info("KMH is loading its stores and replaying anything the last run left unfinished.");
        }

        public static void Ready()
        {
            if (_state == KmhReadyState.Failed) return;
            bool announce = _state != KmhReadyState.Ready;
            _state = KmhReadyState.Ready;
            _why = "";
            if (announce) ServerLog.Info($"KMH is ready. {Persistence.JsonFileStore.DescribeHealth()}");
        }

        public static void Failed(string why)
        {
            _state = KmhReadyState.Failed;
            _why = why ?? "";
            string line = $"{Constants.LogPrefix} KMH is NOT operational: {Describe()}. Player value moves are "
                        + "refused; admin repair is still available. Fix the cause and restart.";
            // Not through ServerLog: it reaches RWT's Printer, and a mismatched Printer signature is exactly this boot failure.
            try { Diagnostics.KmhLogSink.Write($"[{System.DateTime.UtcNow:O}] [error] {line}"); } catch { }
            try { System.Console.Error.WriteLine(line); } catch { }
        }

        internal static void ResetForTest(KmhReadyState to) { _state = to; _why = ""; }
    }
}
