using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
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

        public static KmhComponentSpec BuildBuyComponents(MarketplaceListing l)
        {
            if (l == null || l.RemainingQty <= 0) return null;

            // A season reset restarts listing ids at 1, so the generation travels with the id and is checked on click.
            long gen = Features.Economy.KmhEconomyReset.Generation;
            KmhComponentSpec spec = new KmhComponentSpec();
            spec.Buttons.Add(new KmhButtonSpec
            {
                Label = "Buy 1", CustomId = $"kmh-buy:{gen}:{l.Id}:1", Style = "success",
            });
            if (l.RemainingQty >= 10)
            {
                spec.Buttons.Add(new KmhButtonSpec
                {
                    Label = "Buy 10", CustomId = $"kmh-buy:{gen}:{l.Id}:10", Style = "primary",
                });
            }
            spec.Buttons.Add(new KmhButtonSpec
            {
                Label = $"Buy all ({l.RemainingQty})", CustomId = $"kmh-buy:{gen}:{l.Id}:all", Style = "secondary",
            });
            return spec;
        }

        public static async Task OnInteraction(KmhDiscordInteraction component)
        {
            if (component == null) return;
            string customId = component.CustomId ?? "";
            if (!customId.StartsWith("kmh-buy:", StringComparison.Ordinal)) return;

            try
            {
                await HandleBuyClick(component, customId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: buy-button handler threw", ex);
                try { await component.Respond("Buy failed with an unexpected error.").ConfigureAwait(false); }
                catch (Exception) { }
            }
        }

        // kmh-buy:{generation}:{listing}:{1|10|all}; quantity is the LAST segment, or "Buy 1" on #57 buys 57.
        internal static bool TryParse(string customId, out long generation, out long listingId, out string quantity)
        {
            generation = 0; listingId = 0; quantity = "";
            string[] segs = (customId ?? "").Split(':');
            if (segs.Length != 4 || segs[0] != "kmh-buy" || !long.TryParse(segs[1], out generation)
                || !long.TryParse(segs[2], out listingId)) return false;
            quantity = segs[3];
            return true;
        }

        // Null for a malformed quantity; "all" is what is left, capped like any other.
        internal static int? QuantityFor(string quantity, int remaining)
        {
            int qty;
            if (string.Equals(quantity, "all", StringComparison.OrdinalIgnoreCase)) qty = Math.Min(remaining, 10_000);
            else if (!int.TryParse(quantity, out qty) || qty <= 0) return null;
            return Math.Min(qty, 10_000);
        }

        private static async Task HandleBuyClick(KmhDiscordInteraction component, string customId)
        {
            if (!TryParse(customId, out long builtUnderGen, out long listingId, out string quantity))
            {
                await component.Respond("Malformed buy button.").ConfigureAwait(false);
                return;
            }
            if (!KmhStaleAction.StillCurrent(builtUnderGen))
            {
                await component.Respond(
                    "That button is from before the last economy reset - listing numbers have been reused since. " +
                    "Run the marketplace command again for current listings.").ConfigureAwait(false);
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
                    await component.Respond("Still processing your previous click - give it a moment.")
                        .ConfigureAwait(false);
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
                await component.Respond(
                    "Link your Discord first to use buy buttons. " +
                    "In-game: `/kmh link`, then here: `!kmh-link <code>`.").ConfigureAwait(false);
                return;
            }
            if (Features.Moderation.BanGate.IsBannedAccount(caller))
            {
                await component.Respond("Your Colony Nexus account is banned on this server.").ConfigureAwait(false);
                return;
            }
            if (Features.Moderation.KmhEconomyGuard.IsFrozen(caller))
            {
                await component.Respond(Features.Moderation.KmhEconomyGuard.FrozenRefusal).ConfigureAwait(false);
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
                await component.Respond($"Listing #{listingId} no longer exists.").ConfigureAwait(false);
                return;
            }
            if (string.Equals(listing.SellerUsername, caller, StringComparison.OrdinalIgnoreCase))
            {
                await component.Respond("You can't buy your own listing.").ConfigureAwait(false);
                return;
            }

            int? wanted = QuantityFor(quantity, listing.RemainingQty);
            if (wanted == null)
            {
                await component.Respond("Malformed quantity.").ConfigureAwait(false);
                return;
            }
            int qty = wanted.Value;

            int  boughtQty = Math.Min(qty, listing.RemainingQty);
            long expected  = (long)listing.UnitPriceSilver * boughtQty;
            if (expected > int.MaxValue || expected < 0)
            {
                await component.Respond("Purchase value too large - try a smaller quantity.").ConfigureAwait(false);
                return;
            }

            if (!Maintenance.KmhAdmission.AllowsPlayerFeature(Maintenance.KmhIngress.Discord, "marketplace", out string blocked))
            {
                await component.Respond(blocked).ConfigureAwait(false);
                return;
            }

            // Deferred first, because Discord drops an interaction that has not responded within three seconds.
            await component.Defer().ConfigureAwait(false);

            // Admitted again at the purchase: a reset or backup that began during the defer waits for it or refuses it.
            bool bought;
            string sellerUsername;
            int paidSilver;
            using (Maintenance.KmhValueOps.Scope op = Maintenance.KmhValueOps.BeginFeature(Maintenance.KmhIngress.Discord, "marketplace", out blocked))
            {
                sellerUsername = null; paidSilver = 0;
                bought = op != null && MarketplaceStore.Buy(caller, listingId, qty, out sellerUsername, out boughtQty, out paidSilver);
            }
            if (blocked != null) { await component.Followup(blocked).ConfigureAwait(false); return; }
            if (!bought)
            {
                await component.Followup(
                    "Buy failed - insufficient silver, listing already sold, or another " +
                    "concurrent transaction beat you to it.").ConfigureAwait(false);
                return;
            }

            MarketplaceHandler.BroadcastSnapshot();
            MarketplaceHandler.PushTreasuryTo(caller);
            MarketplaceHandler.PushTreasuryTo(sellerUsername);

            string label = ItemLabelCache.LabelFor(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex);
            await component.Followup(
                $"Bought **{boughtQty}× {DiscordText.Escape(label)}** for `{paidSilver}s`. Items delivered to your treasury.")
                .ConfigureAwait(false);
            ServerLog.Info(
                $"Discord: {caller} bought x{boughtQty} of listing #{listingId} from {sellerUsername} via button");
        }
    }
}
