using System.Globalization;
using Kmh.Discord.Wire;

namespace KMHServerAddon.Features.Discord
{
    internal static class KmhEmbedBuilder
    {
        private const uint DefaultBrand = 0xC88A2A;

        public static uint BrandColor(DiscordConfig cfg)
            => TryBrandColor(cfg?.Branding?.EmbedColorHex, out uint c, out _) ? c : DefaultBrand;

        // Length is checked too, because uint.TryParse reads a five-digit typo as a valid, quite different colour.
        internal static bool TryBrandColor(string raw, out uint color, out string problem)
        {
            color = DefaultBrand;
            problem = "";

            string hex = (raw ?? "").Trim().TrimStart('#');
            if (hex.Length == 0) { problem = "is empty"; return false; }

            if (hex.Length == 3)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);

            if (hex.Length != 6) { problem = $"'{raw}' has {hex.Length} hex digit(s), expected 6 (or 3)"; return false; }
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            { problem = $"'{raw}' is not hexadecimal"; return false; }

            color = v;
            return true;
        }

        // Both shipped defaults count as unset, since existing DiscordConfig.json files keep the old "KMH Server".
        public static string ServerName(DiscordConfig cfg)
        {
            string dn = cfg?.Branding?.DisplayName;
            return !string.IsNullOrWhiteSpace(dn) && dn != "KMH Server" && dn != "Colony Nexus Server" ? dn : KmhServerIdentity.Name;
        }
    }
}
