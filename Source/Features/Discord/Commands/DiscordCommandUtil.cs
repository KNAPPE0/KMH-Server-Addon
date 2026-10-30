using System.Collections.Generic;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordCommandUtil
    {
        // Id only: a display-name fallback would let someone act as any player whose name they copy.
        internal static string LinkedUsername(KmhDiscordMessage raw)
        {
            if (raw?.Author == null) return null;
            return LinkedAccountsStore.FindUsernameByDiscordId(raw.Author.Id);
        }

        internal static string JoinFrom(string[] parts, int startIndex)
        {
            if (parts == null || startIndex >= parts.Length) return "";
            return JoinWords(parts, startIndex, parts.Length);
        }

        internal static string JoinWords(string[] parts, int start, int end)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (i > start) sb.Append(' ');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        internal static string AmbiguousItems(string query, List<string> candidates)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("`").Append(query).Append("` matches multiple items - be more specific:\n");
            foreach (string c in candidates)
            {
                sb.Append("• **").Append(DiscordText.Escape(ItemLabelCache.LabelFor(c)))
                  .Append("** _(").Append(c).Append(")_\n");
            }
            return sb.ToString();
        }
    }
}
