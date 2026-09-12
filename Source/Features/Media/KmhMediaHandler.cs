using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhMediaHandler
    {
        // 24 KB of raw becomes 32 KB of base64, inside the transport's 64 KB frame even after the envelope JSON.
        internal const int ChunkBytes = 24 * 1024;

        private static readonly object _rateLock = new object();
        private static readonly Dictionary<string, List<long>> _resolves = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatMediaRequest, OnRequest);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;

            string id = (env?.GetString("id") ?? "").Trim();
            if (id.Length == 0 || id.Length > 64) { Fail(client, id, "bad request"); return; }

            MediaConfig cfg = MediaConfig.Current;
            if (cfg == null || !cfg.ServerMediaResolverEnabled) { Fail(client, id, "the server owner has turned off media resolving"); return; }

            KmhMediaCache.Entry cached = KmhMediaCache.Get(id);
            if (cached != null) { Send(client, cached); return; }

            string url = KmhMediaCache.SourceUrlFor(id);
            if (string.IsNullOrEmpty(url)) { Fail(client, id, "expired"); return; }

            if (!AllowResolve(user, cfg)) { Fail(client, id, "too many media requests - try again shortly"); return; }

            // Off the network thread: this fetches, decodes and re-encodes, none of which belongs in a packet handler.
            Task<KmhMediaCache.Entry> resolve = ResolveSharedAsync(id, url, cfg);
            _ = Task.Run(async () =>
            {
                try
                {
                    KmhMediaCache.Entry made = await resolve.ConfigureAwait(false);
                    if (made == null) Fail(client, id, ErrorFor(id));
                    else Send(client, made);
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Media resolve threw for {id}: {ex.Message}");
                    Fail(client, id, "could not prepare that media");
                }
            });
        }

        private static readonly object _inFlightLock = new object();
        private static readonly Dictionary<string, Task<KmhMediaCache.Entry>> _inFlight =
            new Dictionary<string, Task<KmhMediaCache.Entry>>(StringComparer.Ordinal);

        internal static int InFlightCount { get { lock (_inFlightLock) return _inFlight.Count; } }

        // One popular gif and twenty players loading it is ONE fetch and one transcode; the rest wait on the same task.
        private static Task<KmhMediaCache.Entry> ResolveSharedAsync(string id, string url, MediaConfig cfg)
        {
            lock (_inFlightLock)
            {
                if (_inFlight.TryGetValue(id, out Task<KmhMediaCache.Entry> running)) return running;

                Task<KmhMediaCache.Entry> started = Task.Run(async () =>
                {
                    try { return await ResolveAsync(id, url, cfg).ConfigureAwait(false); }
                    finally { lock (_inFlightLock) _inFlight.Remove(id); }
                });
                _inFlight[id] = started;
                return started;
            }
        }

        private static readonly Dictionary<string, string> _lastError = new Dictionary<string, string>(StringComparer.Ordinal);

        // Read under the same lock that writes it: resolves finish on pool threads, and an unlocked read can catch a resize.
        private static string ErrorFor(string id)
        {
            lock (_lastError) return _lastError.TryGetValue(id ?? "", out string e) ? e : "could not prepare that media";
        }

        internal static async Task<KmhMediaCache.Entry> ResolveAsync(string id, string url, MediaConfig cfg)
        {
            ServerLog.Debug($"Media resolve {id}: fetching {url}");

            KmhMediaFetch.Result fetched = await KmhMediaFetch.GetAsync(url, cfg, CancellationToken.None)
                                                             .ConfigureAwait(false);
            if (!fetched.Ok)
            {
                Remember(id, fetched.Error);
                ServerLog.Debug($"Media resolve {id}: fetch failed - {fetched.Error}");
                return null;
            }
            ServerLog.Debug($"Media resolve {id}: {fetched.Bytes.Length} bytes, type={fetched.ContentType}, hops={fetched.Hops}");

            KmhMediaTranscode.Result converted = KmhMediaTranscode.Convert(fetched.Bytes, cfg);
            if (!converted.Ok)
            {
                Remember(id, converted.Error);
                ServerLog.Debug($"Media resolve {id}: convert failed - {converted.Error}");
                return null;
            }
            ServerLog.Debug($"Media resolve {id}: -> {converted.Mime} {converted.Width}x{converted.Height} "
                          + $"{converted.Frames} frame(s) {converted.Bytes.Length} bytes"
                          + (converted.PassedThrough ? " (passed through unchanged)" : ""));

            KmhMediaCache.Entry stored = KmhMediaCache.Store(id, url, converted, cfg);
            if (stored == null) Remember(id, "could not cache the converted media");
            return stored;
        }

        private static void Remember(string id, string error)
        {
            lock (_lastError) { _lastError[id] = error ?? "failed"; if (_lastError.Count > 500) _lastError.Clear(); }
        }

        private static void Send(ServerClient client, KmhMediaCache.Entry e)
        {
            byte[] bytes = KmhMediaCache.Read(e);
            if (bytes == null) { Fail(client, e.Id, "expired"); return; }

            int chunks = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ChatMediaMeta, new
            {
                id = e.Id, ok = true, mime = e.Mime, bytes = bytes.Length, chunks,
                hash = e.ContentHash, w = e.Width, h = e.Height, frames = e.Frames, animated = e.Animated,
            });

            for (int i = 0; i < chunks; i++)
            {
                int offset = i * ChunkBytes;
                int len = Math.Min(ChunkBytes, bytes.Length - offset);
                // One refused chunk means the peer is gone; the rest are failed writes carrying half a picture.
                if (!KmhRouter.SendTo(client, KmhProtocol.Kind.ChatMediaChunk, new
                {
                    id = e.Id, i, n = chunks, b64 = Convert.ToBase64String(bytes, offset, len),
                }))
                {
                    ServerLog.Verbose($"Media: send of '{e.Id}' abandoned after {i}/{chunks} chunk(s) - the recipient is gone.");
                    return;
                }
            }
        }

        private static void Fail(ServerClient client, string id, string reason)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.ChatMediaMeta, new { id, ok = false, reason });

        // A resolve is an outbound fetch made on a client's word, so this is what has to be rate limited.
        private static bool AllowResolve(string user, MediaConfig cfg)
        {
            long now = DateTime.UtcNow.Ticks;
            long window = TimeSpan.FromSeconds(cfg.ResolveWindowSeconds).Ticks;
            lock (_rateLock)
            {
                if (!_resolves.TryGetValue(user, out List<long> stamps)) _resolves[user] = stamps = new List<long>();
                stamps.RemoveAll(t => now - t > window);
                if (stamps.Count >= cfg.MaxResolvesPerWindow) return false;
                stamps.Add(now);
                return true;
            }
        }
    }
}
