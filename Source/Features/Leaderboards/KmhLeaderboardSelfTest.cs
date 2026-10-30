using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KMHServerAddon.Features.PlayerStats;
using KMHServerAddon.Features.PlayerStats.Dto;

namespace KMHServerAddon.Features.Leaderboards
{
    internal static class KmhLeaderboardSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw {ex.GetType().Name}: {ex.Message}")); }
            }
            LeaderboardRowDto Row(string name, long value) => new LeaderboardRowDto { Name = name, Value = value };
            LeaderboardEngine.Inputs Players(params (string Name, long Sales)[] rows)
            {
                var inputs = new LeaderboardEngine.Inputs { Season = 4, SeasonStartedTicks = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
                                                            Now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc) };
                foreach ((string n, long s) in rows) inputs.Players.Add(new PlayerLeaderboardEntry { Username = n, SalesEarned = s, GuildName = "Ironclad" });
                return inputs;
            }

            Check("leaderboards: every category has a unique URL-safe key, a known unit and source, and a value", () =>
            {
                var bad = new List<string>();
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] units = { LeaderboardUnit.Silver, LeaderboardUnit.Count, LeaderboardUnit.Points, LeaderboardUnit.Hours, LeaderboardUnit.Days };
                foreach (LeaderboardCategory c in LeaderboardCatalog.All)
                {
                    if (!Regex.IsMatch(c.Key ?? "", "^[a-z0-9]+(-[a-z0-9]+)*$") || !keys.Add(c.Key)) bad.Add("key " + c.Key);
                    if (!units.Contains(c.Unit)) bad.Add(c.Key + " unit " + c.Unit);
                    if (c.Source != LeaderboardSource.Verified && c.Source != LeaderboardSource.Reported) bad.Add(c.Key + " source");
                    bool built = c.Scope == LeaderboardScope.Guild || c.Key == LeaderboardCatalog.Reputation;
                    if (!built && c.PlayerValue == null) bad.Add(c.Key + " has no value");
                    if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Description)) bad.Add(c.Key + " text");
                }
                return (bad.Count == 0 && keys.Count >= 20, $"{keys.Count} categories" + (bad.Count > 0 ? "; " + string.Join(", ", bad) : ""));
            });

            Check("leaderboards: highest first, equal values share a rank, the next rank skips, and input order changes nothing", () =>
            {
                var rows = new List<LeaderboardRowDto> { Row("bob", 50), Row("Alice", 90), Row("carol", 50), Row("dave", 10), Row("Eve", 90) };
                List<LeaderboardRowDto> a = LeaderboardEngine.Rank(rows.Select(x => Row(x.Name, x.Value)));
                rows.Reverse();
                List<LeaderboardRowDto> b = LeaderboardEngine.Rank(rows.Select(x => Row(x.Name, x.Value)));
                string Fmt(List<LeaderboardRowDto> l) => string.Join(" ", l.Select(x => $"{x.Rank}:{x.Name}"));
                return (Fmt(a) == "1:Alice 1:Eve 3:bob 3:carol 5:dave" && Fmt(a) == Fmt(b), Fmt(a));
            });

            Check("leaderboards: nobody is ranked for nothing - zero, negative and nameless rows are dropped", () =>
            {
                List<LeaderboardRowDto> l = LeaderboardEngine.Rank(new[] { Row("a", 0), Row("b", -5), Row("", 9), Row("  ", 9), Row("c", 1) });
                return (l.Count == 1 && l[0].Name == "c" && l[0].Rank == 1, string.Join(",", l.Select(x => x.Name)));
            });

            Check("leaderboards: a banned player is on no board, and the rest close up behind them", () =>
            {
                LeaderboardEngine.Inputs inputs = Players(("ann", 300), ("cheat", 900), ("bea", 200));
                inputs.Hidden = n => n == "cheat";
                LeaderboardBoardDto b = LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), inputs);
                string got = string.Join(" ", b.Rows.Select(x => $"{x.Rank}:{x.Name}"));
                return (got == "1:ann 2:bea" && b.Total == 2, got);
            });

            Check("leaderboards: simulated accounts stay off the boards while unranked, rank when allowed, and always read as SIM", () =>
            {
                var file = new Simulation.SimulatedAccountsFile { ExcludeFromRankings = true, Accounts = new List<string> { " Sim_Ada ", "" } };
                using (Simulation.SimulatedAccounts.UseForTest(file))
                {
                    string Board()
                    {
                        LeaderboardEngine.Inputs inputs = Players(("ann", 300), ("sim_ada", 900), ("bea", 200));
                        inputs.Hidden = LeaderboardEngine.HiddenNow();
                        inputs.HideSimulatedGuilds = Simulation.SimulatedAccounts.ExcludesFromRankings;
                        inputs.GuildRows.Add(new Guilds.GuildStore.GuildSummary { Name = "Bots", MemberCount = 9, AllSimulated = true });
                        inputs.GuildRows.Add(new Guilds.GuildStore.GuildSummary { Name = "Mixed", MemberCount = 3 });
                        string guilds = string.Join(",", LeaderboardEngine.Build(LeaderboardCatalog.Find(LeaderboardCatalog.GuildMembers), inputs).Rows.Select(x => x.Name));
                        return string.Join(" ", LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), inputs).Rows.Select(x => $"{x.Rank}:{x.Name}")) + " | " + guilds;
                    }
                    string off = Board();
                    file.ExcludeFromRankings = false;
                    string on = Board();
                    string roles = Simulation.SimulatedAccounts.WithRoles("Sim_Ada:admin;Boss:owner");
                    string badges = Simulation.SimulatedAccounts.WithBadge("");
                    bool ok = off == "1:ann 2:bea | Mixed" && on == "1:sim_ada 2:ann 3:bea | Bots,Mixed" && roles == "Sim_Ada:admin;Boss:owner;Sim_Ada:sim"
                           && badges == "sim:SIM:929bab" && !Simulation.SimulatedAccounts.IsSimulated("ann")
                           && !Simulation.SimulatedAccounts.IsPerson("SIM_ADA") && Simulation.SimulatedAccounts.IsPerson("ann")
                           && !Simulation.SimulatedAccounts.IsPerson("");
                    bool guildRule = Guilds.GuildStore.AllSimulated(new List<string> { "SIM_ADA" })
                                  && !Guilds.GuildStore.AllSimulated(new List<string> { "sim_ada", "ann" })
                                  && !Guilds.GuildStore.AllSimulated(new List<string>());
                    return (ok && guildRule, $"unranked '{off}', ranked '{on}', roles '{roles}', badges '{badges}', guild rule {guildRule}");
                }
            });

            Check("leaderboards: a board carries its season, its source and each player's guild and online state", () =>
            {
                LeaderboardEngine.Inputs inputs = Players(("ann", 300), ("bea", 200));
                inputs.Online.Add("BEA");
                LeaderboardBoardDto b = LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), inputs);
                LeaderboardBoardDto k = LeaderboardEngine.Build(LeaderboardCatalog.Find("kills"), inputs);
                bool ok = b.Season == 4 && b.SeasonStartedUnix == 1790812800 && b.Source == LeaderboardSource.Verified
                       && k.Source == LeaderboardSource.Reported && b.Rows[0].Guild == "Ironclad" && !b.Rows[0].Online && b.Rows[1].Online
                       && b.Rows[0].Display == Util.SilverFmt.Format(300);
                return (ok, $"season {b.Season} from {b.SeasonStartedUnix}, kills source {k.Source}, online {b.Rows[1].Online}, display '{b.Rows[0].Display}'");
            });

            Check("leaderboards: values read as people say them - minutes under an hour, a day, points", () =>
            {
                LeaderboardCategory active = LeaderboardCatalog.Find("active"), age = LeaderboardCatalog.Find("colony-age"), eco = LeaderboardCatalog.Find("economy");
                string got = string.Join(" | ", active.Format(30), active.Format(2700), active.Format(5400), active.Format(360000),
                                          age.Format(1), age.Format(12), eco.Format(15554), LeaderboardCatalog.Find("contracts").Format(1),
                                          LeaderboardCatalog.Find(LeaderboardCatalog.GuildMembers).Format(4), LeaderboardCatalog.Find("worker-xp").Format(1200),
                                          LeaderboardCatalog.Find("streak").Format(28));
                var bare = LeaderboardCatalog.All.Where(c => c.Unit == LeaderboardUnit.Count && string.IsNullOrWhiteSpace(c.Noun)).Select(c => c.Key).ToList();
                return (got == "1 min | 45 min | 1.5 h | 100 h | 1 day | 12 days | 15,554 pts | 1 contract | 4 members | 1,200 XP | 28 in a row" && bare.Count == 0,
                        got + (bare.Count > 0 ? "; counts with no noun: " + string.Join(", ", bare) : ""));
            });

            Check("leaderboards: guilds rank by members and by treasury, each on its own board", () =>
            {
                var inputs = new LeaderboardEngine.Inputs();
                inputs.GuildRows.Add(new Guilds.GuildStore.GuildSummary { Name = "Many", MemberCount = 9, TreasurySilver = 10 });
                inputs.GuildRows.Add(new Guilds.GuildStore.GuildSummary { Name = "Rich", MemberCount = 2, TreasurySilver = 5000 });
                LeaderboardBoardDto m = LeaderboardEngine.Build(LeaderboardCatalog.Find(LeaderboardCatalog.GuildMembers), inputs);
                LeaderboardBoardDto t = LeaderboardEngine.Build(LeaderboardCatalog.Find(LeaderboardCatalog.GuildTreasury), inputs);
                return (m.Rows[0].Name == "Many" && t.Rows[0].Name == "Rich" && m.Scope == LeaderboardScope.Guild, $"members {m.Rows[0].Name}, treasury {t.Rows[0].Name}");
            });

            Check("leaderboards: a frontier capture reaches the stats snapshot every board reads", () =>
            {
                string user = "selftest_lb_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    PlayerStatsStore.BumpFrontierCaptures(user);
                    PlayerStatsStore.BumpFrontierCaptures(user);
                    PlayerLeaderboardEntry e = PlayerStatsStore.BuildSnapshot().Entries.FirstOrDefault(x => x.Username == user);
                    return (e != null && e.FrontierCaptures == 2, $"captures in the snapshot: {e?.FrontierCaptures.ToString() ?? "no row"}");
                }
                finally { PlayerStatsStore.RemoveUser(user); }
            });

            // BuildSnapshot copies fields by hand, so a stored field it forgets silently never reaches any board.
            const string copied = "leaderboards: every stat the store keeps is copied into the snapshot";
            string src = Maintenance.KmhSecurityAuditSelfTest.ReadSource(System.IO.Path.Combine("Features", "PlayerStats", "PlayerStatsStore.cs"));
            if (src == null) r.Add(Diagnostics.KmhSelfTestResult.Skipped(copied, "no source tree beside this build"));
            else Check(copied, () =>
            {
                int a = src.IndexOf("public static PlayerStatsSnapshot BuildSnapshot()", StringComparison.Ordinal);
                int b = a < 0 ? -1 : src.IndexOf("return s;", a, StringComparison.Ordinal);
                if (b < 0) return (false, "BuildSnapshot not found");
                string body = src.Substring(a, b - a);
                var missing = typeof(PlayerLeaderboardEntry).GetProperties()
                    .Where(p => p.CanWrite && !Regex.IsMatch(body, "\\b" + p.Name + "\\s*="))
                    .Select(p => p.Name).ToList();
                return (missing.Count == 0, missing.Count == 0 ? "all fields" : "not copied: " + string.Join(", ", missing));
            });

            Check("leaderboard image: names are text, never markup, and the image stays valid XML", () =>
            {
                LeaderboardEngine.Inputs inputs = Players(("<script>alert(1)</script>", 500), ("a&b \"q\" 'x'", 400), ("emoji 🛰️ \uD800 end", 300));
                string svg = LeaderboardSvg.Render(LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), inputs), new LeaderboardSvg.Options());
                XDocument.Parse(svg);
                bool ok = !svg.Contains("<script") && svg.Contains("&lt;script&gt;") && svg.Contains("a&amp;b") && svg.Contains("🛰");
                return (ok, $"{svg.Length} chars");
            });

            Check("leaderboard image: a long name is cut with an ellipsis to fit its column", () =>
            {
                string name = "Commander " + new string('W', 80);
                string fit = LeaderboardSvg.Fit(name, 300, 22, true);
                return (fit.EndsWith("…") && LeaderboardSvg.Width(fit, 22, true) <= 300 && fit.Length < name.Length, fit);
            });

            Check("leaderboard image: the same board gives the same bytes, and a changed value changes them", () =>
            {
                var o = new LeaderboardSvg.Options { ServerName = "KMH | Test" };
                string a = LeaderboardSvg.Render(LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("ann", 300), ("bea", 200))), o);
                string b = LeaderboardSvg.Render(LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("ann", 300), ("bea", 200))), o);
                string c = LeaderboardSvg.Render(LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("ann", 300), ("bea", 250))), o);
                return (a == b && a != c, $"{a.Length} chars");
            });

            Check("leaderboard image: wide and compact both draw the podium and every listed row, and an empty board says so", () =>
            {
                var rows = Enumerable.Range(1, 12).Select(i => ($"player{i:00}", (long)(1000 - i))).ToArray();
                LeaderboardBoardDto board = LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(rows));
                string wide = LeaderboardSvg.Render(board, new LeaderboardSvg.Options { MaxRows = 10 });
                string compact = LeaderboardSvg.Render(board, new LeaderboardSvg.Options { MaxRows = 10, Width = LeaderboardSvg.CompactWidth });
                string empty = LeaderboardSvg.Render(LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players()), new LeaderboardSvg.Options());
                bool ok = wide.Contains(">player01<") && wide.Contains(">player10<") && !wide.Contains(">player11<")
                       && compact.Contains(">player10<") && compact.Contains("width=\"640\"") && empty.Contains("No rankings yet");
                XDocument.Parse(compact); XDocument.Parse(empty);
                return (ok, $"wide {wide.Length}, compact {compact.Length}, empty {empty.Length} chars");
            });

            // Global draws a server's page image itself from the rows it stored; its copy of this renderer must give these
            // bytes too (KmhGlobal.Tests LeaderboardTests.ImageVector), or the website and Discord show different pictures.
            LeaderboardBoardDto VectorBoard() => new LeaderboardBoardDto
            {
                Key = "trade", Title = "Trade earnings", Group = "Trade", Unit = "silver", Scope = "player", Source = "verified",
                Description = "Silver earned from marketplace and auction sales.", Total = 4, Season = 3,
                SeasonStartedUnix = 1_790_812_800, GeneratedUnix = 1_792_000_000,
                Rows = new List<LeaderboardRowDto>
                {
                    new LeaderboardRowDto { Rank = 1, Name = "Ann", Guild = "Ironclad", Value = 3000, Display = "$3,000", Online = true },
                    new LeaderboardRowDto { Rank = 2, Name = "Bea <&>", Colony = "Dusk Hold", Value = 2000, Display = "$2,000" },
                    new LeaderboardRowDto { Rank = 2, Name = "Cal", Guild = "Ironclad", Value = 2000, Display = "$2,000" },
                    new LeaderboardRowDto { Rank = 4, Name = "Dee", Value = 100, Display = "$100" },
                },
            };
            var vectorOptions = new LeaderboardSvg.Options { ServerName = "KMH | Vector", Link = "example.org/servers/x/leaderboards/trade" };

            Check("leaderboard image: Global's copy of the renderer draws the same bytes as this one", () =>
            {
                string svg = LeaderboardSvg.Render(VectorBoard(), vectorOptions);
                string hash = Sha256Hex(System.Text.Encoding.UTF8.GetBytes(svg));
                return (hash == ImageVector, hash);
            });

            // The PNG this server posts to Discord; KmhGlobal.Tests LeaderboardTests.ImagePngVector pins the same bytes.
            Check("leaderboard image: the PNG this server draws for Discord is byte for byte the one Global draws", () =>
            {
                byte[] png = LeaderboardPng.Render(VectorBoard(), vectorOptions);
                string hash = Sha256Hex(png);
                return (hash == ImagePngVector && png.Length > 1024, $"{hash} ({png.Length} bytes)");
            });

            Check("leaderboards to Global: off by default, sent only on the owner's switch, never by a federation-only identity", () =>
            {
                const string hex = "5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f";
                string path = "/api/v1/servers/" + hex + "/leaderboards";
                Global.GlobalConfig hadCfg = Global.GlobalConfig.ApplyForTest(new Global.GlobalConfig { Enabled = true });
                Global.GlobalSetupState hadState = Global.GlobalSetupState.ApplyForTest(new Global.GlobalSetupState());
                string hadId = KmhServerIdentity.Id;
                Persistence.KmhDataMeta.SetInstanceIdForTest(hex);
                try
                {
                    Global.GlobalSetupState.Current_.Current = Global.SetupStage.Registered;
                    bool byDefault = new Global.GlobalConfig().PublishLeaderboards;
                    bool off = Global.KmhGlobalDisclosure.MayPublish(path, out string whyOff);
                    Global.GlobalConfig.Current.PublishLeaderboards = true;
                    bool on = Global.KmhGlobalDisclosure.MayPublish(path, out _);
                    bool modsStill = Global.KmhGlobalDisclosure.MayPublish("/api/v1/servers/" + hex + "/mod-profile", out _);
                    Global.GlobalConfig.ApplyForTest(new Global.GlobalConfig { Enabled = false, BackendIdentity = true, PublishLeaderboards = true });
                    Global.GlobalSetupState.ApplyForTest(new Global.GlobalSetupState { BackendRegistered = true });
                    bool backend = Global.KmhGlobalDisclosure.MayPublish(path, out _);
                    bool ok = !byDefault && !off && whyOff.Contains("PublishLeaderboards") && on && modsStill && !backend;
                    return (ok, $"default={byDefault} off={off} ({whyOff}) on={on} mod-profile={modsStill} backend={backend}");
                }
                finally { Persistence.KmhDataMeta.SetInstanceIdForTest(hadId); Global.GlobalConfig.ApplyForTest(hadCfg); Global.GlobalSetupState.ApplyForTest(hadState); }
            });

            Check("leaderboards to Global: the body carries exactly the names Global reads", () =>
            {
                string got = string.Join(",", Global.KmhGlobalDisclosure.Fields(typeof(Global.LeaderboardPublishBody)));
                const string want = "boards.description,boards.group,boards.key,boards.rows.colony,boards.rows.display,boards.rows.guild,"
                                  + "boards.rows.name,boards.rows.online,boards.rows.rank,boards.rows.value,boards.scope,boards.source,"
                                  + "boards.title,boards.total,boards.unit,generated_utc,generation,issued_at,nonce,season,season_started_utc,server_id";
                return (got == want, got);
            });

            Check("leaderboards to Global: at most 100 rows a board, names cleaned and cut to 64, ranks untouched", () =>
            {
                LeaderboardBoardDto big = LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"),
                    Players(Enumerable.Range(1, 150).Select(i => (i == 1 ? "A" + (char)1 + new string('x', 90) : "p" + i, (long)(1000 - i))).ToArray()));
                LeaderboardBoardDto weird = LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("" + (char)2 + (char)0x2028, 50)));
                Global.LeaderboardPublishBody body = Global.KmhGlobalService.BuildLeaderboards(new List<LeaderboardBoardDto> { big, weird }, 4, 1, 2);
                var rows = body.Boards[0].Rows;
                bool ok = rows.Count == 100 && body.Boards[0].Total == 150 && rows[0].Name.Length == 64 && !rows[0].Name.Contains((char)1)
                       && rows[99].Rank == 100 && body.Boards[1].Rows[0].Name == "(unnamed)";
                return (ok, $"rows {rows.Count}/{body.Boards[0].Total}, first name {rows[0].Name.Length} chars, last rank {rows[99].Rank}, odd name '{body.Boards[1].Rows[0].Name}'");
            });

            Check("leaderboards to Global: the same ranking built twice is one publication; a changed value is another", () =>
            {
                var b1 = Global.KmhGlobalService.BuildLeaderboards(new List<LeaderboardBoardDto> { LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("ann", 300))) }, 4, 1, 100);
                var b2 = Global.KmhGlobalService.BuildLeaderboards(new List<LeaderboardBoardDto> { LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("ann", 300))) }, 4, 1, 999);
                var b3 = Global.KmhGlobalService.BuildLeaderboards(new List<LeaderboardBoardDto> { LeaderboardEngine.Build(LeaderboardCatalog.Find("trade"), Players(("ann", 301))) }, 4, 1, 100);
                string k1 = Global.KmhGlobalService.BoardsKey(b1), k2 = Global.KmhGlobalService.BoardsKey(b2), k3 = Global.KmhGlobalService.BoardsKey(b3);
                return (k1 == k2 && k1 != k3, $"{k1.Substring(0, 8)} {k2.Substring(0, 8)} {k3.Substring(0, 8)}");
            });

            // KmhGlobal.Tests CanonicalVectorTests pins the same hash: both sides must sign these bytes for this body.
            Check("leaderboards to Global: the signed bytes are the ones Global verifies", () =>
            {
                var body = new Global.LeaderboardPublishBody
                {
                    ServerId = "0123456789abcdef0123456789abcdef", IssuedAtUnix = 1_792_000_000, Nonce = "ffffffffffffffffffffffffffffffff",
                    Generation = 2, Season = 3, SeasonStartedUnix = 1_790_812_800, GeneratedUnix = 1_791_999_990,
                    Boards = new List<Global.LeaderboardPublishBoard>
                    {
                        new Global.LeaderboardPublishBoard
                        {
                            Key = "trade", Title = "Trade earnings", Group = "Trade", Unit = "silver", Scope = "player", Source = "verified",
                            Description = "Silver earned: über alles", Total = 2,
                            Rows = new List<Global.LeaderboardPublishRow>
                            {
                                new Global.LeaderboardPublishRow { Rank = 1, Name = "Ann", Guild = "Ironclad", Value = 3000, Display = "$3,000", Online = true },
                                new Global.LeaderboardPublishRow { Rank = 2, Name = "Bea:2\n", Colony = "Dusk", Value = 2000, Display = "$2,000" },
                            },
                        },
                    },
                };
                string hash = Sha256Hex(body.Canonical());
                return (hash == CanonicalVector, hash);
            });

            Check("leaderboards to Global: the disclosure says when standings go and that they name players", () =>
            {
                Global.GlobalConfig hadCfg = Global.GlobalConfig.ApplyForTest(new Global.GlobalConfig { Enabled = true, PublishLeaderboards = true });
                try
                {
                    var lines = new List<string>();
                    Global.KmhGlobalDisclosure.Describe(lines.Add);
                    string all = string.Join("\n", lines);
                    bool ok = all.Contains("Global.PublishLeaderboards is on") && all.Contains("boards.rows.name") && all.Contains("Leaderboards name the players")
                           && !all.Contains("player names, save files");
                    return (ok, ok ? $"{lines.Count} lines" : all.Substring(Math.Max(0, all.IndexOf("PublishLeaderboards", StringComparison.Ordinal) - 40)).Substring(0, 200));
                }
                finally { Global.GlobalConfig.ApplyForTest(hadCfg); }
            });

            Check("leaderboards in the game: what clients are sent drops a banned player's stats, colonists and reputation", () =>
            {
                Func<string, bool> banned = n => n == "cheat";
                var stats = new PlayerStats.Dto.PlayerStatsSnapshot { Entries = new List<PlayerLeaderboardEntry>
                    { new PlayerLeaderboardEntry { Username = "ann" }, new PlayerLeaderboardEntry { Username = "cheat" }, null } };
                var roster = new PlayerStats.Dto.ColonistRosterSnapshot();
                roster.Colonists.Add(new PlayerStats.Dto.ColonistEntry { Owner = "cheat", Name = "Rex" });
                roster.Colonists.Add(new PlayerStats.Dto.ColonistEntry { Owner = "ann", Name = "Bo" });
                var rep = new Reputation.Dto.ReputationSnapshot();
                rep.Entries.Add(new Reputation.Dto.ReputationEntryDto { Username = "cheat", Score = 40 });
                rep.Entries.Add(new Reputation.Dto.ReputationEntryDto { Username = "ann", Score = 10 });
                LeaderboardEngine.ForClients(stats, banned); LeaderboardEngine.ForClients(roster, banned); LeaderboardEngine.ForClients(rep, banned);
                string got = string.Join(",", stats.Entries.Select(e => e.Username)) + "|" + string.Join(",", roster.Colonists.Select(x => x.Owner))
                           + "|" + string.Join(",", rep.Entries.Select(x => x.Username));
                return (got == "ann|ann|ann" && !LeaderboardEngine.BannedNow()(""), got);
            });

            Check("leaderboards on Discord: the engine's board - a tie shares gold, a banned player and a zero are absent", () =>
            {
                LeaderboardEngine.Inputs inputs = Players(("ann", 300), ("bea", 300), ("cal", 0), ("cheat", 900), ("dee", 100));
                inputs.Hidden = n => n == "cheat";
                Kmh.Discord.Wire.KmhEmbedSpec e = Discord.DiscordLeaderboardBuilder.Build(10, "sales", inputs, pageFor: _ => "");
                string d = e?.Description ?? "";
                bool ok = d.Contains("🥇 **ann**") && d.Contains("🥇 **bea**") && d.Contains("🥉 **dee**")
                       && !d.Contains("**cal**") && !d.Contains("cheat") && !d.Contains("website")
                       && e.Title.Contains("Trade earnings") && e.Footer.StartsWith("Measured by this server", StringComparison.Ordinal)
                       && e.Footer.Contains("3 ranked");
                return (ok, (e?.Title ?? "null") + " | " + d.Replace("\n", " / ") + " | " + e?.Footer);
            });

            Check("leaderboards on Discord: the bot's old sort words land on their boards, and every board key is one", () =>
            {
                string P(string s) => Discord.DiscordLeaderboardBuilder.NormalizeSort(s);
                string G(string s) => Discord.DiscordLeaderboardBuilder.NormalizeGuildSort(s);
                bool ok = P("wealth") == "colony-wealth" && P("sales") == "trade" && P("quests") == "contracts" && P("xp") == "worker-xp"
                       && P("time") == "active" && P("rep") == LeaderboardCatalog.Reputation && P(" TRADE ") == "trade" && P("vault") == "vault"
                       && G("vault") == LeaderboardCatalog.GuildTreasury && G("members") == LeaderboardCatalog.GuildMembers && G("nonsense") == LeaderboardCatalog.GuildMembers
                       && Discord.DiscordLeaderboardBuilder.SupportedSorts.Length + Discord.DiscordLeaderboardBuilder.SupportedGuildSorts.Length == LeaderboardCatalog.All.Count;
                return (ok, $"wealth->{P("wealth")} vault->{P("vault")}/{G("vault")}");
            });

            Check("leaderboards on Discord: the automatic post opens on the first board anyone is on, as the website does", () =>
            {
                LeaderboardEngine.Inputs inputs = Players(("ann", 300));
                string first = Discord.DiscordLeaderboardBuilder.PostedSort(inputs);
                inputs.Players[0].EconomyScore = 50;
                string second = Discord.DiscordLeaderboardBuilder.PostedSort(inputs);
                return (first == "trade" && second == "economy", first + " then " + second);
            });

            Check("leaderboards on Discord: the website is linked only where Global named a page, and only on Global's own site", () =>
            {
                string From(string json) => Global.KmhGlobalService.PageFrom("https://global.example.org", Newtonsoft.Json.Linq.JObject.Parse(json));
                bool parsed = From("{\"page\":\"/servers/0a1b2c/leaderboards\"}") == "https://global.example.org/servers/0a1b2c/leaderboards"
                           && From("{\"page\":\"https://evil.example/servers/x/leaderboards\"}") == ""
                           && From("{\"page\":\"/servers/x/leaderboards?next=//evil.example\"}") == ""
                           && From("{\"page\":\"/servers/../leaderboards\"}") == ""
                           && From("{\"page\":5}") == "" && From("{}") == ""
                           && Global.KmhGlobalService.PageFrom("javascript:alert(1)", Newtonsoft.Json.Linq.JObject.Parse("{\"page\":\"/servers/x/leaderboards\"}")) == "";
                const string page = "https://global.example.org/servers/0a1b2c/leaderboards";
                var on = new Global.GlobalConfig { Enabled = true, PublishLeaderboards = true };
                bool gated = Global.KmhGlobalService.PageFor(on, false, page, "trade") == page + "/trade"
                          && Global.KmhGlobalService.PageFor(new Global.GlobalConfig { Enabled = true, PublishLeaderboards = false }, false, page, "trade") == ""
                          && Global.KmhGlobalService.PageFor(on, true, page, "trade") == ""
                          && Global.KmhGlobalService.PageFor(on, false, "", "trade") == "";
                LeaderboardEngine.Inputs inputs = Players(("ann", 300));
                Kmh.Discord.Wire.KmhEmbedSpec e = Discord.DiscordLeaderboardBuilder.Build(10, "trade", inputs, pageFor: k => page + "/" + k);
                string d = e?.Description ?? "";
                // With a page the board is Global's picture, versioned so Discord fetches a changed ranking; no rows as text.
                bool linked = d.Contains("[Full standings on the website](" + page + "/trade)") && !d.Contains("**ann**")
                           && e.ImageUrl == page + "/trade/image.png?v=" + new DateTimeOffset(inputs.Now).ToUnixTimeSeconds();
                return (parsed && gated && linked, $"parsed={parsed} gated={gated} linked={linked} image={e?.ImageUrl}");
            });

            Check("leaderboards on Discord: a player's card ranks from the same boards, and a banned player has none", () =>
            {
                LeaderboardEngine.Inputs inputs = Players(("ann", 300), ("bea", 300), ("cheat", 900));
                inputs.Hidden = n => n == "cheat";
                Kmh.Discord.Wire.KmhEmbedSpec card = Discord.DiscordLeaderboardBuilder.BuildPlayerCard("bea", inputs, out _);
                string fields = card == null ? "" : string.Join("\n", card.Fields.Select(f => f.Value));
                Kmh.Discord.Wire.KmhEmbedSpec banned = Discord.DiscordLeaderboardBuilder.BuildPlayerCard("cheat", inputs, out List<string> m);
                bool ok = fields.Contains("Sales " + LeaderboardCatalog.Find("trade").Format(300) + " (#1)")
                       && fields.Contains("Kills " + LeaderboardCatalog.Find("kills").Format(0) + " (unranked)")
                       && card.Footer.StartsWith("Ranked against 2 players", StringComparison.Ordinal)
                       && banned == null && m == null;
                return (ok, fields.Replace("\n", " / "));
            });

            ColonistsAndSeasons(Check);
            return r;
        }

        private static void ColonistsAndSeasons(Action<string, Func<(bool ok, string detail)>> Check)
        {
            ColonistEntry C(string name, int kills, int days) => new ColonistEntry { Name = name, Kills = kills, Days = days };
            string Names(List<ColonistEntry> l) => string.Join(",", l.Select(c => c.Name));

            Check("colonist memorial: one gone from the roster as the save's deaths rise is remembered; more gone than died is no one", () =>
            {
                var before = new PlayerStatsStore.RosterState { SaveId = "s1", PawnsLost = 2, Colonists = { C("Ada", 9, 40), C("Bo", 3, 20), C("Cy", 1, 5) } };
                string died = Names(PlayerStatsStore.FallenBetween(before, "s1", 3, new List<ColonistEntry> { C("Ada", 10, 41), C("Cy", 1, 6) }));
                string unsure = Names(PlayerStatsStore.FallenBetween(before, "s1", 3, new List<ColonistEntry> { C("Ada", 10, 41) }));
                string otherSave = Names(PlayerStatsStore.FallenBetween(before, "s2", 3, new List<ColonistEntry> { C("Ada", 10, 41), C("Cy", 1, 6) }));
                string noDeath = Names(PlayerStatsStore.FallenBetween(before, "s1", 2, new List<ColonistEntry> { C("Ada", 10, 41), C("Cy", 1, 6) }));
                bool ok = died == "Bo" && unsure == "" && otherSave == "" && noDeath == "";
                return (ok, $"died='{died}' unsure='{unsure}' otherSave='{otherSave}' noDeath='{noDeath}'");
            });

            Check("colonist memorial: a colonist outranked out of a full top ten is not counted dead when someone else died", () =>
            {
                var before = new PlayerStatsStore.RosterState { SaveId = "s1", PawnsLost = 0 };
                for (int i = 0; i < 10; i++) before.Colonists.Add(C("P" + i, 10 - i, 30));
                var now = new List<ColonistEntry>();
                for (int i = 0; i < 9; i++) now.Add(C("P" + i, 10 - i, 31));
                now.Add(C("Recruit", 5, 2));
                string outranked = Names(PlayerStatsStore.FallenBetween(before, "s1", 1, now));
                var withoutTop = new List<ColonistEntry>();
                for (int i = 1; i < 10; i++) withoutTop.Add(C("P" + i, 10 - i, 31));
                withoutTop.Add(C("Recruit", 0, 2));
                string died = Names(PlayerStatsStore.FallenBetween(before, "s1", 1, withoutTop));
                return (outranked == "" && died == "P0", $"outranked='{outranked}' died='{died}'");
            });

            Check("colonist memorial: a report stores the fallen with their last stats, and a later roster naming them takes it back", () =>
            {
                string user = "memorial-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    ColonyReport Report(int lost, params ColonistEntry[] roster)
                        => new ColonyReport { SaveId = "save-a", ColonyName = "Dusk", ColonyAgeDays = 40, PawnsLost = lost, Roster = roster.ToList() };
                    FallenColonistEntry Mine() => PlayerStatsStore.BuildColonistRoster().Fallen.FirstOrDefault(f => f.Colonist.Owner == user);
                    PlayerStatsStore.ApplyColonyReport(user, Report(0, C("Ada", 4, 10), C("Bo", 2, 8)));
                    PlayerStatsStore.ApplyColonyReport(user, Report(1, C("Ada", 5, 11)));
                    FallenColonistEntry bo = Mine();
                    bool stored = bo != null && bo.Colonist.Name == "Bo" && bo.Colonist.Kills == 2 && bo.Colonist.Colony == "Dusk" && bo.ColonyDay == 40 && bo.DiedUtcTicks > 0;
                    PlayerStatsStore.ApplyColonyReport(user, Report(1, C("Ada", 5, 11), C("Bo", 2, 9)));
                    bool back = Mine() == null;
                    return (stored && back, $"stored={stored} ({bo?.Colonist?.Name} {bo?.Colonist?.Kills} kills) takenBack={back}");
                }
                finally { PlayerStatsStore.RemoveUser(user); }
            });

            // Nicknames repeat and change; the pawn id does neither, and older clients that send none still match by name.
            Check("colonist memorial: with pawn ids a rename is not a death, and a recruit sharing a fallen colonist's nickname does not erase them", () =>
            {
                string user = "memorial-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    ColonistEntry P(string id, string name, int kills, int days) => new ColonistEntry { Id = id, Name = name, Kills = kills, Days = days };
                    ColonyReport Report(int lost, params ColonistEntry[] roster)
                        => new ColonyReport { SaveId = "save-b", ColonyName = "Dawn", ColonyAgeDays = 50, PawnsLost = lost, Roster = roster.ToList() };
                    List<FallenColonistEntry> Mine() => PlayerStatsStore.BuildColonistRoster().Fallen.Where(f => f.Colonist.Owner == user).ToList();
                    PlayerStatsStore.ApplyColonyReport(user, Report(0, P("Human1", "Ada", 4, 10), P("Human2", "Bo", 2, 8)));
                    // Renamed while a pawn off the roster died.
                    PlayerStatsStore.ApplyColonyReport(user, Report(1, P("Human1", "Ace", 5, 11), P("Human2", "Bo", 2, 9)));
                    bool renameNotDead = Mine().Count == 0;
                    PlayerStatsStore.ApplyColonyReport(user, Report(2, P("Human1", "Ace", 5, 12)));
                    bool boFell = Mine().Count == 1 && Mine()[0].Colonist.Id == "Human2";
                    PlayerStatsStore.ApplyColonyReport(user, Report(2, P("Human1", "Ace", 5, 12), P("Human9", "Bo", 0, 1)));
                    bool keptForRecruit = Mine().Count == 1 && Mine()[0].Colonist.Id == "Human2";
                    PlayerStatsStore.ApplyColonyReport(user, Report(2, P("Human1", "Ace", 5, 12), P("Human9", "Bo", 0, 1), P("Human2", "Bo", 2, 10)));
                    bool takenBack = Mine().Count == 0;
                    bool ok = renameNotDead && boFell && keptForRecruit && takenBack;
                    return (ok, $"renameNotDead={renameNotDead} boFell={boFell} keptWhenRecruitTookName={keptForRecruit} takenBackByOwnId={takenBack}");
                }
                finally { PlayerStatsStore.RemoveUser(user); }
            });

            Check("colonist rosters persist: rosters and the memorial survive the real save and load; an older Colonists.json loads empty", () =>
            {
                var colonists = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase) { ["ann"] = new ColonistProfile { Name = "Ada" } };
                var rosters = new Dictionary<string, PlayerStatsStore.RosterState>(StringComparer.OrdinalIgnoreCase)
                { ["ann"] = new PlayerStatsStore.RosterState { SaveId = "s9", PawnsLost = 4, Colonists = { new ColonistEntry { Id = "Human7", Name = "Ada", Kills = 7, Days = 30 } } } };
                var fallen = new List<PlayerStatsStore.FallenRecord>
                { new PlayerStatsStore.FallenRecord { Username = "ann", SaveId = "s9", ColonyDay = 30, DiedUtcTicks = 5, Colonist = new ColonistEntry { Id = "Human8", Name = "Bo", Kills = 3, Days = 12 } } };
                string json = Persistence.JsonFileStore.ToJson(PlayerStatsStore.CaptureFrom(colonists, rosters, fallen));

                var c2 = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase);
                var r2 = new Dictionary<string, PlayerStatsStore.RosterState>(StringComparer.OrdinalIgnoreCase);
                var f2 = new List<PlayerStatsStore.FallenRecord>();
                PlayerStatsStore.InstallInto(Persistence.JsonFileStore.FromJson<PlayerStatsStore.ColonistState>(json), c2, r2, f2);
                bool kept = c2.ContainsKey("ANN") && r2.TryGetValue("ANN", out PlayerStatsStore.RosterState got)
                         && got.SaveId == "s9" && got.PawnsLost == 4 && Names(got.Colonists) == "Ada" && got.Colonists[0].Kills == 7
                         && got.Colonists[0].Id == "Human7" && f2.Count == 1 && f2[0].Colonist.Name == "Bo" && f2[0].Colonist.Id == "Human8"
                         && f2[0].ColonyDay == 30;

                var c3 = new Dictionary<string, ColonistProfile>(StringComparer.OrdinalIgnoreCase);
                var r3 = new Dictionary<string, PlayerStatsStore.RosterState>(StringComparer.OrdinalIgnoreCase) { ["stale"] = new PlayerStatsStore.RosterState() };
                var f3 = new List<PlayerStatsStore.FallenRecord> { new PlayerStatsStore.FallenRecord() };
                PlayerStatsStore.InstallInto(Persistence.JsonFileStore.FromJson<PlayerStatsStore.ColonistState>("{\"Colonists\":{\"ann\":{\"name\":\"Ada\"}}}"), c3, r3, f3);
                bool old = c3.Count == 1 && r3.Count == 0 && f3.Count == 0;
                return (kept && old, $"kept={kept} oldFile={old} ({json.Length} chars)");
            });

            Check("colonist records: a simulated player's colonists and fallen are hidden while unranked and shown while ranked; at most 100 fallen go out", () =>
            {
                string Owners(bool exclude)
                {
                    var file = new Simulation.SimulatedAccountsFile { ExcludeFromRankings = exclude, Accounts = new List<string> { "sim_ada" } };
                    using (Simulation.SimulatedAccounts.UseForTest(file))
                    {
                        var s = new ColonistRosterSnapshot();
                        s.Colonists.Add(new ColonistEntry { Owner = "sim_ada", Name = "Rex" });
                        s.Colonists.Add(new ColonistEntry { Owner = "ann", Name = "Bo" });
                        s.Fallen.Add(new FallenColonistEntry { Colonist = new ColonistEntry { Owner = "sim_ada", Name = "Old" } });
                        s.Fallen.Add(new FallenColonistEntry { Colonist = new ColonistEntry { Owner = "ann", Name = "Cy" } });
                        PlayerStatsHandler.ColonistRosterForClients(s);
                        return string.Join(",", s.Colonists.Select(c => c.Owner)) + "|" + string.Join(",", s.Fallen.Select(f => f.Colonist.Owner));
                    }
                }
                var many = new ColonistRosterSnapshot();
                for (int i = 0; i < 150; i++) many.Fallen.Add(new FallenColonistEntry { Colonist = new ColonistEntry { Owner = "ann", Name = "F" + i } });
                PlayerStatsHandler.ColonistRosterForClients(many);
                string off = Owners(true), on = Owners(false);
                bool ok = off == "ann|ann" && on == "sim_ada,ann|sim_ada,ann" && many.Fallen.Count == PlayerStatsHandler.MaxFallenSent
                       && many.Fallen[0].Colonist.Name == "F0";
                return (ok, $"unranked {off}; ranked {on}; fallen sent {many.Fallen.Count}");
            });

            Check("season archive: no record goes to a hidden player or an all-simulated guild, they do once ranked, and no blended 'Top Player'", () =>
            {
                var players = new List<PlayerLeaderboardEntry>
                {
                    new PlayerLeaderboardEntry { Username = "ann", Wealth = 100, Kills = 2 },
                    new PlayerLeaderboardEntry { Username = "sim_ada", Wealth = 900, Kills = 50 },
                };
                var reps = new List<Reputation.Dto.ReputationEntryDto>
                { new Reputation.Dto.ReputationEntryDto { Username = "sim_ada", Score = 80 }, new Reputation.Dto.ReputationEntryDto { Username = "ann", Score = 5 } };
                var cols = new List<ColonistEntry> { new ColonistEntry { Owner = "sim_ada", Name = "Rex", Kills = 30 }, new ColonistEntry { Owner = "ann", Name = "Bo", Kills = 1 } };
                var guilds = new List<Guilds.GuildStore.GuildSummary>
                {
                    new Guilds.GuildStore.GuildSummary { Name = "Bots", MemberCount = 9, TreasurySilver = 9000, AllSimulated = true },
                    new Guilds.GuildStore.GuildSummary { Name = "Iron", MemberCount = 2, TreasurySilver = 10 },
                };
                var vaults = new List<(string, long, bool)> { ("_personal:sim_ada", 5000, false), ("_personal:ann", 50, false) };
                string Leaders(bool unranked) => string.Join(", ", Seasons.SeasonStore.LeadersFrom(players, reps, cols, guilds, vaults,
                    n => unranked && n == "sim_ada", unranked).Select(x => x.Category + "=" + x.Holder));
                string off = Leaders(true), on = Leaders(false);
                List<string> calls = Maintenance.KmhIlReader.CallsMadeBy("KMHServerAddon.Features.Seasons.SeasonStore", "BuildCurrentLeaders");
                bool wired = calls.Contains("HiddenNow") && calls.Contains("get_ExcludesFromRankings");
                bool ok = !off.Contains("sim_ada") && !off.Contains("Bots") && off.Contains("Richest Colony=ann") && off.Contains("Deadliest Colonist=Bo (ann)")
                       && off.Contains("Top Banker=ann") && on.Contains("Deadliest Colonist=Rex (sim_ada)") && on.Contains("Top Guild=Bots")
                       && on.Contains("Top Banker=sim_ada") && !off.Contains("Top Player") && !on.Contains("Top Player") && wired;
                return (ok, $"wired={wired}; unranked: {off} | ranked: {on}");
            });

            Check("player stats: a list asked for again within a minute, or unchanged, is not resent; a changed one is, and a quiet one each ten minutes", () =>
            {
                long t0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc).Ticks;
                long At(int s) => t0 + TimeSpan.FromSeconds(s).Ticks;
                int built = 0;
                Func<string> Hash(string h) => () => { built++; return h; };
                bool first = PlayerStatsHandler.ShouldReply(null, 0, At(0), Hash("a"), out string h1) && h1 == "a";
                bool soon = !PlayerStatsHandler.ShouldReply("a", At(0), At(12), Hash("b"), out _);
                int builtSoon = built;
                bool same = !PlayerStatsHandler.ShouldReply("a", At(0), At(61), Hash("a"), out _);
                bool changed = PlayerStatsHandler.ShouldReply("a", At(0), At(61), Hash("b"), out string h2) && h2 == "b";
                bool clockBack = PlayerStatsHandler.ShouldReply("a", At(100), At(0), Hash("a"), out _);
                bool quiet = PlayerStatsHandler.ShouldReply("a", At(0), At(601), Hash("a"), out _);
                bool viaGate = HandlerCalls("OnRequest").Contains("ReplyIfChanged") && HandlerCalls("OnColonistRosterRequest").Contains("ReplyIfChanged");
                bool ok = first && soon && builtSoon == 1 && same && changed && clockBack && quiet && viaGate;
                return (ok, $"first={first} soon={soon} built={builtSoon} same={same} changed={changed} clockBack={clockBack} tenMinutesQuiet={quiet} gated={viaGate}");
            });

            Check("leaderboards: the kills board says hunted animals count, as the game's kill record does", () =>
            {
                string d = LeaderboardCatalog.Find("kills")?.Description ?? "";
                return (d.IndexOf("animals", StringComparison.OrdinalIgnoreCase) >= 0, d);
            });
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2")));
        }

        private static List<string> HandlerCalls(string method)
            => Maintenance.KmhIlReader.CallsMadeBy("KMHServerAddon.Features.PlayerStats.PlayerStatsHandler", method);

        internal const string CanonicalVector = "57d9cc4d33006343dd96d2e35e40581c721764af222784af31e6048dd0aaa82b";

        internal const string ImageVector = "cd3e36cc3494ddc70c56e5fec3c43d064eeab792e31ba7b6b3d04a52602ae2dc";

        internal const string ImagePngVector = "cfbe8b6899e4920915a0d974323b68547f7e749dbf81404882e09426a688c71c";
    }
}
