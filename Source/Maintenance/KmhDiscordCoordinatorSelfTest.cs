using System;
using System.Collections.Generic;
using System.Linq;
using Kmh.Discord.Wire;
using KMHServerAddon.Features.Discord;

namespace KMHServerAddon.Maintenance
{
    // The two things that must never happen: a second gateway session, and a retried request run twice.
    internal static class KmhDiscordCoordinatorSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            List<(string, bool, string)> r = new List<(string, bool, string)>();
            ModeGuards(r);
            RequestShape(r);
            ResultShape(r);
            Privacy(r);
            return r.Select(x => (x.Item1, x.Item2, x.Item3)).ToList();
        }

        private static void ModeGuards(List<(string, bool, string)> r)
        {
            DiscordConfig direct = new DiscordConfig();
            direct.Bot.Token = "a-local-bot-token";
            bool directUsesToken = direct.BotToken.Length > 0 && direct.IsEnabled && !direct.CoordinatorMode;
            r.Add(("coordinator mode off: a local token still runs the bridge directly", directUsesToken, ""));

            DiscordConfig fleet = new DiscordConfig();
            fleet.Bot.Token = "a-local-bot-token";
            fleet.Coordinator.Enabled = true;
            fleet.Coordinator.Url = "http://127.0.0.1:20079";
            bool ignoresToken = fleet.CoordinatorMode && fleet.BotToken.Length == 0 && fleet.IsEnabled;
            r.Add(("coordinator mode on: a leftover local token is ignored, so two bot sessions cannot open",
                   ignoresToken, ignoresToken ? "BotToken is empty in coordinator mode" : "TOKEN STILL LIVE"));

            DiscordConfig unconfigured = new DiscordConfig();
            unconfigured.Coordinator.Enabled = true;
            r.Add(("coordinator mode on with no url: enabled, but the link refuses to guess an endpoint",
                   unconfigured.CoordinatorMode && unconfigured.Coordinator.Url.Length == 0, ""));

            r.Add(("coordinator mode: no link is active in an offline suite, so nothing dials out",
                   !DiscordCoordinatorLink.Active, DiscordCoordinatorLink.Describe()));

            // Every DiscordConfig.json written before coordinator mode existed has no Coordinator property at all.
            DiscordConfig legacy = Newtonsoft.Json.JsonConvert.DeserializeObject<DiscordConfig>(
                "{\"Enabled\":true,\"Bot\":{\"Token\":\"a-local-bot-token\",\"GuildId\":\"123\"}," +
                "\"Channels\":{\"Announcements\":\"777\"}}");
            bool legacyDirect = legacy != null && !legacy.CoordinatorMode && legacy.IsEnabled
                             && legacy.BotToken.Length > 0 && legacy.AnnouncementsChannelId == 777;
            r.Add(("legacy config: a file written before coordinator mode still runs Direct mode unchanged",
                   legacyDirect, legacyDirect ? "token live, channels read from the file" : "DIRECT MODE SUPPRESSED"));

            DiscordConfig fresh = new DiscordConfig();
            r.Add(("fresh install: coordinator mode is off until an owner turns it on",
                   !fresh.CoordinatorMode && fresh.Coordinator.Url.Length == 0, ""));

            // Summary and bridge must read the same flag (Enabled vs IsEnabled), or one says "configured" while off.
            DiscordConfig enabledNoToken = new DiscordConfig { Enabled = true };
            bool honest = enabledNoToken.Enabled && !enabledNoToken.IsEnabled
                       && Features.Comms.CommsStartup.DescribeDiscord(enabledNoToken) == "off - enabled but no bot token";
            r.Add(("discord summary: Enabled with no token is reported as off, not as configured", honest,
                   Features.Comms.CommsStartup.DescribeDiscord(enabledNoToken)));

            r.Add(("discord summary: a bridge that will actually run is reported as configured",
                   Features.Comms.CommsStartup.DescribeDiscord(direct) == "configured",
                   Features.Comms.CommsStartup.DescribeDiscord(direct)));

            r.Add(("discord summary: coordinator mode with no local token still reports configured",
                   Features.Comms.CommsStartup.DescribeDiscord(fleet) == "configured",
                   Features.Comms.CommsStartup.DescribeDiscord(fleet)));

            DiscordConfig turnedOff = new DiscordConfig { Enabled = false };
            turnedOff.Bot.Token = "a-local-bot-token";
            r.Add(("discord summary: a bridge the owner turned off is reported as disabled even with a token",
                   Features.Comms.CommsStartup.DescribeDiscord(turnedOff) == "disabled",
                   Features.Comms.CommsStartup.DescribeDiscord(turnedOff)));

            // Enabled defaults to true, so this is what every fresh server prints until an owner adds a token.
            r.Add(("discord summary: a fresh install says off rather than claiming a bridge it has not got",
                   Features.Comms.CommsStartup.DescribeDiscord(fresh) == "off - enabled but no bot token",
                   Features.Comms.CommsStartup.DescribeDiscord(fresh)));
        }

        private static KmhDiscordCommandRequest Req(string path)
            => new KmhDiscordCommandRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Path = path,
                ServerId = "srv-1",
                UserId = 4242,
                UserName = "someone",
                GuildId = 7,
                ChannelId = 11,
            };

        private static void RequestShape(List<(string, bool, string)> r)
        {
            KmhDiscordCommandRequest req = Req("market.list");
            req.Options["page"] = "3";
            bool reads = req.OptionInt("page", 1) == 3 && req.Option("missing", "fallback") == "fallback";
            r.Add(("request: options are read by name with a stated fallback", reads, ""));

            // Options are typed as strings on the wire, so a hostile value must not become a number.
            req.Options["page"] = "not-a-number";
            r.Add(("request: an unparseable option falls back rather than throwing",
                   req.OptionInt("page", 7) == 7, ""));

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(req);
            KmhDiscordCommandRequest back = Newtonsoft.Json.JsonConvert.DeserializeObject<KmhDiscordCommandRequest>(json);
            bool roundTrip = back.RequestId == req.RequestId && back.Path == req.Path
                          && back.UserId == req.UserId && back.ChannelId == req.ChannelId;
            r.Add(("request: it survives the wire unchanged", roundTrip, ""));

            r.Add(("request: nothing Discord.NET-shaped is on the wire",
                   !json.Contains("Socket", StringComparison.Ordinal), ""));
        }

        private static void ResultShape(List<(string, bool, string)> r)
        {
            KmhDiscordCommandResult err = KmhDiscordCommandResult.Error("nope", KmhDiscordError.Offline);
            r.Add(("result: an error is private and carries its code", err.IsError && err.Ephemeral
                   && err.ErrorCode == KmhDiscordError.Offline, ""));

            KmhDiscordCommandResult embed = KmhDiscordCommandResult.Embed(
                new KmhEmbedSpecBuilder().WithTitle("t").Build());
            r.Add(("result: an embed answer is public unless it says otherwise",
                   !embed.Ephemeral && embed.Embeds.Count == 1, ""));

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(embed);
            KmhDiscordCommandResult back = Newtonsoft.Json.JsonConvert.DeserializeObject<KmhDiscordCommandResult>(json);
            r.Add(("result: it survives the wire unchanged", back.Embeds.Count == 1 && back.Embeds[0].Title == "t", ""));

            List<string> chunks = DiscordSlashCommands.SplitForBlocks(string.Join("\n", Enumerable.Repeat("line", 900)), 1900);
            bool split = chunks.Count > 1 && chunks.All(c => c.Length <= 1900);
            r.Add(("result: long console output is split into Discord-sized blocks", split, $"{chunks.Count} chunk(s)"));

            string oneLine = new string('x', 5000);
            bool hardSplit = DiscordSlashCommands.SplitForBlocks(oneLine, 1900).All(c => c.Length <= 1900);
            r.Add(("result: a single over-long line is hard-split rather than sent whole", hardSplit, ""));
        }

        private static void Privacy(List<(string, bool, string)> r)
        {
            string[] mustStayPrivate = { "whois", "console.run", "help", "here",
                                         "announcement.send", "discord.reload", "config.reload",
                                         "leaderboard.post", "leaderboard.refresh" };
            List<string> leaked = mustStayPrivate.Where(p => !KmhSlashTree.IsPrivate(p)).ToList();
            r.Add(("privacy: every command that was private before is still private", leaked.Count == 0,
                   leaked.Count == 0 ? "" : "PUBLIC NOW: " + string.Join(", ", leaked)));

            string[] publicPaths = { "status", "players", "leaderboard.show", "market.list", "site.list" };
            r.Add(("privacy: the public boards stay public",
                   publicPaths.All(p => !KmhSlashTree.IsPrivate(p)), ""));

            // A private answer on a public path must still arrive privately; the responder is what guarantees it.
            r.Add(("privacy: a private result on a public command is still delivered privately",
                   KmhDiscordCommandResult.Error("denied").Ephemeral, ""));
        }
    }
}
