using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Media
{
    // Resolving exposes the SERVER's IP to the host rather than the player's, which is why this path exists at all.
    internal sealed class MediaConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Off means the client falls back to whatever static preview the source offers, not to a failure.
        public bool ServerMediaResolverEnabled { get; set; } = true;

        // Separate on purpose: an animated webp expands several-fold as a gif, so one number cannot bound both ends.
        public int MaxSourceBytes { get; set; } = 12 * 1024 * 1024;
        public int MaxOutputBytes { get; set; } =  8 * 1024 * 1024;

        // Read from the header before pixels are materialised: a tiny file can claim enormous dimensions.
        public int MaxPixels    { get; set; } = 4_000_000;   // ~2000x2000
        public int MaxDimension { get; set; } = 2048;
        public int MaxFrames    { get; set; } = 240;

        // Empty means any https host; this is the server's own fetch, not the chat image allow-list that guards players.
        public string[] SourceHostAllowList { get; set; } = new string[0];

        // MaxPixels bounds one frame and MaxFrames the count, but an animation costs their product, allocated at once.
        public int MaxDecodedMegabytes { get; set; } = 512;

        // Chat draws a few hundred pixels tall, so a taller animation costs bytes and textures for detail nobody sees. 0 = source size.
        public int AnimationMaxHeight { get; set; } = 320;

        // Whole-operation budget: connect, redirects, download and conversion together.
        public int TimeoutSeconds { get; set; } = 20;
        public int MaxRedirects   { get; set; } = 3;

        public int CacheMaxMegabytes { get; set; } = 256;
        public int CacheTtlMinutes   { get; set; } = 120;

        public int MaxResolvesPerWindow { get; set; } = 20;
        public int ResolveWindowSeconds { get; set; } = 60;

        // 0 disables; Unity reports length once prepared and before Play(), so this is enforced on real metadata.
        public int MaxVideoSeconds { get; set; } = 600;

        public bool WatchPagePlayback { get; set; } = true;
        // Every viewer streams out of this machine's upload, so lowering this is how a small connection copes.
        public int  WatchMaxHeight    { get; set; } = 1080;
        // 0 = no limit. A long video is a long fetch and a big cache file, so most servers want one.
        public int  WatchMaxSeconds   { get; set; } = 0;

        public bool   VideoServerEnabled { get; set; } = true;
        // The relay answers unauthenticated HTTP, so these caps are what bound a peer that stalls mid-header.
        public int    VideoMaxConnections      { get; set; } = 64;
        public int    VideoMaxConnectionsPerIp { get; set; } = 8;
        public string VideoHelperPath    { get; set; } = "";
        public string FfmpegPath         { get; set; } = "";
        public int    VideoMaxFileMegabytes  { get; set; } = 2048;
        public int    VideoCacheMaxMegabytes { get; set; } = 8192;
        public int    VideoCacheHours        { get; set; } = 48;   // 0 keeps files until the size cap alone evicts them
        public int    VideoMaxConcurrentFetches { get; set; } = 2;

        private static MediaConfig _current;
        public static MediaConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

        public static MediaConfig LoadOrDefault()
        {
            MediaConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.MediaConfigFile, out MediaConfig loaded) && loaded != null
                              ? loaded : new MediaConfig();
            cfg.Clamp();
            return cfg;
        }

        // Every limit held in a sane range, so a hand-edited file cannot disable a guard or exhaust the machine.
        public void Clamp()
        {
            MaxSourceBytes  = Clamp(MaxSourceBytes,  16 * 1024, 64 * 1024 * 1024);
            MaxOutputBytes  = Clamp(MaxOutputBytes,  16 * 1024, 64 * 1024 * 1024);
            MaxPixels       = Clamp(MaxPixels,       64 * 64,   64_000_000);
            MaxDimension    = Clamp(MaxDimension,    64,        16_384);
            MaxFrames       = Clamp(MaxFrames,       1,         2_000);
            MaxDecodedMegabytes = Clamp(MaxDecodedMegabytes, 4, 4_096);
            AnimationMaxHeight  = AnimationMaxHeight <= 0 ? 0 : Clamp(AnimationMaxHeight, 64, 2_048);
            TimeoutSeconds  = Clamp(TimeoutSeconds,  2,         120);
            MaxRedirects    = Clamp(MaxRedirects,    0,         10);
            CacheMaxMegabytes    = Clamp(CacheMaxMegabytes,    1,   8_192);
            CacheTtlMinutes      = Clamp(CacheTtlMinutes,      1,   10_080);
            MaxResolvesPerWindow = Clamp(MaxResolvesPerWindow, 1,   1_000);
            ResolveWindowSeconds = Clamp(ResolveWindowSeconds, 1,   3_600);
            MaxVideoSeconds      = MaxVideoSeconds <= 0 ? 0 : Clamp(MaxVideoSeconds, 5, 86_400);
            WatchMaxHeight       = Clamp(WatchMaxHeight, 144, 1080);
            VideoMaxConnections      = Clamp(VideoMaxConnections,      4, 4_096);
            VideoMaxConnectionsPerIp = Clamp(VideoMaxConnectionsPerIp, 1,   256);
            WatchMaxSeconds      = WatchMaxSeconds <= 0 ? 0 : Clamp(WatchMaxSeconds, 5, 86_400);
            VideoMaxFileMegabytes  = Clamp(VideoMaxFileMegabytes,  16, 32_768);
            VideoCacheMaxMegabytes = Clamp(VideoCacheMaxMegabytes, 64, 262_144);
            VideoCacheHours        = VideoCacheHours <= 0 ? 0 : Clamp(VideoCacheHours, 1, 8_760);
            VideoMaxConcurrentFetches = Clamp(VideoMaxConcurrentFetches, 1, 8);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public long CacheMaxBytes    => (long)CacheMaxMegabytes * 1024 * 1024;
        public long MaxDecodedBytes  => (long)MaxDecodedMegabytes * 1024 * 1024;

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.MediaConfigFile))
            {
                JsonFileStore.Save(KmhDataPaths.MediaConfigFile, new MediaConfig());
                return;
            }
            try
            {
                string existing = System.IO.File.ReadAllText(KmhDataPaths.MediaConfigFile);
                MediaConfig cfg = LoadOrDefault();
                if (JsonFileStore.ToJson(cfg).Trim() != existing.Trim())
                    JsonFileStore.Save(KmhDataPaths.MediaConfigFile, cfg);
            }
            catch (System.Exception ex) { Diagnostics.ServerLog.Warn($"Media config top-up skipped: {ex.Message}"); }
        }
    }
}
