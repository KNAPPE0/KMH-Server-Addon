using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.Features.Discord
{
    // Its traffic reaches every KMH server, so what it relays is deliberately narrower than the KmhChat bridge's rules.
    internal static class KmhWorldChatDiscordBridge
    {
        private const int PollSeconds  = 5;
        private const int PollLimit    = 25;
        private const int MaxSeenIds   = 500;

        private static readonly object _lock = new object();
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> _seenOrder = new Queue<string>();
        private static CancellationTokenSource _cts;
        private static readonly object _runLock = new object();
        // The first poll only primes, or enabling the bridge replays the last 25 world-chat lines as if just said.
        private static bool _primed;

        public static bool OutboundEnabled
        {
            get
            {
                DiscordConfig c = DiscordBridge.Config;
                return c != null && c.IsEnabled && c.WorldChatChannelId != 0 && c.WorldChatToDiscordOn;
            }
        }

        public static bool InboundEnabled
        {
            get
            {
                DiscordConfig c = DiscordBridge.Config;
                return c != null && c.IsEnabled && c.WorldChatChannelId != 0 && c.WorldChatFromDiscordOn
                    && KmhGlobalChatRelay.Available;
            }
        }

        // Running means a pump owns a live token: one that was stopped but is still winding down must not block a
        // restart, or a quick reload left nothing polling until the next boot.
        public static void Start()
        {
            CancellationTokenSource cts;
            lock (_runLock)
            {
                if (_cts != null && !_cts.IsCancellationRequested) return;
                _cts = cts = new CancellationTokenSource();
            }
            CancellationToken ct = cts.Token;
            Task.Run(async () =>
            {
                try { await PumpAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ServerLog.Warn($"World chat bridge: stopped ({ex.GetType().Name})."); }
                finally { lock (_runLock) if (ReferenceEquals(_cts, cts)) _cts = null; }
            });
        }

        public static void Stop()
        {
            lock (_runLock) { try { _cts?.Cancel(); } catch (Exception) { } }
            lock (_lock) { _seen.Clear(); _seenOrder.Clear(); _primed = false; }
        }

        internal static bool RunningForTest { get { lock (_runLock) return _cts != null && !_cts.IsCancellationRequested; } }

        // Global charges the line to this server's owner, so nothing here can claim to be a linked player.
        public static void Ingest(string discordDisplay, string text)
        {
            if (!InboundEnabled) return;
            string body = Features.Chat.ChatDiscordText.Readable(text);
            if (string.IsNullOrWhiteSpace(body)) return;
            KmhGlobalChatRelay.Send(discordDisplay, body, ChatRelaySources.Discord);
        }

        // Only when the owner asked for it: most servers want their local chat local.
        public static void IngestInGame(string player, string text)
        {
            DiscordConfig c = DiscordBridge.Config;
            if (c == null || !c.WorldChatRelayInGameOn || !KmhGlobalChatRelay.Available) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            KmhGlobalChatRelay.Send(player, text, ChatRelaySources.InGame);
        }

        private static async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (OutboundEnabled && KmhGlobalChatRelay.Available) await PollOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ServerLog.Verbose($"World chat bridge: poll failed ({ex.GetType().Name})."); }

                await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ct).ConfigureAwait(false);
            }
        }

        private static async Task PollOnceAsync(CancellationToken ct)
        {
            List<WorldChatLine> lines = await KmhGlobalChatRelay.RecentAsync(PollLimit, ct).ConfigureAwait(false);
            if (lines == null || lines.Count == 0) return;

            string me = KmhServerIdentity.Id;
            List<string> post = new List<string>();
            lock (_lock)
            {
                // Oldest first, so Discord reads in the order the messages were actually said.
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    WorldChatLine line = lines[i];
                    if (string.IsNullOrEmpty(line.MessageId)) continue;
                    // Remembered even when it is this server's own echo, so it is not reconsidered on the next poll.
                    if (!Remember(line.MessageId)) continue;
                    if (IsOwnEcho(line, me)) continue;
                    if (_primed) post.Add(Format(line));
                }
                _primed = true;
            }

            foreach (string text in post)
            {
                await DiscordBridge.SendToChannelAsync(DiscordBridge.Config?.WorldChatChannelId ?? 0, text)
                                   .ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
            }
        }

        // False when the id was already relayed. Bounded, or a long-lived server accumulates every id it ever saw.
        private static bool Remember(string messageId)
        {
            if (!_seen.Add(messageId)) return false;
            _seenOrder.Enqueue(messageId);
            while (_seenOrder.Count > MaxSeenIds) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }

        internal static string Format(WorldChatLine line)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("**").Append(DiscordText.SafeName(line.Username)).Append("**");
            if (!string.IsNullOrEmpty(line.OriginServerName))
                sb.Append(" *(").Append(DiscordText.SafeName(line.OriginServerName)).Append(")*");
            sb.Append(": ").Append(DiscordText.SafeRelayBody(DiscordBridge.Redact(line.Body)));
            return sb.ToString();
        }

        // What this server relayed is already in its channel; a local player's line has no relay source and must still post.
        internal static bool IsOwnEcho(WorldChatLine line, string myServerId)
            => line != null
            && !string.IsNullOrEmpty(line.RelaySource)
            && !string.IsNullOrEmpty(myServerId)
            && string.Equals(line.OriginServerId, myServerId, StringComparison.Ordinal);

        internal static void ResetForTest()
        {
            lock (_lock) { _seen.Clear(); _seenOrder.Clear(); _primed = false; }
        }
    }
}
