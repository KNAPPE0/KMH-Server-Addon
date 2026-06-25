using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    // !kmh-wtb command family. Lets a linked player publish a Want-To-Buy board to the configured WTB channel
    // (falls back to the showcase channel if a dedicated WTB channel isn't configured)
    //
    // Subcommands: !kmh-wtb - post (first time) OR refresh !kmh-wtb add <item> <qty> <max-price> !kmh-wtb remove
    // <item> - drop an entry !kmh-wtb list - show your own entries in chat !kmh-wtb clear - empty your entry list
    // !kmh-wtb delete - delete the published board !kmh-wtb tagline <text|clear> - set/clear the headline
    //
    // Aliases: !kmh-want, !kmh-wanted.
    //
    // Entry list is hard-capped per user to keep boards readable and avoid persistent-state bloat from accidental
    // scripts
    internal static class DiscordWtbCommands
    {
        // Max WTB entries per user. 25 = Discord embed field cap, so we never have to truncate when rendering.
        private const int MaxEntriesPerUser = 25;
        // Sanity bounds for input - anything beyond these is almost certainly a fat-finger and we'd rather reject
        // than carry bad data through the persistence layer
        private const int MaxQty            = 10_000;
        private const int MaxUnitPrice      = 1_000_000;

        public static async Task<bool> TryHandleAsync(SocketMessage raw, string cmd, string[] parts)
        {
            switch (cmd)
            {
                case "kmh-wtb":
                case "kmh-want":
                case "kmh-wanted":
                    await HandleWtb(raw, parts).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }

        private static async Task HandleWtb(SocketMessage raw, string[] parts)
        {
            string caller = ResolveLinkedUsername(raw);
            if (string.IsNullOrEmpty(caller))
            {
                await raw.Channel.SendMessageAsync(
                    "Link your Discord first to use the WTB board. " +
                    "In-game: `/kmh link`, then here: `!kmh-link <code>`.")
                    .ConfigureAwait(false);
                return;
            }

            DiscordConfig cfg = DiscordBridge.Config;
            ulong channel = cfg?.EffectiveWtbChannelId ?? 0;

            string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "add":
                    await HandleAdd(raw, caller, parts).ConfigureAwait(false);
                    return;
                case "remove":
                case "rm":
                case "del":
                    await HandleRemove(raw, caller, parts).ConfigureAwait(false);
                    return;
                case "list":
                case "mine":
                case "show":
                    await HandleList(raw, caller).ConfigureAwait(false);
                    return;
                case "clear":
                case "wipe":
                    await HandleClear(raw, caller).ConfigureAwait(false);
                    return;
                case "delete":
                    await HandleDelete(raw, caller).ConfigureAwait(false);
                    return;
                case "tagline":
                    await HandleTagline(raw, caller, parts).ConfigureAwait(false);
                    return;
                case "":
                case "update":
                case "refresh":
                    if (channel == 0)
                    {
                        await raw.Channel.SendMessageAsync(
                            "No WTB channel configured. Ask an admin to set " +
                            "`Channels.Marketplace` in Config/Discord/DiscordConfig.json.")
                            .ConfigureAwait(false);
                        return;
                    }
                    await HandleUpdate(raw, caller, channel).ConfigureAwait(false);
                    return;
                default:
                    await raw.Channel.SendMessageAsync(
                        "Usage: `!kmh-wtb add <item> <qty> <max-price>` · `!kmh-wtb remove <item>` · " +
                        "`!kmh-wtb list` · `!kmh-wtb clear` · `!kmh-wtb` (post/refresh) · " +
                        "`!kmh-wtb delete` · `!kmh-wtb tagline <text|clear>`")
                        .ConfigureAwait(false);
                    return;
            }
        }

        // -- add --

        private static async Task HandleAdd(SocketMessage raw, string caller, string[] parts)
        {
            // Need at least: !kmh-wtb add <item-word(s)> <qty> <price> = parts[0] cmd, parts[1] "add",
            // parts[2..n-2] item, parts[n-2] qty, parts[n-1] price
            if (parts.Length < 5
             || !int.TryParse(parts[parts.Length - 2], out int qty)   || qty   <= 0
             || !int.TryParse(parts[parts.Length - 1], out int price) || price <= 0)
            {
                await raw.Channel.SendMessageAsync(
                    "Usage: `!kmh-wtb add <item> <max-qty> <max-unit-price>`\n" +
                    "Examples:\n" +
                    "  `!kmh-wtb add plasteel 100 10`\n" +
                    "  `!kmh-wtb add \"power armor\" 1 800`")
                    .ConfigureAwait(false);
                return;
            }
            if (qty   > MaxQty)       qty   = MaxQty;
            if (price > MaxUnitPrice) price = MaxUnitPrice;

            System.Text.StringBuilder nameSb = new System.Text.StringBuilder();
            for (int i = 2; i < parts.Length - 2; i++)
            {
                if (i > 2) nameSb.Append(' ');
                nameSb.Append(parts[i]);
            }
            string itemRaw = nameSb.ToString().Replace('_', ' ').Trim();

            // Friendly-name resolution via the cache. Same ambiguous- candidate-list pattern !kmh-sell uses so the
            // WTB UX matches
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
                string fallback = itemRaw.Replace(' ', '_');
                defName = fallback.Length > 64 ? fallback.Substring(0, 64) : fallback;
            }

            bool ok = DiscordUserState.AddOrUpdateWtb(caller, defName, qty, price, MaxEntriesPerUser);
            if (!ok)
            {
                await raw.Channel.SendMessageAsync(
                    $"WTB list is full ({MaxEntriesPerUser} entries max). Use `!kmh-wtb remove <item>` first.")
                    .ConfigureAwait(false);
                return;
            }
            string label = ItemLabelCache.LabelFor(defName);
            await raw.Channel.SendMessageAsync(
                $"Added WTB: **{qty:N0}× {DiscordText.Escape(label)}** @ `≤{price:N0}s`/ea. Run `!kmh-wtb` to refresh your board.")
                .ConfigureAwait(false);
        }

        // -- remove --

        private static async Task HandleRemove(SocketMessage raw, string caller, string[] parts)
        {
            if (parts.Length < 3)
            {
                await raw.Channel.SendMessageAsync("Usage: `!kmh-wtb remove <item>`").ConfigureAwait(false);
                return;
            }
            System.Text.StringBuilder nameSb = new System.Text.StringBuilder();
            for (int i = 2; i < parts.Length; i++)
            {
                if (i > 2) nameSb.Append(' ');
                nameSb.Append(parts[i]);
            }
            string itemRaw = nameSb.ToString().Replace('_', ' ').Trim();
            string defName = ItemLabelCache.ResolveDefNameByQuery(itemRaw, out _);
            if (defName == null) defName = itemRaw.Replace(' ', '_');

            int removed = DiscordUserState.RemoveWtb(caller, defName);
            await raw.Channel.SendMessageAsync(
                removed == 0
                    ? $"No WTB entry for **{DiscordText.Escape(ItemLabelCache.LabelFor(defName))}** found."
                    : $"Removed WTB for **{DiscordText.Escape(ItemLabelCache.LabelFor(defName))}**. Run `!kmh-wtb` to refresh.")
                .ConfigureAwait(false);
        }

        // -- list --

        private static async Task HandleList(SocketMessage raw, string caller)
        {
            DiscordUserState.GetWtb(caller, out _, out _, out string tagline, out List<DiscordUserState.WtbEntry> entries);
            Embed eb = DiscordWtbBuilder.Build(caller, tagline, entries);
            await raw.Channel.SendMessageAsync(embed: eb).ConfigureAwait(false);
        }

        // -- clear --

        private static async Task HandleClear(SocketMessage raw, string caller)
        {
            int n = DiscordUserState.ClearWtb(caller);
            await raw.Channel.SendMessageAsync(
                n == 0
                    ? "Your WTB list was already empty."
                    : $"Cleared {n} WTB entr{(n == 1 ? "y" : "ies")}. Run `!kmh-wtb` to refresh your board.")
                .ConfigureAwait(false);
        }

        // -- update (post/refresh published embed) --

        private static async Task HandleUpdate(SocketMessage raw, string caller, ulong channel)
        {
            DiscordUserState.GetWtb(caller, out ulong prevChannel, out ulong prevMessage, out string tagline, out List<DiscordUserState.WtbEntry> entries);

            // Channel changed (admin reconfigured) - drop old id so we post fresh in the new channel
            if (prevChannel != 0 && prevChannel != channel) prevMessage = 0;

            Embed embed = DiscordWtbBuilder.Build(caller, tagline, entries);

            ulong newId = await DiscordBridge.PostOrEditEmbedAsync(channel, prevMessage, embed).ConfigureAwait(false);
            if (newId == 0)
            {
                await raw.Channel.SendMessageAsync(
                    "Couldn't post WTB board - channel not found / bot lacks permission. See server log.")
                    .ConfigureAwait(false);
                return;
            }

            DiscordUserState.SetWtbRef(caller, channel, newId);
            ServerLog.Info($"Discord: WTB board updated for {caller} ({entries?.Count ?? 0} entries)");

            string verb = prevMessage == 0 ? "Posted" : "Refreshed";
            int    n    = entries?.Count ?? 0;
            await raw.Channel.SendMessageAsync(
                $"{verb} WTB board ({n} entr{(n == 1 ? "y" : "ies")}).")
                .ConfigureAwait(false);
        }

        // -- delete --

        private static async Task HandleDelete(SocketMessage raw, string caller)
        {
            DiscordUserState.GetWtb(caller, out ulong prevChannel, out ulong prevMessage, out _, out _);
            if (prevChannel == 0 || prevMessage == 0)
            {
                await raw.Channel.SendMessageAsync("You don't have an active WTB board to delete.")
                    .ConfigureAwait(false);
                return;
            }
            bool ok = await DiscordBridge.DeleteMessageAsync(prevChannel, prevMessage).ConfigureAwait(false);
            DiscordUserState.ClearWtbBoard(caller);
            ServerLog.Info($"Discord: WTB board deleted for {caller}");
            await raw.Channel.SendMessageAsync(
                ok ? "WTB board removed." : "Couldn't find the message - state cleared regardless.")
                .ConfigureAwait(false);
        }

        // -- tagline --

        private static async Task HandleTagline(SocketMessage raw, string caller, string[] parts)
        {
            if (parts.Length < 3)
            {
                await raw.Channel.SendMessageAsync(
                    "Usage: `!kmh-wtb tagline <text>` or `!kmh-wtb tagline clear`")
                    .ConfigureAwait(false);
                return;
            }
            string second = parts[2].ToLowerInvariant();
            if (second == "clear" || second == "none" || second == "off")
            {
                DiscordUserState.SetWtbTagline(caller, "");
                await raw.Channel.SendMessageAsync("WTB tagline cleared.").ConfigureAwait(false);
                return;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 2; i < parts.Length; i++)
            {
                if (i > 2) sb.Append(' ');
                sb.Append(parts[i]);
            }
            string text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                await raw.Channel.SendMessageAsync("Tagline can't be empty.").ConfigureAwait(false);
                return;
            }
            if (text.Length > 200) text = text.Substring(0, 200);
            DiscordUserState.SetWtbTagline(caller, text);
            await raw.Channel.SendMessageAsync("WTB tagline updated. Run `!kmh-wtb` to refresh.")
                .ConfigureAwait(false);
        }

        // -- helpers --

        private static string ResolveLinkedUsername(SocketMessage raw)
        {
            if (raw?.Author == null) return null;
            ulong  id      = raw.Author.Id;
            string display = ResolveDiscordDisplay(raw.Author);
            string byId    = LinkedAccountsStore.FindUsernameByDiscordId(id);
            if (!string.IsNullOrEmpty(byId)) return byId;
            return string.IsNullOrEmpty(display) ? null : LinkedAccountsStore.FindUsernameByDiscord(display);
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
