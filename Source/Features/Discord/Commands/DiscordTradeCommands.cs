using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordTradeCommands
    {
        // Matches the in-game cap, without which qty times unitPrice can overflow int and corrupt a treasury move.
        private const int MaxBuyQty = 10_000;

        public static async Task<bool> TryHandleAsync(KmhDiscordMessage raw, string cmd, string[] parts)
        {
            switch (cmd)
            {
                case "kmh-buy":
                    await HandleBuy(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-cancel":
                    await HandleCancel(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-sell":
                case "kmh-post":
                    await HandleSell(raw, parts).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }

        private static async Task HandleBuy(KmhDiscordMessage raw, string[] parts)
        {
            string caller = RequireLinkedAsync(raw, out string error);
            if (caller == null)
            {
                await raw.Channel.SendMessageAsync(error).ConfigureAwait(false);
                return;
            }
            if (parts.Length < 2 || !long.TryParse(parts[1], out long listingId))
            {
                await raw.Channel.SendMessageAsync("Usage: `!kmh-buy <listing-id> [qty]`")
                    .ConfigureAwait(false);
                return;
            }
            int qty = 1;
            if (parts.Length >= 3 && int.TryParse(parts[2], out int q) && q > 0) qty = q;
            if (qty > MaxBuyQty) qty = MaxBuyQty;

            // Caller-scoped, so a listing they cannot see reads as "not found" rather than leaking its existence.
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(caller);
            MarketplaceListing listing = null;
            if (snap?.Listings != null)
            {
                foreach (MarketplaceListing l in snap.Listings)
                {
                    if (l.Id == listingId) { listing = l; break; }
                }
            }
            if (listing == null)
            {
                await raw.Channel.SendMessageAsync($"Listing #{listingId} not found (or you can't see it).")
                    .ConfigureAwait(false);
                return;
            }
            if (string.Equals(listing.SellerUsername, caller, StringComparison.OrdinalIgnoreCase))
            {
                await raw.Channel.SendMessageAsync("You can't buy your own listing.")
                    .ConfigureAwait(false);
                return;
            }

            // Mirrors Store.Buy's own clamp to RemainingQty, or the receipt would promise more than was bought.
            int    boughtQty = Math.Min(qty, listing.RemainingQty);
            long   expected  = (long)listing.UnitPriceSilver * boughtQty;
            string label     = ItemLabelCache.LabelFor(listing.ItemDefName, listing.StuffDefName, listing.QualityIndex);
            if (expected > int.MaxValue || expected < 0)
            {
                await raw.Channel.SendMessageAsync(
                    "Purchase value too large - try a smaller quantity.")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                // Counted for the purchase, so a reset or backup that closes the gate waits for it or refuses it.
                bool bought;
                string sellerUsername = null, blocked;
                int paidSilver = 0;
                using (Maintenance.KmhValueOps.Scope op = Maintenance.KmhValueOps.BeginFeature(Maintenance.KmhIngress.Discord, "marketplace", out blocked))
                    bought = op != null && MarketplaceStore.Buy(caller, listingId, qty, out sellerUsername, out boughtQty, out paidSilver);
                if (blocked != null)
                {
                    await raw.Channel.SendMessageAsync(blocked).ConfigureAwait(false);
                    return;
                }
                if (!bought)
                {
                    await raw.Channel.SendMessageAsync(
                        "Buy failed - insufficient silver in your treasury, listing already sold, " +
                        "or another concurrent transaction beat you to it.")
                        .ConfigureAwait(false);
                    return;
                }

                MarketplaceHandler.BroadcastSnapshot();
                MarketplaceHandler.PushTreasuryTo(caller);
                MarketplaceHandler.PushTreasuryTo(sellerUsername);

                KmhEmbedSpec eb = new KmhEmbedSpecBuilder()
                    .WithTitle("Purchase complete")
                    .WithDescription(
                        $"Bought **{boughtQty}× {DiscordText.Escape(label)}** for `{paidSilver}s`.\n" +
                        "Items delivered to your treasury.")
                    .AddField("Listing", $"#{listingId}", inline: true)
                    .AddField("Seller",  DiscordText.SafeName(sellerUsername),  inline: true)
                    .WithColor(0x8CDC8C)
                    .WithCurrentTimestamp()
                    .Build();
                await raw.Channel.SendMessageAsync(embed: eb).ConfigureAwait(false);
                ServerLog.Info(
                    $"Discord: {caller} bought x{boughtQty} of listing #{listingId} from {sellerUsername} for {expected}s");
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: !kmh-buy threw", ex);
                await raw.Channel.SendMessageAsync("Buy failed with an unexpected error - see server log.")
                    .ConfigureAwait(false);
            }
        }

        private static async Task HandleCancel(KmhDiscordMessage raw, string[] parts)
        {
            string caller = RequireLinkedAsync(raw, out string error);
            if (caller == null)
            {
                await raw.Channel.SendMessageAsync(error).ConfigureAwait(false);
                return;
            }
            if (parts.Length < 2 || !long.TryParse(parts[1], out long listingId))
            {
                await raw.Channel.SendMessageAsync("Usage: `!kmh-cancel <listing-id>`")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                // Counted for the cancel, so a reset or backup that closes the gate waits for it or refuses it.
                bool cancelled;
                string blocked;
                using (Maintenance.KmhValueOps.Scope op = Maintenance.KmhValueOps.BeginFeature(Maintenance.KmhIngress.Discord, "marketplace", out blocked))
                    cancelled = op != null && MarketplaceStore.Cancel(caller, listingId);
                if (blocked != null)
                {
                    await raw.Channel.SendMessageAsync(blocked).ConfigureAwait(false);
                    return;
                }
                if (!cancelled)
                {
                    await raw.Channel.SendMessageAsync(
                        $"Cancel failed - listing #{listingId} not found, or you're not the seller.")
                        .ConfigureAwait(false);
                    return;
                }
                MarketplaceHandler.BroadcastSnapshot();
                MarketplaceHandler.PushTreasuryTo(caller);
                await raw.Channel.SendMessageAsync(
                    $"Listing **#{listingId}** cancelled - remaining items refunded to your treasury.")
                    .ConfigureAwait(false);
                ServerLog.Info($"Discord: {caller} cancelled listing #{listingId}");
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: !kmh-cancel threw", ex);
                await raw.Channel.SendMessageAsync("Cancel failed with an unexpected error - see server log.")
                    .ConfigureAwait(false);
            }
        }

        private static async Task HandleSell(KmhDiscordMessage raw, string[] parts)
        {
            string caller = RequireLinkedAsync(raw, out string error);
            if (caller == null)
            {
                await raw.Channel.SendMessageAsync(error).ConfigureAwait(false);
                return;
            }
            if (parts.Length < 4)
            {
                await raw.Channel.SendMessageAsync(
                    "Usage: `!kmh-sell <item> <qty> <unit-price>`\n" +
                    "Examples:\n" +
                    "  `!kmh-sell plasteel 100 10`\n" +
                    "  `!kmh-sell \"packaged survival meal\" 20 15`\n" +
                    "_Friendly names work - server resolves to the matching defName via the " +
                    "item-label cache. Ambiguous names get a candidate list back._")
                    .ConfigureAwait(false);
                return;
            }

            // qty and price are taken from the end, because the item name in between can be several words.
            string[] tokens = ReparseQuoted(raw.Content?.Substring(_config_prefix_len(raw)) ?? "");
            if (tokens.Length < 4
             || !int.TryParse(tokens[tokens.Length - 2], out int qty)   || qty   <= 0
             || !int.TryParse(tokens[tokens.Length - 1], out int price) || price <= 0)
            {
                await raw.Channel.SendMessageAsync(
                    "Couldn't parse qty + price. Both must be positive whole numbers at the end of the line.")
                    .ConfigureAwait(false);
                return;
            }

            string itemRaw = DiscordCommandUtil.JoinWords(tokens, 1, tokens.Length - 2).Replace('_', ' ').Trim();

            int requestedQuality = 0;
            int lastSpace = itemRaw.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                int qw = Util.ItemKey.QualityIndexOf(itemRaw.Substring(lastSpace + 1));
                if (qw > 0) { requestedQuality = qw; itemRaw = itemRaw.Substring(0, lastSpace).Trim(); }
            }

            // An unknown name falls through as a raw defName, which lets a mod the server has not seen still work.
            string defName = ItemLabelCache.ResolveDefNameByQuery(itemRaw, out List<string> candidates);
            if (defName == null)
            {
                if (candidates != null && candidates.Count > 1)
                {
                    await raw.Channel.SendMessageAsync(DiscordCommandUtil.AmbiguousItems(itemRaw, candidates)).ConfigureAwait(false);
                    return;
                }
                string fallback = itemRaw.Replace(' ', '_');
                defName = fallback.Length > 64 ? fallback.Substring(0, 64) : fallback;
            }
            // The same def can sit in the vault plain or as material and quality variants, so the stack must be chosen.
            string sellStuff = ""; int sellQuality = 0;
            {
                var vault = Treasury.TreasuryStore.GetSnapshotFor(caller);
                List<string> variants = new List<string>();
                if (vault?.Items != null)
                {
                    foreach (var kv in vault.Items)
                    {
                        Util.ItemKey.Split(kv.Key, out string d, out _, out int q);
                        if (!string.Equals(d, defName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (requestedQuality > 0 && q != requestedQuality) continue;
                        variants.Add(kv.Key);
                    }
                }
                if (variants.Count == 1)
                {
                    Util.ItemKey.Split(variants[0], out _, out sellStuff, out sellQuality);
                }
                else if (variants.Count > 1)
                {
                    System.Text.StringBuilder vb = new System.Text.StringBuilder();
                    vb.Append("You have multiple variants of that item - add a quality word (or use the in-game marketplace):\n");
                    foreach (string v in variants)
                        vb.Append("• **").Append(DiscordText.Escape(ItemLabelCache.LabelFor(v))).Append("**\n");
                    await raw.Channel.SendMessageAsync(vb.ToString()).ConfigureAwait(false);
                    return;
                }
                else if (requestedQuality > 0)
                {
                    await raw.Channel.SendMessageAsync(
                        $"No **{Util.ItemKey.QualityName(requestedQuality)}** {DiscordText.Escape(ItemLabelCache.LabelFor(defName))} in your treasury.")
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (qty   > MaxBuyQty)        qty   = MaxBuyQty;
            if (price > 1_000_000)        price = 1_000_000;
            long expectedTotal = (long)qty * price;
            if (expectedTotal > int.MaxValue)
            {
                await raw.Channel.SendMessageAsync(
                    "Total list value would overflow - try a smaller qty or price.")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                // Counted for the post, so a reset or backup that closes the gate waits for it or refuses it.
                long id;
                string blocked;
                using (Maintenance.KmhValueOps.Scope op = Maintenance.KmhValueOps.BeginFeature(Maintenance.KmhIngress.Discord, "marketplace", out blocked))
                    id = op == null ? 0 : MarketplaceStore.Post(caller, defName, qty, price, "public", expiresInHours: 0,
                                                                stuffDefName: sellStuff, qualityIndex: sellQuality);
                if (blocked != null)
                {
                    await raw.Channel.SendMessageAsync(blocked).ConfigureAwait(false);
                    return;
                }
                if (id == 0)
                {
                    await raw.Channel.SendMessageAsync(
                        $"Post failed - your treasury doesn't have **{qty}× {defName}** available, " +
                        "or the defName is unknown.")
                        .ConfigureAwait(false);
                    return;
                }
                MarketplaceHandler.BroadcastSnapshot();
                MarketplaceHandler.PushTreasuryTo(caller);
                string sellLabel = ItemLabelCache.LabelFor(defName, sellStuff, sellQuality);
                KmhEmbedSpec eb = new KmhEmbedSpecBuilder()
                    .WithTitle("Listed for sale")
                    .WithDescription(
                        $"**{qty}× {DiscordText.Escape(sellLabel)}** @ `{price}s`/ea  ·  total `{qty * (long)price}s`")
                    .AddField("Listing", $"#{id}", inline: true)
                    .AddField("Seller",  DiscordText.Escape(caller),  inline: true)
                    .WithColor(0xFFC461)
                    .WithCurrentTimestamp()
                    .Build();
                await raw.Channel.SendMessageAsync(embed: eb).ConfigureAwait(false);
                ServerLog.Info($"Discord: {caller} posted listing #{id} ({qty}x {defName} @ {price}s)");
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: !kmh-sell threw", ex);
                await raw.Channel.SendMessageAsync("Post failed with an unexpected error - see server log.")
                    .ConfigureAwait(false);
            }
        }

        internal static string RequireLinkedAsync(KmhDiscordMessage raw, out string errorMessage)
        {
            errorMessage = null;
            if (raw?.Author == null)
            {
                errorMessage = "Could not resolve your Discord identity.";
                return null;
            }
            // Id only: display names are attacker-settable, so a fallback would let anyone drain another player's treasury.
            string user = LinkedAccountsStore.FindUsernameByDiscordId(raw.Author.Id);
            if (string.IsNullOrEmpty(user))
            {
                errorMessage =
                    "Link your Discord first to use trade commands. " +
                    "In-game: `/kmh link`, then on Discord: `!kmh-link <code>`. " +
                    "(Linked long ago? Re-run `/kmh link` so your Discord id is on file.)";
                return null;
            }
            if (Features.Moderation.BanGate.IsBannedAccount(user))
            {
                errorMessage = "Your Colony Nexus account is banned on this server.";
                return null;
            }
            if (Features.Moderation.KmhEconomyGuard.IsFrozen(user))
            {
                errorMessage = Features.Moderation.KmhEconomyGuard.FrozenRefusal;
                return null;
            }
            return user;
        }

        // Must match what DiscordBridge.OnMessage stripped, or re-parsing shifts every token.
        private static int _config_prefix_len(KmhDiscordMessage raw)
        {
            string prefix = DiscordBridge.Config?.CommandPrefix ?? "!";
            return Math.Min(prefix.Length, raw?.Content?.Length ?? 0);
        }

        // Quotes hold a multi-word item name together, which plain whitespace splitting would break apart.
        private static string[] ReparseQuoted(string content)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(content)) return tokens.ToArray();
            System.Text.StringBuilder cur = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < content.Length; i++)
            {
                char ch = content[i];
                if (ch == '"') { inQuotes = !inQuotes; continue; }
                if (!inQuotes && (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r'))
                {
                    if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                    continue;
                }
                cur.Append(ch);
            }
            if (cur.Length > 0) tokens.Add(cur.ToString());
            return tokens.ToArray();
        }
    }
}
