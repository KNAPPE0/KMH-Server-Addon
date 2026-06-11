using System.Globalization;
using Discord;

namespace KMHServerAddon.Features.Discord
{
    // Shared house style for every KMH embed: brand colour from Branding.EmbedColorHex, a DisplayName footer, and a
    // timestamp. Feature code calls Base() then adds its own fields, so leaderboards, marketplace posts, site
    // events and system alerts all read as one consistent system
    internal static class KmhEmbedBuilder
    {
        public static Color BrandColor(DiscordConfig cfg)
        {
            string hex = (cfg?.Branding?.EmbedColorHex ?? "#C88A2A").TrimStart('#');
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                return new Color(v);
            return new Color(0xC8, 0x8A, 0x2A);
        }

        public static EmbedBuilder Base(DiscordConfig cfg, string title, string description = null)
        {
            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(BrandColor(cfg))
                .WithFooter(string.IsNullOrEmpty(cfg?.Branding?.DisplayName) ? "KMH" : cfg.Branding.DisplayName)
                .WithCurrentTimestamp();
            if (!string.IsNullOrEmpty(description)) eb.WithDescription(description);
            return eb;
        }
    }
}
