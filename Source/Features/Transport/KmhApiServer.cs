using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Transport
{
    // Server-side KMH API transport: optional TCP listener using handshake tokens and framed KMH envelopes. Secure by
    // default (loopback bind, auth, caps, throttle); all limits come from TransportConfig.
    internal static class KmhApiServer
    {
        // transport-internal kinds (not in KmhProtocol.Kind)
        private const string KindApiHello    = "kmh.api.hello";       // client -> server { v, token }
        private const string KindApiHelloAck = "kmh.api.hello.ack";   // server -> client { ok, reason? }
        private const string KindPing        = "kmh.ping";
        private const string KindPong        = "kmh.pong";

        private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(10);

        // Limits snapshotted from TransportConfig at Start (reload = restart). Defaults match the config defaults.
        private static int _maxFrameBytes  = 64 * 1024;
        private static int _authTimeoutMs  = 10_000;
        private static int _idleTimeoutMs  = 45_000;

        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static volatile bool _running;

        // token -> (username, expiryUtcTicks). Minted at chat handshake, consumed at API hello.
        private static readonly ConcurrentDictionary<string, (string user, long exp)> _tokens
            = new ConcurrentDictionary<string, (string, long)>();
        // username -> live authed connection
        private static readonly ConcurrentDictionary<string, ApiConn> _conns
            = new ConcurrentDictionary<string, ApiConn>(StringComparer.OrdinalIgnoreCase);

        // DoS guards, rebuilt from config at Start (see TransportGuards.cs).
        private static ConnectionLimiter _limiter = new ConnectionLimiter(200, 6);
        private static AuthFailThrottle  _throttle = new AuthFailThrottle(10, 60, 300);

        private static int _failBlockSeconds = 300;   // for the throttle log line

        public static bool Running => _running;
        public static int  ConnectedCount => _conns.Count;

        public static void Start()
        {
            if (_running) return;
            TransportConfig cfg = TransportConfig.Current;
            if (!cfg.EnableKmhApiTransport)
            {
                ServerLog.Info("KMH API transport: disabled (Config/Transport.json EnableKmhApiTransport=false) - using RWT chat.");
                return;
            }
            try
            {
                _maxFrameBytes  = cfg.MaxFrameKb * 1024;
                _authTimeoutMs  = cfg.AuthTimeoutSeconds * 1000;
                _idleTimeoutMs  = cfg.IdleTimeoutSeconds * 1000;
                _failBlockSeconds = cfg.FailedAuthBlockSeconds;
                _limiter  = new ConnectionLimiter(cfg.MaxConnections, cfg.MaxConnectionsPerIp);
                _throttle = new AuthFailThrottle(cfg.FailedAuthPerIp, cfg.FailedAuthWindowSeconds, cfg.FailedAuthBlockSeconds);

                IPAddress bind = IPAddress.TryParse(cfg.BindAddress, out IPAddress a) ? a : IPAddress.Loopback; // bad value -> safe
                _listener = new TcpListener(bind, cfg.KmhApiPort);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _running = true;
                Task.Run(() => AcceptLoop(_cts.Token));
                ServerLog.Success($"KMH API transport: listening on {bind}:{cfg.KmhApiPort} (auth {(cfg.RequireKmhApiAuth ? "required" : "OFF")}).");
                WarnOnUnsafeConfig(cfg);
            }
            catch (Exception ex)
            {
                _running = false;
                ServerLog.Error($"KMH API transport: could not bind {cfg.BindAddress}:{cfg.KmhApiPort} - staying on chat transport. ({ex.Message})");
            }
        }

        // Boot validation: shout about a config that exposes the port or disables auth, so owners can't drift into it unknowingly.
        private static void WarnOnUnsafeConfig(TransportConfig cfg)
        {
            if (cfg.IsPublicBind && !cfg.RequireKmhApiAuth)
            {
                ServerLog.Warn("=== KMH API SECURITY WARNING: PUBLIC BIND + AUTH OFF ===");
                ServerLog.Warn($"The KMH API is reachable from other machines ({cfg.BindAddress}:{cfg.KmhApiPort}) AND auth is OFF -");
                ServerLog.Warn("anyone who can reach the port can act as any player. Set RequireKmhApiAuth=true or BindAddress=127.0.0.1.");
            }
            else if (!cfg.RequireKmhApiAuth)
            {
                ServerLog.Warn("KMH API: auth is OFF (testing only) - any client can claim any player. Set RequireKmhApiAuth=true.");
            }
            else if (cfg.IsPublicBind)
            {
                ServerLog.Warn($"KMH API: public bind ({cfg.BindAddress}) - reachable by remote players. If unintended set BindAddress=127.0.0.1; only forward port {cfg.KmhApiPort} if you want remote KMH.");
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            foreach (ApiConn c in _conns.Values) c.Close();
            _conns.Clear();
        }

        // Mint a one-time token for a username during the chat handshake. Returns "" if the API transport is off.
        public static string IssueToken(string username)
        {
            if (!_running || string.IsNullOrEmpty(username)) return "";
            ReapTokens();
            byte[] raw = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string token = Convert.ToBase64String(raw);
            _tokens[token] = (username, (DateTime.UtcNow + TokenTtl).Ticks);
            return token;
        }

        public static bool IsAuthed(string username)
            => !string.IsNullOrEmpty(username) && _conns.ContainsKey(username);

        // send to a username's API socket if connected; false -> not on the API, caller falls back to chat
        public static bool TrySend(string username, KmhEnvelope env)
        {
            if (!_running || string.IsNullOrEmpty(username) || env == null) return false;
            return _conns.TryGetValue(username, out ApiConn c) && c.TrySend(env);
        }

        private static async Task AcceptLoop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { if (!_running) break; try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; } continue; }
                string ip = (client.Client?.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?";
                if (!Admit(ip)) { try { client.Close(); } catch { } ServerLog.Protocol($"API: refused {ip} (cap or auth block)."); continue; }
                _ = Task.Run(() => HandleClient(client, ip, ct));
            }
        }

        // Admit a socket only when it's not auth-blocked and under the global + per-IP caps; HandleClient must Release().
        private static bool Admit(string ip)
        {
            if (_throttle.IsBlocked(ip)) return false;
            return _limiter.TryAdmit(ip);
        }

        private static void Release(string ip) => _limiter.Release(ip);

        // Record an auth failure; the throttle trips a temporary block once an IP exceeds the configured rate in-window.
        private static void NoteAuthFail(string ip)
        {
            if (_throttle.NoteFailure(ip, out int total))
            {
                ServerLog.Warn($"KMH API: an IP tripped the failed-auth throttle - blocked for {_failBlockSeconds}s (enable transport debug to see which).");
                ServerLog.Protocol($"KMH API: blocked {ip} for {_failBlockSeconds}s after {total} failed auths.");
            }
        }

        private static async Task HandleClient(TcpClient client, string ip, CancellationToken ct)
        {
            ApiConn conn = null;
            string user = null;
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                // first frame must be an authenticated hello, bounded (async reads ignore NetworkStream.ReadTimeout)
                KmhEnvelope hello = await ReadFrameWithin(stream, _authTimeoutMs, ct).ConfigureAwait(false);
                if (hello == null || hello.Kind != KindApiHello)
                {
                    ServerLog.Protocol("API: first frame was not kmh.api.hello - dropping.");
                    return;
                }

                TransportConfig cfg = TransportConfig.Current;
                if (cfg.RequireKmhApiAuth)
                {
                    string token   = hello.GetString("token") ?? "";
                    string claimed = hello.GetString("username") ?? "";
                    // Prefer the one-time token; with none (RWT chat unavailable) bind to the live verified RWT session
                    // for the same username AND source IP. Either way an external socket can't claim a player.
                    if (!string.IsNullOrEmpty(token) && TryConsumeToken(token, out user)) { /* authed by token */ }
                    else if (TryAuthBySession(claimed, client, out user))                  { /* authed by RWT session+IP */ }
                    else
                    {
                        await WriteFrame(stream, KindApiHelloAck, new { ok = false, reason = "auth" }, ct).ConfigureAwait(false);
                        ServerLog.Protocol($"API: hello rejected for '{claimed}' (no valid token, no matching RWT session/IP).");
                        NoteAuthFail(ip);
                        return;
                    }
                }
                else
                {
                    user = hello.GetString("username") ?? "";  // unauth mode (testing only)
                    if (string.IsNullOrEmpty(user)) { await WriteFrame(stream, KindApiHelloAck, new { ok = false, reason = "no_user" }, ct).ConfigureAwait(false); return; }
                }

                // ack before registering, so no other thread writes this socket mid-handshake
                await WriteFrame(stream, KindApiHelloAck, new { ok = true, v = KmhProtocol.CurrentVersion, build = KmhProtocol.BuildVersion, disabled = string.Join(",", FeaturesConfig.Current.DisabledList()) }, ct).ConfigureAwait(false);
                conn = new ApiConn(client, stream, user);
                if (_conns.TryGetValue(user, out ApiConn prev)) prev.Close();   // one live socket per user
                _conns[user] = conn;
                _throttle.Clear(ip);   // a clean auth clears this IP's failure streak
                ServerLog.Success($"API: {user} connected over the KMH transport.");

                // steady state: heartbeat + feature traffic; drop after the idle timeout of silence
                while (_running && !ct.IsCancellationRequested)
                {
                    KmhEnvelope env = await ReadFrameWithin(stream, _idleTimeoutMs, ct).ConfigureAwait(false);
                    if (env == null) break; // closed or idle
                    if (env.Kind == KindPing) { conn.TrySend(new KmhEnvelope(KindPong, null)); continue; }
                    // feature envelope -> shared router, using the authenticated identity (never the envelope)
                    var sc = KmhRouter.ResolveClient(user);
                    if (sc != null) { ServerLog.Protocol($"API <= {user}: {env.Kind}"); KmhRouter.HandleInbound(sc, env); }
                    else ServerLog.Protocol($"API: dropping '{env.Kind}' from {user} - no live ServerClient.");
                }
            }
            catch (Exception ex) { ServerLog.Protocol($"API: connection for {user ?? "?"} ended: {ex.Message}"); }
            finally
            {
                if (user != null && conn != null && _conns.TryGetValue(user, out ApiConn cur) && cur == conn)
                    _conns.TryRemove(user, out _);
                conn?.Close();
                try { client.Close(); } catch { }
                Release(ip);
            }
        }

        private static bool TryConsumeToken(string token, out string username)
        {
            username = null;
            if (string.IsNullOrEmpty(token)) return false;
            if (!_tokens.TryRemove(token, out (string user, long exp) v)) return false;
            if (DateTime.UtcNow.Ticks > v.exp) return false;
            username = v.user;
            return true;
        }

        // Tokenless auth (used when RWT chat - the token's channel - is down): bind to a live verified RWT session for
        // the same username whose IP matches the API socket. No session or IP mismatch -> refused.
        private static bool TryAuthBySession(string username, TcpClient apiClient, out string user)
        {
            user = null;
            if (string.IsNullOrEmpty(username)) return false;
            var sc = KmhRouter.ResolveClient(username);   // verified + online, or null
            if (sc == null) return false;
            string apiIp = (apiClient.Client?.RemoteEndPoint as IPEndPoint)?.Address?.ToString();
            if (!IpMatches(apiIp, sc.IP))
            {
                ServerLog.Protocol($"API: session-auth IP mismatch for {username} (api {apiIp ?? "?"} vs session {sc.IP ?? "?"}).");
                return false;
            }
            user = username;
            return true;
        }

        // Address compare tolerant of IPv4-mapped-IPv6, loopback (::1 vs 127.0.0.1), and a trailing :port.
        private static bool IpMatches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (IPAddress.TryParse(Host(a), out IPAddress ia) && IPAddress.TryParse(Host(b), out IPAddress ib))
            {
                if (ia.IsIPv4MappedToIPv6) ia = ia.MapToIPv4();
                if (ib.IsIPv4MappedToIPv6) ib = ib.MapToIPv4();
                return ia.Equals(ib) || (IPAddress.IsLoopback(ia) && IPAddress.IsLoopback(ib));
            }
            return string.Equals(Host(a), Host(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Host(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int last = s.LastIndexOf(':');
            return (last > 0 && s.IndexOf(':') == last) ? s.Substring(0, last) : s;   // strip IPv4:port; leave IPv6 intact
        }

        private static void ReapTokens()
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (var kv in _tokens)
                if (now > kv.Value.exp) _tokens.TryRemove(kv.Key, out _);
        }

        // --- framing ---

        private static async Task<KmhEnvelope> ReadFrame(NetworkStream stream, CancellationToken ct)
        {
            byte[] lenBuf = await ReadExactly(stream, 4, ct).ConfigureAwait(false);
            if (lenBuf == null) return null;
            int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (len <= 0 || len > _maxFrameBytes) return null;
            byte[] body = await ReadExactly(stream, len, ct).ConfigureAwait(false);
            if (body == null) return null;
            return KmhEnvelope.TryParse(Encoding.UTF8.GetString(body));
        }

        // ReadFrame but give up after timeoutMs of silence (async reads ignore NetworkStream.ReadTimeout)
        private static async Task<KmhEnvelope> ReadFrameWithin(NetworkStream stream, int timeoutMs, CancellationToken ct)
        {
            Task<KmhEnvelope> read = ReadFrame(stream, ct);
            if (await Task.WhenAny(read, Task.Delay(timeoutMs, ct)).ConfigureAwait(false) != read) return null;
            return await read.ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactly(NetworkStream stream, int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n;
                try { n = await stream.ReadAsync(buf, read, count - read, ct).ConfigureAwait(false); }
                catch { return null; } // closed/disposed (incl. an abandoned read after an idle timeout)
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }

        // handshake ack only - single writer at that point, so async is fine
        private static async Task WriteFrame(NetworkStream stream, string kind, object data, CancellationToken ct)
        {
            byte[] frame = FrameOf(Encoding.UTF8.GetBytes(new KmhEnvelope(kind, data).Serialize()));
            await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
        }

        private static byte[] FrameOf(byte[] body)
        {
            byte[] frame = new byte[4 + body.Length];
            frame[0] = (byte)(body.Length >> 24); frame[1] = (byte)(body.Length >> 16);
            frame[2] = (byte)(body.Length >> 8);  frame[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        private sealed class ApiConn
        {
            private readonly TcpClient _client;
            private readonly object    _sendLock = new object();
            public readonly NetworkStream Stream;
            public readonly string Username;
            public ApiConn(TcpClient c, NetworkStream s, string user) { _client = c; Stream = s; Username = user; }

            // serialized sync write; full-duplex with the receive task's read
            public bool TrySend(KmhEnvelope env)
            {
                try
                {
                    byte[] body = Encoding.UTF8.GetBytes(env.Serialize());
                    if (body.Length > _maxFrameBytes) return false;
                    byte[] frame = FrameOf(body);
                    lock (_sendLock) { Stream.Write(frame, 0, frame.Length); }
                    return true;
                }
                catch { return false; }
            }

            public void Close() { try { Stream?.Close(); } catch { } try { _client?.Close(); } catch { } }
        }
    }
}
