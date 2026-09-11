using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Chat;
using KMHServerAddon.Features.Chat.Dto;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    // Relaying only native lines outward, and stamping every inbound line origin="discord", is what stops the loop.
    internal static class KmhChatDiscordBridge
    {
        public static bool OutboundEnabled
        {
            get { DiscordConfig c = DiscordBridge.Config; return c != null && c.IsEnabled && c.KmhChatChannelId != 0 && c.KmhChatToDiscordOn; }
        }

        public static bool InboundEnabled
        {
            get { DiscordConfig c = DiscordBridge.Config; return c != null && c.IsEnabled && c.KmhChatChannelId != 0 && c.KmhChatFromDiscordOn; }
        }

        public static bool ShouldRelayOutbound(ChatMessage msg)
            => msg != null && msg.Origin == "ingame" && msg.Channel == ChatChannels.Server;

        // Chat is an ordered stream, so it is queued and drained by one paced loop rather than posted per line.
        private const int   MaxQueued      = 200;
        private const int   MaxBatchChars  = 1800;   // Discord's limit is 2000; leave headroom
        private const int   PaceMs         = 1200;

        // The KMH id rides along, or moderating an in-game line could never find the Discord copy to strike out.
        private struct Pending { public long KmhId; public string Line; }

        private static readonly object _qLock = new object();
        private static readonly Queue<Pending> _queue = new Queue<Pending>();
        private static bool _draining;
        private static int  _dropped;

        public static void RelayOutbound(ChatMessage msg)
        {
            if (!OutboundEnabled || !ShouldRelayOutbound(msg)) return;
            // Sender and body are flattened into one line, so an unflattened body could forge a second one.
            string safe = DiscordText.SafeRelayBody(msg.Body);
            if (safe.Length > 1500) safe = safe.Substring(0, 1497) + "…";
            Enqueue(msg.Id, $"**{DiscordText.SafeName(msg.FromUsername)}**: {safe}");
        }

        private static void Enqueue(long kmhId, string line)
        {
            bool start = false;
            lock (_qLock)
            {
                while (_queue.Count >= MaxQueued) { _queue.Dequeue(); _dropped++; }
                _queue.Enqueue(new Pending { KmhId = kmhId, Line = line });
                if (!_draining) { _draining = true; start = true; }
            }
            if (start) Task.Run(DrainAsync);
        }

        private static async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    string batch = TakeBatch(out List<long> ids, out List<string> lines);
                    if (batch == null) return;
                    ulong posted = await DiscordBridge.SendToChannelAsync(DiscordBridge.Config?.KmhChatChannelId ?? 0, batch)
                                                      .ConfigureAwait(false);
                    // Only a real post is indexed, or a later moderation edit would look like it failed.
                    if (posted != 0) KmhChatRelayIndex.RecordBatch(posted, ids, lines);
                    await Task.Delay(PaceMs).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // The normal exit clears this inside TakeBatch; clearing it in both places would let two drains overlap.
                ServerLog.Warn($"KMH chat -> Discord drain stopped: {ex.Message}");
                lock (_qLock) _draining = false;
            }
        }

        // ids and lines come back parallel and in posted order, which is what lets an edit replace exactly one line.
        private static string TakeBatch(out List<long> ids, out List<string> lines)
        {
            ids   = new List<long>();
            lines = new List<string>();
            var sb = new StringBuilder();
            lock (_qLock)
            {
                if (_dropped > 0)
                {
                    // Kept under id 0, because dropping it would shift every later line and misalign an edit.
                    string notice = $"_…{_dropped} chat line(s) omitted (relay was behind)_";
                    sb.Append(notice).Append('\n');
                    lines.Add(notice);
                    ids.Add(0);
                    _dropped = 0;
                }
                while (_queue.Count > 0 && sb.Length + _queue.Peek().Line.Length + 1 <= MaxBatchChars)
                {
                    Pending p = _queue.Dequeue();
                    sb.Append(p.Line).Append('\n');
                    lines.Add(p.Line);
                    ids.Add(p.KmhId);
                }

                if (sb.Length == 0) { _draining = false; return null; }
            }
            return sb.ToString().TrimEnd('\n');
        }

        // An edit rather than a delete, since one Discord message holds several lines nobody moderated.
        public static void RedactOutbound(long kmhId, string byWhom)
        {
            if (!OutboundEnabled || kmhId <= 0) return;
            string replacement = $"_message removed by {DiscordText.SafeRelayBody(byWhom ?? "staff")}_";
            if (!KmhChatRelayIndex.TryRedactOutbound(kmhId, replacement, out ulong messageId, out string newText)) return;

            ulong channel = DiscordBridge.Config?.KmhChatChannelId ?? 0;
            Task.Run(async () =>
            {
                bool ok = await DiscordBridge.EditOwnMessageAsync(channel, messageId, newText).ConfigureAwait(false);
                if (ok) ServerLog.Debug($"KMH->Discord: struck removed message #{kmhId} out of Discord message {messageId}.");
                else    ServerLog.Warn($"KMH->Discord: could not strike removed message #{kmhId} out of Discord message {messageId}.");
            });
        }

        // Every early exit logs its reason, because a relay that stops silently leaves nothing to diagnose.
        public static void Ingest(ulong discordUserId, string discordDisplay, string text, ulong discordMessageId = 0,
                                  string attachmentUrl = null)
        {
            if (!InboundEnabled)
            {
                DiscordConfig c = DiscordBridge.Config;
                ServerLog.Warn("Discord->KMH: inbound relay is OFF "
                             + $"(enabled={c?.IsEnabled}, channel={c?.KmhChatChannelId}, fromDiscord={c?.KmhChatFromDiscordOn}) - message dropped.");
                return;
            }
            text = Features.Chat.ChatDiscordText.Readable(text);

            // knownImage skips only the extension guess, since a Discord proxy url usually carries none.
            string vetted = Features.Chat.ChatImagePolicy.Vet(attachmentUrl, typedByPlayer: false, knownImage: true);
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrEmpty(vetted))
            {
                ServerLog.Debug("Discord->KMH: empty message body and no previewable attachment - nothing to relay.");
                return;
            }
            // Named accurately, or a video labelled "(image)" reads as a broken picture.
            if (string.IsNullOrWhiteSpace(text))
                text = Features.Chat.ChatImagePolicy.LooksLikeVideo(vetted) ? "(video)" : "(image)";

            string linked = LinkedAccountsStore.FindUsernameByDiscordId(discordUserId);
            bool collides = string.IsNullOrEmpty(linked) && PlayerStats.PlayerStatsStore.HasPlayer(discordDisplay);
            ResolveIdentity(linked, discordDisplay, collides, out string sender, out bool verified);

            // The identity, not just today's signed link, since the signature dies before the history does.
            string mediaRef = "";
            if (!string.IsNullOrEmpty(vetted) && discordMessageId != 0)
            {
                if (Features.Chat.ChatDiscordMedia.TryParseAttachmentUrl(attachmentUrl, out ulong chId, out ulong attId, out _))
                    mediaRef = Features.Chat.ChatDiscordMedia.MakeRef(chId, discordMessageId, attId);
                else
                {
                    // An embed carries no attachment id, but the message id still identifies it.
                    ulong ch = DiscordBridge.Config?.KmhChatChannelId ?? 0;
                    if (ch != 0) mediaRef = Features.Chat.ChatDiscordMedia.MakeRef(ch, discordMessageId, 0);
                }
            }

            ChatMessage msg = ChatStore.Post(sender, ChatChannels.Server, text, null, "discord", out string reason, verified, vetted, mediaRef);
            if (msg == null)
            {
                ServerLog.Warn($"Discord->KMH: '{sender}' message REFUSED by the chat store - {reason ?? "no reason given"}.");
                return;
            }

            // Before the broadcast, or a delete arriving a moment later finds no mapping and the copy outlives it.
            KmhChatRelayIndex.RecordInbound(discordMessageId, msg.Id);

            ServerLog.Info($"chat[server] (via Discord) {sender}: {msg.Body}");
            int sent = ChatHandler.BroadcastExternal(msg);
            ServerLog.Debug($"Discord->KMH: relayed message #{msg.Id} to {sent} connected client(s).");
        }

        // Discord resolves a preview by editing the message afterwards, so the picture attaches instead of duplicating.
        public static void AttachLateImage(ulong discordMessageId, string imageUrl)
        {
            if (!InboundEnabled || discordMessageId == 0) return;
            // VetMedia, not Vet: a late preview must be classified the same way a message's own media is.
            string vetted = Features.Chat.ChatImagePolicy.VetMedia(Features.Chat.ChatConfig.Current, imageUrl,
                                                                  typedByPlayer: false, knownImage: true, out bool isVideo);
            if (string.IsNullOrEmpty(vetted)) return;

            if (!KmhChatRelayIndex.TryPeekInbound(discordMessageId, out long kmhId)) return;
            if (!ChatStore.SetImage(ChatChannels.Server, kmhId, vetted, isVideo)) return;   // already had one, or gone

            ServerLog.Debug($"Discord->KMH: attached a late link preview to message #{kmhId}.");
            ChatHandler.RebroadcastServerChannel();
        }
        // Enqueue and take without touching Discord, so ordering and overflow stay testable.
        internal static void ResetQueueForTest() { lock (_qLock) { _queue.Clear(); _dropped = 0; _draining = true; } }
        internal static void EnqueueForTest(string line) { EnqueueForTest(0, line); }
        internal static void EnqueueForTest(long kmhId, string line) { lock (_qLock) { while (_queue.Count >= MaxQueued) { _queue.Dequeue(); _dropped++; } _queue.Enqueue(new Pending { KmhId = kmhId, Line = line }); } }
        internal static string TakeBatchForTest() => TakeBatch(out _, out _);
        internal static string TakeBatchForTest(out List<long> ids, out List<string> lines) => TakeBatch(out ids, out lines);

        // Name and verified status are decided together, or a caller could label an unproven line as a real player.
        public static void ResolveIdentity(string linkedUsername, string discordDisplay, bool displayIsTakenName,
                                           out string sender, out bool verified)
        {
            verified = !string.IsNullOrEmpty(linkedUsername);
            sender   = ResolveSender(linkedUsername, discordDisplay, displayIsTakenName);
        }

        // displayIsTakenName marks a Discord name matching a real account, which is then shown as Discord's, not theirs.
        public static string ResolveSender(string linkedUsername, string discordDisplay, bool displayIsTakenName = false)
            => !string.IsNullOrEmpty(linkedUsername) ? linkedUsername
             : string.IsNullOrEmpty(discordDisplay)  ? "Discord"
             : displayIsTakenName                    ? discordDisplay + " (Discord)"
             : discordDisplay;
    }
}
