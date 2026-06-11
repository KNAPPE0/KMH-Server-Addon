using System;
using System.Collections.Generic;
using Discord;
using KMHServerAddon.Util;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    // Embed builder for the per-user marketplace showcase. Centralizing the layout keeps it consistent across the
    // post-or-edit path and any future sweep refresh.
    //
    // Layout: title with the owner's username, optional italic tagline as description, one inline field per listing
    // (rank/price/qty/`!kmh-buy` hint), footer with refresh timestamp.
    internal static class DiscordShowcaseBuilder
    {
        // Discord caps embeds at 25 fields. We sort by price ascending so the cheapest listings always make the cut
        // when the seller has more than 25 active items
        private const int MaxFields = 25;

        public static Embed Build(string username, string tagline, List<MarketplaceListing> listings)
        {
            string title = $"{username}'s marketplace";
            // Suffix the linked discord handle (when known) so cross- referencing a showcase to a chat user is one
            // glance
            if (LinkedAccountsStore.TryGetLink(username, out string discord) && !string.IsNullOrEmpty(discord))
            {
                title += $"  ·  {discord}";
            }

            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(new Color(0xFF, 0xC4, 0x61))
                .WithCurrentTimestamp();

            if (!string.IsNullOrEmpty(tagline))
            {
                // Italic + leading-space margin so the tagline reads as a banner above the listing table rather
                // than another listing row
                eb.WithDescription($"_{tagline}_");
            }

            if (listings == null || listings.Count == 0)
            {
                eb.AddField("Listings", "_No active listings._", inline: false);
                eb.WithFooter("Use !kmh-sell here (or post in-game) to add listings, then !kmh-showcase to refresh");
                return eb.Build();
            }

            // Sort by unit price ascending - best deals at the top of the visible field set when the seller exceeds
            // the 25-field cap
            List<MarketplaceListing> sorted = new List<MarketplaceListing>(listings);
            sorted.Sort((a, b) => a.UnitPriceSilver.CompareTo(b.UnitPriceSilver));
            int show = Math.Min(MaxFields, sorted.Count);

            for (int i = 0; i < show; i++)
            {
                MarketplaceListing l = sorted[i];
                string label = ItemLabelCache.LabelFor(l.ItemDefName);
                string emoji = DiscordItemIconMap.EmojiFor(l.ItemDefName);
                eb.AddField(
                    $"{emoji} #{l.Id} · {label}",
                    $"**{l.RemainingQty}**× @ `{SilverFmt.Format(l.UnitPriceSilver)}/ea`\n`!kmh-buy {l.Id}`",
                    inline: true);
            }

            string footer = sorted.Count > show
                ? $"Showing {show} of {sorted.Count} listings (cheapest first) · refreshed"
                : $"{sorted.Count} listing{(sorted.Count == 1 ? "" : "s")} · refreshed";
            eb.WithFooter(footer);
            return eb.Build();
        }
    }
}
