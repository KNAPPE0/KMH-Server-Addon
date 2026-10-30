using System;
using KMH.Sdk.Server.Events;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Extensibility;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Discord
{
    // Fed by the event bus, so no feature has to know Discord exists; each event is one activity-log line, never a post.
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
            ServerLog.Diag("Discord: event publisher subscribed to the Colony Nexus event bus");
        }

        // Guarded because Ready re-fires on every reconnect, which would announce the server online again.
        public static void OnBridgeReady()
        {
            if (_announcedStart) return;
            DiscordConfig cfg = DiscordBridge.Config;
            if (!ServerEventsOn(cfg)) { _announcedStart = true; return; }
            ulong ch = cfg.AnnouncementsChannelId;
            if (ch == 0) { _announcedStart = true; return; }

            _announcedStart = true;
            DiscordBridge.PostToChannel(ch, $"🟢 **{DiscordText.SafeName(KmhEmbedBuilder.ServerName(cfg))}** is up and accepting connections.");
        }

        private static void OnMarketplacePost(MarketplacePostEvent e)
        {
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!MarketEventsOn(cfg)) return;
                // A guild-only listing must not leak into a channel the whole server can read.
                if (!string.Equals(e.Visibility, "public", StringComparison.OrdinalIgnoreCase)) return;
                DiscordActivityLog.Add(cfg.MarketplaceChannelId, DiscordActivityLog.Listed(DateTime.UtcNow, e.SellerUsername, e.Qty,
                                       ItemLabelCache.LabelFor(e.ItemDefName), e.UnitPriceSilver));
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: marketplace-post line failed: {ex.Message}"); }
        }

        private static void OnMarketplaceBuy(MarketplaceBuyEvent e)
        {
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!MarketEventsOn(cfg)) return;
                DiscordActivityLog.Add(cfg.MarketplaceChannelId, DiscordActivityLog.Bought(DateTime.UtcNow, e.BuyerUsername, e.QtyBought,
                                       ItemLabelCache.LabelFor(e.ItemDefName), e.TotalSilverPaid, e.SellerUsername));
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: marketplace-buy line failed: {ex.Message}"); }
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
                    DiscordActivityLog.Add(ch, DiscordActivityLog.Removed(DateTime.UtcNow, e.OwnerUsername, e.Tile));
                    return;
                }

                // The unfiltered lookup, since a visibility-scoped one hides a guild site from an empty caller.
                string owner = e.OwnerUsername, what = "a site";
                try
                {
                    SiteEntry s = SiteStore.GetForApi(e.Tile);
                    if (s != null)
                    {
                        string controller = SiteOwnership.ControllerLabel(s);
                        if (!string.IsNullOrWhiteSpace(controller)) owner = controller;
                        what = DiscordActivityLog.SiteWhat(s.SiteName, ItemLabelCache.LabelFor(s.ItemDefName), s.BaseAmountPerCycle);
                    }
                }
                catch { /* the lookup is decoration only */ }
                DiscordActivityLog.Add(ch, DiscordActivityLog.Built(DateTime.UtcNow, owner, what));
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: site line failed: {ex.Message}"); }
        }

        private static void OnQuestApproved(QuestApprovedEvent e)
        {
            try
            {
                DiscordConfig cfg = DiscordBridge.Config;
                if (!ServerEventsOn(cfg)) return;
                DiscordActivityLog.Add(cfg.AnnouncementsChannelId,
                    DiscordActivityLog.QuestDone(DateTime.UtcNow, e.ClaimerUsername, e.PosterUsername, e.BountyPaidSilver));
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: quest line failed: {ex.Message}"); }
        }

        private static void OnGuildChanged(GuildChangedEvent e)
        {
            try
            {
                if (e.Reason != "created") return;
                DiscordConfig cfg = DiscordBridge.Config;
                if (!ServerEventsOn(cfg)) return;
                DiscordActivityLog.Add(cfg.AnnouncementsChannelId, DiscordActivityLog.GuildFounded(DateTime.UtcNow, e.Actor, e.GuildName));
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: guild line failed: {ex.Message}"); }
        }

        private static bool MarketEventsOn(DiscordConfig cfg)
            => cfg != null && cfg.IsEnabled && cfg.PostMarketplaceEvents;

        private static bool ServerEventsOn(DiscordConfig cfg)
            => cfg != null && cfg.IsEnabled && cfg.PostServerEvents;
    }
}
