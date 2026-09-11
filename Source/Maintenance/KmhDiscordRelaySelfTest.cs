using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace KMHServerAddon.Maintenance
{
    // Source-level checks, because the failure mode is control flow rather than a value a test can read.
    internal static class KmhDiscordRelaySelfTest
    {
        private static string ReadSource(string relative)
        {
            try
            {
                // BaseDirectory, not Assembly.Location, which is empty inside the shipped single-file bundle.
                string dir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'));
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Source", relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            string bridge = ReadSource(Path.Combine("Features", "Discord", "Core", "DiscordBridge.cs"));
            if (bridge == null)
            {
                r.Add(("Discord relay: source not available (shipped build) - wiring checks skipped", true, ""));
            }
            else
            {
                // Counted rather than pattern-matched, since a regex over the call stops at a nested ')'.
                int marks = Regex.Matches(bridge, @"relayed\s*=\s*true\s*;").Count;
                r.Add(("Discord relay: both bridges mark the same relay flag", marks == 2, marks + " of 2"));

                bool earlyReturn = Regex.IsMatch(bridge, @"RelayDiscordToInGame[\s\S]{0,120}?\n\s*return\s*;");
                r.Add(("Discord relay: the RWT bridge does not return before KMH's bridge runs", !earlyReturn,
                       earlyReturn ? "an early return would starve KMH chat again" : ""));

                bool bothPresent = bridge.Contains("ChatBridgeChannelId") && bridge.Contains("KmhChatDiscordBridge.Ingest");
                r.Add(("Discord relay: both bridges are wired in OnMessage", bothPresent, ""));

                bool additive = bridge.Contains("relayed = true;") && bridge.Contains("if (relayed) return;");
                r.Add(("Discord relay: the two bridges are additive, not exclusive", additive, ""));

                r.Add(("Discord relay: chat channel wiring is logged at boot",
                       bridge.Contains("Discord: chat wiring"), ""));
                r.Add(("Discord relay: an unset KMH chat channel warns at boot",
                       bridge.Contains("KmhChat.Channel is unset"), ""));
            }

            string handlerSource = ReadSource(Path.Combine("Features", "Chat", "ChatHandler.cs"));
            string ingest = ReadSource(Path.Combine("Features", "Discord", "Posting", "KmhChatDiscordBridge.cs"));
            if (ingest == null)
            {
                r.Add(("Discord relay: ingest source not available - diagnostics checks skipped", true, ""));
            }
            else
            {
                r.Add(("Discord relay: a disabled inbound relay logs why",
                       ingest.Contains("inbound relay is OFF"), ""));
                r.Add(("Discord relay: a store refusal logs the reason",
                       ingest.Contains("REFUSED by the chat store"), ""));
                r.Add(("Discord relay: a successful relay logs how many clients it reached",
                       ingest.Contains("connected client(s)"), ""));

                // Being unlinked must never drop a message; it only changes the displayed name.
                bool dropsUnlinked = Regex.IsMatch(ingest, @"if\s*\(\s*string\.IsNullOrEmpty\(\s*linked\s*\)\s*\)\s*return");
                r.Add(("Discord relay: an unlinked Discord user is NOT dropped", !dropsUnlinked, ""));
            }

            // A Discord display name is self-chosen, so only a verified link makes it an identity.
            r.Add(("sender identity: a linked account wins over the Discord display",
                   Features.Discord.KmhChatDiscordBridge.ResolveSender("Knappe", "someone_else") == "Knappe", ""));
            r.Add(("sender identity: an unlinked display that collides with a real player is disambiguated",
                   Features.Discord.KmhChatDiscordBridge.ResolveSender("", "Knappe", true) == "Knappe (Discord)", ""));
            r.Add(("sender identity: an unlinked non-colliding display is relayed as-is",
                   Features.Discord.KmhChatDiscordBridge.ResolveSender("", "randomer") == "randomer", ""));
            r.Add(("sender identity: an empty display never yields an empty sender",
                   Features.Discord.KmhChatDiscordBridge.ResolveSender("", "") == "Discord", ""));

            Features.Chat.Dto.ChatMessage ingameMsg, linkedMsg, unlinkedMsg;
            var saved = Features.Chat.ChatStore.BuildStateForTest(System.DateTime.UtcNow.Ticks);
            try
            {
                Features.Chat.ChatStore.ResetForTest();
                ingameMsg   = Features.Chat.ChatStore.PostForTest("Knappe", Features.Chat.ChatChannels.Server, "hello", null);
                linkedMsg   = Features.Chat.ChatStore.PostForTest("Knappe", Features.Chat.ChatChannels.Server, "from discord", "discord", true);
                unlinkedMsg = Features.Chat.ChatStore.PostForTest("randomer", Features.Chat.ChatChannels.Server, "unlinked", "discord", false);
            }
            finally { Features.Chat.ChatStore.ResetForTest(); Features.Chat.ChatStore.ApplyLoaded(saved, System.DateTime.UtcNow.Ticks); }

            r.Add(("sender identity: an in-game message is verified by default",
                   ingameMsg != null && ingameMsg.SenderVerified, ""));
            r.Add(("sender identity: a linked Discord relay is marked verified",
                   linkedMsg != null && linkedMsg.SenderVerified, ""));
            r.Add(("sender identity: an unlinked Discord relay is NOT marked verified",
                   unlinkedMsg != null && !unlinkedMsg.SenderVerified, ""));

            // Must survive the wire and the save file, or a restart would relabel an already-shown message.
            string wire = Persistence.JsonFileStore.ToJson(unlinkedMsg);
            var back = Persistence.JsonFileStore.FromJson<Features.Chat.Dto.ChatMessage>(wire);
            r.Add(("sender identity: the flag survives serialisation",
                   back != null && !back.SenderVerified && back.Origin == "discord", ""));
            var reloadedVerified = Persistence.JsonFileStore.FromJson<Features.Chat.Dto.ChatMessage>(
                Persistence.JsonFileStore.ToJson(linkedMsg));
            r.Add(("sender identity: a verified relay stays verified across a reload",
                   reloadedVerified != null && reloadedVerified.SenderVerified, ""));

            // History with no flag must read as unverified, never as an identity nothing checked.
            var legacy = Persistence.JsonFileStore.FromJson<Features.Chat.Dto.ChatMessage>(
                "{\"id\":1,\"channel\":\"server\",\"from\":\"someone\",\"body\":\"old\",\"origin\":\"discord\"}");
            r.Add(("sender identity: legacy history with no flag reads unverified",
                   legacy != null && !legacy.SenderVerified, ""));

            // Name and verification are one decision, so no call site can hardcode the flag separately.
            Features.Discord.KmhChatDiscordBridge.ResolveIdentity("Knappe", "someone_else", false, out string idA, out bool vA);
            r.Add(("sender identity: a link yields both the KMH name and verified status",
                   idA == "Knappe" && vA, ""));

            Features.Discord.KmhChatDiscordBridge.ResolveIdentity("", "Knappe", true, out string idB, out bool vB);
            r.Add(("sender identity: a colliding display is disambiguated AND unverified",
                   idB == "Knappe (Discord)" && !vB, ""));

            Features.Discord.KmhChatDiscordBridge.ResolveIdentity("", "randomer", false, out string idC, out bool vC);
            r.Add(("sender identity: an ordinary unlinked display is unverified",
                   idC == "randomer" && !vC, ""));

            // Ingest must use the joint resolver rather than reading name and flag from separate places.
            if (ingest != null)
                r.Add(("sender identity: ingest resolves name and verification together",
                       Regex.IsMatch(ingest, @"ResolveIdentity\(\s*linked\s*,[^;]*?out\s+bool\s+verified\s*\)")
                       && Regex.IsMatch(ingest, @"ChatStore\.Post\([^;]*?,\s*verified\s*[,)]"), ""));

            // The only cue that does not depend on colour, so an owner must not be able to blank it out.
            r.Add(("marker: an empty or whitespace value falls back to the default",
                   Features.Chat.ChatConfig.ClampMarker("") == "◈"
                   && Features.Chat.ChatConfig.ClampMarker(null) == "◈"
                   && Features.Chat.ChatConfig.ClampMarker("  ") == "◈", ""));
            r.Add(("marker: a value carrying markup never reaches a client",
                   Features.Chat.ChatConfig.ClampMarker("<color=red>x</color>") == "◈", ""));
            r.Add(("marker: an over-long value is cut to a marker-sized string",
                   Features.Chat.ChatConfig.ClampMarker("ABCDEFGH").Length == 4, ""));

            // Without sync, staff moderate one surface while the other keeps showing what they took down.
            Features.Discord.KmhChatRelayIndex.Clear();
            Features.Discord.KmhChatRelayIndex.RecordBatch(9001,
                new List<long> { 11, 12, 13 },
                new List<string> { "**a**: one", "**b**: two", "**c**: three" });

            bool redacted = Features.Discord.KmhChatRelayIndex.TryRedactOutbound(12, "_removed_", out ulong msgId, out string newText);
            r.Add(("moderation sync: a removed line is found in the batch that carries it",
                   redacted && msgId == 9001, ""));
            // The whole point of editing rather than deleting: the lines batched alongside it are untouched.
            r.Add(("moderation sync: only the removed line changes, its neighbours survive",
                   newText == "**a**: one\n_removed_\n**c**: three", newText));
            r.Add(("moderation sync: re-removing the same line is a no-op, not a second edit",
                   !Features.Discord.KmhChatRelayIndex.TryRedactOutbound(12, "_removed_", out _, out _), ""));
            r.Add(("moderation sync: a line that was never relayed is not edited",
                   !Features.Discord.KmhChatRelayIndex.TryRedactOutbound(999, "_removed_", out _, out _), ""));

            Features.Discord.KmhChatRelayIndex.RecordInbound(7001, 55);
            r.Add(("moderation sync: a Discord delete maps back to the KMH line it created",
                   Features.Discord.KmhChatRelayIndex.TryTakeInbound(7001, out long mapped) && mapped == 55, ""));
            // Consumed on use: a recycled Discord id must not be able to remove a second, unrelated KMH message.
            r.Add(("moderation sync: an inbound mapping is used once and then gone",
                   !Features.Discord.KmhChatRelayIndex.TryTakeInbound(7001, out _), ""));
            r.Add(("moderation sync: an unrelated Discord deletion removes nothing",
                   !Features.Discord.KmhChatRelayIndex.TryTakeInbound(4242, out _), ""));

            // A new season restarts message ids at 1, so a surviving mapping would point at a different message.
            Features.Discord.KmhChatRelayIndex.RecordInbound(7002, 56);
            Features.Discord.KmhChatRelayIndex.Clear();
            r.Add(("moderation sync: a season reset drops the index rather than aiming it at new ids",
                   !Features.Discord.KmhChatRelayIndex.TryTakeInbound(7002, out _)
                   && Features.Discord.KmhChatRelayIndex.BatchCountForTest == 0, ""));

            // A one-off misalignment strikes out the wrong player's message, which is worse than no sync.
            Features.Discord.KmhChatDiscordBridge.ResetQueueForTest();
            Features.Discord.KmhChatDiscordBridge.EnqueueForTest(21, "**a**: one");
            Features.Discord.KmhChatDiscordBridge.EnqueueForTest(22, "**b**: two");
            string batchText = Features.Discord.KmhChatDiscordBridge.TakeBatchForTest(out List<long> batchIds, out List<string> batchLines);
            r.Add(("moderation sync: ids and lines come back parallel and in order",
                   batchIds.Count == batchLines.Count && batchIds.Count == 2
                   && batchIds[0] == 21 && batchIds[1] == 22
                   && batchLines[0] == "**a**: one" && batchLines[1] == "**b**: two"
                   && batchText == "**a**: one\n**b**: two", batchText));

            // The opening notice belongs to no KMH message, so the queue is really overflowed rather than faked.
            Features.Discord.KmhChatDiscordBridge.ResetQueueForTest();
            for (int i = 0; i < 260; i++) Features.Discord.KmhChatDiscordBridge.EnqueueForTest(1000 + i, "**x**: " + i);
            string overflowText = Features.Discord.KmhChatDiscordBridge.TakeBatchForTest(out List<long> ofIds, out List<string> ofLines);
            bool leadNotice = ofLines.Count > 0 && ofLines[0].StartsWith("_") && ofIds.Count > 0 && ofIds[0] == 0;
            bool aligned = ofIds.Count == ofLines.Count;
            for (int i = 0; i < ofLines.Count && aligned; i++)
                aligned = overflowText.Split('\n')[i] == ofLines[i];
            r.Add(("moderation sync: an omitted-lines notice holds its own slot and shifts nothing",
                   leadNotice && aligned, leadNotice ? (aligned ? "" : "lists drifted") : "no notice was produced - the drop path was never reached"));

            Features.Discord.KmhChatDiscordBridge.ResetQueueForTest();
            Features.Discord.KmhChatRelayIndex.Clear();

            // It carries no ServerClient, so a client reaching it would remove messages with no authorisation.
            if (handlerSource != null)
                r.Add(("moderation sync: the Discord-driven removal is not a wire handler",
                       Regex.IsMatch(handlerSource, @"internal\s+static\s+bool\s+RemoveRelayed\(")
                       && !Regex.IsMatch(handlerSource, @"RegisterHandler\([^;]*RemoveRelayed"), ""));

            // Previews make a player's client contact a third-party host, so the gates are the feature.
            r.AddRange(ImageChecks());

            // The live push path: a relayed message must reach open Communications windows without a reopen.
            string handler = handlerSource;
            if (handler != null)
            {
                r.Add(("Discord relay: relayed messages broadcast live to connected clients",
                       handler.Contains("BroadcastExternal") && handler.Contains("Kind.ChatMessage"), ""));
                r.Add(("Discord relay: the broadcast reports how many clients it reached",
                       handler.Contains("return sent;"), ""));
            }

            return r;
        }

        private static List<(string, bool, string)> ImageChecks()
        {
            var r = new List<(string, bool, string)>();

            // The classic way an allow-list stops working is a Contains() check, so exact matching is asserted.
            var allowed = new[] { "cdn.discordapp.com" };
            r.Add(("images: the exact allowed host passes",
                   Features.Chat.ChatImagePolicy.IsAllowedHost("cdn.discordapp.com", allowed), ""));
            r.Add(("images: a subdomain of an allowed host passes",
                   Features.Chat.ChatImagePolicy.IsAllowedHost("eu.cdn.discordapp.com", allowed), ""));
            // The one that matters: a substring match would let this through and it is NOT Discord.
            r.Add(("images: a host that merely CONTAINS the allowed name is refused",
                   !Features.Chat.ChatImagePolicy.IsAllowedHost("cdn.discordapp.com.evil.example", allowed), ""));
            r.Add(("images: a lookalike host is refused",
                   !Features.Chat.ChatImagePolicy.IsAllowedHost("cdn-discordapp.com", allowed)
                   && !Features.Chat.ChatImagePolicy.IsAllowedHost("evilcdn.discordapp.com.co", allowed), ""));
            r.Add(("images: case and a trailing dot do not slip past",
                   Features.Chat.ChatImagePolicy.IsAllowedHost("CDN.DiscordApp.com.", allowed), ""));
            r.Add(("images: an empty host or empty list allows nothing",
                   !Features.Chat.ChatImagePolicy.IsAllowedHost("", allowed)
                   && !Features.Chat.ChatImagePolicy.IsAllowedHost("cdn.discordapp.com", new string[0])
                   && !Features.Chat.ChatImagePolicy.IsAllowedHost("cdn.discordapp.com", null), ""));

            r.Add(("images: an image extension is recognised",
                   Features.Chat.ChatImagePolicy.LooksLikeImage("/a/b/pic.PNG")
                   && Features.Chat.ChatImagePolicy.LooksLikeImage("/x.jpeg"), ""));
            r.Add(("images: a non-image path is refused",
                   !Features.Chat.ChatImagePolicy.LooksLikeImage("/a/b/setup.exe")
                   && !Features.Chat.ChatImagePolicy.LooksLikeImage("/a/b/")
                   && !Features.Chat.ChatImagePolicy.LooksLikeImage(""), ""));

            // Previews default off, so on a stock server every one of these must come back empty.
            bool previewsOn = Features.Chat.ChatConfig.Current?.AllowImagePreviews == true;
            string vetted = Features.Chat.ChatImagePolicy.Vet("https://cdn.discordapp.com/a/b.png", typedByPlayer: false);
            r.Add(("images: previews are OFF unless the owner turned them on",
                   previewsOn || vetted == "", previewsOn ? "owner enabled them" : ""));

            if (previewsOn)
            {
                r.Add(("images: an allowed https image is vetted through", vetted.Length > 0, vetted));
                r.Add(("images: plain http is refused even from an allowed host",
                       Features.Chat.ChatImagePolicy.Vet("http://cdn.discordapp.com/a/b.png", false) == "", ""));
                r.Add(("images: a disallowed host is refused",
                       Features.Chat.ChatImagePolicy.Vet("https://evil.example/a/b.png", false) == "", ""));
                r.Add(("images: a non-image URL is refused",
                       Features.Chat.ChatImagePolicy.Vet("https://cdn.discordapp.com/a/b.exe", false) == "", ""));
            }

            // An upgrade tops the host list up rather than replacing it, so an owner's own hosts survive.
            string[] shipped = Features.Chat.ChatConfig.DefaultImageHosts();
            r.Add(("images: the shipped list covers Discord's CDNs",
                   System.Array.IndexOf(shipped, "discordapp.com") >= 0
                   && System.Array.IndexOf(shipped, "discordapp.net") >= 0, string.Join(", ", shipped)));

            // images-ext-N is a different host from media., so listing subdomains one by one would miss it.
            r.Add(("images: a domain entry covers its subdomains",
                   Features.Chat.ChatImagePolicy.IsAllowedHost("media.discordapp.net", shipped)
                   && Features.Chat.ChatImagePolicy.IsAllowedHost("images-ext-1.discordapp.net", shipped)
                   && Features.Chat.ChatImagePolicy.IsAllowedHost("cdn.discordapp.com", shipped), ""));
            r.Add(("images: subdomain matching is still not a substring match",
                   !Features.Chat.ChatImagePolicy.IsAllowedHost("discordapp.net.evil.example", shipped), ""));

            var owner = new[] { "my.cdn.example" };
            string[] merged = Features.Chat.ChatConfig.MergeHosts(owner, shipped);
            r.Add(("images: an update ADDS shipped hosts and keeps the owner's own",
                   System.Array.IndexOf(merged, "my.cdn.example") == 0
                   && System.Array.IndexOf(merged, "discordapp.net") > 0, ""));
            r.Add(("images: merging twice does not duplicate entries",
                   Features.Chat.ChatConfig.MergeHosts(merged, shipped).Length == merged.Length, ""));
            r.Add(("images: merge is null-safe both ways",
                   Features.Chat.ChatConfig.MergeHosts(null, shipped).Length == shipped.Length
                   && Features.Chat.ChatConfig.MergeHosts(owner, null).Length == 1, ""));

            // A Tenor link is a web page, so it stays refused; what works is Discord re-hosting the media.
            r.Add(("images: a gif-site PAGE url is not a host KMH trusts",
                   !Features.Chat.ChatImagePolicy.IsAllowedHost("klipy.com", shipped), ""));
            r.Add(("images: a page url is not an image, whatever the host",
                   !Features.Chat.ChatImagePolicy.LooksLikeImage("/gifs/hop-on-overwatch-1"), ""));

            // The embed already declared it an image, so an extension-less proxy url must not override that.
            r.Add(("images: an extensionless url from an embed is still refused by EXTENSION alone",
                   !Features.Chat.ChatImagePolicy.LooksLikeImage("/external/abc123/https/media.tenor.com/xyz"), ""));

            // A five-digit colour parses fine as a number, so length is checked rather than trusted.
            r.Add(("discord: a five-digit brand colour is refused, not silently misread",
                   !Features.Discord.KmhEmbedBuilder.TryBrandColor("#FFFFF", out _, out string badHexWhy)
                   && badHexWhy.Contains("5 hex digit"), badHexWhy));
            r.Add(("discord: six digits and the three-digit shorthand are both accepted",
                   Features.Discord.KmhEmbedBuilder.TryBrandColor("#C88A2A", out _, out _)
                   && Features.Discord.KmhEmbedBuilder.TryBrandColor("abc", out _, out _)
                   && Features.Discord.KmhEmbedBuilder.TryBrandColor("FFFFFF", out _, out _), ""));
            r.Add(("discord: empty and non-hex values fall back with a reason",
                   !Features.Discord.KmhEmbedBuilder.TryBrandColor("", out _, out _)
                   && !Features.Discord.KmhEmbedBuilder.TryBrandColor("#GGGGGG", out _, out string nonHex)
                   && nonHex.Length > 0, ""));

            // A signed CDN url expires in 24h but chat history keeps 48, so identity must outlive the link.
            const string signed = "https://media.discordapp.net/attachments/125676983546937344/918551905582600212/"
                                + "Shiroket-3.gif?ex=6a8736a0&is=6a85e520&hm=a92f2801d3249016a61e";
            System.DateTime expiry = Features.Chat.ChatDiscordMedia.SignedExpiryUtc(signed) ?? System.DateTime.MinValue;
            r.Add(("media: a Discord signature's expiry is read out of the url",
                   expiry.Year == 2026 && expiry.Month == 8 && expiry.Day == 20, expiry.ToString("u")));
            r.Add(("media: an expired signature is recognised before anyone fetches it",
                   Features.Chat.ChatDiscordMedia.IsExpired(signed, expiry.AddMinutes(1))
                   && !Features.Chat.ChatDiscordMedia.IsExpired(signed, expiry.AddHours(-2)), ""));
            r.Add(("media: an unsigned url has no expiry and is left alone",
                   !Features.Chat.ChatDiscordMedia.IsSigned("https://media.tenor.com/abc/thing.gif")
                   && !Features.Chat.ChatDiscordMedia.IsExpired("https://media.tenor.com/abc/thing.gif",
                                                               System.DateTime.UtcNow), ""));

            r.Add(("media: the channel and attachment come out of the url path",
                   Features.Chat.ChatDiscordMedia.TryParseAttachmentUrl(signed, out ulong chId, out ulong attId, out string fname)
                   && chId == 125676983546937344UL && attId == 918551905582600212UL && fname == "Shiroket-3.gif",
                   $"{chId}/{attId}/{fname}"));

            string mref = Features.Chat.ChatDiscordMedia.MakeRef(chId, 999UL, attId);
            r.Add(("media: a reference round-trips and carries no url",
                   Features.Chat.ChatDiscordMedia.TryParseRef(mref, out ulong rc, out ulong rm, out ulong ra)
                   && rc == chId && rm == 999UL && ra == attId
                   && !mref.Contains("http") && !mref.Contains("hm="), mref));
            // A wrapper spells its source out as free text, so the scheme it claims is re-checked, not trusted.
            r.Add(("media: a wrapper naming a non-https source is refused rather than followed",
                   Features.Chat.ChatMediaUrl.WrappedSourceOf(
                       "https://images-ext-1.discordapp.net/external/abc/http/evil.example/x.webp") == "", ""));

            // Two bridges on one channel would amplify forever, so KMH must recognise its own outbound shape.
            r.Add(("relay loop: a KMH relay line is recognised as an echo",
                   Features.Chat.ChatDiscordText.LooksLikeRelayEcho("**KNAPPE0**: hello")
                   && Features.Chat.ChatDiscordText.LooksLikeRelayEcho("  **Test**: hi there"), ""));
            r.Add(("relay loop: ordinary bot output is not mistaken for an echo",
                   !Features.Chat.ChatDiscordText.LooksLikeRelayEcho("Congratulations KNAPPE!")
                   && !Features.Chat.ChatDiscordText.LooksLikeRelayEcho("**bold** start but no colon")
                   && !Features.Chat.ChatDiscordText.LooksLikeRelayEcho("")
                   && !Features.Chat.ChatDiscordText.LooksLikeRelayEcho("just talking"), ""));
            r.Add(("relay loop: an over-long name is not treated as an echo, so a wall of text cannot dodge the relay",
                   !Features.Chat.ChatDiscordText.LooksLikeRelayEcho("**" + new string('x', 80) + "**: body"), ""));

            // An animated custom emoji is a .gif, which is the one animated format this client decodes.
            r.Add(("discord emoji: a static custom emoji resolves to its png on Discord's CDN",
                   Features.Chat.ChatDiscordEmoji.FirstEmojiUrl("hi <:KMH:1525399087338356842>")
                       == "https://cdn.discordapp.com/emojis/1525399087338356842.png",
                   Features.Chat.ChatDiscordEmoji.FirstEmojiUrl("hi <:KMH:1525399087338356842>")));
            r.Add(("discord emoji: an animated custom emoji resolves to a gif, so it animates",
                   Features.Chat.ChatDiscordEmoji.FirstEmojiUrl("<a:0B_zerotwoHype:876563681763291206>")
                       == "https://cdn.discordapp.com/emojis/876563681763291206.gif",
                   Features.Chat.ChatDiscordEmoji.FirstEmojiUrl("<a:0B_zerotwoHype:876563681763291206>")));
            r.Add(("discord emoji: plain text carries no emoji url",
                   Features.Chat.ChatDiscordEmoji.FirstEmojiUrl("just talking") == ""
                   && Features.Chat.ChatDiscordEmoji.FirstEmojiUrl("") == "", ""));
            r.Add(("discord sticker: a raster sticker resolves, and a lottie one yields nothing to draw",
                   Features.Chat.ChatDiscordEmoji.StickerUrl(749054660769218631UL, "PNG")
                       == "https://media.discordapp.net/stickers/749054660769218631.png"
                   && Features.Chat.ChatDiscordEmoji.StickerUrl(749054660769218631UL, "Lottie") == ""
                   && Features.Chat.ChatDiscordEmoji.StickerUrl(0UL, "PNG") == "", ""));

            // RimWorld's font cannot render the raw form, so only the emoji's name carries meaning.
            r.Add(("discord text: a custom emoji becomes its name, and its snowflake id is dropped",
                   Features.Chat.ChatDiscordText.Readable("hi <:KMH:1525399087338356842> there") == "hi :KMH: there"
                   && Features.Chat.ChatDiscordText.Readable("<a:0B_zerotwoHype:876563681763291206>") == ":0B_zerotwoHype:",
                   Features.Chat.ChatDiscordText.Readable("hi <:KMH:1525399087338356842> there")));
            r.Add(("discord text: mentions read as words rather than bare ids",
                   Features.Chat.ChatDiscordText.Readable("<@123456789012> <@&123456789012> <#123456789012>")
                       == "@someone @role #channel", ""));
            r.Add(("discord text: ordinary text and a lone angle bracket are left alone",
                   Features.Chat.ChatDiscordText.Readable("a < b and 3 <3") == "a < b and 3 <3"
                   && Features.Chat.ChatDiscordText.Readable("") == "", ""));

            // In-game @mentions notify a player, so a mass ping typed in Discord must not carry across as one.
            string zwsp = Features.Chat.ChatDiscordText.Zwsp;
            r.Add(("discord text: a mass ping from Discord is defused before it reaches the game",
                   Features.Chat.ChatDiscordText.Readable("@everyone look").StartsWith("@" + zwsp)
                   && !Features.Chat.ChatDiscordText.Readable("@everyone look").Contains("@everyone")
                   && !Features.Chat.ChatDiscordText.Readable("@here now").Contains("@here")
                   && !Features.Chat.ChatDiscordText.Readable("@ALL of you").Contains("@ALL"),
                   Features.Chat.ChatDiscordText.Readable("@everyone look")));
            r.Add(("discord text: a player's name is still a mention after the sweep",
                   Features.Chat.ChatDiscordText.Readable("@Alice hello") == "@Alice hello", ""));

            r.Add(("media: a malformed reference is refused rather than half-parsed",
                   !Features.Chat.ChatDiscordMedia.TryParseRef("discord:1:2", out _, out _, out _)
                   && !Features.Chat.ChatDiscordMedia.TryParseRef("", out _, out _, out _)
                   && !Features.Chat.ChatDiscordMedia.TryParseRef("http://x", out _, out _, out _)
                   && !Features.Chat.ChatDiscordMedia.TryParseRef("discord:0:0:0", out _, out _, out _), ""));

            // A refresh uses the server's own bot credentials, so a crafted reference could read any channel it sees.
            string dm = Features.Chat.ChatChannels.Dm("Knappe", "Taz");
            string liveRef = Features.Chat.ChatDiscordMedia.MakeRef(chId, 4242UL, attId);
            string dmRef   = Features.Chat.ChatDiscordMedia.MakeRef(chId, 4343UL, attId);
            string craftedRef = Features.Chat.ChatDiscordMedia.MakeRef(999999999999UL, 1UL, 2UL);
            string ownerOfLive, ownerOfDm, ownerOfCrafted;
            var savedRefs = Features.Chat.ChatStore.BuildStateForTest(System.DateTime.UtcNow.Ticks);
            try
            {
                Features.Chat.ChatStore.ResetForTest();
                Features.Chat.ChatStore.Post("relaybot", Features.Chat.ChatChannels.Server, "pic", null, "discord",
                                             out _, true, signed, liveRef);
                Features.Chat.ChatStore.Post("Knappe", dm, "private pic", null, "discord",
                                             out _, true, signed, dmRef);
                ownerOfLive    = Features.Chat.ChatStore.ChannelOfMediaRef(liveRef);
                ownerOfDm      = Features.Chat.ChatStore.ChannelOfMediaRef(dmRef);
                ownerOfCrafted = Features.Chat.ChatStore.ChannelOfMediaRef(craftedRef);
            }
            finally { Features.Chat.ChatStore.ResetForTest(); Features.Chat.ChatStore.ApplyLoaded(savedRefs, System.DateTime.UtcNow.Ticks); }

            r.Add(("media refresh: a reference the server minted resolves to the channel that carries it",
                   ownerOfLive == Features.Chat.ChatChannels.Server, ownerOfLive));
            r.Add(("media refresh: a reference no live message carries resolves to nothing",
                   ownerOfCrafted == "", ownerOfCrafted));
            r.Add(("media refresh: a DM's reference resolves to that DM, not to the public channel",
                   ownerOfDm == dm, ownerOfDm));
            r.Add(("media refresh: only a participant may refresh a DM's media",
                   Features.Chat.ChatHandler.CanUserAccess("Knappe", dm)
                   && Features.Chat.ChatHandler.CanUserAccess("Taz", dm)
                   && !Features.Chat.ChatHandler.CanUserAccess("Mallory", dm), dm));
            r.Add(("media refresh: an empty reference is never treated as a match",
                   Features.Chat.ChatStore.ChannelOfMediaRef("") == "", ""));

            // The rule above only protects anything if the handler consults it before reaching Discord.
            string refreshSrc = ReadSource(Path.Combine("Features", "Chat", "ChatMediaRefresh.cs"));
            if (refreshSrc != null)
            {
                int guard   = refreshSrc.IndexOf("ChatStore.ChannelOfMediaRef(reference)", System.StringComparison.Ordinal);
                int access  = refreshSrc.IndexOf("ChatHandler.CanUserAccess(username, owningChannel)", System.StringComparison.Ordinal);
                int reach   = refreshSrc.IndexOf("ReacquireAsync(channelId", System.StringComparison.Ordinal);
                r.Add(("media refresh: the handler authorises the reference before it reaches Discord",
                       guard > 0 && access > guard && reach > access, $"guard@{guard} access@{access} discord@{reach}"));
            }

            r.Add(("media: a long signed url has a short label for display",
                   Features.Chat.ChatDiscordMedia.ShortLabel(signed) == "Shiroket-3.gif"
                   && Features.Chat.ChatDiscordMedia.ShortLabel("https://klipy.com/gifs/thing") == "klipy.com",
                   Features.Chat.ChatDiscordMedia.ShortLabel(signed)));

            // The late-embed path: Discord delivers the message first and attaches the preview as an edit.
            string ingestSrc = ReadSource(Path.Combine("Features", "Discord", "Posting", "KmhChatDiscordBridge.cs"));
            if (ingestSrc != null)
                r.Add(("images: a late link preview attaches to the message already relayed",
                       ingestSrc.Contains("AttachLateImage") && ingestSrc.Contains("TryPeekInbound"), ""));

            string bridgeSrc = ReadSource(Path.Combine("Features", "Discord", "Core", "DiscordBridge.cs"));
            if (bridgeSrc != null)
            {
                r.Add(("images: message edits are watched, or a pasted gif link never gets its picture",
                       bridgeSrc.Contains("MessageUpdated") && bridgeSrc.Contains("OnMessageUpdated"), ""));
                r.Add(("images: Discord's proxy copy is preferred over the original host",
                       bridgeSrc.Contains("e.Image?.ProxyUrl") && bridgeSrc.Contains("e.Thumbnail?.ProxyUrl"), ""));
                // Embed media is vetted as a KNOWN image - Discord classified it, so the extension guess is skipped.
                r.Add(("images: embed media is vetted as already-known-to-be-an-image",
                       bridgeSrc.Contains("knownImage") || ingestSrc == null || ingestSrc.Contains("knownImage"), ""));
            }

            // Run against an explicit config, or gating on the live one would skip these on a fresh install.
            var on = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true, AllowTypedImageUrls = true,
                ImageHostAllowList = Features.Chat.ChatConfig.DefaultImageHosts(),
            };
            const string proxied = "https://images-ext-1.discordapp.net/external/abc123/https/media.tenor.com/xyz";

            r.Add(("images: Discord's external proxy host is allowed",
                   Features.Chat.ChatImagePolicy.Vet(on, proxied, false, knownImage: true).Length > 0,
                   Features.Chat.ChatImagePolicy.Vet(on, proxied, false, true)));
            // ...but only because the embed vouched for it. The same url with no such claim is still refused.
            r.Add(("images: an extensionless url is refused when nothing vouched for it",
                   Features.Chat.ChatImagePolicy.Vet(on, proxied, false, knownImage: false) == "", ""));
            // knownImage relaxes the TYPE test, never the host or scheme tests.
            r.Add(("images: knownImage does not let an untrusted host through",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://evil.example/whatever", false, true) == "", ""));
            r.Add(("images: knownImage does not permit plain http",
                   Features.Chat.ChatImagePolicy.Vet(on, "http://cdn.discordapp.com/a/b", false, true) == "", ""));
            r.Add(("images: an ordinary allowed image url passes",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://cdn.discordapp.com/a/b.png", false).Length > 0, ""));
            r.Add(("images: a non-image extension is still refused",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://cdn.discordapp.com/a/b.exe", false) == "", ""));

            // Previews off means off, whatever else is true.
            var off = new Features.Chat.ChatConfig { AllowImagePreviews = false };
            r.Add(("images: previews off refuses everything, knownImage included",
                   Features.Chat.ChatImagePolicy.Vet(off, "https://cdn.discordapp.com/a/b.png", false, true) == "", ""));

            // Typed urls are a separate permission from attachments.
            var noTyped = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true, AllowTypedImageUrls = false,
                ImageHostAllowList = Features.Chat.ChatConfig.DefaultImageHosts(),
            };
            r.Add(("images: a typed url is refused when only attachments are allowed",
                   Features.Chat.ChatImagePolicy.Vet(noTyped, "https://cdn.discordapp.com/a/b.png", typedByPlayer: true) == ""
                   && Features.Chat.ChatImagePolicy.Vet(noTyped, "https://cdn.discordapp.com/a/b.png", typedByPlayer: false).Length > 0, ""));

            // Discord re-hosts embed media as WEBP, which nothing here decodes, but the proxy honours ?format=.
            r.Add(("images: a Discord proxy url is asked for a decodable format",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://images-ext-1.discordapp.net/external/a/b")
                       .EndsWith("format=png", StringComparison.Ordinal), ""));
            // ApplyTranscode being correct is worth nothing if Vet never calls it, so the call site is checked too.
            r.Add(("images: a VETTED Discord proxy url carries the format hint",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://images-ext-1.discordapp.net/external/a/b", false, true)
                       .Contains("format="), 
                   Features.Chat.ChatImagePolicy.Vet(on, "https://images-ext-1.discordapp.net/external/a/b", false, true)));
            r.Add(("images: a vetted gif keeps its animation through the hint",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://media.discordapp.net/a/b.gif", false)
                       .EndsWith("format=gif", StringComparison.Ordinal), ""));

            r.Add(("images: a gif stays a gif, or it loses its animation",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b.gif")
                       .EndsWith("format=gif", StringComparison.Ordinal), ""));
            // Measured against the live proxy: WRAPPED, a .webp converts to gif with every frame; unwrapped it answers 415.
            const string klipyProxy = "https://images-ext-1.discordapp.net/external/hash/https/"
                                    + "static.klipy.com/ii/abc/9b/0e/p5dC8SCQ.webp";
            const string tenorProxy = "https://images-ext-1.discordapp.net/external/hash/https/"
                                    + "media1.tenor.com/m/xyz/cry-bozo.gif";
            const string klipyPage  = "https://klipy.com/gifs/lloyd-frontera-tged";

            // Copied from a real session's Chat.json, where one of these animated and the other did not; an invented fixture agreed with both rules.
            const string liveStill = "https://images-ext-1.discordapp.net/external/6FTlKxk6I0RUZgXIh66KEceOsueMc-OEGa_f62-cCmk/"
                                   + "https/static.klipy.com/ii/d7aec6f6f171607374b2065c836f92f4/4b/4e/uW5TytnV.webp";
            const string liveThumb = "https://images-ext-1.discordapp.net/external/EfW8x0oykfeZ9jod-I352bt7VuAIUGxww1pE17HOxE4/"
                                   + "https/i.ytimg.com/vi/NxHGV8KKT6I/maxresdefault.jpg";
            r.Add(("images: the webp that reached a player as a still is asked for gif and takes the resolver",
                   Features.Chat.ChatImagePolicy.WantedFormat(on, liveStill) == "gif"
                   && Features.Chat.ChatMediaUrl.NeedsResolver(on, liveStill)
                   && Features.Chat.ChatMediaUrl.ResolverSourceFor(on, liveStill)
                          == "https://static.klipy.com/ii/d7aec6f6f171607374b2065c836f92f4/4b/4e/uW5TytnV.webp",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, liveStill)));
            r.Add(("images: a real YouTube thumbnail is neither converted nor resolved",
                   Features.Chat.ChatImagePolicy.WantedFormat(on, liveThumb) == null
                   && !Features.Chat.ChatMediaUrl.NeedsResolver(on, liveThumb),
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, liveThumb)));
            // The stored url carries the hint already; the resolver must still see past it to the real source.
            r.Add(("images: an already-hinted live url still resolves to its unflattened source",
                   Features.Chat.ChatMediaUrl.NeedsResolver(on, liveStill + "?format=png")
                   && Features.Chat.ChatMediaUrl.ResolverSourceFor(on, liveStill + "?format=png")
                          == Features.Chat.ChatMediaUrl.ResolverSourceFor(on, liveStill), ""));

            r.Add(("images: the real source is read out of a Discord proxy url",
                   Features.Chat.ChatMediaUrl.SourceOf(klipyProxy)
                       == "https://static.klipy.com/ii/abc/9b/0e/p5dC8SCQ.webp",
                   Features.Chat.ChatMediaUrl.SourceOf(klipyProxy)));
            r.Add(("images: a url that is not a proxy wrapper is left alone",
                   Features.Chat.ChatMediaUrl.SourceOf("https://cdn.discordapp.com/a/b.png")
                       == "https://cdn.discordapp.com/a/b.png", ""));
            r.Add(("images: a webp source is not mistaken for a gif",
                   !Features.Chat.ChatMediaUrl.IsGif(on, klipyProxy)
                   && Features.Chat.ChatMediaUrl.KindOf(on, klipyProxy) == Features.Chat.ChatMediaUrl.Kind.Image, ""));
            r.Add(("images: a gif source behind a proxy is still a gif",
                   Features.Chat.ChatMediaUrl.IsGif(on, tenorProxy), ""));

            // Measured 2026-09-11 on this exact wrapper shape: ?format=gif returned all 42 frames, ?format=png a flat still.
            r.Add(("images: a WRAPPED webp is asked for gif, so the fallback still animates",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, klipyProxy)
                       .EndsWith("format=gif", StringComparison.Ordinal),
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, klipyProxy)));
            r.Add(("images: an UNWRAPPED webp still asks for png, because the proxy answers 415 to a gif request",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b.webp")
                       .EndsWith("format=png", StringComparison.Ordinal),
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b.webp")));
            r.Add(("images: a gif source is asked for gif, so it keeps every frame",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, tenorProxy)
                       .EndsWith("format=gif", StringComparison.Ordinal),
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, tenorProxy)));

            // A share page serves HTML, not media, so it must never reach a texture loader.
            r.Add(("images: a share page is not real media, even with a vouch",
                   !Features.Chat.ChatMediaUrl.IsRealMedia(on, klipyPage, discordSaysMedia: true), ""));

            // Refusing every extension-less wrapper would also refuse the legitimate images Discord re-hosts.
            const string klipyPageProxied = "https://images-ext-1.discordapp.net/external/hash/https/"
                                          + "klipy.com/gifs/lloyd-frontera-tged";
            r.Add(("images: a wrapped page reaches Discord, which refuses it - it never reaches a decoder",
                   Features.Chat.ChatMediaUrl.IsRealMedia(on, klipyPageProxied, discordSaysMedia: true), ""));
            // The BARE share page is the one nothing downstream would catch, and it stays refused.
            r.Add(("images: an unwrapped third-party page names no media and is refused",
                   !Features.Chat.ChatMediaUrl.IsRealMedia(on, klipyPage, discordSaysMedia: true), ""));
            // A real Discord attachment often has no extension either, so the vouch must still admit it.
            r.Add(("images: Discord's vouch still covers an extensionless attachment on its own CDN",
                   Features.Chat.ChatMediaUrl.IsRealMedia(on, "https://cdn.discordapp.com/attachments/1/2/photo",
                                                          discordSaysMedia: true), ""));
            r.Add(("images: a wrapped url naming real media is still fine",
                   Features.Chat.ChatMediaUrl.IsRealMedia(on, tenorProxy, discordSaysMedia: true), ""));

            // Subdomain matching is exact-or-suffix, so media1.tenor.com is not covered by media.tenor.com.
            const string tenorOriginal = "https://media.tenor.com/m/xyz/cry-bozo.gif";
            string selGif = Features.Chat.ChatMediaSelection.Choose(on, tenorProxy, tenorOriginal, out string selWhy);
            r.Add(("images: a gif original is chosen over the proxy that would flatten it",
                   selGif == tenorOriginal && selWhy.Contains("original"), selWhy));
            // Real media on an allow-listed host, so only the gif rule can refuse it and nothing else passes it.
            const string allowedWebp = "https://media.discordapp.net/attachments/1/2/pic.webp";
            string selWebp = Features.Chat.ChatMediaSelection.Choose(on, klipyProxy, allowedWebp, out string webpWhy);
            r.Add(("images: a webp original does NOT displace the proxy - the proxy can convert, the original cannot",
                   selWebp == klipyProxy, webpWhy));
            r.Add(("images: two unusable candidates select nothing at all",
                   Features.Chat.ChatMediaSelection.Choose(on, "https://", null, out _) == "", ""));

            // The preference asks the allow-list first, so it only picks between urls and never widens them.
            string selUnlisted = Features.Chat.ChatMediaSelection.Choose(
                on, tenorProxy, "https://media1.tenor.com/m/xyz/cry-bozo.gif", out string unlistedWhy);
            r.Add(("images: a gif original on a NON-allowed host does not displace the proxy",
                   selUnlisted == tenorProxy, unlistedWhy));
            var pageAllowed = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true, AllowTypedImageUrls = true,
                ImageHostAllowList = new[] { "klipy.com", "tenor.com" },
            };
            r.Add(("images: an allow-listed share page is STILL refused, and says why",
                   Features.Chat.ChatImagePolicy.Vet(pageAllowed, klipyPage, false, true, out string pageWhy) == ""
                   && pageWhy.Contains("names no media file"), pageWhy));
            r.Add(("images: a tenor share page is refused on a vouched embed too",
                   Features.Chat.ChatImagePolicy.Vet(pageAllowed,
                       "https://tenor.com/view/logan-paul-sorry-gif-22857221", false, true) == "", ""));
            r.Add(("images: the media behind a share page still passes",
                   Features.Chat.ChatImagePolicy.Vet(on, tenorProxy, false, true).Length > 0, ""));

            // Every refusal now carries a reason. A blank one meant an owner had nothing to act on.
            r.Add(("images: a refusal names the gate that refused it",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://evil.example/a.png", false, false, out string hostWhy) == ""
                   && hostWhy.Contains("host not allow-listed"), hostWhy));
            // Measured 2026-09-11: asking the proxy to re-encode a 212KB jpeg turned it into 1.1MB of png for the same picture.
            r.Add(("images: a format the client already reads is asked for nothing",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/attachments/1/2/shot.png")
                       == "https://media.discordapp.net/attachments/1/2/shot.png"
                   && Features.Chat.ChatImagePolicy.WantedFormat(on, "https://images-ext-1.discordapp.net/external/h/https/i.ytimg.com/vi/x/hq.jpg") == null,
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/attachments/1/2/shot.png")));
            r.Add(("images: the image extension list is owner-editable",
                   Features.Chat.ChatMediaUrl.KindOf(
                       new Features.Chat.ChatConfig { ImageFileExtensions = new[] { ".heic" } },
                       "https://images-ext-1.discordapp.net/external/h/https/mysite.example/a.heic")
                   == Features.Chat.ChatMediaUrl.Kind.Image, ""));

            r.Add(("images: an existing query is appended to, not clobbered",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b.gif?width=100")
                       == "https://media.discordapp.net/a/b.gif?width=100&format=gif",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b.gif?width=100")));
            r.Add(("images: a format already asked for is left alone",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b?format=gif")
                       == "https://media.discordapp.net/a/b?format=gif", ""));
            // Appending a parameter to some other CDN is at best ignored and at worst breaks a working link.
            r.Add(("images: a non-Discord host is never rewritten",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.tenor.com/a/b.gif")
                       == "https://media.tenor.com/a/b.gif", ""));
            r.Add(("images: a video is never asked to transcode into a picture",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(on, "https://media.discordapp.net/a/b.mp4")
                       == "https://media.discordapp.net/a/b.mp4", ""));
            var noTrans = new Features.Chat.ChatConfig { DiscordProxyTranscode = false, TranscodeHosts = new[] { "discordapp.net" } };
            r.Add(("images: transcoding can be turned off",
                   Features.Chat.ChatImagePolicy.ApplyTranscode(noTrans, "https://media.discordapp.net/a/b")
                       == "https://media.discordapp.net/a/b", ""));

            // The proxy flattens animation, so a real gif prefers the original and everything else prefers the proxy.
            r.Add(("images: only a real gif original can beat the proxy",
                   Features.Chat.ChatMediaUrl.IsGif(on, "https://media1.tenor.com/m/abc/thing.gif")
                   && !Features.Chat.ChatMediaUrl.IsGif(on, "https://static.klipy.com/ii/a/b/c.webp")
                   && !Features.Chat.ChatMediaUrl.IsGif(on, "https://klipy.com/gifs/thing"), ""));
            r.Add(("images: preferring the gif original is owner-switchable",
                   new Features.Chat.ChatConfig().PreferOriginalForAnimated, ""));
            // The preference picks a url; it must never widen what is allowed.
            r.Add(("images: the original still has to be an allowed host",
                   Features.Chat.ChatImagePolicy.Vet(on, "https://sketchy.example/a/thing.gif", false, true) == "", ""));

            // A video is offered as a link and never downloaded, which is why its size is never checked.
            r.Add(("video: a video extension is recognised",
                   Features.Chat.ChatImagePolicy.LooksLikeVideo("/a/b.mp4")
                   && Features.Chat.ChatImagePolicy.LooksLikeVideo("/a/b.WEBM")
                   && !Features.Chat.ChatImagePolicy.LooksLikeVideo("/a/b.png"), ""));

            string vurl = Features.Chat.ChatImagePolicy.VetMedia(on, "https://cdn.discordapp.com/a/clip.mp4",
                                                                 false, true, out bool wasVideo);
            r.Add(("video: an allowed video is vetted and flagged as video",
                   wasVideo && vurl.Length > 0, vurl));
            r.Add(("video: a video on an untrusted host is still refused",
                   Features.Chat.ChatImagePolicy.VetMedia(on, "https://evil.example/clip.mp4", false, true, out _) == "", ""));

            var noVideo = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true, AllowVideoLinks = false,
                ImageHostAllowList = Features.Chat.ChatConfig.DefaultImageHosts(),
                VideoFileExtensions = new[] { ".mp4" },
            };
            r.Add(("video: an owner can refuse video links entirely",
                   Features.Chat.ChatImagePolicy.VetMedia(noVideo, "https://cdn.discordapp.com/a/clip.mp4",
                                                          false, true, out _) == "", ""));

            // An image still comes back NOT flagged as video, or every picture would render as a link.
            Features.Chat.ChatImagePolicy.VetMedia(on, "https://cdn.discordapp.com/a/b.png", false, false, out bool notVideo);
            r.Add(("video: an image is not mistaken for a video", !notVideo, ""));

            // The format lists live in the config, so a format KMH never heard of can be allowed without a build.
            var custom = new Features.Chat.ChatConfig
            {
                AllowImagePreviews = true,
                ImageHostAllowList = Features.Chat.ChatConfig.DefaultImageHosts(),
                ImageFileExtensions = new[] { ".avif" },
            };
            r.Add(("images: the accepted extensions come from the config, not from code",
                   Features.Chat.ChatImagePolicy.HasExtension("/a/b.avif", custom.ImageFileExtensions)
                   && !Features.Chat.ChatImagePolicy.HasExtension("/a/b.png", custom.ImageFileExtensions), ""));
            r.Add(("images: an extension is matched with or without its leading dot, and past a query",
                   Features.Chat.ChatImagePolicy.HasExtension("/a/b.png?x=1", new[] { "png" }), ""));

            // Garbage in is "" out, never an exception - this runs on every single chat message.
            bool threw = false;
            try
            {
                Features.Chat.ChatImagePolicy.Vet(null, false);
                Features.Chat.ChatImagePolicy.Vet("", false);
                Features.Chat.ChatImagePolicy.Vet("not a url at all", false);
                Features.Chat.ChatImagePolicy.Vet("https://", false);
                Features.Chat.ChatImagePolicy.Vet("javascript:alert(1)", false);
            }
            catch { threw = true; }
            r.Add(("images: malformed input yields no preview and never throws", !threw, ""));

            r.Add(("images: the first https URL is found in a message body",
                   Features.Chat.ChatImagePolicy.FirstUrl("look at https://cdn.discordapp.com/a/b.png ok")
                   == "https://cdn.discordapp.com/a/b.png", ""));
            r.Add(("images: a body with no URL yields nothing",
                   Features.Chat.ChatImagePolicy.FirstUrl("just talking") == ""
                   && Features.Chat.ChatImagePolicy.FirstUrl(null) == "", ""));

            // A separate permission from attachments, because an attachment passed through the owner's Discord.
            bool typedOn = Features.Chat.ChatConfig.Current?.AllowTypedImageUrls == true;
            r.Add(("images: typed URLs need their own opt-in, separate from attachments",
                   typedOn || Features.Chat.ChatImagePolicy.Vet("https://cdn.discordapp.com/a/b.png", typedByPlayer: true) == "",
                   typedOn ? "owner enabled typed URLs" : ""));

            return r;
        }
    }
}
