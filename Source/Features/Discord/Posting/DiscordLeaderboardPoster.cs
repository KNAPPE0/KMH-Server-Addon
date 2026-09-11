using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // Edits one live message in place and rolls it over periodically, so the channel gets an archive rather than a post per tick.
    internal static class DiscordLeaderboardPoster
    {
        private static CancellationTokenSource _cts;

        public static void Start()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null || !cfg.IsEnabled)
            {
                return;
            }
            if (cfg.LeaderboardChannelId == 0)
            {
                ServerLog.Info("Discord: leaderboard auto-post disabled (DiscordLeaderboardChannelId = 0)");
                return;
            }
            if (cfg.LeaderboardIntervalMinutes < 1)
            {
                ServerLog.Warn("Discord: leaderboard_interval_minutes < 1 - auto-post disabled");
                return;
            }

            // A reload can call Start again, and two loops would post twice per tick.
            try { _cts?.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            Task.Run(() => Loop(_cts.Token));
            ServerLog.Info(
                $"Discord: leaderboard auto-post every {cfg.LeaderboardIntervalMinutes}m " +
                $"to channel {cfg.LeaderboardChannelId} (top {cfg.LeaderboardTopCount})");
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = null;
        }

        private static async Task Loop(CancellationToken ct)
        {
            DiscordLeaderboardState state = DiscordLeaderboardState.LoadOrDefault();

            // Waits for the gateway to reach Ready, since posting before that just fails.
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                DiscordConfig cfg = DiscordBridge.Config;
                int intervalMin = cfg?.LeaderboardIntervalMinutes ?? 60;
                if (intervalMin < 1) intervalMin = 60;

                try
                {
                    if (cfg != null && cfg.LeaderboardChannelId != 0)
                    {
                        await Tick(cfg, state).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Discord: leaderboard tick failed: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromMinutes(intervalMin), ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
            }
        }

        private static async Task Tick(DiscordConfig cfg, DiscordLeaderboardState state)
        {
            int topN = cfg.LeaderboardTopCount;
            if (topN < 1)  topN = 10;
            if (topN > 25) topN = 25;

            // Zero hours means never roll over, which is why every check below is guarded on it.
            int rolloverHours = cfg.LeaderboardRolloverHours;
            if (rolloverHours < 0) rolloverHours = 0;
            DateTime now = DateTime.UtcNow;
            bool rolloverDue = rolloverHours > 0
                && state.LiveStartedUtcTicks > 0
                && state.LiveMessageId != 0
                && (now - new DateTime(state.LiveStartedUtcTicks, DateTimeKind.Utc)).TotalHours >= rolloverHours;

            DateTime started = state.LiveStartedUtcTicks > 0
                ? new DateTime(state.LiveStartedUtcTicks, DateTimeKind.Utc) : now;

            if (rolloverDue)
            {
                // A failed edit is tolerated: the old message keeps its live banner, but a fresh one still opens.
                Embed[] finals = BuildBoards(topN, isFinalized: true, started, null, 0);
                if (finals != null)
                {
                    await DiscordBridge.PostOrEditEmbedsAsync(cfg.LeaderboardChannelId, state.LiveMessageId, finals)
                        .ConfigureAwait(false);
                }
                state.LiveMessageId       = 0;
                state.LiveStartedUtcTicks = 0;
                started = now;
                state.Save();
                ServerLog.Info("Discord: leaderboard rolled over - previous live message finalized");
            }

            DateTime? resetsAt = rolloverHours > 0 ? started.AddHours(rolloverHours) : (DateTime?)null;
            Embed[] live = BuildBoards(topN, isFinalized: false, started, resetsAt, cfg.LeaderboardIntervalMinutes);
            if (live == null)
            {
                // Quiet rather than empty, or a first-run server posts a blank board every hour.
                return;
            }

            ulong newId = await DiscordBridge.PostOrEditEmbedsAsync(cfg.LeaderboardChannelId, state.LiveMessageId, live)
                .ConfigureAwait(false);
            if (newId == 0)
            {
                // State is left untouched, so the next tick retries rather than orphaning the live message.
                ServerLog.Warn("Discord: leaderboard post/edit returned 0 - see prior warnings");
                return;
            }

            if (state.LiveStartedUtcTicks == 0)
            {
                state.LiveStartedUtcTicks = now.Ticks;
            }
            state.LiveMessageId       = newId;
            state.LastUpdatedUtcTicks = now.Ticks;
            state.Save();
        }

        // Null means no player data at all, which the caller treats as "post nothing".
        private static Embed[] BuildBoards(int topN, bool isFinalized,
                                           DateTime startedUtc, DateTime? resetsAtUtc, int updateEveryMin)
        {
            Embed players = DiscordLeaderboardBuilder.Build(topN, DiscordLeaderboardBuilder.DefaultSort,
                                                            isFinalized, startedUtc, resetsAtUtc, updateEveryMin);
            if (players == null) return null;
            Embed guilds = DiscordLeaderboardBuilder.BuildGuilds(topN, DiscordLeaderboardBuilder.DefaultGuildSort, isFinalized);
            return guilds == null ? new[] { players } : new[] { players, guilds };
        }
    }
}
