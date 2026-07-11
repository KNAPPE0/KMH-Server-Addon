using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // Periodic auto-poster for the player leaderboard. Edits a single live message in place (state persisted to
    // discord_leaderboard_state .json) and rolls over every leaderboard_rollover_hours so the channel gets a daily
    // archive trail instead of one post per tick. Self-exits when the bridge is off or the config is invalid
    internal static class DiscordLeaderboardPoster
    {
        private static CancellationTokenSource _cts;

        public static void Start()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null || !cfg.IsEnabled)
            {
                // Bridge will log its own disabled state; no extra noise.
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

            // Cancel any previous loop (defensive - Start is only called once from Main, but we may invoke it again
            // from a future reload-config command)
            try { _cts?.Cancel(); } catch { /* ignore */ }
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

            // Warm-up (30s) before the first tick so the Discord connection + Ready have a chance to settle.
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

            // Detect rollover. LiveStartedUtcTicks is stamped on the very first post; rollover hours = 0 means
            // "never rollover"
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
                // Edit the current live message one last time with the finalized banner, then drop our ref so the
                // next post creates a fresh live message underneath. Edit failure here is non-fatal - worst case
                // the previous message stays "live"-tagged in chat history, and we still open a fresh one
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

            // Build the live player + guild boards for this tick and edit-or-post as one message.
            DateTime? resetsAt = rolloverHours > 0 ? started.AddHours(rolloverHours) : (DateTime?)null;
            Embed[] live = BuildBoards(topN, isFinalized: false, started, resetsAt, cfg.LeaderboardIntervalMinutes);
            if (live == null)
            {
                // No data yet - stay quiet so first-run servers don't spam an empty embed every hour
                return;
            }

            ulong newId = await DiscordBridge.PostOrEditEmbedsAsync(cfg.LeaderboardChannelId, state.LiveMessageId, live)
                .ConfigureAwait(false);
            if (newId == 0)
            {
                // Failed to post + failed to edit - leave state as-is so the next tick retries. Loud log so it's
                // debuggable
                ServerLog.Warn("Discord: leaderboard post/edit returned 0 - see prior warnings");
                return;
            }

            // First-ever post stamps the rollover-start clock.
            if (state.LiveStartedUtcTicks == 0)
            {
                state.LiveStartedUtcTicks = now.Ticks;
            }
            state.LiveMessageId       = newId;
            state.LastUpdatedUtcTicks = now.Ticks;
            state.Save();
        }

        // Player board + guild board as one message; guild board is skipped while no guilds exist. Null when
        // there's no player data at all
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
