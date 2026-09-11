using System;

namespace KMHServerAddon.Features.Chat
{
    // Classified from the source url, because a Discord proxy wrapper's own path names nothing useful.
    internal static class ChatMediaUrl
    {
        internal enum Kind { Unknown, Gif, Image, Video }

        internal static string SourceOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url ?? "";

            Uri uri;
            try { if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || uri == null) return url; }
            catch { return url; }

            // The indexes below follow the wrapper's shape: /external/<signature>/<scheme>/<host>/<rest...>
            string[] seg = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 4) return url;
            if (!string.Equals(seg[0], "external", StringComparison.OrdinalIgnoreCase)) return url;

            string scheme = seg[2];
            if (!string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)) return url;

            string host = seg[3];
            if (host.Length == 0 || host.IndexOf('.') < 0) return url;

            string rest = seg.Length > 4 ? "/" + string.Join("/", seg, 4, seg.Length - 4) : "/";
            return scheme.ToLowerInvariant() + "://" + host + rest;
        }

        // Read from the owner's TranscodeHosts, so the list stays editable rather than baked into the binary.
        internal static bool IsDiscordMediaHost(ChatConfig cfg, string url)
        {
            try
            {
                if (cfg == null || !Uri.TryCreate(url ?? "", UriKind.Absolute, out Uri uri) || uri == null) return false;
                return ChatImagePolicy.IsAllowedHost(uri.Host, cfg.TranscodeHosts);
            }
            catch { return false; }
        }

        // Unknown means the url names no media file at all, which is what a share page looks like.
        internal static Kind KindOf(ChatConfig cfg, string url)
        {
            string source = SourceOf(url);
            string path;
            try { path = Uri.TryCreate(source, UriKind.Absolute, out Uri u) && u != null ? u.AbsolutePath : source; }
            catch { path = source; }

            if (ChatImagePolicy.HasExtension(path, cfg?.VideoFileExtensions)) return Kind.Video;
            if (ChatImagePolicy.HasExtension(path, GifExtensions)) return Kind.Gif;
            if (ChatImagePolicy.HasExtension(path, cfg?.ImageFileExtensions)) return Kind.Image;
            return Kind.Unknown;
        }

        // Not owner-editable, because it states what the client's decoder can animate rather than a preference.
        private static readonly string[] GifExtensions = { ".gif" };

        internal static bool IsGif(ChatConfig cfg, string url) => KindOf(cfg, url) == Kind.Gif;

        // Narrower than the image allow-list on purpose: anything outside this set takes the resolver path instead.
        private static readonly string[] ClientDecodable = { ".png", ".jpg", ".jpeg", ".gif" };

        // Read from the SOURCE, so a proxy wrapper that names no extension is judged by what it actually carries.
        internal static bool ClientCanDecode(string url)
            => ChatImagePolicy.HasExtension(PathOf(SourceOf(StripFormatParam(url ?? ""))), ClientDecodable);

        // The ?format= hint is stripped first, since it is what would make the answer no for the very media that needs converting.
        internal static bool NeedsResolver(ChatConfig cfg, string vettedUrl)
        {
            if (string.IsNullOrWhiteSpace(vettedUrl)) return false;
            string bare = StripFormatParam(vettedUrl);
            if (KindOf(cfg, bare) == Kind.Video) return false;
            if (KindOf(cfg, bare) == Kind.Unknown) return false;
            return !ChatImagePolicy.HasExtension(PathOf(SourceOf(bare)), ClientDecodable);
        }

        // The original, not the proxy, because converting Discord's flattened copy would yield a one-frame gif.
        internal static string ResolverSourceFor(ChatConfig cfg, string vettedUrl)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(vettedUrl)) return "";
            string bare = StripFormatParam(vettedUrl);
            string src  = WrappedSourceOf(bare);
            return string.IsNullOrEmpty(src) ? bare : src;
        }

        // The wrapper carries its source as free text, so the scheme is re-checked rather than trusted.
        internal static string WrappedSourceOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string src = SourceOf(url);
            if (string.Equals(src, url, StringComparison.Ordinal)) return "";
            try { return new Uri(src).Scheme == Uri.UriSchemeHttps ? src : ""; }
            catch { return ""; }
        }

        internal static string StripFormatParam(string url)
        {
            if (string.IsNullOrEmpty(url)) return url ?? "";
            int q = url.IndexOf('?');
            if (q < 0) return url;

            string head = url.Substring(0, q);
            string[] parts = url.Substring(q + 1).Split('&');
            var kept = new System.Collections.Generic.List<string>();
            foreach (string part in parts)
                if (part.Length > 0 && !part.StartsWith("format=", StringComparison.OrdinalIgnoreCase)) kept.Add(part);
            return kept.Count == 0 ? head : head + "?" + string.Join("&", kept);
        }

        private static string PathOf(string url)
        {
            try { return Uri.TryCreate(url, UriKind.Absolute, out Uri u) && u != null ? u.AbsolutePath : url; }
            catch { return url; }
        }

        // An extension-less url is accepted only on Discord's own CDN, and only where Discord itself called it media.
        internal static bool IsRealMedia(ChatConfig cfg, string url, bool discordSaysMedia)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (KindOf(cfg, url) != Kind.Unknown) return true;
            if (!discordSaysMedia) return false;
            return IsDiscordMediaHost(cfg, url);
        }

        // Not a gate: it only tells the resolver that the proxy would hand back a flattened copy.
        internal static bool IsWrapped(string url)
            => !string.Equals(SourceOf(url), url, StringComparison.Ordinal);

        // Keeps a 400-character signed CDN link out of every log entry.
        internal static string Describe(ChatConfig cfg, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(none)";
            string source = SourceOf(url);
            string kind = KindOf(cfg, url).ToString().ToLowerInvariant();
            try
            {
                Uri u = new Uri(url);
                string via = ReferenceEquals(source, url) || source == url ? "" : " via " + u.Host;
                Uri s = new Uri(source);
                return $"{kind} {s.Host}{Trim(s.AbsolutePath)}{via}";
            }
            catch { return kind + " " + Trim(url); }
        }

        private static string Trim(string s)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= 60 ? s : s.Substring(0, 57) + "...");
    }
}
