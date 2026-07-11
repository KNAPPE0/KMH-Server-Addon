using System;
using System.Collections.Generic;
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
    // Mutating market commands (!kmh-buy/-sell/-cancel). Security: authenticates the caller via the snowflake id on
    // LinkedAccountsStore, not display name.
    internal static class DiscordTradeCommands
    {
        // Same hard cap the in-game Buy path uses. Without this, qty * unitPrice can overflow int and corrupt
        // treasury moves
        private const int MaxBuyQty = 10_000;

        public static async Task<bool> TryHandleAsync(SocketMessage raw, string cmd, string[] parts)
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

        // -- !kmh-buy --

        private static async Task HandleBuy(SocketMessage raw, string[] parts)
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

            // Pre-buy lookup for the receipt. Caller-scoped so a guild-only listing the caller can't see returns
            // "not found" rather than leaking its existence
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

            // Compute what we expect to take + pay. Store.Buy clamps to RemainingQty; we mirror that so the receipt
            // matches reality
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
                if (!MarketplaceStore.Buy(caller, listingId, qty, out string sellerUsername))
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

                Embed eb = new EmbedBuilder()
                    .WithTitle("Purchase complete")
                    .WithDescription(
                        $"Bought **{boughtQty}× {DiscordText.Escape(label)}** for `{expected}s`.\n" +
                        "Items delivered to your treasury.")
                    .AddField("Listing", $"#{listingId}", inline: true)
                    .AddField("Seller",  DiscordText.Escape(sellerUsername),  inline: true)
                    .WithColor(new Color(0x8C, 0xDC, 0x8C))
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

        // -- !kmh-cancel --

        private static async Task HandleCancel(SocketMessage raw, string[] parts)
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
                if (!MarketplaceStore.Cancel(caller, listingId))
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

        // -- !kmh-sell --

        private static async Task HandleSell(SocketMessage raw, string[] parts)
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

            // Last two tokens must be qty + price; everything between parts[1] and them is the (possibly
            // multi-word) item name, so "!kmh-sell power armor 1 800" parses correctly.
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

            System.Text.StringBuilder nameSb = new System.Text.StringBuilder();
            for (int i = 1; i < tokens.Length - 2; i++)
            {
                if (i > 1) nameSb.Append(' ');
                nameSb.Append(tokens[i]);
            }
            string itemRaw = nameSb.ToString().Replace('_', ' ').Trim();

            // optional trailing quality word: "!kmh-sell power armor excellent 1 800"
            int requestedQuality = 0;
            int lastSpace = itemRaw.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                int qw = Util.ItemKey.QualityIndexOf(itemRaw.Substring(lastSpace + 1));
                if (qw > 0) { requestedQuality = qw; itemRaw = itemRaw.Substring(0, lastSpace).Trim(); }
            }

            // Resolve friendly name → defName via the cache. Ambiguous matches return a candidate list so the user
            // can re-issue more precisely. Unknown items fall through to raw input as a defName (works for mods the
            // server hasn't seen yet - the treasury withdraw will fail cleanly if it really isn't one)
            string defName = ItemLabelCache.ResolveDefNameByQuery(itemRaw, out List<string> candidates);
            if (defName == null)
            {
                if (candidates != null && candidates.Count > 1)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.Append("`").Append(itemRaw).Append("` matches multiple items - be more specific:\n");
                    foreach (string c in candidates)
                    {
                        sb.Append("• **").Append(DiscordText.Escape(ItemLabelCache.LabelFor(c)))
                          .Append("** _(").Append(c).Append(")_\n");
                    }
                    await raw.Channel.SendMessageAsync(sb.ToString()).ConfigureAwait(false);
                    return;
                }
                // Last resort - treat the raw input as a defName.
                string fallback = itemRaw.Replace(' ', '_');
                defName = fallback.Length > 64 ? fallback.Substring(0, 64) : fallback;
            }
            // Resolve which vault stack this sells: the def may exist plain or as material/quality variants.
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
                // zero variants + no quality asked: fall through with the plain def - the escrow fails cleanly
            }

            // Mirror the in-game post path's sanity bounds.
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
                long id = MarketplaceStore.Post(caller, defName, qty, price, "public", expiresInHours: 0,
                                                stuffDefName: sellStuff, qualityIndex: sellQuality);
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
                Embed eb = new EmbedBuilder()
                    .WithTitle("Listed for sale")
                    .WithDescription(
                        $"**{qty}× {DiscordText.Escape(sellLabel)}** @ `{price}s`/ea  ·  total `{qty * (long)price}s`")
                    .AddField("Listing", $"#{id}", inline: true)
                    .AddField("Seller",  DiscordText.Escape(caller),  inline: true)
                    .WithColor(new Color(0xFF, 0xC4, 0x61))
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

        // -- helpers --

        // Returns the in-game username linked to the calling Discord user. Returns null + populates the error
        // message when the user isn't linked - caller forwards the message and returns
        private static string RequireLinkedAsync(SocketMessage raw, out string errorMessage)
        {
            errorMessage = null;
            if (raw?.Author == null)
            {
                errorMessage = "Could not resolve your Discord identity.";
                return null;
            }
            ulong  id      = raw.Author.Id;
            string display = ResolveDiscordDisplay(raw.Author);
            string user    = LinkedAccountsStore.FindUsernameByDiscordId(id);
            if (string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(display))
                user = LinkedAccountsStore.FindUsernameByDiscord(display);
            if (string.IsNullOrEmpty(user))
            {
                errorMessage =
                    "Link your Discord first to use trade commands. " +
                    "In-game: `/kmh link`, then on Discord: `!kmh-link <code>`.";
                return null;
            }
            return user;
        }

        // Compute the prefix length for the calling message so we can strip the leading "!" / "kmh-prefix" off
        // raw.Content before re-parsing. Same length DiscordBridge.OnMessage stripped to compute `cmd`
        private static int _config_prefix_len(SocketMessage raw)
        {
            string prefix = DiscordBridge.Config?.CommandPrefix ?? "!";
            return Math.Min(prefix.Length, raw?.Content?.Length ?? 0);
        }

        // Quote-aware tokenizer - splits on whitespace but treats anything inside "..." as a single token, so
        // multi-word item names (`!kmh-sell "power armor" 1 800`) parse correctly.
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

        private static string ResolveDiscordDisplay(IUser user)
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
