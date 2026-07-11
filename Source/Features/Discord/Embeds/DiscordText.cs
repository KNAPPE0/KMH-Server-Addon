namespace KMHServerAddon.Features.Discord
{
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
    }
}
