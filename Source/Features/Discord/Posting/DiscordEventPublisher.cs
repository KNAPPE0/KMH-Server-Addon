using System;
using System.Linq;
using Discord;
using KMH.Sdk.Server.Events;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Extensibility;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Util;

namespace KMHServerAddon.Features.Discord
{
    // Turns in-game events into branded Discord embeds by subscribing to the KmhEventBus, so no feature needs to know
    // Discord exists. Each post is gated by its Post* toggle + a configured channel, and is fire-and-forget via
    // DiscordBridge so a slow/down Discord never stalls the game thread. Player join/leave stay with
    // DiscordPlayerAnnouncer (it dedups those), so we don't double up here.
    internal static class DiscordEventPublisher
    {
        private static bool _started;
        private static bool _announcedStart;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            KmhEventBus bus = KmhEventBus.Instance;
            bus.MarketplacePost += OnMarketplacePost;
            bus.MarketplaceBuy  += OnMarketplaceBuy;
            bus.SiteChanged     += OnSiteChanged;
            bus.QuestApproved   += OnQuestApproved;
            bus.GuildChanged    += OnGuildChanged;
            ServerLog.Info("Discord: event publisher subscribed to the KMH event bus");
        }

        // Called from DiscordBridge.OnReady once the bot is connected. Posts a single "server online" announcement
        // per process (Ready can re-fire on reconnects, so we guard it)
        public static void OnBridgeReady()
        {
            if (_announcedStart) return;
            DiscordConfig cfg = DiscordBridge.Config;
            if (!ServerEventsOn(cfg)) { _announcedStart = true; return; }
            ulong ch = cfg.AnnouncementsChannelId;
            if (ch == 0) { _announcedStart = true; return; }

            _announcedStart = true;
            string name = string.IsNullOrEmpty(cfg.Branding?.DisplayName) ? "The server" : cfg.Branding.DisplayName;
            DiscordBridge.PostEmbedToChannel(ch,
                KmhEmbedBuilder.Base(cfg, "🟢 Server online", $"**{name}** is up and accepting connections."),
                DiscordIcons.Logo);
        }

        // -------- event handlers --------

        private static void OnMarketplacePost(MarketplacePostEvent e)
        {
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!MarketEventsOn(cfg)) return;
                // Only surface public listings - guild-only posts shouldn't leak to a shared channel
                if (!string.Equals(e.Visibility, "public", StringComparison.OrdinalIgnoreCase)) return;
                ulong ch = cfg.MarketplaceChannelId;
                if (ch == 0) return;

                EmbedBuilder eb = KmhEmbedBuilder.Base(cfg, "🛒 New listing")
                    .AddField("Item",   $"{e.Qty}× {DiscordText.Escape(ItemLabelCache.LabelFor(e.ItemDefName))}", true)
                    .AddField("Price",  $"{SilverFmt.Format(e.UnitPriceSilver)}/ea", true)
                    .AddField("Seller", Name(e.SellerUsername), true);
                DiscordBridge.PostEmbedToChannel(ch, eb, DiscordIcons.Marketplace);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: marketplace-post embed failed: {ex.Message}"); }
        }

        private static void OnMarketplaceBuy(MarketplaceBuyEvent e)
        {
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!MarketEventsOn(cfg)) return;
                ulong ch = cfg.MarketplaceChannelId;
                if (ch == 0) return;

                EmbedBuilder eb = KmhEmbedBuilder.Base(cfg, "🪙 Item sold")
                    .AddField("Item",  $"{e.QtyBought}× {DiscordText.Escape(ItemLabelCache.LabelFor(e.ItemDefName))}", true)
                    .AddField("Total", SilverFmt.Format(e.TotalSilverPaid), true)
                    .AddField("Buyer → Seller", $"{Name(e.BuyerUsername)} → {Name(e.SellerUsername)}", true);
                DiscordBridge.PostEmbedToChannel(ch, eb, DiscordIcons.Marketplace);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: marketplace-buy embed failed: {ex.Message}"); }
        }

        private static void OnSiteChanged(SiteChangedEvent e)
        {
            try
            {
                if (e.Reason != "built" && e.Reason != "removed") return;
                DiscordConfig cfg = DiscordBridge.Config;
                if (cfg == null || !cfg.IsEnabled || !cfg.PostSiteEvents) return;
                ulong ch = cfg.SiteEventsChannelId;
                if (ch == 0) return;

                if (e.Reason == "removed")
                {
                    DiscordBridge.PostEmbedToChannel(ch, KmhEmbedBuilder.Base(cfg, "🏚️ Site removed",
                        $"A site on tile **{e.Tile}** (owner **{Name(e.OwnerUsername)}**) was removed."),
                        DiscordIcons.SiteDestroyed);
                    return;
                }

                // built - name what it produces. Use the unfiltered by-tile lookup, NOT a visibility-scoped snapshot:
                // a guild-only site is invisible to an empty caller, which used to drop the Produces field entirely.
                string produces = "";
                try
                {
                    SiteEntry s = SiteStore.GetForApi(e.Tile);
                    if (s != null) produces = $"{s.BaseAmountPerCycle}× {DiscordText.Escape(ItemLabelCache.LabelFor(s.ItemDefName))}";
                }
                catch { /* lookup is decoration only */ }

                EmbedBuilder eb = KmhEmbedBuilder.Base(cfg, "🏗️ New site built")
                    .AddField("Owner", Name(e.OwnerUsername), true)
                    .AddField("Tile",  e.Tile.ToString(), true);
                if (!string.IsNullOrEmpty(produces)) eb.AddField("Produces", produces, true);
                DiscordBridge.PostEmbedToChannel(ch, eb, DiscordIcons.SiteCreated);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: site embed failed: {ex.Message}"); }
        }

        private static void OnQuestApproved(QuestApprovedEvent e)
        {
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!ServerEventsOn(cfg)) return;
                ulong ch = cfg.AnnouncementsChannelId;
                if (ch == 0) return;

                EmbedBuilder eb = KmhEmbedBuilder.Base(cfg, "✅ Quest completed",
                        $"**{Name(e.ClaimerUsername)}** completed a quest for **{Name(e.PosterUsername)}**.")
                    .AddField("Bounty", SilverFmt.Format(e.BountyPaidSilver), true);
                DiscordBridge.PostEmbedToChannel(ch, eb, DiscordIcons.Quest);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: quest embed failed: {ex.Message}"); }
        }

        private static void OnGuildChanged(GuildChangedEvent e)
        {
            try
            {
                if (e.Reason != "created") return;
                DiscordConfig cfg = DiscordBridge.Config;
                if (!ServerEventsOn(cfg)) return;
                ulong ch = cfg.AnnouncementsChannelId;
                if (ch == 0) return;

                DiscordBridge.PostEmbedToChannel(ch, KmhEmbedBuilder.Base(cfg, "🏰 New guild",
                    $"**{Name(e.Actor)}** founded the guild **{Name(e.GuildName)}**."),
                    DiscordIcons.Guild);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: guild embed failed: {ex.Message}"); }
        }

        // -------- gating helpers --------

        private static bool MarketEventsOn(DiscordConfig cfg)
            => cfg != null && cfg.IsEnabled && cfg.PostMarketplaceEvents;

        private static bool ServerEventsOn(DiscordConfig cfg)
            => cfg != null && cfg.IsEnabled && cfg.PostServerEvents;

        // Escapes too - these names land in markdown-rendered embed descriptions/fields.
        private static string Name(string s) => DiscordText.Escape(string.IsNullOrEmpty(s) ? "?" : s);
    }
}
