using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Transport
{
    // KMH transport config; off by default, using RWT chat unless the owner enables the KMH-owned port.
    internal sealed class TransportConfig
    {
        // Master switch for the KMH API listener. Off = chat transport only (no port opened).
        public bool   EnableKmhApiTransport   { get; set; } = false;

        // Port the KMH API listens on when enabled. Distinct from RWT's own port.
        public int    KmhApiPort              { get; set; } = 5099;

        // Interface to bind. "0.0.0.0" = reachable by remote players (like the RWT server); "127.0.0.1" = local only.
        public string BindAddress             { get; set; } = "0.0.0.0";

        // Require handshake tokens for API clients; keep on for public binds to prevent spoofed actions.
        public bool   RequireKmhApiAuth       { get; set; } = true;

        // If the KMH API is unreachable, let clients fall back to the RWT-chat transport so KMH still works.
        public bool   AllowChatTransportFallback { get; set; } = true;

        // Verbose KMH logging (ServerLog.Debug / ServerLog.Protocol). Off by default.
        public bool   DebugLogging            { get; set; } = false;

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

        private void Clamp()
        {
            if (KmhApiPort < 1 || KmhApiPort > 65535) KmhApiPort = 5099;
            if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "0.0.0.0";
        }
    }
}
