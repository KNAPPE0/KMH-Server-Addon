using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;

namespace KMHServerAddon.Features.Discord
{
    // Periodic refresh of every active !kmh-showcase post so embeds stay current with treasury moves. Cadence via
    // showcase_sweep_interval_ minutes (default 30m, 0 disables). Edit failure clears the cached message id so the
    // next user action re-posts fresh
    internal static class DiscordShowcaseSweep
    {
        private static CancellationTokenSource _cts;

        public static void Start()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null || !cfg.IsEnabled) return; // bridge logs its own disabled state
            if (cfg.ShowcaseChannelId == 0)
            {
                ServerLog.Info("Discord: showcase sweep disabled (DiscordShowcaseChannelId = 0)");
                return;
            }
            if (cfg.ShowcaseSweepIntervalMinutes < 1)
            {
                ServerLog.Info("Discord: showcase sweep disabled (showcase_sweep_interval_minutes < 1)");
                return;
            }

            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = new CancellationTokenSource();
            Task.Run(() => Loop(_cts.Token));
            ServerLog.Info($"Discord: showcase sweep every {cfg.ShowcaseSweepIntervalMinutes}m");
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = null;
        }

        private static async Task Loop(CancellationToken ct)
        {
            // First sweep waits the full interval - gives the rest of bootstrap (Discord login, store loads, first
            // client connects to push item labels) time to settle
            while (!ct.IsCancellationRequested)
            {
                DiscordConfig cfg = DiscordBridge.Config;
                int intervalMin = cfg?.ShowcaseSweepIntervalMinutes ?? 30;
                if (intervalMin < 1) intervalMin = 30;

                try { await Task.Delay(TimeSpan.FromMinutes(intervalMin), ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }

                try
                {
                    await RunSweep().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Discord: showcase sweep tick failed: {ex.Message}");
                }
            }
        }

        private static async Task RunSweep()
        {
            List<string> users = DiscordUserState.ListUsersWithShowcase();
            if (users.Count == 0) return;

            int refreshed = 0;
            int cleared   = 0;
            foreach (string username in users)
            {
                DiscordUserState.GetShowcase(username, out ulong channelId, out ulong messageId, out string tagline);
                if (channelId == 0 || messageId == 0) continue;

                MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(username);
                List<MarketplaceListing> mine = new List<MarketplaceListing>();
                if (snap?.Listings != null)
                {
                    foreach (MarketplaceListing l in snap.Listings)
                    {
                        if (string.Equals(l.SellerUsername, username, StringComparison.OrdinalIgnoreCase))
                            mine.Add(l);
                    }
                }

                Embed embed = DiscordShowcaseBuilder.Build(username, tagline, mine);
                ulong newId = await DiscordBridge.PostOrEditEmbedAsync(channelId, messageId, embed)
                    .ConfigureAwait(false);
                if (newId == 0)
                {
                    // Edit failed AND fresh post failed - clear the stale ref so the user's next !kmh-showcase
                    // posts cleanly rather than trying to edit something we can't see
                    DiscordUserState.ClearShowcase(username);
                    cleared++;
                    continue;
                }

                if (newId != messageId)
                {
                    // Fell through to fresh post (e.g., original message was deleted manually). Persist the new id
                    DiscordUserState.SetShowcase(username, channelId, newId, tagline);
                }
                refreshed++;
            }

            if (refreshed > 0 || cleared > 0)
            {
                ServerLog.Verbose(
                    $"Discord: showcase sweep refreshed {refreshed}, cleared {cleared} (of {users.Count})");
            }
        }
    }
}
