using System.Net;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Transport
{
    // KMH API transport config: on by default since v1.2.0 - public bind is only OK because auth/caps/throttle are required.
    internal sealed class TransportConfig
    {
        public int    SchemaVersion           { get; set; } = 1;

        // Master switch for the KMH API listener. Off = chat transport only (no port opened).
        public bool   EnableKmhApiTransport   { get; set; } = true;

        // Port the KMH API listens on when enabled. Distinct from RWT's own port. Forward this for remote KMH.
        public int    KmhApiPort              { get; set; } = 5099;

        // Public host clients should dial for the API when it differs from the RWT address (NAT/proxy/split hosts).
        // Empty = clients dial the RWT server IP they already connected to.
        public string PublicApiHost           { get; set; } = "";

        // Interface to bind. "0.0.0.0" = accept remote players (auth still required); "127.0.0.1" = local only.
        public string BindAddress             { get; set; } = "0.0.0.0";

        // Require handshake tokens / verified-session auth. Keep ON - off lets any socket claim any player.
        public bool   RequireKmhApiAuth       { get; set; } = true;

        // If the KMH API is unreachable, let clients fall back to the RWT-chat transport so KMH still works.
        public bool   AllowChatTransportFallback { get; set; } = true;

        // Verbose KMH logging (ServerLog.Debug / ServerLog.Protocol), incl. IPs in transport diagnostics. Off by default.
        public bool   DebugLogging            { get; set; } = false;

        // --- limits / DoS guards (tunable; sane defaults) ---
        public int    MaxConnections          { get; set; } = 200;   // total in-flight sockets
        public int    MaxConnectionsPerIp     { get; set; } = 6;     // concurrent sockets per source IP
        public int    AuthTimeoutSeconds      { get; set; } = 10;    // drop a socket that doesn't finish the hello in time
        public int    IdleTimeoutSeconds      { get; set; } = 45;    // drop a socket silent this long (3x the 15s heartbeat)
        public int    MaxFrameKb              { get; set; } = 64;    // reject frames larger than this
        public int    FailedAuthPerIp         { get; set; } = 10;    // failed auths from one IP in the window before a block
        public int    FailedAuthWindowSeconds { get; set; } = 60;
        public int    FailedAuthBlockSeconds  { get; set; } = 300;   // how long a tripped IP stays blocked

        // True if the bind reaches beyond this machine (anything not loopback) - used for the public-facing warning.
        [Newtonsoft.Json.JsonIgnore]
        public bool IsPublicBind
            => !(IPAddress.TryParse(BindAddress, out IPAddress a) && IPAddress.IsLoopback(a));

        private static TransportConfig _current;
        public static TransportConfig Current => _current ?? (_current = LoadOrDefault());

        public static TransportConfig LoadOrDefault()
        {
            TransportConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.TransportConfigFile, out TransportConfig loaded) && loaded != null
                ? loaded : new TransportConfig();
            cfg.Clamp();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.TransportConfigFile))
                JsonFileStore.Save(KmhDataPaths.TransportConfigFile, new TransportConfig());
        }

        public static void Reload() => _current = LoadOrDefault();

        // Test seam: runs the loader's clamping so the security harness can check it.
        internal static TransportConfig ClampForTest(TransportConfig cfg) { cfg.Clamp(); return cfg; }

        private void Clamp()
        {
            if (KmhApiPort < 1 || KmhApiPort > 65535) KmhApiPort = 5099;
            if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "127.0.0.1";
            MaxConnections          = Bound(MaxConnections, 1, 100000, 200);
            MaxConnectionsPerIp     = Bound(MaxConnectionsPerIp, 1, MaxConnections, 6);
            AuthTimeoutSeconds      = Bound(AuthTimeoutSeconds, 1, 120, 10);
            IdleTimeoutSeconds      = Bound(IdleTimeoutSeconds, 5, 3600, 45);
            MaxFrameKb              = Bound(MaxFrameKb, 1, 1024, 64);
            FailedAuthPerIp         = Bound(FailedAuthPerIp, 1, 100000, 10);
            FailedAuthWindowSeconds = Bound(FailedAuthWindowSeconds, 1, 3600, 60);
            FailedAuthBlockSeconds  = Bound(FailedAuthBlockSeconds, 0, 86400, 300);
        }

        private static int Bound(int v, int min, int max, int fallback) => (v < min || v > max) ? fallback : v;
    }
}
