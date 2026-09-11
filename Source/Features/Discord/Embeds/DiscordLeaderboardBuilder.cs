using System;
using System.Collections.Generic;
using Discord;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.Features.PlayerStats;
using KMHServerAddon.Features.PlayerStats.Dto;
using KMHServerAddon.Util;

namespace KMHServerAddon.Features.Discord
{
    // Plain markdown lines, not code-block tables: a table past ~56 chars wraps mid-column and comes out mangled.
    internal static class DiscordLeaderboardBuilder
    {
        public const string DefaultSort = "wealth";

        public static readonly string[] SupportedSorts =
            { "wealth", "kills", "age", "time", "active", "score", "silver", "sales", "spent", "quests", "posted", "sites", "xp" };

        public const string DefaultGuildSort = "members";
        public static readonly string[] SupportedGuildSorts = { "members", "treasury" };

        private static readonly Color LiveColor  = new Color(0x4A, 0x90, 0xE2);
        private static readonly Color FinalColor = new Color(0x7A, 0x7A, 0x7A);
        private static readonly Color GuildColor = new Color(0xBA, 0x84, 0xF6);
        private static readonly Color RepColor   = new Color(0x6A, 0xC6, 0x7A);
        private static readonly Color CardColor  = new Color(0xE2, 0xB9, 0x4A);

        private static string Rank(int i) => i switch
        {
            0 => "🥇", 1 => "🥈", 2 => "🥉",
            _ => $"`#{i + 1,2}`",
        };

        public static Embed Build(int topN, string sortKey, bool isFinalized = false,
                                  DateTime? liveStartedUtc = null, DateTime? resetsAtUtc = null, int updateEveryMin = 0)
        {
            topN = Math.Clamp(topN, 1, 25);
            List<PlayerLeaderboardEntry> rows = PlayerStatsStore.BuildSnapshot()?.Entries;
            if (rows == null || rows.Count == 0) return null;

            string key = NormalizeSort(sortKey);
            rows.Sort((a, b) => ReadMetric(b, key).CompareTo(ReadMetric(a, key)));
            int shown = Math.Min(topN, rows.Count);

            var sb = new System.Text.StringBuilder();
            AppendStatusLine(sb, isFinalized, liveStartedUtc, resetsAtUtc, updateEveryMin);
            for (int i = 0; i < shown; i++)
            {
                PlayerLeaderboardEntry e = rows[i];
                sb.Append(Rank(i)).Append(" **").Append(DiscordText.SafeName(e.Username)).Append("**");
                if (e.IsLinkedToDiscord) sb.Append(" 🔗");
                sb.Append(" · ").Append(FormatMetric(ReadMetric(e, key), key));
                if (!string.IsNullOrEmpty(e.ColonyName)) sb.Append(" · _").Append(DiscordText.SafeName(e.ColonyName)).Append('_');
                sb.Append('\n');
            }

            return new EmbedBuilder()
                .WithTitle($"🏆 Player Standings — top {shown} by {SortLabel(key)}")
                .WithColor(isFinalized ? FinalColor : LiveColor)
                .WithDescription(sb.ToString())
                .WithFooter($"Sorts: {string.Join(" · ", SupportedSorts)}  |  !kmh-rank <player> for a stat card")
                .WithCurrentTimestamp()
                .Build();
        }

        public static Embed BuildGuilds(int topN, string sortKey, bool isFinalized = false)
        {
            topN = Math.Clamp(topN, 1, 25);
            List<GuildStore.GuildSummary> rows = GuildStore.ComputeLeaderboard();
            if (rows == null || rows.Count == 0) return null;

            string key = NormalizeGuildSort(sortKey);
            rows.Sort((a, b) => ReadGuildMetric(b, key).CompareTo(ReadGuildMetric(a, key)));
            int shown = Math.Min(topN, rows.Count);

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                GuildStore.GuildSummary g = rows[i];
                sb.Append(Rank(i)).Append(" **").Append(DiscordText.SafeName(g.Name)).Append("** · ")
                  .Append(g.MemberCount).Append(g.MemberCount == 1 ? " member" : " members")
                  .Append(" · ").Append(SilverFmt.Format(g.TreasurySilver)).Append(" vault")
                  .Append('\n');
            }

            return new EmbedBuilder()
                .WithTitle($"🏰 Guild Leaderboard — top {shown} by {key}")
                .WithColor(isFinalized ? FinalColor : GuildColor)
                .WithDescription(sb.ToString())
                .WithFooter($"Sorts: {string.Join(" · ", SupportedGuildSorts)}")
                .WithCurrentTimestamp()
                .Build();
        }

        public static Embed BuildReputation(int topN)
        {
            topN = Math.Clamp(topN, 1, 25);
            List<Reputation.Dto.ReputationEntryDto> rows = Reputation.ReputationStore.BuildSnapshot()?.Entries;
            if (rows == null || rows.Count == 0) return null;

            rows.Sort((a, b) => b.Score.CompareTo(a.Score));
            int shown = Math.Min(topN, rows.Count);

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                Reputation.Dto.ReputationEntryDto e = rows[i];
                sb.Append(Rank(i)).Append(" **").Append(DiscordText.SafeName(e.Username)).Append("** · ")
                  .Append(e.Score.ToString("N0")).Append(" · ").Append(e.Tier ?? "Neutral").Append('\n');
            }

            return new EmbedBuilder()
                .WithTitle($"🤝 Reputation — top {shown} by quest trust")
                .WithColor(RepColor)
                .WithDescription(sb.ToString())
                .WithFooter("Reputation comes from quest behaviour")
                .WithCurrentTimestamp()
                .Build();
        }

        // matches carries the candidates when the query is ambiguous, so a caller can offer them.
        public static Embed BuildPlayerCard(string query, out List<string> matches)
        {
            matches = null;
            if (string.IsNullOrWhiteSpace(query)) return null;
            List<PlayerLeaderboardEntry> all = PlayerStatsStore.BuildSnapshot()?.Entries;
            if (all == null || all.Count == 0) return null;

            string q = query.Trim();
            PlayerLeaderboardEntry me = all.Find(e => string.Equals(e.Username, q, StringComparison.OrdinalIgnoreCase));
            if (me == null)
            {
                List<PlayerLeaderboardEntry> hits =
                    all.FindAll(e => (e.Username ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hits.Count == 1) me = hits[0];
                else if (hits.Count > 1)
                {
                    matches = hits.ConvertAll(h => h.Username);
                    if (matches.Count > 8) matches = matches.GetRange(0, 8);
                    return null;
                }
                else return null;
            }

            int RankOf(Func<PlayerLeaderboardEntry, long> metric)
            {
                long mine = metric(me);
                int better = 0;
                foreach (PlayerLeaderboardEntry e in all) if (metric(e) > mine) better++;
                return better + 1;
            }

            int overall = RankOf(e => e.EconomyScore);
            (int repScore, string repTier) = Reputation.ReputationStore.Get(me.Username);

            string who = me.IsLinkedToDiscord ? "Linked to Discord 🔗" : "Not linked to Discord";
            string guild = string.IsNullOrEmpty(me.GuildName) ? "No guild" : $"Guild: **{DiscordText.SafeName(me.GuildName)}**";
            long firstSeenUnix = TicksToUnix(me.FirstSeenUtcTicks);

            return new EmbedBuilder()
                .WithTitle($"📊 {me.Username} — #{overall} overall")
                .WithColor(CardColor)
                .WithDescription($"{guild} · {repTier} ({repScore:N0} rep) · {who}")
                .AddField("Colony",
                    (string.IsNullOrEmpty(me.ColonyName) ? "_(not reported yet)_" : $"**{DiscordText.SafeName(me.ColonyName)}**") + "\n" +
                    $"Wealth {SilverFmt.Format(me.TotalWealth)} (#{RankOf(e => e.TotalWealth)})\n" +
                    (me.KmhWealth > 0 ? $"_{SilverFmt.Format(me.Wealth)} on-map · {SilverFmt.Format(me.KmhWealth)} in KMH_\n" : "") +
                    $"Kills {me.Kills:N0} (#{RankOf(e => e.Kills)})\n" +
                    $"{me.ColonyAgeDays:N0}d old · {me.TimePlayedHours:N0}h played" +
                    (string.IsNullOrEmpty(me.TopColonistName) ? "" : $"\nTop colonist: **{DiscordText.SafeName(me.TopColonistName)}** ({me.TopColonistKills:N0} kills)"), inline: true)
                .AddField("Economy",
                    $"Score **{me.EconomyScore:N0}** (#{RankOf(e => e.EconomyScore)})\n" +
                    $"Donated {SilverFmt.Format(me.SilverDonated)} (#{RankOf(e => e.SilverDonated)})\n" +
                    $"Sales {SilverFmt.Format(me.SalesEarned)} (#{RankOf(e => e.SalesEarned)})\n" +
                    $"Spent {SilverFmt.Format(me.PurchasesSpent)} (#{RankOf(e => e.PurchasesSpent)})", inline: true)
                .AddField("Quests",
                    $"Completed **{me.QuestsCompleted:N0}** (#{RankOf(e => e.QuestsCompleted)})\n" +
                    $"Posted {me.QuestsPosted:N0} (#{RankOf(e => e.QuestsPosted)})", inline: true)
                .AddField("Sites",
                    $"Built **{me.SitesBuilt:N0}** (#{RankOf(e => e.SitesBuilt)})\n" +
                    $"Worker XP {me.WorkerXp:N0} (#{RankOf(e => e.WorkerXp)})", inline: true)
                .AddField("Member since", firstSeenUnix > 0 ? $"<t:{firstSeenUnix}:D> (<t:{firstSeenUnix}:R>)" : "(unknown)")
                .WithFooter($"Ranked against {all.Count} player{(all.Count == 1 ? "" : "s")}")
                .WithCurrentTimestamp()
                .Build();
        }

        private static void AppendStatusLine(System.Text.StringBuilder sb, bool isFinalized,
                                             DateTime? startedUtc, DateTime? resetsAtUtc, int updateEveryMin)
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

        public static string NormalizeSort(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "donated": return "silver";
                case "purchases": return "spent";
                case "built": return "sites";
                case "workerxp": return "xp";
                case "colonyage": case "colony": return "age";
                case "playtime": case "played": return "time";
                case "activetime": case "notafk": return "active";
                case "kill": return "kills";
            }
            return Array.IndexOf(SupportedSorts, s) >= 0 ? s : DefaultSort;
        }

        public static string NormalizeGuildSort(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == "silver" || s == "vault") s = "treasury";
            return Array.IndexOf(SupportedGuildSorts, s) >= 0 ? s : DefaultGuildSort;
        }

        private static long ReadMetric(PlayerLeaderboardEntry e, string key) => key switch
        {
            "wealth" => e.TotalWealth,
            "kills"  => e.Kills,
            "age"    => e.ColonyAgeDays,
            "time"   => e.TimePlayedHours,
            "active" => e.ActiveSeconds,
            "silver" => e.SilverDonated,
            "sales"  => e.SalesEarned,
            "spent"  => e.PurchasesSpent,
            "quests" => e.QuestsCompleted,
            "posted" => e.QuestsPosted,
            "sites"  => e.SitesBuilt,
            "xp"     => e.WorkerXp,
            _        => e.EconomyScore,
        };

        private static string SortLabel(string key) => key switch
        {
            "wealth" => "colony wealth",
            "kills"  => "kills",
            "age"    => "colony age",
            "time"   => "time played",
            "active" => "time actually playing here",
            "silver" => "silver donated",
            "sales"  => "sales earned",
            "spent"  => "silver spent",
            "quests" => "quests completed",
            "posted" => "quests posted",
            "sites"  => "sites built",
            "xp"     => "worker XP",
            _        => "economy score",
        };

        private static string FormatMetric(long v, string key) => key switch
        {
            "wealth" => $"{SilverFmt.Format(v)} wealth",
            "kills"  => $"{v:N0} kill{(v == 1 ? "" : "s")}",
            "age"    => $"{v:N0}d old",
            "time"   => $"{v:N0}h played",
            "active" => $"{ActiveSpan(v)} active",
            "silver" => $"{SilverFmt.Format(v)} donated",
            "sales"  => $"{SilverFmt.Format(v)} sales",
            "spent"  => $"{SilverFmt.Format(v)} spent",
            "quests" => $"{v:N0} quest{(v == 1 ? "" : "s")}",
            "posted" => $"{v:N0} posted",
            "sites"  => $"{v:N0} site{(v == 1 ? "" : "s")}",
            "xp"     => $"{v:N0} XP",
            _        => $"{v:N0} score",
        };

        internal static string ActiveSpan(long seconds)
        {
            if (seconds < 60) return seconds <= 0 ? "0m" : "1m";
            long minutes = seconds / 60;
            if (minutes < 60) return minutes + "m";
            long hours = minutes / 60;
            return hours < 24 ? hours + "h" : (hours / 24) + "d";
        }

        private static long ReadGuildMetric(GuildStore.GuildSummary g, string key)
            => key == "treasury" ? g.TreasurySilver : g.MemberCount;
    }
}
