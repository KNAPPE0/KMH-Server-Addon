using System;
using System.Collections.Generic;
using Kmh.Discord.Wire;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordWtbBuilder
    {
        private const int MaxFields = 25;

        public static KmhEmbedSpec Build(string username, string tagline, List<DiscordUserState.WtbEntry> entries)
        {
            string title = $"{username} is buying";
            if (LinkedAccountsStore.TryGetLink(username, out string discord) && !string.IsNullOrEmpty(discord))
            {
                title += $"  ·  {discord}";
            }

            KmhEmbedSpecBuilder eb = new KmhEmbedSpecBuilder()
                .WithTitle(title)
                .WithColor(0xBA84F6)
                .WithCurrentTimestamp();

            if (!string.IsNullOrEmpty(tagline))
            {
                // Escaped like a name: a tagline is player text, and "[x](url)" would post as a disguised link.
                eb.WithDescription($"_{DiscordText.SafeName(tagline)}_");
            }

            if (entries == null || entries.Count == 0)
            {
                eb.AddField("Wanted", "_Empty list - add entries with `!kmh-wtb add <item> <qty> <max-price>`._",
                    inline: false);
                return eb.Build();
            }

            List<DiscordUserState.WtbEntry> sorted = new List<DiscordUserState.WtbEntry>(entries);
            sorted.Sort((a, b) => b.AddedUtcTicks.CompareTo(a.AddedUtcTicks));
            int show = Math.Min(MaxFields, sorted.Count);

            for (int i = 0; i < show; i++)
            {
                DiscordUserState.WtbEntry e = sorted[i];
                string label = ItemLabelCache.LabelFor(e.ItemDefName);
                string emoji = DiscordItemIconMap.EmojiFor(e.ItemDefName);
                eb.AddField(
                    $"{emoji} {DiscordText.Escape(label)}",
                    $"Up to **{e.MaxQty:N0}**× @ `≤{e.MaxUnitPriceSilver:N0}s`/ea",
                    inline: true);
            }

            string footer = sorted.Count > show
                ? $"Showing {show} of {sorted.Count} entries (newest first) · refreshed"
                : $"{sorted.Count} entr{(sorted.Count == 1 ? "y" : "ies")} · refreshed";
            eb.WithFooter(footer);
            return eb.Build();
        }
    }
}
