using System;
using System.Collections.Generic;
using System.Text;
using KMHServerAddon.Features.Chat.Dto;
using KMHServerAddon.Persistence;
using KMHServerAddon.Security;

namespace KMHServerAddon.Features.Chat
{
    // Every identity here is server-set by the caller, never taken from a packet.
    internal static class ChatStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<ChatMessage>> _byChannel =
            new Dictionary<string, List<ChatMessage>>(StringComparer.Ordinal);
        private static long _nextId = 1;

        private static readonly Util.KmhRateWindow _recentSends = new Util.KmhRateWindow();

        // The client stamps each send with a token, so a reconnect retry does not post the message twice.
        private static readonly KmhSeenGuard _dedup = new KmhSeenGuard(TimeSpan.FromMinutes(2));

        public static int TotalMessageCount
        {
            get
            {
                int n = 0;
                lock (_lock)
                    foreach (List<ChatMessage> ch in _byChannel.Values) n += ch?.Count ?? 0;
                return n;
            }
        }

        // Read through Current on every access, so an edited config takes effect without a restart.
        public static int MaxMessageLength    => ChatConfig.Current.MaxMessageLength;
        public static int MaxRecentPerChannel => ChatConfig.Current.MaxRecentPerChannel;
        public static int MaxSendsPerWindow   => ChatConfig.Current.MaxSendsPerWindow;
        public static int SendWindowSeconds   => ChatConfig.Current.SendWindowSeconds;
        public static int RetentionHours      => ChatConfig.Current.RetentionHours;

        public static ChatMessage Post(string from, string channel, string bodyIn, string token, string origin, out string reason, bool senderVerified = true,
                                       string imageUrl = null, string mediaRef = null)
        {
            reason = null;
            if (string.IsNullOrEmpty(from))          { reason = "No sender.";      return null; }
            if (!ChatChannels.IsWellFormed(channel)) { reason = "Unknown channel."; return null; }
            string body = Clamp(Sanitize(bodyIn), MaxMessageLength);
            if (string.IsNullOrEmpty(body))         { reason = "Empty message.";  return null; }

            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                // Before MarkIfNew, which consumes the token, or a refused send would burn it and swallow the retry.
                if (!AllowSendLocked(from, now)) { reason = "You're chatting too fast - slow down."; return null; }
                if (!string.IsNullOrEmpty(token) && !_dedup.MarkIfNew($"{from}|{channel}|{token}", now))
                { reason = "Duplicate message ignored."; return null; }

                ChatConfig icfg = ChatConfig.Current;
                string vettedMedia = string.IsNullOrEmpty(imageUrl)
                    ? ChatImagePolicy.VetMedia(icfg, ChatImagePolicy.FirstUrl(body), typedByPlayer: true,  knownImage: false, out bool mediaIsVideo)
                    : ChatImagePolicy.VetMedia(icfg, imageUrl,                       typedByPlayer: false, knownImage: true,  out mediaIsVideo);

                string mediaId = ResolverIdFor(icfg, vettedMedia, mediaIsVideo);

                ChatMessage msg = new ChatMessage
                {
                    Id = _nextId++, Channel = channel, FromUsername = from,
                    Body = body, SentUtcTicks = now, Origin = string.IsNullOrEmpty(origin) ? "ingame" : origin,
                    SenderVerified = senderVerified,
                    // Vetted on the way in, so a caller only ever proposes a url and the policy decides.
                    ImageUrl = vettedMedia,
                    IsVideo  = mediaIsVideo,
                    MediaId  = mediaId,
                    MediaRef = mediaRef ?? "",
                };
                List<ChatMessage> ring = Ring(channel);
                ring.Add(msg);
                TrimRing(ring, now);
                _dirty = true;
                return msg;
            }
        }

        public static List<ChatMessage> Recent(string channel)
        {
            List<ChatMessage> outList = new List<ChatMessage>();
            if (!ChatChannels.IsWellFormed(channel)) return outList;
            lock (_lock) if (_byChannel.TryGetValue(channel, out List<ChatMessage> ring)) outList.AddRange(ring);
            return outList;
        }

        // Answers only for references a live message carries, so a client cannot name a channel of its own choosing.
        public static string ChannelOfMediaRef(string mediaRef)
        {
            if (string.IsNullOrEmpty(mediaRef)) return "";
            lock (_lock)
            {
                foreach (KeyValuePair<string, List<ChatMessage>> kv in _byChannel)
                    foreach (ChatMessage m in kv.Value)
                        if (m != null && string.Equals(m.MediaRef, mediaRef, StringComparison.Ordinal)) return kv.Key;
            }
            return "";
        }

        // Alongside the url, never instead of it, so an older client or a cold cache still shows the still.
        internal static string ResolverIdFor(ChatConfig cfg, string vettedMedia, bool isVideo)
        {
            if (isVideo || string.IsNullOrEmpty(vettedMedia)) return "";
            if (!Media.MediaConfig.Current.ServerMediaResolverEnabled) return "";
            if (!ChatMediaUrl.NeedsResolver(cfg, vettedMedia)) return "";

            string fetchFrom = ChatMediaUrl.ResolverSourceFor(cfg, vettedMedia);
            return string.IsNullOrEmpty(fetchFrom) ? "" : Media.KmhMediaCache.Register(fetchFrom);
        }

        // Fills an empty slot only, so a late preview can never replace a picture people have already seen.
        public static bool SetImage(string channel, long id, string vettedUrl, bool isVideo = false)
        {
            if (!ChatChannels.IsWellFormed(channel) || id <= 0 || string.IsNullOrEmpty(vettedUrl)) return false;
            lock (_lock)
            {
                if (!_byChannel.TryGetValue(channel, out List<ChatMessage> ring)) return false;
                foreach (ChatMessage m in ring)
                {
                    if (m == null || m.Id != id) continue;
                    if (!string.IsNullOrEmpty(m.ImageUrl)) return false;
                    m.ImageUrl = vettedUrl;
                    // Decided by the same policy as a message's own media, or the client feeds a video to an image decoder.
                    m.IsVideo  = isVideo;
                    // Discord attaches an embed AFTER the message, and skipping this left every late gif a flattened still.
                    m.MediaId  = ResolverIdFor(ChatConfig.Current, vettedUrl, isVideo);
                    _dirty = true;
                    return true;
                }
            }
            return false;
        }
        public static bool Remove(string channel, long id)
        {
            if (!ChatChannels.IsWellFormed(channel) || id <= 0) return false;
            lock (_lock)
                if (_byChannel.TryGetValue(channel, out List<ChatMessage> ring))
                {
                    bool removed = ring.RemoveAll(m => m != null && m.Id == id) > 0;
                    if (removed) _dirty = true;
                    return removed;
                }
            return false;
        }

        private static List<ChatMessage> Ring(string channel)
        {
            if (!_byChannel.TryGetValue(channel, out List<ChatMessage> ring)) { ring = new List<ChatMessage>(); _byChannel[channel] = ring; }
            return ring;
        }

        internal static void TrimRing(List<ChatMessage> ring, long now)
        {
            if (ring.Count > MaxRecentPerChannel) ring.RemoveRange(0, ring.Count - MaxRecentPerChannel);
            long cutoff = now - TimeSpan.FromHours(RetentionHours).Ticks;
            int drop = 0;
            while (drop < ring.Count && ring[drop].SentUtcTicks < cutoff) drop++;
            if (drop > 0) ring.RemoveRange(0, drop);
        }

        private static bool AllowSendLocked(string from, long now)
            => _recentSends.Allow(from, now, MaxSendsPerWindow, SendWindowSeconds);

        internal static string Normalize(string s) => Clamp(Sanitize(s), MaxMessageLength);

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) if (c == '\n' || !char.IsControl(c)) sb.Append(c);
            return sb.ToString().Trim();
        }

        private static string Clamp(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s.Substring(0, max) : s);

        // The empty state is written through, so the wipe is deliberate rather than something a restart could undo.
        public static void ClearForNewSeason()
        {
            lock (_lock) { _byChannel.Clear(); _recentSends.Clear(); _nextId = 1; }
            // Ids restart at 1, so a kept relay mapping would let a Discord delete remove an unrelated new line.
            Discord.KmhChatRelayIndex.Clear();
            SaveToDisk();
        }

        // The dedup guard is deliberately absent, since a stale token would swallow the first message after a restart.
        internal sealed class PersistedState
        {
            public long NextId { get; set; } = 1;
            public Dictionary<string, List<ChatMessage>> Channels { get; set; } = new Dictionary<string, List<ChatMessage>>();
        }

        public static void LoadFromDisk()
        {
            if (!ChatConfig.Current.PersistHistory) return;
            if (!JsonFileStore.TryLoad(KmhDataPaths.ChatFile, out PersistedState s) || s?.Channels == null) return;
            ApplyLoaded(s, DateTime.UtcNow.Ticks);
        }

        // Split from the file read so the self-test drives this exact validation rather than a stand-in that could diverge.
        internal static void ApplyLoaded(PersistedState s, long now)
        {
            if (s?.Channels == null) return;
            RebuildMediaIds(s);
            lock (_lock)
            {
                _byChannel.Clear();
                long maxId = 0;
                foreach (KeyValuePair<string, List<ChatMessage>> kv in s.Channels)
                {
                    // An older or hand-edited file can name a channel this build cannot route.
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || !ChatChannels.IsWellFormed(kv.Key)) continue;

                    List<ChatMessage> ring = new List<ChatMessage>();
                    foreach (ChatMessage m in kv.Value)
                    {
                        if (m == null || string.IsNullOrEmpty(m.FromUsername) || string.IsNullOrEmpty(m.Body)) continue;
                        // Re-stamped and re-sanitized because the file is owner-editable.
                        m.Channel = kv.Key;
                        m.Body    = Clamp(Sanitize(m.Body), MaxMessageLength);
                        m.Origin  = string.IsNullOrEmpty(m.Origin) ? "ingame" : m.Origin;
                        if (string.IsNullOrEmpty(m.Body)) continue;
                        if (m.Id > maxId) maxId = m.Id;
                        ring.Add(m);
                    }
                    ring.Sort((a, b) => a.SentUtcTicks.CompareTo(b.SentUtcTicks));
                    // Applied on load too, or time passing while the server was down would resurrect a week-old ring.
                    TrimRing(ring, now);
                    if (ring.Count > 0) _byChannel[kv.Key] = ring;
                }
                // Ids must never be reissued, because staff removal and client dedup both key on them.
                _nextId = Math.Max(s.NextId, maxId + 1);
                if (_nextId < 1) _nextId = 1;
            }
        }

        // The id cache is wiped at boot, so ids are re-derived here rather than trusted from the file.
        internal static int RebuildMediaIds(PersistedState s)
        {
            if (s?.Channels == null) return 0;
            ChatConfig cfg = ChatConfig.Current;
            if (cfg == null || !Media.MediaConfig.Current.ServerMediaResolverEnabled) return 0;

            int rebuilt = 0;
            foreach (KeyValuePair<string, List<ChatMessage>> kv in s.Channels)
            {
                if (kv.Value == null) continue;
                foreach (ChatMessage m in kv.Value)
                {
                    if (m == null) continue;
                    string id = ResolverIdFor(cfg, m.ImageUrl, m.IsVideo);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!string.Equals(id, m.MediaId, StringComparison.Ordinal)) { m.MediaId = id; rebuilt++; }
                }
            }
            if (rebuilt > 0) Diagnostics.ServerLog.Debug($"Chat: re-pointed {rebuilt} media id(s) at their source.");
            return rebuilt;
        }

        public static void SaveToDisk()
        {
            if (!ChatConfig.Current.PersistHistory) return;
            long seq;
            PersistedState s;
            lock (_lock)
            {
                s      = BuildStateLocked(DateTime.UtcNow.Ticks);
                seq    = JsonFileStore.NextSequence();
                _dirty = false;
            }
            JsonFileStore.Save(KmhDataPaths.ChatFile, s, seq);
        }

        // The scheduler's periodic drain is load-bearing: FlushAll alone would lose everything on a force-kill.
        private static bool _dirty;

        public static bool Dirty { get { lock (_lock) return _dirty; } }

        public static void SaveIfDirty()
        {
            if (!ChatConfig.Current.PersistHistory) return;
            lock (_lock) { if (!_dirty) return; }
            SaveToDisk();
        }

        private static PersistedState BuildStateLocked(long now)
        {
            PersistedState s = new PersistedState();
            foreach (KeyValuePair<string, List<ChatMessage>> kv in _byChannel)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                List<ChatMessage> ring = new List<ChatMessage>(kv.Value);
                TrimRing(ring, now);
                if (ring.Count > 0) s.Channels[kv.Key] = ring;
            }
            s.NextId = _nextId;
            return s;
        }

        // Snapshot and restore without touching disk, so a suite can put the server's real state back afterwards.
        internal static PersistedState BuildStateForTest(long now) { lock (_lock) return BuildStateLocked(now); }
        internal static void ResetForTest() { lock (_lock) { _byChannel.Clear(); _recentSends.Clear(); _nextId = 1; } }
        internal static ChatMessage PostForTest(string from, string channel, string body, string origin, bool senderVerified = true, string imageUrl = null)
            => Post(from, channel, body, null, origin, out _, senderVerified, imageUrl);
    }
}
