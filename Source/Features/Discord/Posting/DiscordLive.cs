using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Maintenance;

namespace KMHServerAddon.Features.Discord
{
    internal sealed class DiscordLivePost
    {
        public string Title = "";
        public string Content = "";
        public List<(string Name, byte[] Bytes)> Files;
    }

    // Where live messages go: the bridge in production, a recorder in the self-tests.
    internal interface IDiscordLiveSink
    {
        Task<ulong> PostOrEditAsync(ulong channelId, ulong messageId, string content,
                                    IReadOnlyList<(string Name, byte[] Bytes)> files, bool postIfMissing);
        Task<ulong> PostAsync(ulong channelId, string content, IReadOnlyList<ulong> pingRoles);
    }

    internal sealed class DiscordBridgeSink : IDiscordLiveSink
    {
        public static readonly DiscordBridgeSink Instance = new DiscordBridgeSink();

        public Task<ulong> PostOrEditAsync(ulong channelId, ulong messageId, string content,
                                           IReadOnlyList<(string Name, byte[] Bytes)> files, bool postIfMissing)
            => DiscordBridge.PostOrEditFilesAsync(channelId, messageId, content, files, postIfMissing);

        public Task<ulong> PostAsync(ulong channelId, string content, IReadOnlyList<ulong> pingRoles)
            => DiscordBridge.SendToChannelAsync(channelId, content, pingRoles);
    }

    // Each scheduler job only kicks off the work, one at a time, so a slow upload never holds the scheduler.
    internal static class DiscordLive
    {
        internal static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(25);

        private sealed class SingleFlight
        {
            private int _busy;
            public void Kick(string what, Func<Task> work)
            {
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
                Task.Run(async () =>
                {
                    try { await work().ConfigureAwait(false); }
                    catch (Exception ex) { Warn(what, ex.Message); }
                    finally { Volatile.Write(ref _busy, 0); }
                });
            }
        }

        private static readonly SingleFlight Ticker = new SingleFlight(), Flusher = new SingleFlight();
        // Tick and flush both write the live state file, so they take turns.
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static volatile bool _on;
        private static volatile bool _refreshBoards;
        private static volatile bool _reloadState = true;
        private static DiscordLeaderboardState _boards = new DiscordLeaderboardState();
        private static DiscordLiveState _live = new DiscordLiveState();

        public static bool On => _on;

        // Never waits for the gate: a reload can come from the game's own thread, and an upload may be holding it.
        public static void Start()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            _reloadState = true;
            DiscordShowcaseSweep.ResetSchedule(DateTime.UtcNow);
            _on = cfg != null && cfg.IsEnabled;
            if (!_on) return;

            // Registered by name, so a reload replaces both jobs rather than running two of either.
            KmhScheduler.Register("discord-live", TickEvery, () => Ticker.Kick("live tick", TickAsync),
                                  KmhJobKind.Infrastructure, TimeSpan.FromSeconds(30));
            KmhScheduler.Register("discord-activity", FlushEvery, () => Flusher.Kick("activity flush", FlushAsync),
                                  KmhJobKind.Infrastructure, FlushEvery);
            ServerLog.Info(Describe(cfg));
        }

        // A moderator's "post the leaderboard now": today's boards update on the next pass, created if missing.
        public static void RefreshBoardsNow()
        {
            _refreshBoards = true;
            Ticker.Kick("live tick", TickAsync);
        }

        internal static string Describe(DiscordConfig cfg)
        {
            var parts = new List<string>();
            if (cfg.LeaderboardChannelId != 0 && cfg.LeaderboardIntervalMinutes >= 1 && cfg.LiveLeaderboardBoards.Length > 0)
                parts.Add($"leaderboard ({string.Join(", ", cfg.LiveLeaderboardBoards)}) every {DiscordDayWindow.Every(cfg.LeaderboardIntervalMinutes)}");
            if (cfg.MarketplaceChannelId != 0 && cfg.MarketBoardIntervalMinutes >= 1)
                parts.Add($"market board every {DiscordDayWindow.Every(cfg.MarketBoardIntervalMinutes)}");
            if (cfg.PostStatus) parts.Add("admin status");
            if (cfg.PostAlerts) parts.Add("alerts" + (cfg.AlertPingModerators ? " (pinging moderators)" : ""));
            return parts.Count == 0
                ? "Discord: no live messages (no leaderboard, marketplace or admin channel set)"
                : $"Discord: live messages - {string.Join(", ", parts)}; the day turns over at {cfg.LiveDayStartUtcHour:00}:00 UTC";
        }

        private static Task TickAsync()
        {
            if (!_on || !DiscordBridge.Ready) return Task.CompletedTask;
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null) return Task.CompletedTask;
            bool refresh = _refreshBoards;
            _refreshBoards = false;
            return RunTickAsync(cfg, DateTime.UtcNow, DiscordBridgeSink.Instance, DiscordBridge.CanEdit, refresh);
        }

        // Runs against the process's own state, which is what saves it; a self-test drives it with a recorder.
        internal static async Task RunTickAsync(DiscordConfig cfg, DateTime now, IDiscordLiveSink sink, bool canEdit, bool refresh)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                LoadIfAsked();
                try
                {
                    if (await DiscordLeaderboardPoster.TickAsync(cfg, _boards, now, sink, canEdit, refresh).ConfigureAwait(false))
                        _boards.Save();
                }
                catch (Exception ex) { Warn("leaderboard", ex.Message); }
                bool changed = false;
                try { changed |= await DiscordShowcaseSweep.TickAsync(cfg, _live, now, sink, canEdit).ConfigureAwait(false); }
                catch (Exception ex) { Warn("market board", ex.Message); }
                try { changed |= await DiscordAdminStatus.TickAsync(cfg, _live, now, sink, canEdit).ConfigureAwait(false); }
                catch (Exception ex) { Warn("status", ex.Message); }
                if (changed) _live.Save();
            }
            finally { Gate.Release(); }
        }

        private static async Task FlushAsync()
        {
            if (!_on || !DiscordBridge.Ready || !DiscordActivityLog.HasWork) return;
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null) return;
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                LoadIfAsked();
                if (await DiscordActivityLog.FlushAsync(_live, DateTime.UtcNow, cfg.LiveDayStartUtcHour,
                                                        DiscordBridgeSink.Instance, DiscordBridge.CanEdit).ConfigureAwait(false))
                    _live.Save();
            }
            finally { Gate.Release(); }
        }

        // Called with the gate held, so no tick can save the old state over what was just read back.
        private static void LoadIfAsked()
        {
            if (!_reloadState) return;
            _reloadState = false;
            _boards = DiscordLeaderboardState.LoadOrDefault();
            _live = DiscordLiveState.LoadOrDefault();
        }

        private static void Warn(string what, string message)
        {
            if (KmhLogThrottle.Allow("discord.live." + what, TimeSpan.FromMinutes(15), DateTime.UtcNow.Ticks, out int more))
                ServerLog.Warn($"Discord: {what} update failed: {message}{(more > 0 ? $" (+{more} more since)" : "")}");
        }

        // "fresh" tells the final edit whether it runs at the window's turn or long after (a server that was down).
        internal static async Task<bool> TickSlotAsync(DiscordLiveSlot slot, ulong channelId, DateTime now,
                                                       (DateTime Start, DateTime End) window, TimeSpan every, bool force,
                                                       Func<DiscordLivePost> live, Func<bool, DiscordLivePost> final,
                                                       IDiscordLiveSink sink)
        {
            bool changed = false;
            // A moved channel leaves its old message as it was; the new channel starts its own.
            if (slot.MessageId != 0 && slot.ChannelId != channelId) { slot.Reset(); changed = true; }

            if (slot.MessageId != 0 && slot.WindowStartTicks != window.Start.Ticks)
            {
                bool fresh = slot.WindowEndTicks > 0 && now.Ticks - slot.WindowEndTicks <= (every + TimeSpan.FromMinutes(5)).Ticks;
                DiscordLivePost f = final(fresh);
                // A failed final edit is tolerated: the old message keeps its live line, and the new day still opens.
                if (f != null) await sink.PostOrEditAsync(channelId, slot.MessageId, f.Content, f.Files, false).ConfigureAwait(false);
                ServerLog.Diag($"Discord: live message {slot.Key} finished for its window");
                slot.Reset();
                changed = true;
            }

            bool due = force || slot.MessageId == 0 || now.Ticks - slot.LastUpdatedTicks >= every.Ticks;
            if (!due) return changed;
            DiscordLivePost p = live();
            if (p == null) return changed;

            ulong id = await sink.PostOrEditAsync(channelId, slot.MessageId, p.Content, p.Files, true).ConfigureAwait(false);
            // State is left as it was, so the next tick retries rather than orphaning the message.
            if (id == 0) return changed;
            if (slot.MessageId != id) ServerLog.Diag($"Discord: live message {slot.Key} posted for the window starting {window.Start:yyyy-MM-dd HH:mm} UTC");
            slot.ChannelId = channelId;
            slot.MessageId = id;
            slot.WindowStartTicks = window.Start.Ticks;
            slot.WindowEndTicks = window.End.Ticks;
            slot.LastUpdatedTicks = now.Ticks;
            slot.Title = p.Title ?? "";
            slot.LastContent = p.Content ?? "";
            return true;
        }

        // A finished text message keeps its body; only the first line changes from live to final.
        internal static DiscordLivePost Finished(DiscordLiveSlot slot, string header)
        {
            string body = slot?.LastContent ?? "";
            int nl = body.IndexOf('\n');
            return new DiscordLivePost
            {
                Title = slot?.Title ?? "",
                Content = DiscordCompactText.Cap(header + (nl < 0 ? "" : body.Substring(nl)), DiscordCompactText.MaxChars),
            };
        }

        // What a restart does to this state: read it back from disk.
        internal static void ReloadStateForTest()
        {
            _reloadState = false;
            _boards = DiscordLeaderboardState.LoadOrDefault();
            _live = DiscordLiveState.LoadOrDefault();
        }

        internal static DiscordLeaderboardState BoardsForTest => _boards;
    }
}
