using System;
using System.Collections.Generic;
using Kmh.Discord.Wire;
using KMHServerAddon.Util;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordShowcaseBuilder
    {
        // Discord's own embed field cap.
        private const int MaxFields = 25;

        internal static List<MarketplaceListing> ListingsBy(List<MarketplaceListing> listings, string seller)
        {
            List<MarketplaceListing> mine = new List<MarketplaceListing>();
            if (listings != null)
            {
                foreach (MarketplaceListing l in listings)
                {
                    if (string.Equals(l.SellerUsername, seller, StringComparison.OrdinalIgnoreCase))
                        mine.Add(l);
                }
            }
            return mine;
        }

        public static KmhEmbedSpec Build(string username, string tagline, List<MarketplaceListing> listings)
        {
            string title = $"{username}'s marketplace";
            if (LinkedAccountsStore.TryGetLink(username, out string discord) && !string.IsNullOrEmpty(discord))
            {
                title += $"  ·  {discord}";
            }

            KmhEmbedSpecBuilder eb = new KmhEmbedSpecBuilder()
                .WithTitle(title)
                .WithColor(0xFFC461)
                .WithCurrentTimestamp();

            if (!string.IsNullOrEmpty(tagline))
            {
                // Escaped like a name: a tagline is player text, and "[x](url)" would post as a disguised link.
                eb.WithDescription($"_{DiscordText.SafeName(tagline)}_");
            }

            if (listings == null || listings.Count == 0)
            {
                eb.AddField("Listings", "_No active listings._", inline: false);
                eb.WithFooter("Use !kmh-sell here (or post in-game) to add listings, then !kmh-showcase to refresh");
                return eb.Build();
            }

            // Cheapest first, so a seller over the field cap still shows their best prices rather than an arbitrary 25.
            List<MarketplaceListing> sorted = new List<MarketplaceListing>(listings);
            sorted.Sort((a, b) => a.UnitPriceSilver.CompareTo(b.UnitPriceSilver));
            int show = Math.Min(MaxFields, sorted.Count);

            for (int i = 0; i < show; i++)
            {
                MarketplaceListing l = sorted[i];
                string label = ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex);
                string emoji = DiscordItemIconMap.EmojiFor(l.ItemDefName);
                eb.AddField(
                    $"{emoji} #{l.Id} · {DiscordText.Escape(label)}",
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
