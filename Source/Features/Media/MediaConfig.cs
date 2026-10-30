using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Media
{
    // Resolving exposes the SERVER's IP to the host rather than the player's, which is why this path exists at all.
    internal sealed class MediaConfig
    {
        public int SchemaVersion { get; set; } = 1;

        public MediaImageSettings Images { get; set; } = new MediaImageSettings();
        public MediaFetchSettings Fetch  { get; set; } = new MediaFetchSettings();
        public MediaCacheSettings Cache  { get; set; } = new MediaCacheSettings();
        public MediaVideoSettings Video  { get; set; } = new MediaVideoSettings();
        public MediaWatchSettings Watch  { get; set; } = new MediaWatchSettings();

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
            Images ??= new MediaImageSettings();
            Fetch  ??= new MediaFetchSettings();
            Cache  ??= new MediaCacheSettings();
            Video  ??= new MediaVideoSettings();
            Watch  ??= new MediaWatchSettings();
            Images.Clamp();
            Fetch.Clamp();
            Cache.Clamp();
            Video.Clamp();
            Watch.Clamp();
        }

        internal static int Bound(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Create only; new fields arrive through the shared boot backfill.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.MediaConfigFile))
                JsonFileStore.Save(KmhDataPaths.MediaConfigFile, new MediaConfig());
        }
    }

    internal sealed class MediaImageSettings
    {
        // Off means the client falls back to whatever static preview the source offers, not to a failure.
        public bool ResolverEnabled { get; set; } = true;

        // Separate on purpose: an animated webp expands several-fold as a gif, so one number cannot bound both ends.
        public int MaxSourceBytes { get; set; } = 12 * 1024 * 1024;
        public int MaxOutputBytes { get; set; } =  8 * 1024 * 1024;

        // Read from the header before pixels are materialised: a tiny file can claim enormous dimensions.
        public int MaxPixels    { get; set; } = 4_000_000;
        public int MaxDimension { get; set; } = 2048;
        public int MaxFrames    { get; set; } = 240;

        // MaxPixels bounds one frame and MaxFrames the count, but an animation costs their product, allocated at once.
        public int MaxDecodedMegabytes { get; set; } = 512;

        // Chat draws a few hundred pixels tall, so taller animations waste bytes and textures; 0 = source size.
        public int AnimationMaxHeight { get; set; } = 320;

        [Newtonsoft.Json.JsonIgnore] public long MaxDecodedBytes => (long)MaxDecodedMegabytes * 1024 * 1024;

        internal void Clamp()
        {
            MaxSourceBytes  = MediaConfig.Bound(MaxSourceBytes,  16 * 1024, 64 * 1024 * 1024);
            MaxOutputBytes  = MediaConfig.Bound(MaxOutputBytes,  16 * 1024, 64 * 1024 * 1024);
            MaxPixels       = MediaConfig.Bound(MaxPixels,       64 * 64,   64_000_000);
            MaxDimension    = MediaConfig.Bound(MaxDimension,    64,        16_384);
            MaxFrames       = MediaConfig.Bound(MaxFrames,       1,         2_000);
            MaxDecodedMegabytes = MediaConfig.Bound(MaxDecodedMegabytes, 4, 4_096);
            AnimationMaxHeight  = AnimationMaxHeight <= 0 ? 0 : MediaConfig.Bound(AnimationMaxHeight, 64, 2_048);
        }
    }

    internal sealed class MediaFetchSettings
    {
        // The SERVER's own fetch, not the chat list that guards players - two machines, two lists, one seed.
        // Empty means any https host, still refused for every private range by the address rules.
        public string[] SourceHostAllowList { get; set; } = Chat.ChatConfig.DefaultImageHosts();

        // Whole-operation budget: connect, redirects, download and conversion together.
        public int TimeoutSeconds { get; set; } = 20;
        public int MaxRedirects   { get; set; } = 3;

        public int MaxResolvesPerWindow { get; set; } = 20;
        public int ResolveWindowSeconds { get; set; } = 60;

        internal void Clamp()
        {
            TimeoutSeconds = MediaConfig.Bound(TimeoutSeconds, 2, 120);
            MaxRedirects   = MediaConfig.Bound(MaxRedirects,   0,  10);
            MaxResolvesPerWindow = MediaConfig.Bound(MaxResolvesPerWindow, 1, 1_000);
            ResolveWindowSeconds = MediaConfig.Bound(ResolveWindowSeconds, 1, 3_600);
        }
    }

    internal sealed class MediaCacheSettings
    {
        public int MaxMegabytes { get; set; } = 256;
        public int TtlMinutes   { get; set; } = 120;

        // Derived: writing it to the file offers an owner a setting that is silently overwritten.
        [Newtonsoft.Json.JsonIgnore] public long MaxBytes => (long)MaxMegabytes * 1024 * 1024;

        internal void Clamp()
        {
            MaxMegabytes = MediaConfig.Bound(MaxMegabytes, 1,  8_192);
            TtlMinutes   = MediaConfig.Bound(TtlMinutes,   1, 10_080);
        }
    }

    internal sealed class MediaVideoSettings
    {
        public bool ServerEnabled { get; set; } = true;

        // 0 disables; Unity reports length once prepared and before Play(), so this is enforced on real metadata.
        public int MaxSeconds { get; set; } = 600;

        // The relay answers unauthenticated HTTP, so these caps are what bound a peer that stalls mid-header.
        public int MaxConnections      { get; set; } = 64;
        public int MaxConnectionsPerIp { get; set; } = 8;

        public string HelperPath { get; set; } = "";
        public string FfmpegPath { get; set; } = "";

        public int MaxFileMegabytes  { get; set; } = 2048;
        public int CacheMaxMegabytes { get; set; } = 8192;
        public int CacheHours        { get; set; } = 48;   // 0 keeps files until the size cap alone evicts them
        public int MaxConcurrentFetches { get; set; } = 2;

        internal void Clamp()
        {
            MaxSeconds          = MaxSeconds <= 0 ? 0 : MediaConfig.Bound(MaxSeconds, 5, 86_400);
            MaxConnections      = MediaConfig.Bound(MaxConnections,      4, 4_096);
            MaxConnectionsPerIp = MediaConfig.Bound(MaxConnectionsPerIp, 1,   256);
            MaxFileMegabytes    = MediaConfig.Bound(MaxFileMegabytes,   16, 32_768);
            CacheMaxMegabytes   = MediaConfig.Bound(CacheMaxMegabytes,  64, 262_144);
            CacheHours          = CacheHours <= 0 ? 0 : MediaConfig.Bound(CacheHours, 1, 8_760);
            MaxConcurrentFetches = MediaConfig.Bound(MaxConcurrentFetches, 1, 8);
        }
    }

    internal sealed class MediaWatchSettings
    {
        public bool PagePlayback { get; set; } = true;

        // Every viewer streams out of this machine's upload, so lowering this is how a small connection copes.
        public int MaxHeight { get; set; } = 1080;

        // 0 = no limit. A long video is a long fetch and a big cache file, so most servers want one.
        public int MaxSeconds { get; set; } = 0;

        internal void Clamp()
        {
            MaxHeight  = MediaConfig.Bound(MaxHeight, 144, 1080);
            MaxSeconds = MaxSeconds <= 0 ? 0 : MediaConfig.Bound(MaxSeconds, 5, 86_400);
        }
    }
}
