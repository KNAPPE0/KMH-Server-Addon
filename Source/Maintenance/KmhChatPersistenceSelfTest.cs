using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Chat;
using KMHServerAddon.Features.Chat.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Round-trips through the real serializer and load path, so a stand-in cannot diverge from what the server does.
    internal static class KmhChatPersistenceSelfTest
    {
        private const string Guild = "guild:testers";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            long now = DateTime.UtcNow.Ticks;

            ChatStore.PersistedState live = ChatStore.BuildStateForTest(now);
            try
            {
                ChatStore.ResetForTest();

                ChatMessage a = ChatStore.PostForTest("Alice", ChatChannels.Server, "hello server", "ingame");
                ChatMessage b = ChatStore.PostForTest("Bob",   ChatChannels.Server, "from discord", "discord");
                ChatMessage g = ChatStore.PostForTest("Alice", Guild,               "guild line",   "ingame");
                string dm     = ChatChannels.Dm("Alice", "Bob");
                ChatMessage d = ChatStore.PostForTest("Alice", dm,                  "direct line",  "ingame");

                bool posted = a != null && b != null && g != null && d != null;
                r.Add(("Chat persistence: fixture posted across server/guild/dm", posted,
                    posted ? "" : $"a={a != null} b={b != null} g={g != null} d={d != null}"));
                if (!posted) return r;

                string json = JsonFileStore.ToJson(ChatStore.BuildStateForTest(now));
                bool wrote = !string.IsNullOrEmpty(json) && json.Contains("hello server");
                r.Add(("Chat persistence: state serializes to JSON", wrote, wrote ? $"{json.Length} chars" : "empty/missing body"));

                ChatStore.PersistedState reloaded = JsonFileStore.FromJson<ChatStore.PersistedState>(json);
                ChatStore.ResetForTest();                 // prove the reload, not leftover memory
                ChatStore.ApplyLoaded(reloaded, now);

                List<ChatMessage> server = ChatStore.Recent(ChatChannels.Server);
                List<ChatMessage> guild  = ChatStore.Recent(Guild);
                List<ChatMessage> direct = ChatStore.Recent(dm);

                r.Add(("Chat persistence: server history survives a reload", server.Count == 2, $"{server.Count} message(s)"));
                r.Add(("Chat persistence: guild history survives a reload",  guild.Count  == 1, $"{guild.Count} message(s)"));
                r.Add(("Chat persistence: DM history survives a reload",     direct.Count == 1, $"{direct.Count} message(s)"));

                if (server.Count == 2)
                {
                    ChatMessage m0 = server[0], m1 = server[1];
                    bool content = m0.Body == "hello server" && m1.Body == "from discord";
                    r.Add(("Chat persistence: message text survives verbatim", content, $"'{m0.Body}' / '{m1.Body}'"));

                    bool sender = m0.FromUsername == "Alice" && m1.FromUsername == "Bob";
                    r.Add(("Chat persistence: sender identity survives", sender, $"{m0.FromUsername} / {m1.FromUsername}"));

                    // Losing the mark would silently present a relayed message as native in-game.
                    bool origin = m0.Origin == "ingame" && m1.Origin == "discord";
                    r.Add(("Chat persistence: Discord origin survives", origin, $"{m0.Origin} / {m1.Origin}"));

                    bool stamps = m0.SentUtcTicks == a.SentUtcTicks && m1.SentUtcTicks == b.SentUtcTicks;
                    r.Add(("Chat persistence: timestamps survive exactly", stamps, $"{m0.SentUtcTicks} vs {a.SentUtcTicks}"));

                    bool ids = m0.Id == a.Id && m1.Id == b.Id;
                    r.Add(("Chat persistence: message ids survive", ids, $"{m0.Id},{m1.Id} vs {a.Id},{b.Id}"));

                    bool ordered = m0.SentUtcTicks <= m1.SentUtcTicks;
                    r.Add(("Chat persistence: history reloads oldest-first", ordered, ""));

                    bool channel = m0.Channel == ChatChannels.Server && m1.Channel == ChatChannels.Server;
                    r.Add(("Chat persistence: channel survives", channel, $"{m0.Channel} / {m1.Channel}"));
                }

                // FlushAll runs only on save, backup and exit, so without the dirty flag a crash costs everything since boot.
                ChatStore.ResetForTest();
                ChatStore.SaveToDisk();
                bool cleanAfterSave = !ChatStore.Dirty;
                ChatStore.PostForTest("Alice", ChatChannels.Server, "unsaved line", "ingame");
                r.Add(("Chat persistence: a new message marks the store dirty", cleanAfterSave && ChatStore.Dirty,
                    $"clean-after-save={cleanAfterSave} dirty-after-post={ChatStore.Dirty}"));

                // A staff removal that is not written back would reappear on the next boot.
                ChatStore.SaveToDisk();
                ChatMessage victim = ChatStore.PostForTest("Bob", ChatChannels.Server, "remove me", "ingame");
                ChatStore.SaveToDisk();
                bool removed = victim != null && ChatStore.Remove(ChatChannels.Server, victim.Id);
                r.Add(("Chat persistence: a staff removal marks the store dirty", removed && ChatStore.Dirty, $"removed={removed}"));

                ChatStore.ResetForTest();
                ChatStore.ApplyLoaded(reloaded, now);

                // A reissued id would let a staff removal delete the wrong message.
                ChatMessage next = ChatStore.PostForTest("Alice", ChatChannels.Server, "after restart", "ingame");
                bool freshId = next != null && next.Id > d.Id;
                r.Add(("Chat persistence: ids are not reissued after a reload", freshId, next == null ? "post refused" : $"{next.Id} > {d.Id}"));

                ChatStore.ResetForTest();
                ChatStore.ApplyLoaded(reloaded, now);
                ChatStore.ApplyLoaded(reloaded, now);
                int after = ChatStore.Recent(ChatChannels.Server).Count;
                r.Add(("Chat persistence: reload replaces rather than duplicates", after == 2, $"{after} message(s) after two loads"));

                ChatStore.ResetForTest();
                var aged = new ChatStore.PersistedState { NextId = 99 };
                aged.Channels[ChatChannels.Server] = new List<ChatMessage>
                {
                    new ChatMessage { Id = 1, Channel = ChatChannels.Server, FromUsername = "Alice", Body = "ancient",
                                      Origin = "ingame", SentUtcTicks = now - TimeSpan.FromHours(ChatStore.RetentionHours + 12).Ticks },
                    new ChatMessage { Id = 2, Channel = ChatChannels.Server, FromUsername = "Alice", Body = "recent",
                                      Origin = "ingame", SentUtcTicks = now },
                };
                ChatStore.ApplyLoaded(aged, now);
                List<ChatMessage> kept = ChatStore.Recent(ChatChannels.Server);
                bool agedOut = kept.Count == 1 && kept[0].Body == "recent";
                r.Add(("Chat persistence: retention applies on load, not just on post", agedOut, $"{kept.Count} kept"));

                // The file is owner-editable, so a message must not be routed by a channel field it claims for itself.
                ChatStore.ResetForTest();
                var spoof = new ChatStore.PersistedState { NextId = 5 };
                spoof.Channels[ChatChannels.Server] = new List<ChatMessage>
                {
                    new ChatMessage { Id = 1, Channel = Guild, FromUsername = "Mallory", Body = "wrong channel",
                                      Origin = "ingame", SentUtcTicks = now },
                };
                ChatStore.ApplyLoaded(spoof, now);
                bool restamped = ChatStore.Recent(ChatChannels.Server).Count == 1 && ChatStore.Recent(Guild).Count == 0;
                r.Add(("Chat persistence: a mismatched channel field is re-stamped from its key", restamped, ""));

                // Discord attaches an embed by EDITING the message, and that path skipped the resolver entirely.
                ChatStore.ResetForTest();
                // Already vetted, because what is under test is SetImage's own resolver call, not the host allow-list.
                const string wrapped = "https://images-ext-1.discordapp.net/external/h/https/"
                                     + "static.klipy.com/ii/a/b/c.webp?format=gif";
                ChatMessage late = ChatStore.PostForTest("Alice", ChatChannels.Server, "https://klipy.com/gifs/x", "discord");
                bool blank = late != null && string.IsNullOrEmpty(late.MediaId);
                bool attached = ChatStore.SetImage(ChatChannels.Server, late.Id, wrapped);
                r.Add(("Chat persistence: a LATE Discord embed gets a resolver id, or every late gif is a flat still",
                       blank && attached && !string.IsNullOrEmpty(late.MediaId),
                       $"blank={blank} attached={attached} id='{late?.MediaId}'"));

                ChatMessage decodable = ChatStore.PostForTest("Alice", ChatChannels.Server, "another", "discord");
                ChatStore.SetImage(ChatChannels.Server, decodable.Id, "https://cdn.discordapp.com/attachments/1/2/pic.png");
                r.Add(("Chat persistence: a late embed the client can decode is still left on the direct path",
                       string.IsNullOrEmpty(decodable.MediaId) && !decodable.IsVideo, $"id='{decodable.MediaId}'"));

                // A late VIDEO must not be handed to the image decoder, and must not take the resolver path either.
                ChatMessage clip = ChatStore.PostForTest("Alice", ChatChannels.Server, "a clip", "discord");
                ChatStore.SetImage(ChatChannels.Server, clip.Id, "https://cdn.discordapp.com/attachments/1/2/clip.mp4", true);
                r.Add(("Chat persistence: a late embed that is a video is marked as one",
                       clip.IsVideo && string.IsNullOrEmpty(clip.MediaId), $"video={clip.IsVideo} id='{clip.MediaId}'"));

                // History written before the resolver existed carries a url and no media id.
                ChatStore.ResetForTest();
                var legacy = new ChatStore.PersistedState { NextId = 20 };
                legacy.Channels[ChatChannels.Server] = new List<ChatMessage>
                {
                    new ChatMessage { Id = 1, Channel = ChatChannels.Server, FromUsername = "Alice", Body = "old webp",
                                      Origin = "discord", SentUtcTicks = now, MediaId = "",
                                      ImageUrl = "https://cdn.discordapp.com/attachments/1/2/pic.webp" },
                    new ChatMessage { Id = 2, Channel = ChatChannels.Server, FromUsername = "Alice", Body = "old png",
                                      Origin = "discord", SentUtcTicks = now, MediaId = "",
                                      ImageUrl = "https://cdn.discordapp.com/attachments/1/3/pic.png" },
                };
                int rebuilt = ChatStore.RebuildMediaIds(legacy);
                ChatMessage webp = legacy.Channels[ChatChannels.Server][0];
                ChatMessage png  = legacy.Channels[ChatChannels.Server][1];
                r.Add(("Chat persistence: legacy media a client cannot decode gets its resolver id back on load",
                       rebuilt == 1 && !string.IsNullOrEmpty(webp.MediaId), $"rebuilt={rebuilt} id='{webp.MediaId}'"));
                r.Add(("Chat persistence: media the client can already decode is left alone",
                       string.IsNullOrEmpty(png.MediaId), $"id='{png.MediaId}'"));

                // The id -> source cache is wiped at boot, so an id carried forward untouched resolves to nothing.
                string derived = webp.MediaId;
                webp.MediaId = "stale-from-an-older-rule";
                int again = ChatStore.RebuildMediaIds(legacy);
                r.Add(("Chat persistence: a stale media id is re-pointed at its source rather than kept",
                       again == 1 && webp.MediaId == derived, $"rebuilt={again} id='{webp.MediaId}'"));

                int settled = ChatStore.RebuildMediaIds(legacy);
                r.Add(("Chat persistence: re-running changes nothing once the ids are right",
                       settled == 0 && webp.MediaId == derived, $"rebuilt={settled}"));
            }
            finally
            {
                ChatStore.ResetForTest();
                ChatStore.ApplyLoaded(live, now);   // hand the server its real chat back
            }

            return r;
        }
    }
}
