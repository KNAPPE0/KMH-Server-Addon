namespace KMHServerAddon.Features.Discord
{
    // The three hazards stay separate because not all of them are unwanted everywhere.
    internal static class DiscordText
    {
        public static string Escape(string s)
            => (s ?? "")
                .Replace("\\", "\\\\")
                .Replace("*", "\\*")
                .Replace("_", "\\_")
                .Replace("`", "\\`")
                .Replace("~", "\\~")
                .Replace("|", "\\|");

        // A zero-width space after the @, which kills the ping while still reading as the text the player typed.
        public static string NoMentions(string s) => (s ?? "").Replace("@", "@​");

        // Collapsed so player text cannot forge a second relay line that reads as someone else's message.
        public static string OneLine(string s)
            => (s ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        public static string SafeName(string s) => OneLine(NoMentions(Escape(s)));

        // Markdown is left intact here on purpose, so relayed chat still reads naturally in Discord.
        public static string SafeRelayBody(string s) => OneLine(NoMentions(s));
    }
}
