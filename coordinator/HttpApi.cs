using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
using Newtonsoft.Json;

namespace KmhDiscordCoordinator
{
    // Game servers dial in, so the management surface stays on one host instead of one per public game server.
    public sealed class HttpApi
    {
        private readonly CoordinatorConfig _cfg;
        private readonly ServerRegistry _registry;
        private readonly GatewayHost _gateway;
        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentDictionary<string, long> _nonces = new ConcurrentDictionary<string, long>();

        private const int MaxBodyBytes = 1024 * 1024;

        public HttpApi(CoordinatorConfig cfg, ServerRegistry registry, GatewayHost gateway)
        {
            _cfg = cfg;
            _registry = registry;
            _gateway = gateway;
            _listener.Prefixes.Add(cfg.Listen.EndsWith("/") ? cfg.Listen : cfg.Listen + "/");
        }

        public void Start(CancellationToken ct)
        {
            _listener.Start();
            CoordLog.Info($"listening on {_cfg.Listen}");
            for (int i = 0; i < 4; i++) _ = AcceptLoop(ct);
        }

        public void Stop()
        {
            try { _listener.Stop(); } catch (Exception) { }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Handle(ctx, ct);
            }
        }

        private async Task Handle(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "";
                switch (path)
                {
                    case "/v1/register":  await Register(ctx, ct).ConfigureAwait(false); return;
                    case "/v1/outbound":  await Outbound(ctx, ct).ConfigureAwait(false); return;
                    case "/v1/events":    await Events(ctx, ct).ConfigureAwait(false); return;
                    case "/v1/results":   await Results(ctx, ct).ConfigureAwait(false); return;
                    case "/v1/heartbeat": await Heartbeat(ctx, ct).ConfigureAwait(false); return;
                    case "/v1/health":    await Json(ctx, 200, new { ok = true, gateway = _gateway.Connected,
                                                                     servers = _registry.All().Count,
                                                                     pending = _registry.PendingCount }).ConfigureAwait(false); return;
                    default: await Json(ctx, 404, new { error = "no such endpoint" }).ConfigureAwait(false); return;
                }
            }
            catch (Exception ex)
            {
                CoordLog.Warn($"request failed: {ex.Message}");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch (Exception) { }
            }
        }

        private async Task Register(HttpListenerContext ctx, CancellationToken ct)
        {
            KmhDiscordRegisterRequest req = await ReadJson<KmhDiscordRegisterRequest>(ctx).ConfigureAwait(false);
            if (req == null) { await Json(ctx, 400, new { error = "bad body" }).ConfigureAwait(false); return; }

            if (req.Protocol != KmhDiscordProtocol.Version)
            {
                await Refuse(ctx, 400, $"protocol {req.Protocol} != {KmhDiscordProtocol.Version}").ConfigureAwait(false);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - req.IssuedAtUnix) > KmhDiscordAuth.MaxSkew.TotalSeconds)
            {
                await Refuse(ctx, 401, KmhDiscordError.Replay).ConfigureAwait(false);
                return;
            }

            SweepNonces(now);
            if (string.IsNullOrEmpty(req.Nonce) || _nonces.ContainsKey(req.Nonce))
            {
                await Refuse(ctx, 401, KmhDiscordError.Replay).ConfigureAwait(false);
                return;
            }

            CoordinatorConfig.ServerEntry entry = _registry.Entry(req.ServerId);
            if (entry == null)
            {
                CoordLog.Warn($"register refused: unknown or disabled server_id {req.ServerId}");
                await Refuse(ctx, 403, KmhDiscordError.UnknownServer).ConfigureAwait(false);
                return;
            }

            byte[] canonical = KmhDiscordAuth.Canonical(req.ServerId, req.Generation, req.InstanceId, req.Nonce, req.IssuedAtUnix);
            if (!KmhDiscordAuth.Verify(entry.PublicKey, canonical, req.Signature))
            {
                CoordLog.Warn($"register refused: bad signature for {entry.Alias}");
                await Refuse(ctx, 401, KmhDiscordError.Unauthorized).ConfigureAwait(false);
                return;
            }

            // Recorded only once signed, so an unauthenticated caller cannot fill the replay window.
            if (!_nonces.TryAdd(req.Nonce, now))
            {
                await Refuse(ctx, 401, KmhDiscordError.Replay).ConfigureAwait(false);
                return;
            }

            if (!await GlobalRevocation.AllowRegistrationAsync(req.ServerId).ConfigureAwait(false))
            {
                CoordLog.Warn($"register refused: {entry.Alias} is not current with Global");
                await Refuse(ctx, 403, KmhDiscordError.Revoked).ConfigureAwait(false);
                return;
            }

            string session = KmhDiscordAuth.NewSessionToken();
            ServerRegistry.ClaimOutcome outcome = _registry.Claim(req, entry, session, out LiveServer live, out string note);
            if (outcome == ServerRegistry.ClaimOutcome.Superseded)
            {
                CoordLog.Warn($"register refused for {entry.Alias}: {note}");
                await Refuse(ctx, 409, KmhDiscordError.Replay).ConfigureAwait(false);
                return;
            }
            if (outcome == ServerRegistry.ClaimOutcome.UnknownServer)
            {
                await Refuse(ctx, 403, KmhDiscordError.UnknownServer).ConfigureAwait(false);
                return;
            }

            if (note.Length > 0) CoordLog.Warn($"instance collision: {note}");
            CoordLog.Info($"{entry.Alias} registered ({live.DisplayName}, Colony Nexus {live.KmhVersion}, "
                        + $"{live.Capabilities.Count} capabilities)");

            KmhDiscordRegisterResponse res = new KmhDiscordRegisterResponse
            {
                Ok = true,
                SessionToken = session,
                LeaseSeconds = _cfg.LeaseSeconds,
                BotUserId = _gateway.SelfUserId,
                Alias = entry.Alias,
            };
            foreach (KeyValuePair<string, ulong> kv in live.Channels) res.Channels[kv.Key] = kv.Value;
            await Json(ctx, 200, res).ConfigureAwait(false);
        }

        private static Task Refuse(HttpListenerContext ctx, int status, string error)
            => Json(ctx, status, new KmhDiscordRegisterResponse { Ok = false, Error = error });

        private void SweepNonces(long now)
        {
            if (_nonces.Count < 4096) return;
            long cutoff = now - (long)KmhDiscordAuth.MaxSkew.TotalSeconds;
            foreach (KeyValuePair<string, long> kv in _nonces)
                if (kv.Value < cutoff) _nonces.TryRemove(kv.Key, out _);
        }

        private LiveServer Caller(HttpListenerContext ctx)
        {
            string auth = ctx.Request.Headers["Authorization"] ?? "";
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
            LiveServer s = _registry.BySession(auth.Substring(7).Trim());
            if (s == null || !s.Online(DateTimeOffset.UtcNow)) return null;
            // Every route, not only the heartbeat the addon never sends, so a revoked server loses Discord at once.
            if (!GlobalRevocation.Allowed(s.ServerId)) { _registry.Drop(s, "Global no longer lists this server"); return null; }
            return s;
        }

        private async Task Outbound(HttpListenerContext ctx, CancellationToken ct)
        {
            LiveServer caller = Caller(ctx);
            if (caller == null) { await Json(ctx, 401, new { error = KmhDiscordError.Unauthorized }).ConfigureAwait(false); return; }

            List<KmhDiscordOutbound> ops = await ReadJson<List<KmhDiscordOutbound>>(ctx).ConfigureAwait(false);
            if (ops == null) { await Json(ctx, 400, new { error = "bad body" }).ConfigureAwait(false); return; }

            List<KmhDiscordOutboundResult> results = new List<KmhDiscordOutboundResult>();
            foreach (KmhDiscordOutbound op in ops)
                results.Add(await _gateway.ApplyAsync(caller, op).ConfigureAwait(false));
            await Json(ctx, 200, results).ConfigureAwait(false);
        }

        private async Task Events(HttpListenerContext ctx, CancellationToken ct)
        {
            LiveServer caller = Caller(ctx);
            if (caller == null) { await Json(ctx, 401, new { error = KmhDiscordError.Unauthorized }).ConfigureAwait(false); return; }

            int wait = 25;
            string raw = ctx.Request.QueryString["wait"];
            if (int.TryParse(raw, out int w)) wait = Math.Max(0, Math.Min(w, 50));

            // Long-poll: one outbound connection carries both directions and nothing listens on the game host.
            DateTime until = DateTime.UtcNow.AddSeconds(wait);
            List<KmhDiscordInbound> batch = new List<KmhDiscordInbound>();
            while (true)
            {
                while (batch.Count < 64 && caller.Inbox.TryDequeue(out KmhDiscordInbound ev)) batch.Add(ev);
                if (batch.Count > 0 || DateTime.UtcNow >= until || ct.IsCancellationRequested) break;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            _registry.Renew(caller, caller.Players);
            await Json(ctx, 200, new { events = batch, dropped = Interlocked.Read(ref caller.Dropped) }).ConfigureAwait(false);
        }

        private async Task Results(HttpListenerContext ctx, CancellationToken ct)
        {
            LiveServer caller = Caller(ctx);
            if (caller == null) { await Json(ctx, 401, new { error = KmhDiscordError.Unauthorized }).ConfigureAwait(false); return; }

            List<KmhDiscordCommandResult> results = await ReadJson<List<KmhDiscordCommandResult>>(ctx).ConfigureAwait(false);
            if (results == null) { await Json(ctx, 400, new { error = "bad body" }).ConfigureAwait(false); return; }

            int matched = 0;
            foreach (KmhDiscordCommandResult r in results)
                if (r != null && !string.IsNullOrEmpty(r.RequestId) && _registry.Complete(r.RequestId, r)) matched++;
            await Json(ctx, 200, new { accepted = matched }).ConfigureAwait(false);
        }

        private async Task Heartbeat(HttpListenerContext ctx, CancellationToken ct)
        {
            LiveServer caller = Caller(ctx);
            if (caller == null) { await Json(ctx, 401, new { error = KmhDiscordError.Unauthorized }).ConfigureAwait(false); return; }
            if (!GlobalRevocation.Allowed(caller.ServerId))
            {
                _registry.Drop(caller, "Global no longer lists this server");
                await Json(ctx, 403, new { error = KmhDiscordError.Revoked }).ConfigureAwait(false);
                return;
            }

            KmhDiscordOutbound body = await ReadJson<KmhDiscordOutbound>(ctx).ConfigureAwait(false);
            _registry.Renew(caller, body?.Players ?? caller.Players);
            await Json(ctx, 200, new { ok = true, lease_seconds = _cfg.LeaseSeconds }).ConfigureAwait(false);
        }

        private static async Task<T> ReadJson<T>(HttpListenerContext ctx) where T : class
        {
            if (ctx.Request.ContentLength64 > MaxBodyBytes) return null;
            using StreamReader sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            string body = await ReadBoundedAsync(sr, MaxBodyBytes).ConfigureAwait(false);
            if (body == null) return null;
            try { return JsonConvert.DeserializeObject<T>(body); } catch (Exception) { return null; }
        }

        // A chunked body has no length up front, so the cap is applied while reading.
        internal static async Task<string> ReadBoundedAsync(TextReader reader, int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            char[] buf = new char[8192];
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                sb.Append(buf, 0, n);
                if (sb.Length > maxChars) return null;
            }
            return sb.ToString();
        }

        internal int NonceCount => _nonces.Count;

        private static async Task Json(HttpListenerContext ctx, int status, object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }
}
