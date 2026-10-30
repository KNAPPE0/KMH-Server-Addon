using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // Sees every HttpClient request in the process, so a path that bypasses KmhGlobalClient's check still shows.
    internal sealed class KmhGlobalIsolationMonitor : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>>
    {
        private const string HttpListenerName = "HttpHandlerDiagnosticListener";
        private const string RequestEvent     = "System.Net.Http.Request";

        private static readonly object _lock = new object();
        private static KmhGlobalIsolationMonitor _instance;
        private static IDisposable _all;
        private static long _escaped;
        private static readonly ConcurrentDictionary<string, bool> _escapedHosts =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        public static bool Running { get { lock (_lock) return _all != null; } }

        // Anything above zero is an isolation bypass.
        public static long Escaped => Interlocked.Read(ref _escaped);
        public static string EscapedHosts => string.Join(", ", _escapedHosts.Keys);

        public static void StartIfSelfTest()
        {
            if (KmhGlobalSafety.SelfTestActive && !KmhGlobalSafety.ProductionOptIn) Start();
        }

        internal static void Start()
        {
            lock (_lock)
            {
                if (_all != null) return;
                _instance = new KmhGlobalIsolationMonitor();
                _all = DiagnosticListener.AllListeners.Subscribe(_instance);
            }
        }

        internal static void StopForTest()
        {
            lock (_lock)
            {
                _all?.Dispose();
                _all = null;
                if (_instance != null) lock (_instance._subscriptions) { foreach (IDisposable s in _instance._subscriptions) s.Dispose(); }
                _instance = null;
            }
        }

        internal static void ResetForTest() { Interlocked.Exchange(ref _escaped, 0); _escapedHosts.Clear(); }

        internal static bool IsGlobalTarget(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || KmhGlobalSafety.IsLocalTestHost(uri.Host)) return false;
            string configured = GlobalConfig.Current?.BaseUrl ?? "";
            if (Uri.TryCreate(configured, UriKind.Absolute, out Uri cfg)
             && string.Equals(cfg.Host, uri.Host, StringComparison.OrdinalIgnoreCase)) return true;
            return KmhGlobalSafety.IsProduction(uri.GetLeftPart(UriPartial.Authority));
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != HttpListenerName) return;
            // Only the request event: enabling the activity events would add trace headers to every request.
            lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this, (name, a, b) => name == RequestEvent));
        }

        public void OnNext(KeyValuePair<string, object> e)
        {
            if (e.Key != RequestEvent) return;
            try
            {
                HttpRequestMessage req = e.Value?.GetType().GetProperty("Request")?.GetValue(e.Value) as HttpRequestMessage;
                if (req == null || !IsGlobalTarget(req.RequestUri)) return;
                Interlocked.Increment(ref _escaped);
                if (_escapedHosts.TryAdd(req.RequestUri.Host, true))
                    ServerLog.Error($"Global: a request to {req.RequestUri.Host} left this self-test run - isolation was bypassed.");
            }
            catch (Exception) { }   // an observer that throws would break the request it is watching
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
