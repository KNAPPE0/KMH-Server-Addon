using System.Globalization;
using Discord;

namespace KMHServerAddon.Features.Discord
{
    internal static class KmhEmbedBuilder
    {
        public static Color BrandColor(DiscordConfig cfg)
            => TryBrandColor(cfg?.Branding?.EmbedColorHex, out Color c, out _) ? c : new Color(0xC8, 0x8A, 0x2A);

        // Length is checked too, because uint.TryParse reads a five-digit typo as a valid, quite different colour.
        internal static bool TryBrandColor(string raw, out Color color, out string problem)
        {
            color = new Color(0xC8, 0x8A, 0x2A);
            problem = "";

            string hex = (raw ?? "").Trim().TrimStart('#');
            if (hex.Length == 0) { problem = "is empty"; return false; }

            if (hex.Length == 3)   // #abc -> #aabbcc
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);

            if (hex.Length != 6) { problem = $"'{raw}' has {hex.Length} hex digit(s), expected 6 (or 3)"; return false; }
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            { problem = $"'{raw}' is not hexadecimal"; return false; }

            color = new Color(v);
            return true;
        }

        public static EmbedBuilder Base(DiscordConfig cfg, string title, string description = null)
        {
            // The server identity names the footer, unless an owner already set a custom Branding.DisplayName.
            string dn = cfg?.Branding?.DisplayName;
            string footer = !string.IsNullOrWhiteSpace(dn) && dn != "KMH Server" ? dn : KmhServerIdentity.Name;
            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(BrandColor(cfg))
                .WithFooter(footer)
                .WithCurrentTimestamp();
            if (!string.IsNullOrEmpty(description)) eb.WithDescription(description);
            return eb;
        }
    }
}
