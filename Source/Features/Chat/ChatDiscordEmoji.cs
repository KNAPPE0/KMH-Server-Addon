using System.Text.RegularExpressions;

namespace KMHServerAddon.Features.Chat
{
    // A custom emoji is a CDN picture rather than a character, so left alone it reaches a player as literal digits.
    internal static class ChatDiscordEmoji
    {
        private static readonly Regex Custom = new Regex(@"<(a?):([A-Za-z0-9_]{2,32}):(\d{5,25})>", RegexOptions.Compiled);

        // Discord's own CDN, which is already an allow-listed image host - no new trust is granted by this.
        internal const string EmojiCdn   = "https://cdn.discordapp.com/emojis/";
        internal const string StickerCdn = "https://media.discordapp.net/stickers/";

        internal static string FirstEmojiUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Match m = Custom.Match(text);
            if (!m.Success) return "";
            bool animated = m.Groups[1].Value == "a";
            return EmojiCdn + m.Groups[3].Value + (animated ? ".gif" : ".png");
        }

        // Raster only, because Discord serves lottie stickers as JSON that nothing here can draw.
        internal static string StickerUrl(ulong stickerId, string format)
        {
            if (stickerId == 0) return "";
            switch ((format ?? "").ToLowerInvariant())
            {
                case "png":
                case "apng":
                case "default": return StickerCdn + stickerId + ".png";
                case "gif":     return StickerCdn + stickerId + ".gif";
                default:        return "";
            }
        }
    }
}
