using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Chat;
using KMHServerAddon.Features.Chat.Dto;
using KMHServerAddon.Features.Discord;

namespace KMHServerAddon.Maintenance
{
    // Live send and broadcast are deliberately not exercised, because they would add messages other players see.
    internal static class KmhChatSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            string clamped = ChatStore.Normalize(new string('x', ChatStore.MaxMessageLength + 40));
            r.Add(("Chat: overlong message clamped", clamped.Length == ChatStore.MaxMessageLength, $"{clamped.Length}/{ChatStore.MaxMessageLength}"));

            string norm = ChatStore.Normalize("a\tb\nc");   // \t is a control char -> stripped, \n kept
            r.Add(("Chat: control stripped, \\n kept", norm == "ab\nc", $"'{norm}'"));

            r.Add(("Chat: blank normalizes to empty", ChatStore.Normalize("   ") == "", ""));

            long now = DateTime.UtcNow.Ticks;
            var ring = new List<ChatMessage>();
            for (int i = 0; i < ChatStore.MaxRecentPerChannel + 50; i++) ring.Add(new ChatMessage { Id = i + 1, SentUtcTicks = now });
            ChatStore.TrimRing(ring, now);
            r.Add(("Chat: ring bounded by count", ring.Count == ChatStore.MaxRecentPerChannel, $"{ring.Count}/{ChatStore.MaxRecentPerChannel}"));

            long old = now - TimeSpan.FromHours(ChatStore.RetentionHours + 1).Ticks;
            var aged = new List<ChatMessage> { new ChatMessage { Id = 1, SentUtcTicks = old }, new ChatMessage { Id = 2, SentUtcTicks = now } };
            ChatStore.TrimRing(aged, now);
            r.Add(("Chat: stale line dropped by age", aged.Count == 1 && aged[0].Id == 2, $"count={aged.Count}"));

            // Balances are int while caps and off-map value are long, so a large enough credit could wrap to a debt.
            int nearMax = int.MaxValue - 10;
            r.Add(("Treasury: a credit past int.MaxValue saturates, never wraps",
                   Util.KmhSafe.AddSaturating(nearMax, 1000, out bool cl1) == int.MaxValue && cl1, ""));
            r.Add(("Treasury: merging two large vaults cannot go negative",
                   Util.KmhSafe.AddSaturating(int.MaxValue - 1, int.MaxValue - 1, out _) > 0, ""));
            r.Add(("Treasury: an ordinary credit is exact and unflagged",
                   Util.KmhSafe.AddSaturating(1000, 234, out bool cl2) == 1234 && !cl2, ""));
            r.Add(("Treasury: a debit past int.MinValue saturates",
                   Util.KmhSafe.AddSaturating(int.MinValue + 5, -100, out _) == int.MinValue, ""));

            // Mail ids come straight from the request, so these two predicates are the whole defence for attached value.
            var toBob = new Features.Mail.Dto.MailMessage { FromUsername = "amy", ToUsername = "bob" };
            r.Add(("Mail: only the recipient may claim/decline/read/delete",
                   Features.Mail.MailStore.MayReceive(toBob, "bob")
                   && Features.Mail.MailStore.MayReceive(toBob, "BOB")
                   && !Features.Mail.MailStore.MayReceive(toBob, "amy")
                   && !Features.Mail.MailStore.MayReceive(toBob, "carl"), ""));
            r.Add(("Mail: only the sender may recall",
                   Features.Mail.MailStore.MayRecall(toBob, "amy")
                   && !Features.Mail.MailStore.MayRecall(toBob, "bob")
                   && !Features.Mail.MailStore.MayRecall(toBob, "carl"), ""));
            r.Add(("Mail: a null message or blank caller is never authorised",
                   !Features.Mail.MailStore.MayReceive(null, "bob")
                   && !Features.Mail.MailStore.MayReceive(toBob, "")
                   && !Features.Mail.MailStore.MayRecall(null, "amy")
                   && !Features.Mail.MailStore.MayRecall(toBob, null), ""));

            bool dmSym = ChatChannels.Dm("Bob", "amy") == ChatChannels.Dm("amy", "BOB");
            r.Add(("Chat: DM channel is direction-independent", dmSym, ChatChannels.Dm("Bob", "amy")));

            bool wf = ChatChannels.IsWellFormed(ChatChannels.Server)
                   && ChatChannels.IsWellFormed(ChatChannels.Guild("Wolves"))
                   && ChatChannels.IsWellFormed(ChatChannels.Dm("a", "b"))
                   && !ChatChannels.IsWellFormed("bogus") && !ChatChannels.IsWellFormed("guild:") && !ChatChannels.IsWellFormed("dm:solo");
            r.Add(("Chat: channel well-formedness", wf, ""));

            bool parse = ChatChannels.TryParseGuild(ChatChannels.Guild("Wolves"), out string gn) && gn == "Wolves"
                      && ChatChannels.TryParseDm(ChatChannels.Dm("a", "b"), out _, out _);
            r.Add(("Chat: guild/DM channel parse", parse, gn));

            // A '|' inside a username can build an id the parser rejects, or make two different pairs share one room.
            r.Add(("Chat: a DM id always parses back, or is refused",
                   string.IsNullOrEmpty(ChatChannels.Dm("user|weird", "bob"))
                   || ChatChannels.TryParseDm(ChatChannels.Dm("user|weird", "bob"), out _, out _),
                   ChatChannels.Dm("user|weird", "bob")));
            r.Add(("Chat: distinct pairs cannot share a DM id",
                   string.IsNullOrEmpty(ChatChannels.Dm("a", "b|c"))
                   || ChatChannels.Dm("a", "b|c") != ChatChannels.Dm("a|b", "c"),
                   ChatChannels.Dm("a", "b|c") + " vs " + ChatChannels.Dm("a|b", "c")));

            string dm = ChatChannels.Dm("amy", "bob");
            bool part = ChatChannels.IsDmParticipant("AMY", dm) && ChatChannels.IsDmParticipant("bob", dm) && !ChatChannels.IsDmParticipant("carl", dm);
            r.Add(("Chat: DM participant check", part, ""));

            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spammer" };
            var feed = new List<ChatMessage>
            {
                new ChatMessage { Id = 1, FromUsername = "amy" },
                new ChatMessage { Id = 2, FromUsername = "Spammer" },
                new ChatMessage { Id = 3, FromUsername = "bob" },
            };
            var filtered = ChatModerationStore.FilterOut(feed, blocked);
            r.Add(("Chat: blocked sender filtered from history", filtered.Count == 2 && !filtered.Exists(m => m.Id == 2), $"kept {filtered.Count}"));

            // Every request rebuilds a whole snapshot under a store lock, so the cap is what stops one client stalling everyone.
            r.Add(("Router: snapshot requests count toward the flood cap",
                   SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.TreasuryRequest)
                   && SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.MarketplaceRequest)
                   && SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.GuildRequest), ""));
            r.Add(("Router: mutations count toward the flood cap",
                   SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.TreasuryDepositSilver)
                   && SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.MarketplaceBuy), ""));
            r.Add(("Router: only handshake + diagnostics are exempt",
                   !SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.Ping)
                   && !SubProtocol.KmhRouter.CountsTowardInboundCap(SubProtocol.KmhProtocol.Kind.HelloAck)
                   && !SubProtocol.KmhRouter.CountsTowardInboundCap(null), ""));
            // A ping answers with a pong and an ack re-sends the hello, so exempt from the feature cap is not uncapped.
            r.Add(("Router: the exempt kinds have a cap of their own",
                   SubProtocol.KmhRouter.MaxHandshakePerWindow > 0
                   && SubProtocol.KmhRouter.MaxHandshakePerWindow < SubProtocol.KmhRouter.MaxInboundPerWindow,
                   $"{SubProtocol.KmhRouter.MaxHandshakePerWindow}/{SubProtocol.KmhRouter.HandshakeWindowSeconds}s "
                 + $"vs feature {SubProtocol.KmhRouter.MaxInboundPerWindow}/{SubProtocol.KmhRouter.InboundWindowSeconds}s"));

            // The list is rewritten per change with no check the target exists, so the ceiling is what stops a disk fill.
            r.Add(("Chat: block list refuses additions at the ceiling",
                   ChatModerationStore.AtBlockLimit(200, 200) && ChatModerationStore.AtBlockLimit(201, 200), ""));
            r.Add(("Chat: under the ceiling still accepts", !ChatModerationStore.AtBlockLimit(199, 200), ""));
            r.Add(("Chat: a 0/negative ceiling means unlimited, not locked out",
                   !ChatModerationStore.AtBlockLimit(9999, 0) && !ChatModerationStore.AtBlockLimit(9999, -1), ""));

            // The ceiling bounds the list's size but not one name being toggled forever, rewriting the file each time.
            var churn = new Util.KmhRateWindow();
            long churnAt = System.DateTime.UtcNow.Ticks;
            var churnCfg = new ChatConfig { MaxBlockChangesPerWindow = 20, BlockChangeWindowSeconds = 60 };
            int refused = 0;
            for (int i = 0; i < 30; i++)
                if (ChatModerationStore.RefuseForChurn(churn, "griefer", true, churnAt, churnCfg)) refused++;
            r.Add(("Chat: block-list churn is refused past the window cap", refused == 10, $"{30 - refused} of 30 allowed"));
            r.Add(("Chat: the window slides, so churn throttles rather than locks out",
                   !ChatModerationStore.RefuseForChurn(churn, "griefer", true,
                       churnAt + System.TimeSpan.FromSeconds(61).Ticks, churnCfg), ""));
            r.Add(("Chat: one player's churn does not throttle another",
                   !ChatModerationStore.RefuseForChurn(churn, "bystander", true, churnAt, churnCfg), ""));
            // A repeat of something already set rewrites nothing, so it must not burn the player's own allowance.
            var quiet = new Util.KmhRateWindow();
            int noOpRefusals = 0;
            for (int i = 0; i < 100; i++)
                if (ChatModerationStore.RefuseForChurn(quiet, "amy", false, churnAt, churnCfg)) noOpRefusals++;
            r.Add(("Chat: a no-op block change is never charged", noOpRefusals == 0
                   && !ChatModerationStore.RefuseForChurn(quiet, "amy", true, churnAt, churnCfg), $"{noOpRefusals} refused"));

            var cfg = new ChatConfig { MaxMessageLength = 100000, MaxSendsPerWindow = 0, SendWindowSeconds = -5, RetentionHours = 99999,
                                       MaxBlockChangesPerWindow = 0, BlockChangeWindowSeconds = 99999 };
            cfg.Clamp();
            r.Add(("Chat: block-churn limits clamped too",
                   cfg.MaxBlockChangesPerWindow >= 1 && cfg.BlockChangeWindowSeconds <= 3600,
                   $"{cfg.MaxBlockChangesPerWindow}/{cfg.BlockChangeWindowSeconds}s"));
            bool clampOk = cfg.MaxMessageLength <= 4000 && cfg.MaxSendsPerWindow >= 1 && cfg.SendWindowSeconds >= 1 && cfg.RetentionHours <= 720;
            r.Add(("Chat: config limits clamped to sane range", clampOk, $"len={cfg.MaxMessageLength} rate={cfg.MaxSendsPerWindow}/{cfg.SendWindowSeconds}s ret={cfg.RetentionHours}h"));

            bool relayGuard =
                   KmhChatDiscordBridge.ShouldRelayOutbound(new ChatMessage { Origin = "ingame", Channel = ChatChannels.Server })
                && !KmhChatDiscordBridge.ShouldRelayOutbound(new ChatMessage { Origin = "discord", Channel = ChatChannels.Server })   // loop guard
                && !KmhChatDiscordBridge.ShouldRelayOutbound(new ChatMessage { Origin = "ingame", Channel = ChatChannels.Guild("W") }); // server-only
            r.Add(("Chat: Discord outbound loop guard (ingame server only)", relayGuard, ""));

            var win = new KMHServerAddon.Util.KmhRateWindow();
            long t0 = DateTime.UtcNow.Ticks;
            bool w1 = win.Allow("bob", t0, 2, 30) && win.Allow("bob", t0, 2, 30);
            bool w2 = !win.Allow("bob", t0, 2, 30);
            bool w3 = win.Allow("amy", t0, 2, 30);
            bool w4 = win.Allow("bob", t0 + TimeSpan.FromSeconds(31).Ticks, 2, 30);
            r.Add(("Chat: shared rate window (per-user, slides, refuses over cap)", w1 && w2 && w3 && w4, $"{w1}{w2}{w3}{w4}"));

            KmhChatDiscordBridge.ResetQueueForTest();
            KmhChatDiscordBridge.EnqueueForTest("one");
            KmhChatDiscordBridge.EnqueueForTest("two");
            KmhChatDiscordBridge.EnqueueForTest("three");
            string batch = KmhChatDiscordBridge.TakeBatchForTest();
            bool ordered = batch == "one\ntwo\nthree";
            bool drained = KmhChatDiscordBridge.TakeBatchForTest() == null;
            r.Add(("Chat: Discord relay batches in order then drains", ordered && drained, $"'{batch?.Replace("\n", "|")}'"));

            KmhChatDiscordBridge.ResetQueueForTest();
            for (int i = 0; i < 260; i++) KmhChatDiscordBridge.EnqueueForTest($"line{i}");
            string over = KmhChatDiscordBridge.TakeBatchForTest();
            bool notedDrop = over != null && over.Contains("omitted");
            r.Add(("Chat: Discord relay reports dropped lines when behind", notedDrop, notedDrop ? "overflow noted" : "overflow SILENT"));
            KmhChatDiscordBridge.ResetQueueForTest();

            bool identity = KmhChatDiscordBridge.ResolveSender("Alice", "al#1") == "Alice"
                         && KmhChatDiscordBridge.ResolveSender(null, "al#1") == "al#1"
                         && KmhChatDiscordBridge.ResolveSender(null, "") == "Discord";
            r.Add(("Chat: Discord sender identity (linked wins, else display)", identity, ""));

            // An unlinked Discord display that matches a real account must not be shown as that account.
            string spoof = KmhChatDiscordBridge.ResolveSender(null, "Alice", displayIsTakenName: true);
            bool noSpoof = spoof != "Alice" && spoof.Contains("Alice");
            r.Add(("Chat: unlinked Discord name matching an account is disambiguated", noSpoof, spoof));

            // Playtime is a client's claim, so the wall clock decides how much of it is believable.
            long tNow = DateTime.UtcNow.Ticks;
            long tMinuteAgo = DateTime.UtcNow.AddMinutes(-1).Ticks;
            bool clamps = Features.PlayerStats.PlayerStatsStore.Believable(3600, tMinuteAgo, tNow) <= 62
                       && Features.PlayerStats.PlayerStatsStore.Believable(30, tMinuteAgo, tNow) == 30
                       && Features.PlayerStats.PlayerStatsStore.Believable(99999, 0, tNow)
                              == Features.PlayerStats.PlayerStatsStore.MaxReportSeconds
                       && Features.PlayerStats.PlayerStatsStore.Believable(0, tMinuteAgo, tNow) == 0
                       && Features.PlayerStats.PlayerStatsStore.Believable(-5, tMinuteAgo, tNow) == 0;
            r.Add(("Standings: reported playtime can never outrun the time that actually passed", clamps,
                   Features.PlayerStats.PlayerStatsStore.Believable(3600, tMinuteAgo, tNow).ToString()));

            // Connected time is measured here rather than claimed, so only a clock that jumps can distort it.
            long tHourAgo = DateTime.UtcNow.AddHours(-1).Ticks;
            bool rolls = Features.PlayerStats.PlayerStatsStore.Rollable(tHourAgo, tNow) >= 3599
                      && Features.PlayerStats.PlayerStatsStore.Rollable(tHourAgo, tNow) <= 3601
                      && Features.PlayerStats.PlayerStatsStore.Rollable(0, tNow) == 0
                      && Features.PlayerStats.PlayerStatsStore.Rollable(tNow, tMinuteAgo) == 0
                      && Features.PlayerStats.PlayerStatsStore.Rollable(1, tNow)
                             == Features.PlayerStats.PlayerStatsStore.MaxSessionRollSeconds;
            r.Add(("Standings: a session credits the time it really ran, and a clock jump is capped", rolls,
                   Features.PlayerStats.PlayerStatsStore.Rollable(tHourAgo, tNow).ToString()));

            return r;
        }
    }
}
