using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Auctions.Dto;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordShowcaseSweep
    {
        internal const int BoardListings = 15, BoardAuctions = 5;

        private static DateTime _lastShowcases = DateTime.MinValue;

        // A full interval first, so item labels have arrived from a client before anything is drawn.
        internal static void ResetSchedule(DateTime now) => _lastShowcases = now;

        internal static async Task<bool> TickAsync(DiscordConfig cfg, DiscordLiveState state, DateTime now, IDiscordLiveSink sink, bool canEdit)
        {
            ulong ch = cfg.ShowcaseChannelId;
            if (ch == 0) return false;
            bool changed = false;

            int every = cfg.MarketBoardIntervalMinutes;
            if (every >= 1)
            {
                if (!canEdit) DiscordBridge.Once("market", "Discord: the live market board needs the bot's own gateway (coordinator mode cannot edit a message), so it is off.");
                else
                {
                    var window = DiscordDayWindow.For(now, cfg.LiveDayStartUtcHour, 24);
                    DiscordLiveSlot slot = state.Market;
                    changed |= await DiscordLive.TickSlotAsync(slot, ch, now, window, TimeSpan.FromMinutes(every), false,
                        live: () =>
                        {
                            string text = Board(PublicListings(), PublicAuctions(now), now,
                                                LiveHeader(every, window.End, now), keepEmpty: slot.MessageId != 0);
                            return text == null ? null : new DiscordLivePost { Title = "Market board", Content = text };
                        },
                        final: _ => DiscordLive.Finished(slot, FinalHeader(new DateTime(slot.WindowStartTicks, DateTimeKind.Utc),
                                                                          new DateTime(slot.WindowEndTicks, DateTimeKind.Utc))),
                        sink).ConfigureAwait(false);
                }
            }

            if (now - _lastShowcases >= TimeSpan.FromMinutes(cfg.ShowcaseSweepIntervalMinutes))
            {
                _lastShowcases = now;
                await RefreshShowcasesAsync().ConfigureAwait(false);
            }
            return changed;
        }

        internal static string LiveHeader(int everyMinutes, DateTime windowEnd, DateTime now)
            => $"🏪 **Market board** · live · updates every {DiscordDayWindow.Every(everyMinutes)} · final at {DiscordDayWindow.EndsAt(windowEnd, now)}";

        internal static string FinalHeader(DateTime windowStart, DateTime windowEnd)
            => $"🏪 **Market board** · final for {DiscordDayWindow.Label(windowStart, windowEnd)}";

        // Only what anyone may see: a guild-only listing never reaches a channel the whole server reads.
        private static List<MarketplaceListing> PublicListings()
        {
            var list = new List<MarketplaceListing>();
            foreach (MarketplaceListing l in MarketplaceStore.BuildSnapshot("")?.Listings ?? new List<MarketplaceListing>())
                if (l != null && IsPublic(l.Visibility) && l.RemainingQty > 0) list.Add(l);
            return list;
        }

        private static List<AuctionDto> PublicAuctions(DateTime now)
        {
            var list = new List<AuctionDto>();
            foreach (AuctionDto a in Auctions.AuctionStore.BuildSnapshot("")?.Auctions ?? new List<AuctionDto>())
                if (a != null && IsPublic(a.Visibility) && a.EndsUtcTicks > now.Ticks) list.Add(a);
            return list;
        }

        private static bool IsPublic(string visibility)
            => string.IsNullOrEmpty(visibility) || string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase);

        // Null when there is nothing to show and no board yet; an existing board says it is empty instead of going stale.
        internal static string Board(List<MarketplaceListing> listings, List<AuctionDto> auctions, DateTime now, string header, bool keepEmpty)
        {
            listings = listings ?? new List<MarketplaceListing>();
            auctions = auctions ?? new List<AuctionDto>();
            if (listings.Count == 0 && auctions.Count == 0 && !keepEmpty) return null;

            var sb = new StringBuilder(header);
            if (listings.Count == 0) sb.Append("\nNo open listings right now.");
            else
            {
                var newest = new List<MarketplaceListing>(listings);
                newest.Sort((a, b) => b.ListedUtcTicks != a.ListedUtcTicks ? b.ListedUtcTicks.CompareTo(a.ListedUtcTicks) : b.Id.CompareTo(a.Id));
                sb.Append($"\n**Open listings** · {listings.Count} · newest first");
                for (int i = 0; i < newest.Count && i < BoardListings; i++)
                {
                    MarketplaceListing l = newest[i];
                    string label = ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex);
                    sb.Append($"\n`#{l.Id}` {DiscordCompactText.Qty(l.RemainingQty)}× {Name(label)} · {DiscordCompactText.Silver(l.UnitPriceSilver)} each · {Name(l.SellerUsername)}");
                }
            }

            if (auctions.Count > 0)
            {
                var soonest = new List<AuctionDto>(auctions);
                soonest.Sort((a, b) => a.EndsUtcTicks.CompareTo(b.EndsUtcTicks));
                sb.Append("\n**Auctions ending soon**");
                for (int i = 0; i < soonest.Count && i < BoardAuctions; i++)
                {
                    AuctionDto a = soonest[i];
                    string label = ItemLabelCache.LabelFor(a.ItemDefName, a.StuffDefName, a.QualityIndex);
                    string bid = a.CurrentBid > 0 ? $"bid {DiscordCompactText.Silver(a.CurrentBid)} ({a.BidCount})" : $"opens at {DiscordCompactText.Silver(a.StartingBid)}";
                    long ends = new DateTimeOffset(new DateTime(a.EndsUtcTicks, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    sb.Append($"\n`A{a.Id}` {DiscordCompactText.Qty(a.Qty)}× {Name(label)} · {bid} · ends <t:{ends}:R>");
                }
            }
            sb.Append("\n-# `!kmh-market` lists every listing · `!kmh-buy <id> [qty]` buys one");
            return DiscordCompactText.Cap(DiscordCompactText.NoPreviews(sb.ToString()), DiscordCompactText.MaxChars);
        }

        private static string Name(string s) => DiscordText.SafeName(string.IsNullOrWhiteSpace(s) ? "?" : s.Trim());

        // A player's own showcase stays theirs to post and remove; this only keeps it current.
        private static async Task RefreshShowcasesAsync()
        {
            List<string> users = DiscordUserState.ListUsersWithShowcase();
            if (users.Count == 0) return;

            int refreshed = 0;
            int cleared   = 0;
            foreach (string username in users)
            {
                DiscordUserState.GetShowcase(username, out ulong channelId, out ulong messageId, out string tagline);
                if (channelId == 0 || messageId == 0) continue;

                // The public view: a showcase is posted where the whole server reads it.
                MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot("");
                List<MarketplaceListing> mine = DiscordShowcaseBuilder.ListingsBy(snap?.Listings, username);

                string text = DiscordCompactText.Render(DiscordShowcaseBuilder.Build(username, tagline, mine));
                ulong newId = await DiscordBridge.PostOrEditTextAsync(channelId, messageId, text).ConfigureAwait(false);
                if (newId == 0)
                {
                    // Cleared so the next user action posts fresh rather than editing a message that cannot be reached.
                    DiscordUserState.ClearShowcase(username);
                    cleared++;
                    continue;
                }

                if (newId != messageId)
                {
                    DiscordUserState.SetShowcase(username, channelId, newId, tagline);
                }
                refreshed++;
            }

            if (refreshed > 0 || cleared > 0)
            {
                ServerLog.Verbose(
                    $"Discord: showcase sweep refreshed {refreshed}, cleared {cleared} (of {users.Count})");
            }
        }
    }
}
