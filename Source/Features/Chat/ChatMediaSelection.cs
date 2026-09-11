namespace KMHServerAddon.Features.Chat
{
    // The proxy wins by default; an animated original wins only because the proxy would flatten it.
    internal static class ChatMediaSelection
    {
        internal static string Choose(ChatConfig cfg, string proxy, string original, out string why)
        {
            why = "";
            if (cfg == null) { why = "no chat config"; return ""; }

            bool preferOriginal =
                cfg.PreferOriginalForAnimated
                && !string.IsNullOrEmpty(original)
                && ChatMediaUrl.IsGif(cfg, original)
                && ChatImagePolicy.IsAllowedHost(HostOf(original), cfg.ImageHostAllowList);

            string first  = preferOriginal ? original : proxy;
            string second = preferOriginal ? proxy    : original;

            string chosen = FirstUsable(cfg, first, second);
            if (string.IsNullOrEmpty(chosen))
            {
                why = $"neither candidate names real media (proxy={ChatMediaUrl.Describe(cfg, proxy)}, "
                    + $"original={ChatMediaUrl.Describe(cfg, original)})";
                return "";
            }

            why = (chosen == original ? "original" : "proxy") + ": " + ChatMediaUrl.Describe(cfg, chosen)
                + (preferOriginal ? " (gif original keeps the animation the proxy would flatten)" : "");
            return chosen;
        }

        // Discord's proxy strips extensions, so its own CDN is taken on trust while a bare third-party url is not.
        private static string FirstUsable(ChatConfig cfg, string a, string b)
        {
            if (!string.IsNullOrEmpty(a) && ChatMediaUrl.IsRealMedia(cfg, a, discordSaysMedia: true)) return a;
            if (!string.IsNullOrEmpty(b) && ChatMediaUrl.IsRealMedia(cfg, b, discordSaysMedia: true)) return b;
            return "";
        }

        private static string HostOf(string url)
        {
            try { return new System.Uri(url).Host; } catch { return ""; }
        }
    }
}
