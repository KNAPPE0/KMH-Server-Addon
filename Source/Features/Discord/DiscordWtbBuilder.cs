using System;
using System.Collections.Generic;
using Discord;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    // Embed builder for the per-user Want-To-Buy board. Parallel shape to DiscordShowcaseBuilder: title with owner
    // + linked Discord handle, optional italic tagline, one field per entry (item / max qty / max unit price).
    // Footer notes how many entries are shown
    //
    // Sort order: newest-first. The most recent ask reads as the most urgent, and trims to the 25-field embed cap
    // by dropping the oldest
    internal static class DiscordWtbBuilder
    {
        private const int MaxFields = 25;

        public static Embed Build(string username, string tagline, List<DiscordUserState.WtbEntry> entries)
        {
            string title = $"{username} is buying";
            if (LinkedAccountsStore.TryGetLink(username, out string discord) && !string.IsNullOrEmpty(discord))
            {
                title += $"  ·  {discord}";
            }

            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(new Color(0xBA, 0x84, 0xF6))
                .WithCurrentTimestamp();

            if (!string.IsNullOrEmpty(tagline))
            {
                eb.WithDescription($"_{tagline}_");
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
                    $"{emoji} {label}",
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
