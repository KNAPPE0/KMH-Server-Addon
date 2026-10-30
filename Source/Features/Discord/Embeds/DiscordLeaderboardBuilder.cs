using System;
using System.Collections.Generic;
using System.Text;
using Kmh.Discord.Wire;
using KMHServerAddon.Features.Leaderboards;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Util;

namespace KMHServerAddon.Features.Discord
{
    // Plain markdown lines, not code-block tables: a table past ~56 chars wraps mid-column and comes out mangled.
    internal static class DiscordLeaderboardBuilder
    {
        public const string DefaultSort = "economy";
        public const string DefaultGuildSort = LeaderboardCatalog.GuildMembers;

        // The catalog's keys: the website's addresses and the game's list use the same words.
        public static readonly string[] SupportedSorts = Keys(LeaderboardScope.Player);
        public static readonly string[] SupportedGuildSorts = Keys(LeaderboardScope.Guild);

        // Older command words, so a habit still finds its board; "wealth" lands on what the colony itself reports.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["score"] = "economy", ["wealth"] = "colony-wealth", ["kill"] = "kills",
            ["age"] = "colony-age", ["colony"] = "colony-age", ["colonyage"] = "colony-age",
            ["time"] = "active", ["playtime"] = "active", ["played"] = "active", ["activetime"] = "active", ["notafk"] = "active",
            ["silver"] = "donated", ["sales"] = "trade", ["spent"] = "purchases", ["quests"] = "contracts",
            ["sites"] = "sites-built", ["built"] = "sites-built", ["xp"] = "worker-xp", ["workerxp"] = "worker-xp",
            ["rep"] = LeaderboardCatalog.Reputation,
        };

        private static readonly Dictionary<string, string> GuildAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["members"] = LeaderboardCatalog.GuildMembers, ["treasury"] = LeaderboardCatalog.GuildTreasury,
            ["silver"] = LeaderboardCatalog.GuildTreasury, ["vault"] = LeaderboardCatalog.GuildTreasury,
        };

        // The order the website and the game open on; the first with anyone on it is the default board.
        private static readonly string[] Preferred = { "economy", "trade", "contracts" };

        private const uint LiveColor  = 0x4A90E2;
        private const uint FinalColor = 0x7A7A7A;
        private const uint GuildColor = 0xBA84F6;
        private const uint RepColor   = 0x6AC67A;
        private const uint CardColor  = 0xE2B94A;

        // Under Discord's 4096: rows stop here, so the closing link is never what gets cut.
        private const int RowBudget = 3700;

        private static string[] Keys(string scope)
        {
            var keys = new List<string>();
            foreach (LeaderboardCategory c in LeaderboardCatalog.All) if (c.Scope == scope) keys.Add(c.Key);
            return keys.ToArray();
        }

        // Equal values share a rank, so two players can both hold gold.
        private static string Rank(int rank) => rank switch
        {
            1 => "🥇", 2 => "🥈", 3 => "🥉",
            _ => $"`#{rank,2}`",
        };

        public static string NormalizeSort(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            LeaderboardCategory c = LeaderboardCatalog.Find(s);
            if (c != null && c.Scope == LeaderboardScope.Player) return c.Key;
            return Aliases.TryGetValue(s, out string key) ? key : DefaultSort;
        }

        public static string NormalizeGuildSort(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            LeaderboardCategory c = LeaderboardCatalog.Find(s);
            if (c != null && c.Scope == LeaderboardScope.Guild) return c.Key;
            return GuildAliases.TryGetValue(s, out string key) ? key : DefaultGuildSort;
        }

        // The automatic post's board: the first preferred one anyone is on, as the website chooses.
        internal static string PostedSort(LeaderboardEngine.Inputs inputs)
        {
            foreach (string key in Preferred)
            {
                LeaderboardBoardDto b = LeaderboardEngine.Build(LeaderboardCatalog.Find(key), inputs);
                if (b != null && b.Rows.Count > 0) return key;
            }
            return DefaultSort;
        }

        public static KmhEmbedSpec Build(int topN, string sortKey, bool isFinalized = false,
                                         DateTime? liveStartedUtc = null, DateTime? resetsAtUtc = null, int updateEveryMin = 0)
            => Build(topN, sortKey, LeaderboardEngine.Live(), isFinalized, liveStartedUtc, resetsAtUtc, updateEveryMin);

        // pageFor names a board's website page, "" for none; the live one asks what Global last said.
        internal static KmhEmbedSpec Build(int topN, string sortKey, LeaderboardEngine.Inputs inputs, bool isFinalized = false,
                                           DateTime? liveStartedUtc = null, DateTime? resetsAtUtc = null, int updateEveryMin = 0,
                                           Func<string, string> pageFor = null)
        {
            LeaderboardBoardDto board = LeaderboardEngine.Build(LeaderboardCatalog.Find(NormalizeSort(sortKey)), inputs);
            uint colour = isFinalized ? FinalColor : board?.Key == LeaderboardCatalog.Reputation ? RepColor : LiveColor;
            return Embed(board, topN, inputs, colour, board?.Key == LeaderboardCatalog.Reputation ? "🤝" : "🏆",
                         isFinalized, liveStartedUtc, resetsAtUtc, updateEveryMin, pageFor);
        }

        public static KmhEmbedSpec BuildGuilds(int topN, string sortKey, bool isFinalized = false)
            => BuildGuilds(topN, sortKey, LeaderboardEngine.Live(), isFinalized);

        internal static KmhEmbedSpec BuildGuilds(int topN, string sortKey, LeaderboardEngine.Inputs inputs, bool isFinalized = false,
                                                 Func<string, string> pageFor = null)
        {
            LeaderboardBoardDto board = LeaderboardEngine.Build(LeaderboardCatalog.Find(NormalizeGuildSort(sortKey)), inputs);
            return Embed(board, topN, inputs, isFinalized ? FinalColor : GuildColor, "🏰", isFinalized, null, null, 0, pageFor);
        }

        public static KmhEmbedSpec BuildReputation(int topN) => Build(topN, LeaderboardCatalog.Reputation);

        // The rows as message text, for where no image can go: the coordinator carries no files.
        internal static string TextRows(LeaderboardBoardDto board, int topN, LeaderboardEngine.Inputs inputs)
        {
            string icon = board?.Scope == LeaderboardScope.Guild ? "🏰" : board?.Key == LeaderboardCatalog.Reputation ? "🤝" : "🏆";
            return DiscordCompactText.Render(Embed(board, topN, inputs, LiveColor, icon, false, null, null, 0, _ => ""));
        }

        // Null when nobody is on the board: the poster then posts nothing, and a command says there is no data yet.
        private static KmhEmbedSpec Embed(LeaderboardBoardDto board, int topN, LeaderboardEngine.Inputs inputs, uint colour, string icon,
                                          bool isFinalized, DateTime? startedUtc, DateTime? resetsAtUtc, int updateEveryMin,
                                          Func<string, string> pageFor)
        {
            if (board == null || board.Rows.Count == 0) return null;
            topN = Math.Clamp(topN, 1, 25);
            string page = (pageFor ?? Global.KmhGlobalService.LeaderboardPageFor)(board.Key) ?? "";
            if (page.Length > 0)
            {
                // The board as the picture Global draws from the standings it published; the text is only the way there.
                var brief = new StringBuilder();
                AppendStatusLine(brief, isFinalized, startedUtc, resetsAtUtc, updateEveryMin);
                brief.Append("[Full standings on the website](").Append(page).Append(')');
                return new KmhEmbedSpecBuilder()
                    .WithTitle($"{icon} {board.Title}")
                    .WithColor(colour)
                    .WithDescription(brief.ToString())
                    .WithImageUrl(ImageUrl(page, board))
                    .WithFooter(SourceNote(board) + $" · {board.Total} ranked · season {board.Season}")
                    .WithCurrentTimestamp()
                    .Build();
            }
            bool players = board.Scope == LeaderboardScope.Player;
            HashSet<string> linked = players ? Linked(inputs) : null;

            var sb = new StringBuilder();
            AppendStatusLine(sb, isFinalized, startedUtc, resetsAtUtc, updateEveryMin);
            int shown = 0;
            for (int i = 0; i < board.Rows.Count && shown < topN; i++)
            {
                LeaderboardRowDto row = board.Rows[i];
                var line = new StringBuilder();
                line.Append(Rank(row.Rank)).Append(" **").Append(DiscordText.SafeName(row.Name)).Append("**");
                if (linked != null && linked.Contains(row.Name)) line.Append(" 🔗");
                line.Append(" · ").Append(row.Display);
                if (board.Key == LeaderboardCatalog.Reputation) line.Append(" · ").Append(Reputation.ReputationStore.Get(row.Name).Item2 ?? "Neutral");
                else if (players && !string.IsNullOrEmpty(row.Colony)) line.Append(" · _").Append(DiscordText.SafeName(row.Colony)).Append('_');
                line.Append('\n');
                if (sb.Length + line.Length > RowBudget) break;
                sb.Append(line);
                shown++;
            }

            if (shown < board.Total) sb.Append('\n').Append(board.Total - shown).Append(" more on this board.");

            return new KmhEmbedSpecBuilder()
                .WithTitle($"{icon} {board.Title} — top {shown}")
                .WithColor(colour)
                .WithDescription(sb.ToString())
                .WithFooter(SourceNote(board) + $" · {board.Total} ranked · season {board.Season}")
                .WithCurrentTimestamp()
                .Build();
        }

        // A new address for every ranking, or Discord keeps showing the picture it fetched first.
        internal static string ImageUrl(string page, LeaderboardBoardDto board)
            => page + "/image.png?v=" + board.GeneratedUnix.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Said on every board, as the game and the website say it: whose word the numbers are.
        internal static string SourceNote(LeaderboardBoardDto board)
            => board.Source == LeaderboardSource.Reported ? "As each colony's game reports it" : "Measured by this server";

        private static HashSet<string> Linked(LeaderboardEngine.Inputs inputs)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlayerLeaderboardEntry e in inputs.Players)
                if (e != null && e.IsLinkedToDiscord && !string.IsNullOrEmpty(e.Username)) set.Add(e.Username);
            return set;
        }

        // matches carries the candidates when the query is ambiguous, so a caller can offer them.
        public static KmhEmbedSpec BuildPlayerCard(string query, out List<string> matches)
            => BuildPlayerCard(query, LeaderboardEngine.Live(), out matches);

        internal static KmhEmbedSpec BuildPlayerCard(string query, LeaderboardEngine.Inputs inputs, out List<string> matches)
        {
            matches = null;
            if (string.IsNullOrWhiteSpace(query)) return null;
            // A banned player is on no board, so they get no card that ranks them either.
            List<PlayerLeaderboardEntry> all = inputs.Players.FindAll(e => !string.IsNullOrEmpty(e?.Username) && !inputs.Hidden(e.Username));
            if (all.Count == 0) return null;

            string q = query.Trim();
            PlayerLeaderboardEntry me = all.Find(e => string.Equals(e.Username, q, StringComparison.OrdinalIgnoreCase));
            if (me == null)
            {
                List<PlayerLeaderboardEntry> hits = all.FindAll(e => e.Username.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hits.Count == 1) me = hits[0];
                else if (hits.Count > 1)
                {
                    matches = hits.ConvertAll(h => h.Username);
                    if (matches.Count > 8) matches = matches.GetRange(0, 8);
                    return null;
                }
                else return null;
            }

            var boards = new Dictionary<string, LeaderboardBoardDto>(StringComparer.Ordinal);
            string Stat(string key)
            {
                LeaderboardCategory c = LeaderboardCatalog.Find(key);
                if (!boards.TryGetValue(key, out LeaderboardBoardDto b)) boards[key] = b = LeaderboardEngine.Build(c, inputs);
                LeaderboardRowDto row = b?.Rows.Find(x => string.Equals(x.Name, me.Username, StringComparison.OrdinalIgnoreCase));
                return row == null ? c.Format(0) + " (unranked)" : $"{row.Display} (#{row.Rank})";
            }
            int? OverallRank()
            {
                Stat("economy");
                return boards["economy"]?.Rows.Find(x => string.Equals(x.Name, me.Username, StringComparison.OrdinalIgnoreCase))?.Rank;
            }

            int? overall = OverallRank();
            (int repScore, string repTier) = Reputation.ReputationStore.Get(me.Username);
            string who = me.IsLinkedToDiscord ? "Linked to Discord 🔗" : "Not linked to Discord";
            string guild = string.IsNullOrEmpty(me.GuildName) ? "No guild" : $"Guild: **{DiscordText.SafeName(me.GuildName)}**";
            long firstSeenUnix = TicksToUnix(me.FirstSeenUtcTicks);

            return new KmhEmbedSpecBuilder()
                .WithTitle(overall.HasValue ? $"📊 {DiscordText.SafeName(me.Username)} — #{overall} by economy score" : $"📊 {DiscordText.SafeName(me.Username)}")
                .WithColor(CardColor)
                .WithDescription($"{guild} · {repTier} ({repScore:N0} rep) · {who}")
                .AddField("Colony (as its game reports)",
                    (string.IsNullOrEmpty(me.ColonyName) ? "_(not reported yet)_" : $"**{DiscordText.SafeName(me.ColonyName)}**") + "\n" +
                    $"Wealth {Stat("colony-wealth")}\n" +
                    $"Kills {Stat("kills")}\n" +
                    $"Raids survived {Stat("raids")}\n" +
                    $"{me.ColonyAgeDays:N0}d old · {me.TimePlayedHours:N0}h played" +
                    (string.IsNullOrEmpty(me.TopColonistName) ? "" : $"\nTop colonist: **{DiscordText.SafeName(me.TopColonistName)}** ({me.TopColonistKills:N0} kills)"), inline: true)
                .AddField("Economy",
                    $"Score {Stat("economy")}\n" +
                    $"Donated {Stat("donated")}\n" +
                    $"Vault {Stat("vault")}\n" +
                    $"Sales {Stat("trade")}\n" +
                    $"Spent {Stat("purchases")}", inline: true)
                .AddField("Contracts",
                    $"Completed {Stat("contracts")}\n" +
                    $"Posted {Stat("posted")}", inline: true)
                .AddField("Sites",
                    $"Built {Stat("sites-built")}\n" +
                    $"Worker XP {Stat("worker-xp")}\n" +
                    $"Active {Stat("active")}", inline: true)
                .AddField("Member since", firstSeenUnix > 0 ? $"<t:{firstSeenUnix}:D> (<t:{firstSeenUnix}:R>)" : "(unknown)")
                .WithFooter($"Ranked against {all.Count} player{(all.Count == 1 ? "" : "s")}; a zero ranks nobody")
                .WithCurrentTimestamp()
                .Build();
        }

        private static void AppendStatusLine(StringBuilder sb, bool isFinalized, DateTime? startedUtc, DateTime? resetsAtUtc, int updateEveryMin)
        {
            if (isFinalized)
            {
                long s = ToUnix(startedUtc), e = ToUnix(DateTime.UtcNow);
                sb.Append(s > 0 ? $"⏹ _Final snapshot · <t:{s}:f> → <t:{e}:f>_" : "⏹ _Final snapshot_").Append("\n\n");
            }
            else if (updateEveryMin > 0)
            {
                long r = ToUnix(resetsAtUtc);
                sb.Append($"📡 _Live · updates every {updateEveryMin}m");
                if (r > 0) sb.Append($" · resets <t:{r}:R>");
                sb.Append('_').Append("\n\n");
            }
        }

        private static long ToUnix(DateTime? utc)
            => utc.HasValue ? ((DateTimeOffset)DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;

        private static long TicksToUnix(long utcTicks)
            => utcTicks > 0 ? ((DateTimeOffset)new DateTime(utcTicks, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
    }
}
