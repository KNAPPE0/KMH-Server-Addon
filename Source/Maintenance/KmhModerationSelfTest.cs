using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Features.Moderation;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhModerationSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            BanStore.ResetForTest();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            BanStore.Add(new BanEntry { Username = "Griefer", Reason = "raided spawn", AtUnix = now }, out _);
            r.Add(("bans: a name ban matches whatever case it is typed in",
                   BanStore.Find("griefer", "", "", now) != null && BanStore.Find("Someone", "", "", now) == null, ""));

            BanStore.Add(new BanEntry { Username = "Alt", Ip = "203.0.113.7", AtUnix = now }, out _);
            r.Add(("bans: an address ban catches a reconnect from a different port",
                   BanStore.Find("DifferentName", "", "203.0.113.7:51544", now) != null, ""));

            // A port stripped from an IPv6 literal would take part of the address with it.
            r.Add(("bans: an IPv6 address keeps its colons",
                   BanStore.HostOnly("[2001:db8::1]:5000") == "2001:db8::1"
                && BanStore.HostOnly("2001:db8::1") == "2001:db8::1"
                && BanStore.HostOnly("203.0.113.7:5000") == "203.0.113.7", BanStore.HostOnly("[2001:db8::1]:5000")));

            BanStore.Add(new BanEntry { Username = "Temp", AtUnix = now, ExpiresUnix = now + 60 }, out _);
            r.Add(("bans: a timed ban stops matching once it expires",
                   BanStore.Find("Temp", "", "", now) != null && BanStore.Find("Temp", "", "", now + 61) == null, ""));

            int lifted = BanStore.Remove("Griefer", out _);
            r.Add(("bans: lifting reports what it actually removed",
                   lifted == 1 && BanStore.Remove("NeverBanned", out _) == 0 && BanStore.Find("Griefer", "", "", now) == null, ""));

            r.Add(("bans: an entry naming nobody is refused rather than banning everyone",
                   !BanStore.Add(new BanEntry { AtUnix = now }, out _), ""));

            BanStore.ResetForTest();

            string[] tags = ServerTags.Normalize(new[]
            {
                " PvP ", "pvp", "Role Play", "rwt-26.8.31.1", "slot-3", "notset", "", null, "kmh-1.3.1",
                "a-very-long-tag-name-that-keeps-going-and-going",
            });
            r.Add(("tags: case and spacing collapse so two spellings are one filter",
                   Array.IndexOf(tags, "pvp") >= 0 && Array.IndexOf(tags, "role-play") >= 0
                && CountOf(tags, "pvp") == 1, string.Join(",", tags)));

            r.Add(("tags: values the listing already carries as fields are dropped",
                   Array.IndexOf(tags, "rwt-26.8.31.1") < 0 && Array.IndexOf(tags, "slot-3") < 0
                && Array.IndexOf(tags, "notset") < 0 && Array.IndexOf(tags, "kmh-1.3.1") < 0, string.Join(",", tags)));

            bool bounded = true;
            foreach (string t in tags)
                if (t.Length == 0 || t.Length > ServerTags.MaxTagLength || t.EndsWith("-", StringComparison.Ordinal))
                    bounded = false;
            r.Add(("tags: every published tag is non-empty, bounded and has no trailing separator", bounded,
                   string.Join(",", tags)));

            string[] many = new string[40];
            for (int i = 0; i < many.Length; i++) many[i] = "tag" + i;
            r.Add(("tags: the published list is capped", ServerTags.Normalize(many).Length == ServerTags.MaxTags, ""));

            r.Add(("tags: a null list normalizes to an empty one rather than throwing",
                   ServerTags.Normalize(null).Length == 0, ""));

            var mine = new WorldChatLine
            {
                MessageId = "m1", Username = "Someone", Body = "hi",
                OriginServerId = "SERVER-A", RelaySource = ChatRelaySources.Discord,
            };
            var theirs = new WorldChatLine
            {
                MessageId = "m2", Username = "Other", Body = "hi",
                OriginServerId = "SERVER-B", RelaySource = ChatRelaySources.Discord,
            };
            var player = new WorldChatLine
            {
                MessageId = "m3", Username = "Linked", Body = "hi",
                OriginServerId = "SERVER-A", RelaySource = "",
            };
            var ownInGame = new WorldChatLine
            {
                MessageId = "m4", Username = "Local", Body = "hi",
                OriginServerId = "SERVER-A", RelaySource = ChatRelaySources.InGame,
            };
            r.Add(("world chat: anything this server relayed is not posted back into its own channel",
                   Features.Discord.KmhWorldChatDiscordBridge.IsOwnEcho(mine, "SERVER-A")
                && Features.Discord.KmhWorldChatDiscordBridge.IsOwnEcho(ownInGame, "SERVER-A")
                && !Features.Discord.KmhWorldChatDiscordBridge.IsOwnEcho(theirs, "SERVER-A"), ""));

            // Only Global saw a local player's own line, so suppressing it would drop them from their own channel.
            r.Add(("world chat: a local player's own world-chat line is still posted",
                   !Features.Discord.KmhWorldChatDiscordBridge.IsOwnEcho(player, "SERVER-A"), ""));

            // A server with no identity yet must not treat every relayed line as its own and go silent.
            r.Add(("world chat: an unidentified server treats nothing as its own echo",
                   !Features.Discord.KmhWorldChatDiscordBridge.IsOwnEcho(mine, ""), ""));

            // A reload stops and starts the bridge at once; the old pump still winding down must not block the new one.
            {
                bool was = Features.Discord.KmhWorldChatDiscordBridge.RunningForTest;
                Features.Discord.KmhWorldChatDiscordBridge.Start();
                Features.Discord.KmhWorldChatDiscordBridge.Stop();
                Features.Discord.KmhWorldChatDiscordBridge.Start();
                System.Threading.Thread.Sleep(250);
                bool running = Features.Discord.KmhWorldChatDiscordBridge.RunningForTest;
                if (!was) Features.Discord.KmhWorldChatDiscordBridge.Stop();
                r.Add(("world chat: a stop and an immediate start leave the bridge polling", running,
                       running ? "a pump is running" : "NOTHING IS POLLING"));
            }

            string rendered = Features.Discord.KmhWorldChatDiscordBridge.Format(new WorldChatLine
            {
                Username = "Bob", Body = "hello", OriginServerName = "Vanilla",
            });
            r.Add(("world chat: a relayed line names its sender and the server it came from",
                   rendered.Contains("Bob") && rendered.Contains("hello") && rendered.Contains("Vanilla"), rendered));

            return r;
        }

        private static int CountOf(string[] values, string want)
        {
            int n = 0;
            foreach (string v in values) if (v == want) n++;
            return n;
        }
    }
}
