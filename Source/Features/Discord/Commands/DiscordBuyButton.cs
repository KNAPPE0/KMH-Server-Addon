using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordBuyButton
    {
        private const int DebounceWindowMs = 2_000;
        private static readonly ConcurrentDictionary<string, long> _debounce
            = new ConcurrentDictionary<string, long>();

        public static MessageComponent BuildBuyComponents(MarketplaceListing l)
        {
            if (l == null || l.RemainingQty <= 0) return null;

            // A season reset restarts listing ids at 1, so the generation travels with the id and is checked on click.
            long gen = Features.Economy.KmhEconomyReset.Generation;
            ComponentBuilder cb = new ComponentBuilder();
            cb.WithButton(
                label:    "Buy 1",
                customId: $"kmh-buy:{gen}:{l.Id}:1",
                style:    ButtonStyle.Success);
            if (l.RemainingQty >= 10)
            {
                cb.WithButton(
                    label:    "Buy 10",
                    customId: $"kmh-buy:{gen}:{l.Id}:10",
                    style:    ButtonStyle.Primary);
            }
            cb.WithButton(
                label:    $"Buy all ({l.RemainingQty})",
                customId: $"kmh-buy:{gen}:{l.Id}:all",
                style:    ButtonStyle.Secondary);
            return cb.Build();
        }

        public static async Task OnInteraction(SocketMessageComponent component)
        {
            if (component == null) return;
            string customId = component.Data?.CustomId ?? "";
            if (!customId.StartsWith("kmh-buy:", StringComparison.Ordinal)) return;

            try
            {
                await HandleBuyClick(component, customId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: buy-button handler threw", ex);
                try
                {
                    if (!component.HasResponded)
                        await component.RespondAsync("Buy failed with an unexpected error.", ephemeral: true)
                            .ConfigureAwait(false);
                }
                catch { }
            }
        }

        private static async Task HandleBuyClick(SocketMessageComponent component, string customId)
        {
            string[] segs = customId.Split(':');
            if (segs.Length != 4 || !long.TryParse(segs[1], out long builtUnderGen)
                || !long.TryParse(segs[2], out long listingId))
            {
                await component.RespondAsync("Malformed buy button.", ephemeral: true).ConfigureAwait(false);
                return;
            }
            if (!KmhStaleAction.StillCurrent(builtUnderGen))
            {
                await component.RespondAsync(
                    "That button is from before the last economy reset - listing numbers have been reused since. " +
                    "Run the marketplace command again for current listings.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            // Buy is already atomic; this only stops parallel clicks producing duplicate receipts.
            ulong clickerId = component.User?.Id ?? 0;
            if (clickerId != 0)
            {
                string key = $"{clickerId}:{listingId}";
                long nowTicks  = DateTime.UtcNow.Ticks;
                long lastTicks = _debounce.TryGetValue(key, out long t) ? t : 0;
                long sinceMs   = lastTicks == 0 ? long.MaxValue : (nowTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
                if (sinceMs < DebounceWindowMs)
                {
                    await component.RespondAsync(
                        "Still processing your previous click - give it a moment.",
                        ephemeral: true).ConfigureAwait(false);
                    return;
                }
                _debounce[key] = nowTicks;

                // Every stale entry, since the key is user+listing and checking only one lets the map grow.
                if (_debounce.Count > 256)
                {
                    long staleBefore = nowTicks - TimeSpan.TicksPerMillisecond * DebounceWindowMs * 5;
                    foreach (var kv in _debounce)
                        if (kv.Value < staleBefore) _debounce.TryRemove(kv.Key, out _);
                }
            }

            // Id only: a display-name fallback would let someone spend another player's treasury by copying their name.
            string caller = LinkedAccountsStore.FindUsernameByDiscordId(clickerId);
            if (string.IsNullOrEmpty(caller))
            {
                await component.RespondAsync(
                    "Link your Discord first to use buy buttons. " +
                    "In-game: `/kmh link`, then here: `!kmh-link <code>`.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }

            MarketplaceListing listing = null;
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(caller);
            if (snap?.Listings != null)
            {
                foreach (MarketplaceListing l in snap.Listings)
                {
                    if (l.Id == listingId) { listing = l; break; }
                }
            }
            if (listing == null)
            {
                await component.RespondAsync($"Listing #{listingId} no longer exists.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            if (string.Equals(listing.SellerUsername, caller, StringComparison.OrdinalIgnoreCase))
            {
                await component.RespondAsync("You can't buy your own listing.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            int qty;
            if (string.Equals(segs[2], "all", StringComparison.OrdinalIgnoreCase))
            {
                qty = Math.Min(listing.RemainingQty, 10_000);
            }
            else if (!int.TryParse(segs[2], out qty) || qty <= 0)
            {
                await component.RespondAsync("Malformed quantity.", ephemeral: true).ConfigureAwait(false);
                return;
            }
            if (qty > 10_000) qty = 10_000;

            int  boughtQty = Math.Min(qty, listing.RemainingQty);
            long expected  = (long)listing.UnitPriceSilver * boughtQty;
            if (expected > int.MaxValue || expected < 0)
            {
                await component.RespondAsync("Purchase value too large - try a smaller quantity.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!Maintenance.KmhAdmission.AllowsPlayerFeature(Maintenance.KmhIngress.Discord, "marketplace", out string blocked))
            {
                await component.RespondAsync(blocked, ephemeral: true).ConfigureAwait(false);
                return;
            }

            // Deferred first, because Discord drops an interaction that has not responded within three seconds.
            await component.DeferAsync(ephemeral: true).ConfigureAwait(false);

            if (!MarketplaceStore.Buy(caller, listingId, qty, out string sellerUsername, out boughtQty, out int paidSilver))
            {
                await component.FollowupAsync(
                    "Buy failed - insufficient silver, listing already sold, or another " +
                    "concurrent transaction beat you to it.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }

            MarketplaceHandler.BroadcastSnapshot();
            MarketplaceHandler.PushTreasuryTo(caller);
            MarketplaceHandler.PushTreasuryTo(sellerUsername);

            string label = ItemLabelCache.LabelFor(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex);
            await component.FollowupAsync(
                $"Bought **{boughtQty}× {DiscordText.Escape(label)}** for `{paidSilver}s`. Items delivered to your treasury.",
                ephemeral: true).ConfigureAwait(false);
            ServerLog.Info(
                $"Discord: {caller} bought x{boughtQty} of listing #{listingId} from {sellerUsername} via button");
        }
    }
}
