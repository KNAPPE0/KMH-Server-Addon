using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Leaderboards;

namespace KMHServerAddon.Features.Discord
{
    // One message per board per day, edited live, so the channel keeps a daily archive instead of a post per tick.
    internal static class DiscordLeaderboardPoster
    {
        internal const string AutoBoard = "auto";

        // Set only by a self-test, so the real tick runs on planted standings and a cheap image.
        internal static Func<LeaderboardEngine.Inputs> InputsForTest;
        internal static Func<LeaderboardBoardDto, (string Name, byte[] Bytes)> ImageForTest;

        // The board a configured word names, from one read of the stores; null for a word that names none.
        internal static LeaderboardBoardDto Resolve(string token, LeaderboardEngine.Inputs inputs)
        {
            LeaderboardCategory c;
            switch (token)
            {
                case AutoBoard:
                case "posted":
                    c = LeaderboardCatalog.Find(DiscordLeaderboardBuilder.PostedSort(inputs));
                    break;
                case "guilds":
                case "guild":
                    c = LeaderboardCatalog.Find(DiscordLeaderboardBuilder.DefaultGuildSort);
                    break;
                default:
                    c = LeaderboardCatalog.Find(token);
                    if (c == null && DiscordLeaderboardBuilder.NormalizeSort(token) is string p && p != DiscordLeaderboardBuilder.DefaultSort)
                        c = LeaderboardCatalog.Find(p);
                    if (c == null && DiscordLeaderboardBuilder.NormalizeGuildSort(token) is string g && g != DiscordLeaderboardBuilder.DefaultGuildSort)
                        c = LeaderboardCatalog.Find(g);
                    break;
            }
            return c == null ? null : LeaderboardEngine.Build(c, inputs);
        }

        internal static (string Name, byte[] Bytes) Image(LeaderboardBoardDto board, DiscordConfig cfg)
        {
            byte[] png = LeaderboardPng.Render(board, new LeaderboardSvg.Options
            {
                Width = LeaderboardSvg.WideWidth,
                ServerName = KmhEmbedBuilder.ServerName(cfg),
                Link = Page(board).Replace("https://", "").Replace("http://", ""),
            });
            return ("leaderboard-" + FileSafe(board.Key) + ".png", png);
        }

        private static string Page(LeaderboardBoardDto board) => Global.KmhGlobalService.LeaderboardPageFor(board?.Key ?? "") ?? "";

        private static string FileSafe(string key)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in key ?? "") sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? char.ToLowerInvariant(ch) : '-');
            return sb.Length == 0 ? "board" : sb.ToString();
        }

        internal static string LiveLine(string title, int everyMinutes, DateTime windowEnd, DateTime now, string page = "")
            => $"**{DiscordText.SafeName(title)}** · live · updates every {DiscordDayWindow.Every(everyMinutes)} · final at {DiscordDayWindow.EndsAt(windowEnd, now)}"
             + (string.IsNullOrEmpty(page) ? "" : $" · [full standings](<{page}>)");

        internal static string FinalLine(string title, DateTime windowStart, DateTime windowEnd)
            => $"**{DiscordText.SafeName(title)}** · final for {DiscordDayWindow.Label(windowStart, windowEnd)}";

        // inputsFor and imageFor let a self-test drive the real tick without the live stores or the renderer.
        internal static async Task<bool> TickAsync(DiscordConfig cfg, DiscordLeaderboardState state, DateTime now, IDiscordLiveSink sink,
                                                   bool canEdit, bool force,
                                                   Func<LeaderboardEngine.Inputs> inputsFor = null,
                                                   Func<LeaderboardBoardDto, (string Name, byte[] Bytes)> imageFor = null)
        {
            ulong ch = cfg.LeaderboardChannelId;
            string[] tokens = cfg.LiveLeaderboardBoards;
            int every = cfg.LeaderboardIntervalMinutes;
            if (ch == 0 || tokens.Length == 0 || every < 1) return false;

            var window = DiscordDayWindow.For(now, cfg.LiveDayStartUtcHour, cfg.LeaderboardRolloverHours);
            LeaderboardEngine.Inputs inputs = null;
            LeaderboardEngine.Inputs Inputs() => inputs ??= (inputsFor ?? InputsForTest ?? LeaderboardEngine.Live)();
            Func<LeaderboardBoardDto, (string, byte[])> image = imageFor ?? ImageForTest ?? (b => Image(b, cfg));

            bool changed = false;
            foreach (string token in tokens)
            {
                DiscordLiveSlot slot = state.Slot(token);
                if (!canEdit) { changed |= await FinalOnlyAsync(cfg, slot, token, ch, window, Inputs, sink).ConfigureAwait(false); continue; }

                changed |= await DiscordLive.TickSlotAsync(slot, ch, now, window, TimeSpan.FromMinutes(every), force,
                    live: () =>
                    {
                        LeaderboardBoardDto b = Resolve(token, Inputs());
                        if (b == null) { Unknown(token); return null; }
                        // Nobody ranked posts nothing; a board that empties mid-day (a season reset) still shows it.
                        if (b.Rows.Count == 0 && slot.MessageId == 0) return null;
                        return new DiscordLivePost
                        {
                            Title = b.Title,
                            Content = LiveLine(b.Title, every, window.End, now, Page(b)),
                            Files = new List<(string, byte[])> { image(b) },
                        };
                    },
                    final: fresh =>
                    {
                        var start = new DateTime(slot.WindowStartTicks, DateTimeKind.Utc);
                        var end = new DateTime(slot.WindowEndTicks > 0 ? slot.WindowEndTicks : slot.WindowStartTicks, DateTimeKind.Utc);
                        var post = new DiscordLivePost { Title = slot.Title, Content = FinalLine(slot.Title, start, end) };
                        // Long after the turn the standings have moved on, so the last image stays as the day's record.
                        LeaderboardBoardDto b = fresh ? Resolve(token, Inputs()) : null;
                        if (b != null && b.Rows.Count > 0) post.Files = new List<(string, byte[])> { image(b) };
                        return post;
                    },
                    sink).ConfigureAwait(false);
            }
            // A board taken out of the config keeps its last message as it was.
            if (state.Boards.RemoveAll(b => Array.IndexOf(tokens, b.Key) < 0) > 0) changed = true;
            return changed;
        }

        // The coordinator returns no message ids, so its servers get each finished day once, as text.
        private static async Task<bool> FinalOnlyAsync(DiscordConfig cfg, DiscordLiveSlot slot, string token, ulong ch,
                                                       (DateTime Start, DateTime End) window, Func<LeaderboardEngine.Inputs> inputs,
                                                       IDiscordLiveSink sink)
        {
            if (slot.WindowStartTicks == window.Start.Ticks) return false;
            bool hadDay = slot.WindowStartTicks != 0;
            var start = new DateTime(slot.WindowStartTicks, DateTimeKind.Utc);
            var end = new DateTime(slot.WindowEndTicks, DateTimeKind.Utc);
            slot.WindowStartTicks = window.Start.Ticks;
            slot.WindowEndTicks = window.End.Ticks;
            if (!hadDay) return true;
            LeaderboardBoardDto b = Resolve(token, inputs());
            if (b == null || b.Rows.Count == 0) return true;
            string rows = DiscordLeaderboardBuilder.TextRows(b, cfg.LeaderboardTopCount, inputs());
            await sink.PostOrEditAsync(ch, 0, DiscordCompactText.Cap(FinalLine(b.Title, start, end) + "\n" + rows, DiscordCompactText.MaxChars),
                                       null, true).ConfigureAwait(false);
            return true;
        }

        private static void Unknown(string token)
        {
            if (KmhLogThrottle.Allow("discord.board." + token, TimeSpan.FromHours(6), DateTime.UtcNow.Ticks, out _))
                ServerLog.Warn($"Discord: Live.LeaderboardBoards names \"{token}\", which is no leaderboard - use auto, guilds or a board key such as trade.");
        }

        // A command's answer: the image and one line where a file can go, the rows as text where it cannot.
        internal static KmhDiscordCommandResult CommandResult(LeaderboardBoardDto board, DiscordConfig cfg, LeaderboardEngine.Inputs inputs, bool files)
        {
            if (board == null || board.Rows.Count == 0) return null;
            if (!files) return KmhDiscordCommandResult.Text(DiscordLeaderboardBuilder.TextRows(board, cfg.LeaderboardTopCount, inputs));
            var r = KmhDiscordCommandResult.Text(CommandLine(board));
            r.Files = new List<(string Name, byte[] Bytes)> { Image(board, cfg) };
            return r;
        }

        internal static string CommandLine(LeaderboardBoardDto board)
        {
            string page = Page(board);
            int shown = Math.Min(10, board.Rows.Count);
            return $"**{DiscordText.SafeName(board.Title)}** · top {shown} of {board.Total} ranked · season {board.Season}"
                 + (page.Length == 0 ? "" : $" · [full standings](<{page}>)");
        }
    }
}
