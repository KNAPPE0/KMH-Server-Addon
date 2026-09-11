using System;
using System.Globalization;

namespace KMHServerAddon.Features.Chat
{
    // Discord signs CDN urls for 24h but chat is retained for 48, so identity is stored and the url re-fetched.
    internal static class ChatDiscordMedia
    {
        internal const string Scheme = "discord:";

        internal static string MakeRef(ulong channelId, ulong messageId, ulong attachmentId)
            => channelId == 0 || messageId == 0 ? "" : $"{Scheme}{channelId}:{messageId}:{attachmentId}";

        internal static bool TryParseRef(string reference, out ulong channelId, out ulong messageId, out ulong attachmentId)
        {
            channelId = messageId = attachmentId = 0;
            if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith(Scheme, StringComparison.Ordinal)) return false;

            string[] bits = reference.Substring(Scheme.Length).Split(':');
            if (bits.Length != 3) return false;
            return ulong.TryParse(bits[0], out channelId)
                && ulong.TryParse(bits[1], out messageId)
                && ulong.TryParse(bits[2], out attachmentId)
                && channelId != 0 && messageId != 0;
        }

        // Null is ordinary: tenor, giphy and the rest serve stable urls that carry no expiry at all.
        internal static DateTime? SignedExpiryUtc(string url)
        {
            string ex = QueryValue(url, "ex");
            if (string.IsNullOrEmpty(ex)) return null;
            if (!long.TryParse(ex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long seconds)) return null;
            if (seconds <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
            catch { return null; }
        }

        // The skew covers the round trip, so a url does not expire between this decision and the client's fetch.
        internal static bool IsExpired(string url, DateTime utcNow, int skewSeconds = 60)
        {
            DateTime? expiry = SignedExpiryUtc(url);
            return expiry.HasValue && expiry.Value <= utcNow.AddSeconds(skewSeconds);
        }

        internal static bool IsSigned(string url) => SignedExpiryUtc(url).HasValue;

        // The offsets below follow the CDN path shape: /attachments/<channelId>/<attachmentId>/<filename>
        internal static bool TryParseAttachmentUrl(string url, out ulong channelId, out ulong attachmentId, out string fileName)
        {
            channelId = attachmentId = 0;
            fileName = "";
            try
            {
                if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out Uri uri) || uri == null) return false;
                string[] seg = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                int at = Array.FindIndex(seg, x => string.Equals(x, "attachments", StringComparison.OrdinalIgnoreCase));
                if (at < 0 || seg.Length < at + 4) return false;
                if (!ulong.TryParse(seg[at + 1], out channelId)) return false;
                if (!ulong.TryParse(seg[at + 2], out attachmentId)) return false;
                fileName = seg[at + 3];
                return true;
            }
            catch { return false; }
        }

        // Shown instead of a 200-character signed url above the picture it is already displaying.
        internal static string ShortLabel(string url)
        {
            try
            {
                if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out Uri uri) || uri == null) return "media";
                string[] seg = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string last = seg.Length > 0 ? seg[seg.Length - 1] : "";
                if (last.Length > 0 && last.IndexOf('.') > 0)
                    return last.Length <= 48 ? last : last.Substring(0, 45) + "...";
                return uri.Host;
            }
            catch { return "media"; }
        }

        private static string QueryValue(string url, string key)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            if (q < 0) return "";
            foreach (string part in url.Substring(q + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(part.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                    return part.Substring(eq + 1);
            }
            return "";
        }
    }
}
