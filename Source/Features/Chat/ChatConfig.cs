using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Chat
{
    // Every caller reads through Current, so an edited file takes effect without a restart.
    internal sealed class ChatConfig
    {
        public int SchemaVersion { get; set; } = 1;

        public int  MaxMessageLength    { get; set; } = 512;
        public int  MaxRecentPerChannel { get; set; } = 100;   // history kept per channel (reconnect context)
        public int  MaxSendsPerWindow   { get; set; } = 20;    // rate limit: sends allowed per window
        public int  SendWindowSeconds   { get; set; } = 30;
        public int  RetentionHours      { get; set; } = 48;
        public bool BlockingEnabled     { get; set; } = true;  // players can block others

        // Persists the same bounded ring a reconnecting client is served, so it adds durability but no retention.
        public bool PersistHistory      { get; set; } = true;

        // Nothing upstream checks a blocked name is real, so without a ceiling one client could fill the disk.
        public int  MaxBlocksPerUser    { get; set; } = 200;

        // The cap bounds list size; this bounds rewrite churn, which toggling one name would otherwise cause forever.
        public int  MaxBlockChangesPerWindow { get; set; } = 20;
        public int  BlockChangeWindowSeconds { get; set; } = 60;

        // Presentation only, never permissions or identity, and the client lifts anything unreadable.
        public string ThemeAccent      { get; set; } = "";
        public string ThemeServerChat  { get; set; } = "";
        public string ThemeGuildChat   { get; set; } = "";
        public string ThemeDirectMsg   { get; set; } = "";
        public string ThemeDiscord     { get; set; } = "";

        // Names and bodies are separate so branding author names does not wash out the log itself.
        public string ThemeNameNormal  { get; set; } = "";
        public string ThemeTextNormal  { get; set; } = "";
        public string ThemeNameDiscord { get; set; } = "";
        public string ThemeTextDiscord { get; set; } = "";

        // World-map marker rims. Outpost states keep KMH's colours: a warning an owner could style into calm is worse than none.
        public string MarkerMine       { get; set; } = "";
        public string MarkerTheirs     { get; set; } = "";

        // The one cue that does not depend on colour, so it can never be blanked or carry markup.
        public string DiscordMarker    { get; set; } = "◈";

        // Off by default because a preview makes a player's own client contact a third-party host.
        public bool AllowImagePreviews { get; set; } = false;

        // Matched exactly or by subdomain, never as a substring, so cdn.discordapp.com.evil.example is refused.
        public string[] ImageHostAllowList { get; set; } = DefaultImageHosts();

        // Leaving this on re-adds a host you removed, so turn it off to keep the list exactly as written.
        public bool AutoAddNewImageHosts { get; set; } = true;

        // Kept here rather than hidden in the exe, so an owner can see the whole shipped list.
        internal static string[] DefaultImageHosts() => new[]
        {
            "discordapp.com",     // cdn.discordapp.com - attachments
            "discordapp.net",     // media. and images-ext-N. - Discord's re-hosted embed media
            "media.tenor.com",
            "i.giphy.com",
            "media.giphy.com",
            "i.imgur.com",
        };

        // Generous because the client's own decoder caps frames and pixels, so a huge file truncates rather than grows.
        public int MaxImageBytes { get; set; } = 32 * 1024 * 1024;

        // Says what may be OFFERED, not what the client can decode, which is why WebP is listed.
        public string[] ImageFileExtensions { get; set; } = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        // Recognised so a video reaches the inline player rather than the image decoder.
        public string[] VideoFileExtensions { get; set; } = new[] { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v" };

        // Discord's proxy flattens an animated source, so a gif prefers the original; the host must still be allowed.
        public bool PreferOriginalForAnimated { get; set; } = true;

        // Opening a video link leaves the game, so it is offered rather than loaded automatically.
        public bool AllowVideoLinks { get; set; } = true;

        // Discord re-hosts embed media as WEBP, which nothing here reads, but its proxy accepts ?format=.
        public bool DiscordProxyTranscode { get; set; } = true;

        // Only these honour ?format=; appending it elsewhere would be ignored at best and break the url at worst.
        public string[] TranscodeHosts { get; set; } = new[] { "discordapp.net", "discordapp.com" };

        // Separate from the Discord path, because an attachment passed through the owner's own Discord.
        public bool AllowTypedImageUrls { get; set; } = false;

        private static ChatConfig _current;
        public static ChatConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

        public static ChatConfig LoadOrDefault()
        {
            ChatConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.ChatConfigFile, out ChatConfig loaded) && loaded != null ? loaded : new ChatConfig();
            cfg.Clamp();
            return cfg;
        }

        // Clamped so a hand-edited config cannot disable the guards.
        public void Clamp()
        {
            MaxMessageLength    = Clamp(MaxMessageLength,    16, 4000);
            MaxRecentPerChannel = Clamp(MaxRecentPerChannel, 10, 1000);
            MaxSendsPerWindow   = Clamp(MaxSendsPerWindow,    1, 240);
            SendWindowSeconds   = Clamp(SendWindowSeconds,    1, 3600);
            RetentionHours      = Clamp(RetentionHours,       1, 720);
            MaxBlocksPerUser    = Clamp(MaxBlocksPerUser,     1, 5000);
            MaxBlockChangesPerWindow = Clamp(MaxBlockChangesPerWindow, 1, 1000);
            BlockChangeWindowSeconds = Clamp(BlockChangeWindowSeconds, 1, 3600);
            DiscordMarker            = ClampMarker(DiscordMarker);
            ImageHostAllowList       = ImageHostAllowList ?? new string[0];
            if (AutoAddNewImageHosts) ImageHostAllowList = MergeHosts(ImageHostAllowList, DefaultImageHosts());
            MaxImageBytes            = Clamp(MaxImageBytes, 16 * 1024, 64 * 1024 * 1024);
            ImageFileExtensions      = ImageFileExtensions ?? new string[0];
            VideoFileExtensions      = VideoFileExtensions ?? new string[0];
            TranscodeHosts           = TranscodeHosts      ?? new string[0];
        }

        // Only ever adds, so an owner's own entries survive an update untouched.
        internal static string[] MergeHosts(string[] owner, string[] shipped)
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string h in owner ?? new string[0])
                if (!string.IsNullOrWhiteSpace(h) && seen.Add(h.Trim())) result.Add(h.Trim());
            foreach (string h in shipped ?? new string[0])
                if (!string.IsNullOrWhiteSpace(h) && seen.Add(h.Trim())) result.Add(h.Trim());
            return result.ToArray();
        }
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Angle brackets would carry rich-text markup into every chat line, and a blank would remove the cue entirely.
        internal static string ClampMarker(string m)
        {
            if (string.IsNullOrWhiteSpace(m)) return "◈";
            m = m.Trim();
            // Refused whole rather than stripped, since stripping would leave a worse marker and hide the mistake.
            if (m.IndexOf('<') >= 0 || m.IndexOf('>') >= 0) return "◈";
            return m.Length > 4 ? m.Substring(0, 4) : m;
        }

        // Rewritten from its own loaded values, so a field added by an update becomes visible instead of staying hidden.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.ChatConfigFile))
            {
                JsonFileStore.Save(KmhDataPaths.ChatConfigFile, new ChatConfig());
                return;
            }

            try
            {
                string existing = System.IO.File.ReadAllText(KmhDataPaths.ChatConfigFile);
                ChatConfig cfg = LoadOrDefault();
                if (JsonFileStore.ToJson(cfg).Trim() != existing.Trim())
                    JsonFileStore.Save(KmhDataPaths.ChatConfigFile, cfg);
            }
            catch (System.Exception ex) { Diagnostics.ServerLog.Warn($"Chat config top-up skipped: {ex.Message}"); }
        }
    }
}
