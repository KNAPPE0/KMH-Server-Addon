using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Util;

namespace KMHServerAddon.Features.Global
{
    // Relays bridged Discord lines; Global charges them to the owner's account, so the owner stays answerable.
    internal static class KmhGlobalChatRelay
    {
        // Global's limiter is the authority; this only stops a Discord flood aging out the heartbeat's nonce window.
        private const int    MaxQueued = 100;
        private const int    PaceMs    = 900;
        private const int    MaxBody   = 500;

        private struct Pending { public string Name; public string Body; public string Source; }

        private static readonly object _lock = new object();
        private static readonly Queue<Pending> _queue = new Queue<Pending>();
        private static bool _draining;
        private static int  _dropped;

        public static bool Available
        {
            get
            {
                GlobalConfig cfg = GlobalConfig.Current;
                return cfg != null && cfg.Enabled
                    && GlobalSetupState.Current_.IsOwnerRegistered
                    && !string.IsNullOrEmpty(KmhServerIdentity.Id);
            }
        }

        public static void Send(string displayName, string body, string source)
        {
            if (!Available) return;
            string text = (body ?? "").Trim();
            if (text.Length == 0) return;
            text = KmhSafe.Cap(text, MaxBody);

            bool start = false;
            lock (_lock)
            {
                while (_queue.Count >= MaxQueued) { _queue.Dequeue(); _dropped++; }
                _queue.Enqueue(new Pending { Name = displayName ?? "", Body = text, Source = source });
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
                    Pending next;
                    lock (_lock)
                    {
                        if (_queue.Count == 0) { _draining = false; return; }
                        next = _queue.Dequeue();
                        if (_dropped > 0)
                        {
                            ServerLog.Warn($"Global chat relay: dropped {_dropped} queued line(s) under load.");
                            _dropped = 0;
                        }
                    }

                    await PostAsync(next).ConfigureAwait(false);
                    await Task.Delay(PaceMs).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                lock (_lock) _draining = false;
                ServerLog.Warn($"Global chat relay: drain stopped ({ex.GetType().Name}).");
            }
        }

        private static async Task PostAsync(Pending p)
        {
            try
            {
                GlobalConfig cfg = GlobalConfig.Current;
                ChatRelayBody body = new ChatRelayBody
                {
                    ServerId     = KmhServerIdentity.Id,
                    Generation   = cfg.SigningGeneration,
                    IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nonce        = Guid.NewGuid().ToString("N"),
                    DisplayName  = p.Name ?? "",
                    Body         = p.Body,
                    Source       = p.Source,
                };
                body.Signature = KmhServerKey.Sign(body.Canonical());
                if (string.IsNullOrEmpty(body.Signature)) return;

                KmhGlobalClient client = new KmhGlobalClient(cfg.BaseUrl);
                GlobalCallResult r = await client.PostAsync("/api/v1/chat/global/relay", body, null, CancellationToken.None)
                                                 .ConfigureAwait(false);
                if (r.Ok) return;

                // Never retried: a retry of a filtered or rate-limited line is the same line arriving again.
                ServerLog.Verbose($"Global chat relay: refused ({r.ErrorCode}).");
            }
            catch (Exception ex) { ServerLog.Verbose($"Global chat relay: post failed ({ex.GetType().Name})."); }
        }

        public static async Task<List<WorldChatLine>> RecentAsync(int limit, CancellationToken ct)
        {
            if (!Available) return null;
            try
            {
                GlobalConfig cfg = GlobalConfig.Current;
                ChatRelayReadBody body = new ChatRelayReadBody
                {
                    ServerId     = KmhServerIdentity.Id,
                    Generation   = cfg.SigningGeneration,
                    IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nonce        = Guid.NewGuid().ToString("N"),
                    Limit        = limit,
                };
                body.Signature = KmhServerKey.Sign(body.Canonical());
                if (string.IsNullOrEmpty(body.Signature)) return null;

                KmhGlobalClient client = new KmhGlobalClient(cfg.BaseUrl);
                GlobalCallResult r = await client.PostAsync("/api/v1/chat/global/relay/recent", body, null, ct)
                                                 .ConfigureAwait(false);
                if (!r.Ok || r.Body == null) return null;

                List<WorldChatLine> lines = new List<WorldChatLine>();
                Newtonsoft.Json.Linq.JArray rows = r.Body["messages"] as Newtonsoft.Json.Linq.JArray;
                if (rows == null) return lines;
                foreach (Newtonsoft.Json.Linq.JToken t in rows)
                {
                    Newtonsoft.Json.Linq.JObject o = t as Newtonsoft.Json.Linq.JObject;
                    if (o == null) continue;
                    lines.Add(new WorldChatLine
                    {
                        MessageId        = (string)o["message_id"] ?? "",
                        Username         = (string)o["username"] ?? "",
                        Body             = (string)o["body"] ?? "",
                        OriginServerId   = (string)o["origin_server_id"] ?? "",
                        OriginServerName = (string)o["origin_server_name"] ?? "",
                        RelaySource      = (string)o["relay_source"] ?? "",
                        SentAtUnix       = (long?)o["sent_at"] ?? 0,
                    });
                }
                return lines;
            }
            catch (Exception ex)
            {
                ServerLog.Verbose($"Global chat relay: read failed ({ex.GetType().Name}).");
                return null;
            }
        }

        internal static void ClearForTest() { lock (_lock) { _queue.Clear(); _dropped = 0; _draining = false; } }
    }

    internal sealed class WorldChatLine
    {
        public string MessageId        = "";
        public string Username         = "";
        public string Body             = "";
        public string OriginServerId   = "";
        public string OriginServerName = "";
        public string RelaySource      = "";
        public long   SentAtUnix;
    }
}
