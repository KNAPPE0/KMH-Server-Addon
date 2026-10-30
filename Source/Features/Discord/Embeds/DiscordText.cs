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
                .Replace("|", "\\|")
                // A colony name like "[Claim prize](https://...)" would otherwise render as a masked link.
                .Replace("[", "\\[")
                .Replace("]", "\\]")
                .Replace("(", "\\(")
                .Replace(")", "\\)");

        // Inside a ``` block a run of three backticks would close it, and what follows renders as Discord markdown.
        public static string NoFenceBreak(string s)
        {
            string t = s ?? "";
            while (t.Contains("```")) t = t.Replace("```", "``​`");
            return t;
        }

        // A zero-width space after the @, which kills the ping while still reading as the text the player typed.
        public static string NoMentions(string s) => (s ?? "").Replace("@", "@​");

        // Collapsed so player text cannot forge a second relay line that reads as someone else's message.
        public static string OneLine(string s)
            => (s ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        public static string SafeName(string s) => OneLine(NoMentions(Escape(s)));

        // Markdown stays so relayed chat reads naturally, but a masked link could show one address and open another, and a
        // bare link would unfurl an invite card in the channel.
        private static readonly System.Text.RegularExpressions.Regex RelayLink =
            new System.Text.RegularExpressions.Regex(@"(?<!<)(https?://[^\s<>()\[\]]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex RelayInvite =
            new System.Text.RegularExpressions.Regex(@"discord(app)?\.(gg|com/invite)/", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static string SafeRelayBody(string s)
            => RelayInvite.Replace(RelayLink.Replace(OneLine(NoMentions(s)).Replace("](", "]​("), "<$1>"), m => m.Value.Replace(".", "​."));
    }
}
