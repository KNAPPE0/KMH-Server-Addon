using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordWtbCommands
    {
        // A full board still fits one message.
        private const int MaxEntriesPerUser = 25;

        private const int MaxQty            = 10_000;
        private const int MaxUnitPrice      = 1_000_000;

        public static async Task<bool> TryHandleAsync(KmhDiscordMessage raw, string cmd, string[] parts)
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

        private static async Task HandleWtb(KmhDiscordMessage raw, string[] parts)
        {
            string caller = DiscordCommandUtil.LinkedUsername(raw);
            if (string.IsNullOrEmpty(caller))
            {
                await raw.Channel.SendMessageAsync(
                    "Link your Discord first to use the WTB board. " +
                    "In-game: `/kmh link`, then here: `!kmh-link <code>`.")
                    .ConfigureAwait(false);
                return;
            }
            if (Features.Moderation.BanGate.IsBannedAccount(caller))
            {
                await raw.Channel.SendMessageAsync("Your Colony Nexus account is banned on this server.").ConfigureAwait(false);
                return;
            }
            if (Features.Moderation.KmhEconomyGuard.IsFrozen(caller))
            {
                await raw.Channel.SendMessageAsync(Features.Moderation.KmhEconomyGuard.FrozenRefusal).ConfigureAwait(false);
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

        // The indexes below follow the shape: !kmh-wtb add <item words...> <qty> <price>
        private static async Task HandleAdd(KmhDiscordMessage raw, string caller, string[] parts)
        {
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

            string itemRaw = DiscordCommandUtil.JoinWords(parts, 2, parts.Length - 2).Replace('_', ' ').Trim();

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

        private static async Task HandleRemove(KmhDiscordMessage raw, string caller, string[] parts)
        {
            if (parts.Length < 3)
            {
                await raw.Channel.SendMessageAsync("Usage: `!kmh-wtb remove <item>`").ConfigureAwait(false);
                return;
            }
            string itemRaw = DiscordCommandUtil.JoinFrom(parts, 2).Replace('_', ' ').Trim();
            string defName = ItemLabelCache.ResolveDefNameByQuery(itemRaw, out _);
            if (defName == null) defName = itemRaw.Replace(' ', '_');

            int removed = DiscordUserState.RemoveWtb(caller, defName);
            await raw.Channel.SendMessageAsync(
                removed == 0
                    ? $"No WTB entry for **{DiscordText.Escape(ItemLabelCache.LabelFor(defName))}** found."
                    : $"Removed WTB for **{DiscordText.Escape(ItemLabelCache.LabelFor(defName))}**. Run `!kmh-wtb` to refresh.")
                .ConfigureAwait(false);
        }

        private static async Task HandleList(KmhDiscordMessage raw, string caller)
        {
            DiscordUserState.GetWtb(caller, out _, out _, out string tagline, out List<DiscordUserState.WtbEntry> entries);
            KmhEmbedSpec eb = DiscordWtbBuilder.Build(caller, tagline, entries);
            await raw.Channel.SendMessageAsync(embed: eb).ConfigureAwait(false);
        }

        private static async Task HandleClear(KmhDiscordMessage raw, string caller)
        {
            int n = DiscordUserState.ClearWtb(caller);
            await raw.Channel.SendMessageAsync(
                n == 0
                    ? "Your WTB list was already empty."
                    : $"Cleared {n} WTB entr{(n == 1 ? "y" : "ies")}. Run `!kmh-wtb` to refresh your board.")
                .ConfigureAwait(false);
        }

        private static async Task HandleUpdate(KmhDiscordMessage raw, string caller, ulong channel)
        {
            DiscordUserState.GetWtb(caller, out ulong prevChannel, out ulong prevMessage, out string tagline, out List<DiscordUserState.WtbEntry> entries);

            // The old message sits in the previous channel, so its id is dropped rather than edited here.
            if (prevChannel != 0 && prevChannel != channel) prevMessage = 0;

            string text = DiscordCompactText.Render(DiscordWtbBuilder.Build(caller, tagline, entries));

            ulong newId = await DiscordBridge.PostOrEditTextAsync(channel, prevMessage, text).ConfigureAwait(false);
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

        private static async Task HandleDelete(KmhDiscordMessage raw, string caller)
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

        private static async Task HandleTagline(KmhDiscordMessage raw, string caller, string[] parts)
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
            string text = DiscordCommandUtil.JoinFrom(parts, 2).Trim();
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
    }
}
