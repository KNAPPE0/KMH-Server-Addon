using System.Net;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Transport
{
    // A public bind is only safe because auth, the caps and the throttle are all required alongside it.
    internal sealed class TransportConfig
    {
        public int    SchemaVersion           { get; set; } = 1;

        // Off opens no port at all and leaves KMH on the chat transport.
        public bool   EnableKmhApiTransport   { get; set; } = true;

        // Distinct from RWT's own port, and the one to forward for remote KMH.
        public int    KmhApiPort              { get; set; } = 5099;

        // Empty means clients dial the RWT server IP they already connected to.
        public string PublicApiHost           { get; set; } = "";

        // "0.0.0.0" accepts remote players and auth is still required; "127.0.0.1" is local only.
        public string BindAddress             { get; set; } = "0.0.0.0";

        // Off lets any socket claim any player.
        public bool   RequireKmhApiAuth       { get; set; } = true;

        // Keeps KMH working when the API port is unreachable.
        public bool   AllowChatTransportFallback { get; set; } = true;

        // Off by default because transport diagnostics include client IPs.
        public bool   DebugLogging            { get; set; } = false;

        public int    MaxConnections          { get; set; } = 200;
        public int    MaxConnectionsPerIp     { get; set; } = 6;
        public int    AuthTimeoutSeconds      { get; set; } = 10;    // a socket that never finishes the hello
        public int    IdleTimeoutSeconds      { get; set; } = 45;    // 3x the 15s heartbeat
        public int    MaxFrameKb              { get; set; } = 64;
        public int    FailedAuthPerIp         { get; set; } = 10;
        public int    FailedAuthWindowSeconds { get; set; } = 60;
        public int    FailedAuthBlockSeconds  { get; set; } = 300;

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

        // Seam for the security harness, which has to prove a hand-edited file really is clamped.
        internal static TransportConfig ClampForTest(TransportConfig cfg) { cfg.Clamp(); return cfg; }

        // Owner policy decides what a transport may carry, so proving the rule needs the setting flipped in memory.
        internal static TransportConfig ApplyForTest(TransportConfig cfg) { TransportConfig prev = _current; _current = cfg; return prev; }

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
