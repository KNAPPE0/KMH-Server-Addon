using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Chat
{
    // Decided once on the server, so no client ever decides what its own player is asked to fetch.
    internal static class ChatImagePolicy
    {
        // knownImage skips only the extension test, for proxy urls that carry none; the host check still applies.
        public static string Vet(string url, bool typedByPlayer, bool knownImage = false)
            => Vet(ChatConfig.Current, url, typedByPlayer, knownImage);

        internal static string Vet(ChatConfig cfg, string url, bool typedByPlayer, bool knownImage = false)
            => Vet(cfg, url, typedByPlayer, knownImage, out _);

        // Reports why it said no, so a link that will not preview leaves the owner something to act on.
        internal static string Vet(ChatConfig cfg, string url, bool typedByPlayer, bool knownImage, out string reason)
        {
            reason = "";
            if (cfg == null) { reason = "no chat config"; return ""; }
            if (!cfg.AllowImagePreviews) { reason = "image previews are off (AllowImagePreviews)"; return ""; }
            if (typedByPlayer && !cfg.AllowTypedImageUrls) { reason = "typed links may not preview (AllowTypedImageUrls)"; return ""; }
            if (string.IsNullOrWhiteSpace(url)) { reason = "empty url"; return ""; }

            Uri uri;
            try { if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || uri == null) { reason = "unparseable url"; return ""; } }
            catch { reason = "unparseable url"; return ""; }

            // A plain-http preview would be a downgrade the player cannot see and cannot refuse.
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) { reason = "not https"; return ""; }
            if (!IsAllowedHost(uri.Host, cfg.ImageHostAllowList)) { reason = $"host not allow-listed: {uri.Host}"; return ""; }

            // An extension-less url is a share page rather than media, unless Discord itself put it in a media slot.
            if (!ChatMediaUrl.IsRealMedia(cfg, url, knownImage))
            {
                reason = knownImage
                    ? $"names no media file and is not on a Discord media host: {uri.Host}"
                    : $"names no media file: {uri.Host}";
                return "";
            }

            // Rebuilt rather than echoed, so a fragment or embedded credential cannot ride into every client's log.
            string clean = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, uri.AbsolutePath)
                           { Query = uri.Query.TrimStart('?') }.Uri.ToString();
            return ApplyTranscode(cfg, clean);
        }

        // isVideo comes back true for media KMH offers as a link, so it is not failed as an unreadable image.
        internal static string VetMedia(ChatConfig cfg, string url, bool typedByPlayer, bool knownImage, out bool isVideo)
        {
            isVideo = false;
            if (cfg == null || string.IsNullOrWhiteSpace(url)) return "";

            Uri uri;
            try { if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || uri == null) return ""; }
            catch { return ""; }

            if (ChatMediaUrl.KindOf(cfg, url) == ChatMediaUrl.Kind.Video)
            {
                if (!cfg.AllowVideoLinks) return "";
                // Never transcoded or fetched, so the host and scheme gates are the whole of it.
                string vetted = Vet(cfg, url, typedByPlayer, knownImage: true);
                isVideo = vetted.Length > 0;
                return vetted;
            }

            return Vet(cfg, url, typedByPlayer, knownImage);
        }

        // Never a substring test, or cdn.discordapp.com.evil.example would pass.
        internal static bool IsAllowedHost(string host, IEnumerable<string> allowed)
        {
            if (string.IsNullOrWhiteSpace(host) || allowed == null) return false;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            foreach (string a in allowed)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                string want = a.Trim().TrimEnd('.').ToLowerInvariant();
                if (want.Length == 0) continue;
                if (host == want) return true;
                if (host.EndsWith("." + want, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // The cfg-taking overloads are the real rule; a caller passing one must never fall through to the live config.
        internal static bool LooksLikeImage(string path) => LooksLikeImage(ChatConfig.Current, path);
        internal static bool LooksLikeVideo(string path) => LooksLikeVideo(ChatConfig.Current, path);
        internal static bool LooksLikeImage(ChatConfig cfg, string path) => HasExtension(path, cfg?.ImageFileExtensions);
        internal static bool LooksLikeVideo(ChatConfig cfg, string path) => HasExtension(path, cfg?.VideoFileExtensions);

        internal static bool HasExtension(string path, string[] extensions)
        {
            if (string.IsNullOrEmpty(path) || extensions == null) return false;
            string p = path.ToLowerInvariant();
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            foreach (string ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext)) continue;
                string e = ext.Trim().ToLowerInvariant();
                if (!e.StartsWith(".", StringComparison.Ordinal)) e = "." + e;
                if (p.EndsWith(e, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Asked for only where the client would otherwise be stuck: measured, the proxy re-encodes a 212KB jpeg into 1.1MB of png.
        internal static string ApplyTranscode(ChatConfig cfg, string url)
        {
            if (cfg == null || !cfg.DiscordProxyTranscode || string.IsNullOrEmpty(url)) return url;

            Uri uri;
            try { if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri == null) return url; }
            catch { return url; }
            if (!IsAllowedHost(uri.Host, cfg.TranscodeHosts)) return url;
            if (url.IndexOf("format=", StringComparison.OrdinalIgnoreCase) >= 0) return url;
            // Asking a proxy for an mp4 "as png" only produces a broken url.
            if (ChatMediaUrl.KindOf(cfg, url) == ChatMediaUrl.Kind.Video) return url;

            string want = WantedFormat(cfg, url);
            if (want == null) return url;
            string sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            return url + sep + "format=" + want;
        }

        // null asks for nothing, which is the right answer for a source the proxy already serves as it is.
        internal static string WantedFormat(ChatConfig cfg, string url)
        {
            // Always asked for, because the proxy hands back a flattened still of an animated gif otherwise.
            if (ChatMediaUrl.IsGif(cfg, url)) return "gif";
            if (ChatMediaUrl.ClientCanDecode(url)) return null;
            // A WRAPPED webp converts to gif with every frame intact; asked of the proxy directly it answers 415.
            return ChatMediaUrl.IsWrapped(url) ? "gif" : "png";
        }

        // Only the first, because a chat message is one line rather than a gallery.
        internal static string FirstUrl(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int i = body.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int end = i;
            while (end < body.Length && !char.IsWhiteSpace(body[end])) end++;
            return body.Substring(i, end - i);
        }
    }
}
