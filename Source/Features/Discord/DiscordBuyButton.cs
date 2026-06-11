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
    // Click-to-buy buttons attached to the cheapest listing in !kmh-compare embeds. customId scheme
    // "kmh-buy:<id>:<qty|all>". Click goes through MarketplaceStore.Buy after a 2s per-user/ per-listing debounce;
    // receipts are ephemeral. Discord's 25-button- per-message cap is why we only attach to the cheapest listing
    internal static class DiscordBuyButton
    {
        private const int DebounceWindowMs = 2_000;
        private static readonly ConcurrentDictionary<string, long> _debounce
            = new ConcurrentDictionary<string, long>();

        // Build the Buy 1 / Buy 10 / Buy all button row for a listing. Returns null when the listing is empty or
        // invalid - caller skips attaching components in that case
        public static MessageComponent BuildBuyComponents(MarketplaceListing l)
        {
            if (l == null || l.RemainingQty <= 0) return null;

            ComponentBuilder cb = new ComponentBuilder();
            cb.WithButton(
                label:    "Buy 1",
                customId: $"kmh-buy:{l.Id}:1",
                style:    ButtonStyle.Success);
            if (l.RemainingQty >= 10)
            {
                cb.WithButton(
                    label:    "Buy 10",
                    customId: $"kmh-buy:{l.Id}:10",
                    style:    ButtonStyle.Primary);
            }
            cb.WithButton(
                label:    $"Buy all ({l.RemainingQty})",
                customId: $"kmh-buy:{l.Id}:all",
                style:    ButtonStyle.Secondary);
            return cb.Build();
        }

        // Wire from DiscordBridge.Start: _client.ButtonExecuted += OnInteraction.
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
                catch { /* worst-case nothing more we can do */ }
            }
        }

        private static async Task HandleBuyClick(SocketMessageComponent component, string customId)
        {
            string[] segs = customId.Split(':');
            if (segs.Length != 3 || !long.TryParse(segs[1], out long listingId))
            {
                await component.RespondAsync("Malformed buy button.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            // Per-user / per-listing debounce. Buy itself is atomic on the store side, but parallel clicks would
            // still produce duplicate ephemeral receipts that confuse the clicker
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

                // Cheap stale-entry cleanup - bounds the dictionary so a long-running server doesn't grow it
                // unbounded
                if (_debounce.Count > 256)
                {
                    foreach (var kv in _debounce)
                    {
                        if ((nowTicks - kv.Value) / TimeSpan.TicksPerMillisecond > DebounceWindowMs * 5)
                            _debounce.TryRemove(kv.Key, out _);
                        break;
                    }
                }
            }

            // Resolve clicker → in-game username. Same id-preferred lookup as the typed commands
            string display = ResolveDisplay(component.User);
            string caller  = LinkedAccountsStore.FindUsernameByDiscordId(clickerId);
            if (string.IsNullOrEmpty(caller) && !string.IsNullOrEmpty(display))
                caller = LinkedAccountsStore.FindUsernameByDiscord(display);
            if (string.IsNullOrEmpty(caller))
            {
                await component.RespondAsync(
                    "Link your Discord first to use buy buttons. " +
                    "In-game: `/kmh link`, then here: `!kmh-link <code>`.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }

            // Find the listing for the receipt before running Buy.
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

            // Resolve qty from button id.
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

            // Defer first - Discord requires interactions to respond within 3s. We'll FollowupAsync with the
            // receipt
            await component.DeferAsync(ephemeral: true).ConfigureAwait(false);

            if (!MarketplaceStore.Buy(caller, listingId, qty, out string sellerUsername))
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

            string label = ItemLabelCache.LabelFor(listing.ItemDefName);
            await component.FollowupAsync(
                $"Bought **{boughtQty}× {label}** for `{expected}s`. Items delivered to your treasury.",
                ephemeral: true).ConfigureAwait(false);
            ServerLog.Info(
                $"Discord: {caller} bought x{boughtQty} of listing #{listingId} from {sellerUsername} via button");
        }

        private static string ResolveDisplay(IUser user)
        {
            string g = user?.GlobalName;
            if (!string.IsNullOrEmpty(g)) return g;
            string u = user?.Username;
            string d = user?.Discriminator;
            if (!string.IsNullOrEmpty(d) && d != "0" && d != "0000") return $"{u}#{d}";
            return u ?? "";
        }
    }
}
