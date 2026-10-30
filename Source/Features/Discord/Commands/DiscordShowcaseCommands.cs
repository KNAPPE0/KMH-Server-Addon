using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    // The message is edited in place rather than re-posted, so a showcase does not spam the channel.
    internal static class DiscordShowcaseCommands
    {
        public static async Task<bool> TryHandleAsync(KmhDiscordMessage raw, string cmd, string[] parts)
        {
            switch (cmd)
            {
                case "kmh-showcase":
                case "kmh-shopfront":
                case "kmh-myshop":
                    await HandleShowcase(raw, parts).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }

        private static async Task HandleShowcase(KmhDiscordMessage raw, string[] parts)
        {
            string caller = DiscordCommandUtil.LinkedUsername(raw);
            if (string.IsNullOrEmpty(caller))
            {
                await raw.Channel.SendMessageAsync(
                    "Link your Discord first to publish a showcase. " +
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
            ulong showcaseChannel = cfg?.ShowcaseChannelId ?? 0;
            if (showcaseChannel == 0)
            {
                await raw.Channel.SendMessageAsync(
                    "Showcase channel is not configured. Ask an admin to set " +
                    "`Channels.Marketplace` in `Config/Discord/DiscordConfig.json`.")
                    .ConfigureAwait(false);
                return;
            }

            string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "delete":
                case "remove":
                case "clear":
                    await HandleDelete(raw, caller).ConfigureAwait(false);
                    return;
                case "tagline":
                    await HandleTagline(raw, caller, parts).ConfigureAwait(false);
                    return;
                case "":
                case "update":
                case "refresh":
                    await HandleUpdate(raw, caller, showcaseChannel).ConfigureAwait(false);
                    return;
                default:
                    await raw.Channel.SendMessageAsync(
                        "Usage: `!kmh-showcase` (post/refresh) · `!kmh-showcase delete` · " +
                        "`!kmh-showcase tagline <text|clear>`")
                        .ConfigureAwait(false);
                    return;
            }
        }

        private static async Task HandleUpdate(KmhDiscordMessage raw, string caller, ulong showcaseChannel)
        {
            // The public view: a showcase is posted where the whole server reads it, so guild-only listings stay out.
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot("");
            List<MarketplaceListing> mine = DiscordShowcaseBuilder.ListingsBy(snap?.Listings, caller);

            DiscordUserState.GetShowcase(caller, out ulong prevChannel, out ulong prevMessage, out string tagline);

            // The old message sits in the previous channel, so its id is dropped rather than edited here.
            if (prevChannel != 0 && prevChannel != showcaseChannel)
            {
                prevMessage = 0;
            }

            string text = DiscordCompactText.Render(DiscordShowcaseBuilder.Build(caller, tagline, mine));

            ulong newId = await DiscordBridge.PostOrEditTextAsync(showcaseChannel, prevMessage, text)
                .ConfigureAwait(false);
            if (newId == 0)
            {
                await raw.Channel.SendMessageAsync(
                    "Couldn't post showcase - channel not found / bot lacks permission. See server log.")
                    .ConfigureAwait(false);
                return;
            }

            DiscordUserState.SetShowcase(caller, showcaseChannel, newId, tagline);
            ServerLog.Info($"Discord: showcase updated for {caller} ({mine.Count} listings)");

            string verb = prevMessage == 0 ? "Posted" : "Refreshed";
            await raw.Channel.SendMessageAsync(
                $"{verb} showcase ({mine.Count} listing{(mine.Count == 1 ? "" : "s")}).")
                .ConfigureAwait(false);
        }

        private static async Task HandleDelete(KmhDiscordMessage raw, string caller)
        {
            DiscordUserState.GetShowcase(caller, out ulong prevChannel, out ulong prevMessage, out _);
            if (prevChannel == 0 || prevMessage == 0)
            {
                await raw.Channel.SendMessageAsync("You don't have an active showcase to delete.")
                    .ConfigureAwait(false);
                return;
            }
            bool ok = await DiscordBridge.DeleteMessageAsync(prevChannel, prevMessage).ConfigureAwait(false);
            DiscordUserState.ClearShowcase(caller);
            ServerLog.Info($"Discord: showcase deleted for {caller} (message {prevMessage})");
            await raw.Channel.SendMessageAsync(
                ok ? "Showcase removed." : "Couldn't find the message to delete - state cleared regardless.")
                .ConfigureAwait(false);
        }

        private static async Task HandleTagline(KmhDiscordMessage raw, string caller, string[] parts)
        {
            if (parts.Length < 3)
            {
                await raw.Channel.SendMessageAsync(
                    "Usage: `!kmh-showcase tagline <text>` or `!kmh-showcase tagline clear`\n" +
                    "The tagline appears italicized at the top of your showcase " +
                    "(e.g. \"DM me to negotiate bulk discounts\").")
                    .ConfigureAwait(false);
                return;
            }
            string second = parts[2].ToLowerInvariant();
            if (second == "clear" || second == "none" || second == "off")
            {
                DiscordUserState.SetTagline(caller, "");
                await raw.Channel.SendMessageAsync("Tagline cleared. Run `!kmh-showcase` to refresh your post.")
                    .ConfigureAwait(false);
                return;
            }

            string text = DiscordCommandUtil.JoinFrom(parts, 2).Trim();
            if (text.Length == 0)
            {
                await raw.Channel.SendMessageAsync("Tagline can't be empty.").ConfigureAwait(false);
                return;
            }
            if (text.Length > 200) text = text.Substring(0, 200);

            DiscordUserState.SetTagline(caller, text);
            await raw.Channel.SendMessageAsync("Tagline updated. Run `!kmh-showcase` to refresh your post.")
                .ConfigureAwait(false);
        }
    }
}
