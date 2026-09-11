using System.Text.RegularExpressions;

namespace KMHServerAddon.Features.Chat
{
    // RimWorld has no glyph for Discord's markup, so left alone it reaches a player as literal characters.
    internal static class ChatDiscordText
    {
        // KMH posts as "**name**: body", so a bot message in that shape is another bridge's echo of our own line.
        private static readonly Regex RelayEcho = new Regex(@"^\*\*[^*\r\n]{1,64}\*\*:\s", RegexOptions.Compiled);

        internal static bool LooksLikeRelayEcho(string text)
            => !string.IsNullOrEmpty(text) && RelayEcho.IsMatch(text.TrimStart());

        // The snowflake is dropped rather than shown, since it is a Discord id players have no reason to see.
        private static readonly Regex CustomEmoji = new Regex(@"<a?:([A-Za-z0-9_]{2,32}):\d{5,25}>", RegexOptions.Compiled);

        // The name behind a mention is not knowable without Discord's tables, so a neutral marker replaces it.
        private static readonly Regex UserMention    = new Regex(@"<@!?\d{5,25}>", RegexOptions.Compiled);
        private static readonly Regex RoleMention    = new Regex(@"<@&\d{5,25}>", RegexOptions.Compiled);
        private static readonly Regex ChannelMention = new Regex(@"<#\d{5,25}>", RegexOptions.Compiled);

        private static readonly Regex Timestamp = new Regex(@"<t:\d{1,20}(:[tTdDfFR])?>", RegexOptions.Compiled);

        // Defused so nobody outside the game can notify every player at once through the bridge.
        private static readonly Regex MassMention = new Regex(@"@(everyone|here|all)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal const string Zwsp = "\u200b";

        internal static string Readable(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            string outp = CustomEmoji.Replace(text, ":$1:");
            outp = UserMention.Replace(outp, "@someone");
            outp = RoleMention.Replace(outp, "@role");
            outp = ChannelMention.Replace(outp, "#channel");
            outp = Timestamp.Replace(outp, "(time)");
            outp = MassMention.Replace(outp, "@" + Zwsp + "$1");
            return outp;
        }
    }
}
