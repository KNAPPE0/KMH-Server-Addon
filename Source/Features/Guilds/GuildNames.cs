using System;

namespace KMHServerAddon.Features.Guilds
{
    // Guild names share the treasury's case-insensitive key space with personal vault keys, so none may look like one.
    internal static class GuildNames
    {
        internal const int MaxLength = 48;

        internal static bool TryNormalize(string raw, out string name, out string why)
        {
            name = "";
            why = "";
            string trimmed = (raw ?? "").Trim();
            if (trimmed.Length == 0) { why = "A guild needs a name."; return false; }
            if (trimmed.Length > MaxLength) trimmed = trimmed.Substring(0, MaxLength).TrimEnd();

            // '<' and '>' would be read as rich text in every other player's guild views.
            foreach (char c in trimmed)
                if (char.IsControl(c) || c == ':' || c == '<' || c == '>' || c == '\u200B' || c == '\uFEFF')
                { why = "A guild name cannot contain ':', '<', '>' or control characters."; return false; }
            // '_' leads every reserved key ("_personal:<name>"), so no guild may start with one.
            if (trimmed[0] == '_') { why = "A guild name cannot start with '_'."; return false; }
            // Its name rides into guild invites and letters every member reads.
            foreach (char c in trimmed)
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                { why = "A guild name cannot contain invisible characters."; return false; }
            if (Moderation.KmhBrandWords.PosesAsPlatform(trimmed))
            { why = "That guild name is reserved for Colony Nexus itself."; return false; }

            name = trimmed;
            return true;
        }

        // For names already on disk: only the collision itself is refused, so an older, looser name still loads.
        internal static bool CollidesWithPersonalKey(string name)
            => !string.IsNullOrEmpty(name)
            && name.TrimStart().StartsWith(Treasury.TreasuryStore.PersonalPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
