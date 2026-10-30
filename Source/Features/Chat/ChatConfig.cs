using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Chat
{
    internal sealed class ChatLimits
    {
        public int MaxMessageLength    { get; set; } = 512;
        public int MaxRecentPerChannel { get; set; } = 100;
        public int MaxSendsPerWindow   { get; set; } = 20;
        public int SendWindowSeconds   { get; set; } = 30;
        public int RetentionHours      { get; set; } = 168;

        // Persists the same bounded ring a reconnecting client is served, so it adds durability but no retention.
        public bool PersistHistory { get; set; } = true;
    }

    internal sealed class ChatBlocking
    {
        public bool Enabled { get; set; } = true;

        // Nothing upstream checks a blocked name is real, so without a ceiling one client could fill the disk.
        public int MaxPerUser { get; set; } = 200;

        // The cap bounds list size; this bounds rewrite churn, which toggling one name would otherwise cause forever.
        public int MaxChangesPerWindow { get; set; } = 20;
        public int ChangeWindowSeconds { get; set; } = 60;
    }

    internal sealed class ChatTheme
    {
        public string Accent     { get; set; } = "";
        public string ServerChat { get; set; } = "";
        public string GuildChat  { get; set; } = "";
        public string DirectMsg  { get; set; } = "";
        public string Discord    { get; set; } = "";

        // Names and bodies are separate so branding author names does not wash out the log itself.
        public string NameNormal  { get; set; } = "";
        public string TextNormal  { get; set; } = "";
        public string NameDiscord { get; set; } = "";
        public string TextDiscord { get; set; } = "";

        // World-map marker rims. Outpost states keep KMH's colours: a warning styled into calm is worse than none.
        public string MarkerMine   { get; set; } = "";
        public string MarkerTheirs { get; set; } = "";

        // The one cue that does not depend on colour, so it can never be blanked or carry markup.
        public string DiscordMarker { get; set; } = "◈";
    }

    // Every caller reads through Current, so an edited file takes effect without a restart.
    internal sealed class ChatConfig
    {
        public int SchemaVersion { get; set; } = 1;

        public ChatLimits   Limits   { get; set; } = new ChatLimits();
        public ChatBlocking Blocking { get; set; } = new ChatBlocking();

        // Presentation only, never permissions or identity, and the client lifts anything unreadable.
        public ChatTheme Theme { get; set; } = new ChatTheme();

        // On for new servers only. There is deliberately no defaults-upgrade entry: a chosen false and a
        // generated false are the same byte, so migrating one would flip the other.
        public bool AllowImagePreviews { get; set; } = true;

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
            "giphy.com",          // i., media. and media0-4. - the GIF picker's and shared links' hosts
            "i.imgur.com",
            "i.redd.it",
            "upload.wikimedia.org",
            "images.steamusercontent.com",
            "steamuserimages-a.akamaihd.net",
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

        // Typed links preview only from ImageHostAllowList, over https, within the Media limits; off keeps them as text.
        public bool AllowTypedImageUrls { get; set; } = true;

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
            Limits   ??= new ChatLimits();
            Blocking ??= new ChatBlocking();
            Limits.MaxMessageLength    = Clamp(Limits.MaxMessageLength,    16, 4000);
            Limits.MaxRecentPerChannel = Clamp(Limits.MaxRecentPerChannel, 10, 1000);
            Limits.MaxSendsPerWindow   = Clamp(Limits.MaxSendsPerWindow,    1, 240);
            Limits.SendWindowSeconds   = Clamp(Limits.SendWindowSeconds,    1, 3600);
            Limits.RetentionHours      = Clamp(Limits.RetentionHours,       1, 720);
            Blocking.MaxPerUser          = Clamp(Blocking.MaxPerUser,          1, 5000);
            Blocking.MaxChangesPerWindow = Clamp(Blocking.MaxChangesPerWindow, 1, 1000);
            Blocking.ChangeWindowSeconds = Clamp(Blocking.ChangeWindowSeconds, 1, 3600);
            Theme ??= new ChatTheme();
            Theme.DiscordMarker      = ClampMarker(Theme.DiscordMarker);
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

        // Create only. New fields arrive through the shared boot backfill; a full reserialize here would also
        // drop any key this build does not know about.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.ChatConfigFile))
                JsonFileStore.Save(KmhDataPaths.ChatConfigFile, new ChatConfig());
        }

        // Clamp merges these at load anyway; this only keeps the file honest for an owner reading it.
        public static void MergeShippedImageHosts()
        {
            string path = KmhDataPaths.ChatConfigFile;
            if (!System.IO.File.Exists(path)) return;
            try
            {
                var file = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
                if (file["AutoAddNewImageHosts"] is Newtonsoft.Json.Linq.JValue auto && auto.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
                    && !(bool)auto.Value) return;
                if (!(file["ImageHostAllowList"] is Newtonsoft.Json.Linq.JArray list)) return;

                var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (Newtonsoft.Json.Linq.JToken t in list) seen.Add((t.ToString() ?? "").Trim());

                var added = new List<string>();
                foreach (string h in DefaultImageHosts())
                    if (seen.Add(h)) { list.Add(h); added.Add(h); }

                if (added.Count == 0) return;
                JsonFileStore.Save(path, file);
                Diagnostics.ServerLog.Info($"Chat: added newly shipped image host(s) to Chat.json: {string.Join(", ", added)}");
            }
            catch (System.Exception ex) { Diagnostics.ServerLog.Warn($"Chat: image host top-up skipped: {ex.Message}"); }
        }
    }
}
