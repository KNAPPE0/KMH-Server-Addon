using System.Net;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Transport
{
    // A public bind is only safe because auth, the caps and the throttle are all required alongside it.
    internal sealed class TransportConfig
    {
        public int    SchemaVersion           { get; set; } = 1;

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

        public TransportAdvanced Advanced { get; set; } = new TransportAdvanced();

        // Where each key of an older Transport.json lives now.
        internal static readonly System.Collections.Generic.IReadOnlyList<(string From, string To)> Moves = new[]
        {
            ("DebugLogging", "Advanced.DebugLogging"), ("MaxConnections", "Advanced.MaxConnections"),
            ("MaxConnectionsPerIp", "Advanced.MaxConnectionsPerIp"), ("AuthTimeoutSeconds", "Advanced.AuthTimeoutSeconds"),
            ("IdleTimeoutSeconds", "Advanced.IdleTimeoutSeconds"), ("MaxFrameKb", "Advanced.MaxFrameKb"),
            ("FailedAuthPerIp", "Advanced.FailedAuthPerIp"), ("FailedAuthWindowSeconds", "Advanced.FailedAuthWindowSeconds"),
            ("FailedAuthBlockSeconds", "Advanced.FailedAuthBlockSeconds"),
        };

        // Anything another machine could reach, including an address that does not resolve: unknown is treated as exposed.
        [Newtonsoft.Json.JsonIgnore]
        public bool IsPublicBind => !(TryResolveBind(BindAddress, out IPAddress a) && IsLoopbackAddress(a));

        // The one reading of BindAddress, shared by the listener and every check, so they can never disagree.
        internal static bool TryResolveBind(string raw, out IPAddress address)
        {
            address = null;
            string s = (raw ?? "").Trim();
            if (s.Equals("localhost", System.StringComparison.OrdinalIgnoreCase)) { address = IPAddress.Loopback; return true; }
            if (s.Length > 2 && s[0] == '[' && s[s.Length - 1] == ']') s = s.Substring(1, s.Length - 2);
            // IPAddress.TryParse accepts "1" or "127.1"; only a dotted quad or an IPv6 literal counts as a bind address.
            if (!IPAddress.TryParse(s, out IPAddress parsed)) return false;
            if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && s.Split('.').Length != 4) return false;
            address = parsed;
            return true;
        }

        // IsLoopback already counts ::ffff:127.0.0.1, which the bind self-test pins.
        internal static bool IsLoopbackAddress(IPAddress a) => a != null && IPAddress.IsLoopback(a);

        // Auth off is a test mode, and only loopback is safe for it.
        [Newtonsoft.Json.JsonIgnore]
        public string UnsafeReason
        {
            get
            {
                if (!TryResolveBind(BindAddress, out _))
                    return $"BindAddress '{BindAddress}' is not an IP address. Use 0.0.0.0 (all IPv4), :: (all IPv6), a local interface address, or 127.0.0.1.";
                if (IsPublicBind && !RequireKmhApiAuth)
                    return $"BindAddress {BindAddress} is reachable from other machines and RequireKmhApiAuth is false, so anyone who can reach "
                         + $"port {KmhApiPort} could act as any player. Set RequireKmhApiAuth=true, or BindAddress=127.0.0.1 for local testing.";
                return null;
            }
        }

        private static TransportConfig _current;
        public static TransportConfig Current => _current ?? (_current = LoadOrDefault());

        public static TransportConfig LoadOrDefault()
        {
            TransportConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.TransportConfigFile, Moves, out TransportConfig loaded) && loaded != null
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
            TransportAdvanced a = Advanced ??= new TransportAdvanced();
            a.MaxConnections          = Bound(a.MaxConnections, 1, 100000, 200);
            a.MaxConnectionsPerIp     = Bound(a.MaxConnectionsPerIp, 1, a.MaxConnections, 6);
            a.AuthTimeoutSeconds      = Bound(a.AuthTimeoutSeconds, 1, 120, 10);
            a.IdleTimeoutSeconds      = Bound(a.IdleTimeoutSeconds, 5, 3600, 45);
            a.MaxFrameKb              = Bound(a.MaxFrameKb, 1, 1024, 64);
            a.FailedAuthPerIp         = Bound(a.FailedAuthPerIp, 1, 100000, 10);
            a.FailedAuthWindowSeconds = Bound(a.FailedAuthWindowSeconds, 1, 3600, 60);
            a.FailedAuthBlockSeconds  = Bound(a.FailedAuthBlockSeconds, 0, 86400, 300);
        }

        private static int Bound(int v, int min, int max, int fallback) => (v < min || v > max) ? fallback : v;
    }

    internal sealed class TransportAdvanced
    {
        // Off by default because transport diagnostics include client IPs.
        public bool   DebugLogging            { get; set; } = false;

        public int    MaxConnections          { get; set; } = 200;
        public int    MaxConnectionsPerIp     { get; set; } = 6;
        public int    AuthTimeoutSeconds      { get; set; } = 10;
        public int    IdleTimeoutSeconds      { get; set; } = 45;    // 3x the 15s heartbeat
        public int    MaxFrameKb              { get; set; } = 64;
        public int    FailedAuthPerIp         { get; set; } = 10;
        public int    FailedAuthWindowSeconds { get; set; } = 60;
        public int    FailedAuthBlockSeconds  { get; set; } = 300;
    }
}
