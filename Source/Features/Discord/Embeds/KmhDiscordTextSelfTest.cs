using System.Collections.Generic;

namespace KMHServerAddon.Features.Discord
{
    // The relay flattens sender and body into one line, so a surviving newline forges somebody else's message.
    internal static class KmhDiscordTextSelfTest
    {
        private const string Zwsp = "​";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Reading the quantity from the listing's segment would make "Buy 1" on #57 buy 57.
            {
                var listing = new Marketplace.Dto.MarketplaceListing { Id = 57, RemainingQty = 80, SellerUsername = "seller" };
                var got = new List<string>();
                foreach (Kmh.Discord.Wire.KmhButtonSpec b in DiscordBuyButton.BuildBuyComponents(listing).Buttons)
                {
                    bool parsed = DiscordBuyButton.TryParse(b.CustomId, out _, out long id, out string qty);
                    got.Add($"{b.Label}->{(parsed ? id + "x" + DiscordBuyButton.QuantityFor(qty, listing.RemainingQty) : "unparsed")}");
                }
                string joined = string.Join(", ", got);
                r.Add(("Discord: a buy button buys the quantity on its label, from the listing it names",
                       joined == "Buy 1->57x1, Buy 10->57x10, Buy all (80)->57x80", joined));
            }

            {
                var created = new HashSet<ulong> { 20 };
                var handMade = new[] { (10UL, "Staff (Admin)") };
                var both = new[] { (10UL, "Staff (Admin)"), (20UL, "Staff (Admin)") };
                DiscordGuildRoleSync.RoleChoice refused = DiscordGuildRoleSync.Choose(handMade, "Staff (Admin)", created, out _);
                DiscordGuildRoleSync.RoleChoice ours = DiscordGuildRoleSync.Choose(both, "Staff (Admin)", created, out ulong id);
                DiscordGuildRoleSync.RoleChoice fresh = DiscordGuildRoleSync.Choose(handMade, "Farmers (Member)", created, out _);
                r.Add(("Discord: a guild named after an owner's role is not given that role",
                       refused == DiscordGuildRoleSync.RoleChoice.Refuse && ours == DiscordGuildRoleSync.RoleChoice.UseOurs && id == 20
                       && fresh == DiscordGuildRoleSync.RoleChoice.Create, $"{refused}/{ours}:{id}/{fresh}"));
                r.Add(("Discord: role sync takes away only roles it made",
                       !DiscordGuildRoleSync.MayRemove(10, "Staff (Admin)", "", created)
                       && DiscordGuildRoleSync.MayRemove(20, "Staff (Admin)", "", created)
                       && !DiscordGuildRoleSync.MayRemove(20, "Staff (Admin)", "Staff (Admin)", created), ""));
            }

            string spoof = "hello\n**Admin**: the server is moving, join discord.gg/evil";
            string body  = DiscordText.SafeRelayBody(spoof);
            r.Add(("Discord: relay body cannot open a new line",
                   !body.Contains("\n") && !body.Contains("\r"), body));

            r.Add(("Discord: relay body keeps markdown readable",
                   DiscordText.SafeRelayBody("*hi*") == "*hi*", ""));

            // A text label, because Discord still renders [label](<url>) as a masked link once the url is wrapped.
            string relayMasked = DiscordText.SafeRelayBody("[Colony Nexus](https://evil.example/x) see https://a.example/b");
            string invite = DiscordText.SafeRelayBody("join discord.gg/evil or discordapp.com/invite/evil");
            r.Add(("Discord: relay body shows no masked link, unfurls no link and carries no live invite",
                   !relayMasked.Contains("](") && relayMasked.Contains("<https://evil.example/x>") && relayMasked.Contains("<https://a.example/b>")
                   && !invite.Contains("discord.gg/") && !invite.Contains("discordapp.com/invite/"), relayMasked + " | " + invite));

            string relayed = KmhChatDiscordBridge.LineFor("bob", "my box is 203.0.113.9");
            r.Add(("Discord: a relayed chat line is redacted before its batch is measured",
                   !relayed.Contains("203.0.113.9"), relayed));

            r.Add(("Discord: relay body defuses @everyone",
                   !DiscordText.SafeRelayBody("@everyone").Contains("@everyone")
                   && DiscordText.SafeRelayBody("@everyone").Contains("@" + Zwsp), ""));
            r.Add(("Discord: name defuses @everyone",
                   !DiscordText.SafeName("@everyone").Contains("@everyone"), ""));

            r.Add(("Discord: name cannot inject markdown",
                   DiscordText.SafeName("**Admin**") == "\\*\\*Admin\\*\\*", DiscordText.SafeName("**Admin**")));
            r.Add(("Discord: name cannot break the line",
                   !DiscordText.SafeName("bob\nadmin").Contains("\n"), ""));

            r.Add(("Discord: CRLF and CR are flattened",
                   !DiscordText.OneLine("a\r\nb").Contains("\r")
                   && !DiscordText.OneLine("a\rb").Contains("\r")
                   && !DiscordText.OneLine("a\r\nb").Contains("\n"), ""));

            r.Add(("Discord: Escape is markdown-only",
                   DiscordText.Escape("a*b") == "a\\*b" && DiscordText.Escape("@x") == "@x", ""));

            // A colony or player name is free text, and Discord renders [text](url) as a link wherever it lands.
            string masked = DiscordText.SafeName("[Claim prize](https://evil.example)");
            r.Add(("Discord: a name cannot become a masked link", masked == "\\[Claim prize\\]\\(https://evil.example\\)", masked));

            string fenced = DiscordConsoleFeed.Block("player says ``` then ```` and `` ok");
            int fences = 0;
            for (int i = fenced.IndexOf("```", StringComparison.Ordinal); i >= 0; i = fenced.IndexOf("```", i + 3, StringComparison.Ordinal)) fences++;
            r.Add(("Discord console feed: backticks in a console line cannot close the code block",
                   fences == 2 && fenced.StartsWith("```\n", StringComparison.Ordinal) && fenced.EndsWith("\n```", StringComparison.Ordinal),
                   fenced.Replace("\n", " / ")));

            r.Add(("Discord: null and empty are safe",
                   DiscordText.SafeName(null) == "" && DiscordText.SafeRelayBody(null) == ""
                   && DiscordText.OneLine(null) == "" && DiscordText.NoMentions(null) == "", ""));

            // An unrecognised sort falls back to the default, so a key missing from the vocabulary is otherwise invisible.
            r.Add(("Discord: every leaderboard sort key survives normalisation, and aliases land on a real one",
                   AllSortsRoundTrip()
                   && DiscordLeaderboardBuilder.NormalizeSort("activetime") == "active"
                   && DiscordLeaderboardBuilder.NormalizeSort("notafk") == "active"
                   && DiscordLeaderboardBuilder.NormalizeSort("nonsense") == DiscordLeaderboardBuilder.DefaultSort, ""));

            Live(r);
            return r;
        }

        // Records what would have gone to Discord, and hands out message ids as Discord would.
        private sealed class Recorder : IDiscordLiveSink
        {
            public readonly List<(ulong Channel, ulong Message, string Content, byte[] File, bool PostIfMissing)> Calls
                = new List<(ulong, ulong, string, byte[], bool)>();
            public ulong NextId = 100;
            public bool Fail;

            public Task<ulong> PostOrEditAsync(ulong channelId, ulong messageId, string content,
                                               IReadOnlyList<(string Name, byte[] Bytes)> files, bool postIfMissing)
            {
                Calls.Add((channelId, messageId, content, files != null && files.Count > 0 ? files[0].Bytes : null, postIfMissing));
                if (Fail) return Task.FromResult(0UL);
                return Task.FromResult(messageId != 0 ? messageId : ++NextId);
            }

            public Task<ulong> PostAsync(ulong channelId, string content, IReadOnlyList<ulong> pingRoles)
            {
                Calls.Add((channelId, 0, content, null, true));
                return Task.FromResult(++NextId);
            }
        }

        private static void Live(List<(string Name, bool Ok, string Detail)> r)
        {
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw {ex.GetType().Name}: {ex.Message}")); }
            }
            Leaderboards.LeaderboardEngine.Inputs Standings()
            {
                var inputs = new Leaderboards.LeaderboardEngine.Inputs { Season = 2, Now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc) };
                inputs.Players.Add(new PlayerStats.Dto.PlayerLeaderboardEntry { Username = "Ann", SalesEarned = 300 });
                inputs.Players.Add(new PlayerStats.Dto.PlayerLeaderboardEntry { Username = "Bea", SalesEarned = 200 });
                return inputs;
            }
            DiscordConfig Cfg()
            {
                var cfg = new DiscordConfig { Enabled = true };
                cfg.Channels.Leaderboard = "111111111111111111";
                cfg.Live.LeaderboardBoards = new[] { "auto" };
                return cfg;
            }
            // Each run gets its own state files, so nothing here touches the runner's KMH-Data.
            T Sandboxed<T>(Func<T> body)
            {
                string dir = Path.Combine(Path.GetTempPath(), "kmh-discord-live-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                DiscordLeaderboardState.PathForTest = Path.Combine(dir, "LeaderboardState.json");
                DiscordLiveState.PathForTest = Path.Combine(dir, "LiveState.json");
                DiscordLeaderboardPoster.InputsForTest = Standings;
                DiscordLeaderboardPoster.ImageForTest = b => ("leaderboard-" + b.Key + ".png", new byte[] { 1, 2, 3 });
                try { DiscordLive.ReloadStateForTest(); return body(); }
                finally
                {
                    DiscordLeaderboardState.PathForTest = null;
                    DiscordLiveState.PathForTest = null;
                    DiscordLeaderboardPoster.InputsForTest = null;
                    DiscordLeaderboardPoster.ImageForTest = null;
                    DiscordLive.ReloadStateForTest();
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }
            }
            void Tick(DiscordConfig cfg, DateTime at, Recorder rec) => DiscordLive.RunTickAsync(cfg, at, rec, true, false).GetAwaiter().GetResult();

            Check("Discord live board: at the turn of the day the old message gets its final edit, then a new one is posted", () => Sandboxed(() =>
            {
                DiscordConfig cfg = Cfg();
                var rec = new Recorder();
                Tick(cfg, new DateTime(2026, 10, 15, 23, 40, 0, DateTimeKind.Utc), rec);
                Tick(cfg, new DateTime(2026, 10, 15, 23, 50, 0, DateTimeKind.Utc), rec);
                Tick(cfg, new DateTime(2026, 10, 16, 0, 0, 30, DateTimeKind.Utc), rec);
                if (rec.Calls.Count != 3) return (false, $"{rec.Calls.Count} call(s): " + string.Join(" | ", rec.Calls.ConvertAll(c => $"{c.Message}:{c.Content}")));
                var first = rec.Calls[0]; var final = rec.Calls[1]; var next = rec.Calls[2];
                bool ok = first.Message == 0 && first.Content.Contains("live · updates every 15 min · final at 00:00 UTC") && first.File != null
                       && final.Message == 101 && !final.PostIfMissing && final.Content == "**Trade earnings** · final for 15 Oct 2026" && final.File != null
                       && next.Message == 0 && next.Content.Contains("final at 00:00 UTC") && DiscordLive.BoardsForTest.Slot("auto").MessageId == 102;
                return (ok, $"post #{first.Message} '{first.Content}' / final on #{final.Message} '{final.Content}' / then #{next.Message}");
            }));

            Check("Discord live board: a restart carries on editing the same day's message instead of posting a second", () => Sandboxed(() =>
            {
                DiscordConfig cfg = Cfg();
                var rec = new Recorder();
                Tick(cfg, new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc), rec);
                DiscordLive.ReloadStateForTest();
                Tick(cfg, new DateTime(2026, 10, 15, 9, 20, 0, DateTimeKind.Utc), rec);
                bool ok = rec.Calls.Count == 2 && rec.Calls[0].Message == 0 && rec.Calls[1].Message == 101 && rec.Calls[1].PostIfMissing;
                return (ok, string.Join(" | ", rec.Calls.ConvertAll(c => $"#{c.Message}")));
            }));

            Check("Discord live board: nobody ranked posts nothing, and the posted image is a real PNG over 1 KB", () => Sandboxed(() =>
            {
                DiscordConfig cfg = Cfg();
                var rec = new Recorder();
                DiscordLeaderboardPoster.InputsForTest = () => new Leaderboards.LeaderboardEngine.Inputs();
                Tick(cfg, new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc), rec);
                int emptyCalls = rec.Calls.Count;
                DiscordLeaderboardPoster.InputsForTest = Standings;
                DiscordLeaderboardPoster.ImageForTest = null;
                Tick(cfg, new DateTime(2026, 10, 15, 9, 1, 0, DateTimeKind.Utc), rec);
                byte[] png = rec.Calls.Count == 1 ? rec.Calls[0].File : null;
                bool isPng = png != null && png.Length > 1024 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47;
                return (emptyCalls == 0 && isPng, $"calls with nobody ranked={emptyCalls}; image {(png == null ? "none" : png.Length + " bytes")}");
            }));

            Check("Discord activity log: one line per event, names unable to ping or format, the time in UTC", () =>
            {
                DateTime at = new DateTime(2026, 10, 15, 12, 4, 0, DateTimeKind.Utc);
                string listed = DiscordActivityLog.Listed(at, "@everyone", 47, "Wood *log*\nfake line", 1);
                string bought = DiscordActivityLog.Bought(at.AddMinutes(2), "KNAPPE0", 3, "Steel", 15, "Sim<@123>Otis");
                bool ok = listed == "🛒 12:04 @" + Zwsp + "everyone listed 47× Wood \\*log\\* fake line @ 1s"
                       && bought == "💰 12:06 KNAPPE0 bought 3× Steel for 15s from Sim<@" + Zwsp + "123>Otis";
                return (ok, listed + " / " + bought);
            });

            Check("Discord activity log: lines fill one message under Discord's limit, then a part 2 starts", () =>
            {
                DiscordActivityLog.ResetForTest();
                var state = new DiscordLiveState();
                var rec = new Recorder();
                DateTime at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < 40; i++)
                    DiscordActivityLog.Enqueue(222, DiscordActivityLog.Listed(at, "Seller" + i, i + 1, "A fairly long item label number " + i, 10));
                DiscordActivityLog.FlushAsync(state, at, 0, rec, true).GetAwaiter().GetResult();
                DiscordActivityLog.ResetForTest();
                bool under = rec.Calls.TrueForAll(c => c.Content.Length <= DiscordCompactText.MaxChars);
                bool parts = rec.Calls.Count >= 2 && rec.Calls[0].Message == 0 && rec.Calls[1].Message == 0
                          && rec.Calls[1].Content.Contains("part 2") && rec.Calls[rec.Calls.Count - 1].Content.Contains("Seller39");
                int total = 0;
                foreach (var c in rec.Calls) total += c.Content.Split('\n').Length - 1;
                return (under && parts && total == 40, $"{rec.Calls.Count} message(s), longest {rec.Calls.ConvertAll(c => c.Content.Length).DefaultIfEmpty(0).Max()} chars, {total} line(s)");
            });

            Check("Discord activity log: a burst inside the edit gap waits for the next flush and lands in the same message", () =>
            {
                DiscordActivityLog.ResetForTest();
                var state = new DiscordLiveState();
                var rec = new Recorder();
                DateTime at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
                DiscordActivityLog.Enqueue(333, "first");
                DiscordActivityLog.FlushAsync(state, at, 0, rec, true).GetAwaiter().GetResult();
                DiscordActivityLog.Enqueue(333, "second");
                DiscordActivityLog.FlushAsync(state, at.AddSeconds(5), 0, rec, true).GetAwaiter().GetResult();
                int during = rec.Calls.Count;
                DiscordActivityLog.FlushAsync(state, at.AddSeconds(30), 0, rec, true).GetAwaiter().GetResult();
                DiscordActivityLog.ResetForTest();
                bool ok = during == 1 && rec.Calls.Count == 2 && rec.Calls[0].Message == 0 && rec.Calls[1].Message == 101
                       && rec.Calls[1].Content.EndsWith("first\nsecond", StringComparison.Ordinal);
                return (ok, $"calls inside the gap={during}, after={rec.Calls.Count}");
            });

            Check("Discord activity log: lines Discord refused are kept for the next flush, not lost", () =>
            {
                DiscordActivityLog.ResetForTest();
                var state = new DiscordLiveState();
                var rec = new Recorder { Fail = true };
                DateTime at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
                DiscordActivityLog.Enqueue(444, "kept one");
                DiscordActivityLog.Enqueue(444, "kept two");
                DiscordActivityLog.FlushAsync(state, at, 0, rec, true).GetAwaiter().GetResult();
                rec.Fail = false;
                DiscordActivityLog.FlushAsync(state, at.AddSeconds(30), 0, rec, true).GetAwaiter().GetResult();
                DiscordActivityLog.ResetForTest();
                bool ok = rec.Calls.Count == 2 && rec.Calls[1].Content.EndsWith("kept one\nkept two", StringComparison.Ordinal)
                       && state.Logs.Count == 1 && state.Logs[0].MessageId == 101;
                return (ok, $"{rec.Calls.Count} call(s); retried: {(rec.Calls.Count > 1 ? rec.Calls[1].Content.Replace("\n", " / ") : "nothing")}");
            });

            Check("Discord status: one compact message names what an owner must see, and says when persistence is degraded", () =>
            {
                var f = new DiscordAdminStatus.Facts
                {
                    People = 3, Simulated = 30, Uptime = new TimeSpan(2, 4, 12, 0), Build = "1.3.1 (rel-279)",
                    PersistenceOk = false, PersistenceDetail = "Persistence: 1 unwritable, 2 unsaved/retrying",
                    Backup = "15 Oct 12:00 UTC (scheduled) · OK", Global = "live · last heartbeat 12:41 UTC", OpenReports = 2,
                    Discord = "connected · no send errors",
                };
                string text = DiscordAdminStatus.Compose(f, DiscordAdminStatus.LiveHeader(new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc),
                                                                                        new DateTime(2026, 10, 15, 12, 45, 0, DateTimeKind.Utc)));
                string[] lines = text.Split('\n');
                bool ok = lines.Length == 8 && lines[0].Contains("final at 00:00 UTC") && lines[1] == "👥 Online: 3 people · 30 simulated"
                       && lines[2].StartsWith("⏱️ Up 2d 4h 12m · Colony Nexus 1.3.1 \\(rel-279\\)", StringComparison.Ordinal)
                       && lines[3].Contains("DEGRADED") && lines[3].Contains("1 unwritable") && lines[6] == "📝 Open reports: 2"
                       && text.Length <= DiscordCompactText.MaxChars;
                return (ok, text.Replace("\n", " / "));
            });

            Check("Discord alerts: a condition alerts once, says when it clears, and the channel's hourly budget holds", () =>
            {
                var m = new DiscordAlerts.Memory();
                DateTime at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
                var bad = new DiscordAlerts.Inputs { PersistenceDegraded = true, PersistenceDetail = "1 unwritable" };
                int first = DiscordAlerts.Evaluate(bad, m, at).Count;
                int again = DiscordAlerts.Evaluate(bad, m, at.AddMinutes(1)).Count;
                List<string> cleared = DiscordAlerts.Evaluate(new DiscordAlerts.Inputs(), m, at.AddMinutes(2));
                int reminder = DiscordAlerts.Evaluate(bad, m, at.AddHours(7)).Count;
                var noisy = new DiscordAlerts.Inputs();
                for (int i = 0; i < 20; i++) noisy.ErrorBursts.Add(("Source" + i, 6, "boom @here"));
                List<string> burst = DiscordAlerts.Evaluate(noisy, new DiscordAlerts.Memory(), at);
                bool ok = first == 1 && again == 0 && cleared.Count == 1 && cleared[0].Contains("recovered") && reminder == 1
                       && burst.Count == DiscordAlerts.MaxPerHour && !burst[0].Contains("@here");
                return (ok, $"first={first} again={again} cleared={cleared.Count} reminder={reminder} burst={burst.Count}");
            });

            Check("Discord alerts: the error tally finds a source failing over and over, not scattered noise", () =>
            {
                Diagnostics.KmhErrorTally.ResetForTest();
                DateTime at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < 5; i++) Diagnostics.KmhErrorTally.Note("Persistence: save FAILED for Guilds.json (" + i + ")", at.AddSeconds(i));
                Diagnostics.KmhErrorTally.Note("Discord: one failure", at);
                var hot = Diagnostics.KmhErrorTally.Bursts(at.AddMinutes(1), DiscordAlerts.ErrorWindow, DiscordAlerts.ErrorBurst);
                var later = Diagnostics.KmhErrorTally.Bursts(at.AddMinutes(11), DiscordAlerts.ErrorWindow, DiscordAlerts.ErrorBurst);
                Diagnostics.KmhErrorTally.ResetForTest();
                bool ok = hot.Count == 1 && hot[0].Source == "Persistence" && hot[0].Count == 5 && later.Count == 0;
                return (ok, string.Join(", ", hot.ConvertAll(h => $"{h.Source}x{h.Count}")) + $" / after the window: {later.Count}");
            });

            Check("Discord replies: a card becomes compact text that cannot ping and unfurls no link", () =>
            {
                var card = new Kmh.Discord.Wire.KmhEmbedSpecBuilder().WithTitle("Search: @everyone")
                    .WithDescription("See https://example.org/x and [page](https://example.org/y)")
                    .AddField("Listing", "#12", true).AddField("Seller", "Ann", true).WithFooter("2 listings").Build();
                string text = DiscordCompactText.Render(card);
                bool ok = text == "**Search: @" + Zwsp + "everyone**\nSee <https://example.org/x> and [page](<https://example.org/y>)\n"
                                + "**Listing:** #12 · **Seller:** Ann\n-# 2 listings";
                return (ok, text.Replace("\n", " / "));
            });
        }

        private static bool AllSortsRoundTrip()
        {
            foreach (string key in DiscordLeaderboardBuilder.SupportedSorts)
                if (DiscordLeaderboardBuilder.NormalizeSort(key) != key) return false;
            return true;
        }
    }
}
