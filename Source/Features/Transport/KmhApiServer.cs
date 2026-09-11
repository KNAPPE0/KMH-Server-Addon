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
    internal static class KmhApiServer
    {
        // Transport-internal, so these are deliberately absent from KmhProtocol.Kind.
        private const string KindApiHello    = "kmh.api.hello";       // client -> server { v, token }
        private const string KindApiHelloAck = "kmh.api.hello.ack";   // server -> client { ok, reason? }
        private const string KindPing        = "kmh.ping";
        private const string KindPong        = "kmh.pong";

        private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(10);

        // Snapshotted at Start, which is why this config area needs a restart rather than a reload.
        private static int _maxFrameBytes  = 64 * 1024;
        private static int _authTimeoutMs  = 10_000;
        private static int _idleTimeoutMs  = 45_000;

        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static volatile bool _running;

        // The owning session is part of the credential: a token outliving its session would let a later connection inherit its authority.
        private struct TokenRecord { public string User; public ServerClient Owner; public long Exp; }
        private static readonly ConcurrentDictionary<string, TokenRecord> _tokens
            = new ConcurrentDictionary<string, TokenRecord>();
        // Indexed by username for lookup only; ApiConn.Owner is the authority, and every accessor checks it.
        private static readonly ConcurrentDictionary<string, ApiConn> _conns
            = new ConcurrentDictionary<string, ApiConn>(StringComparer.OrdinalIgnoreCase);

        private static ConnectionLimiter _limiter = new ConnectionLimiter(200, 6);
        private static AuthFailThrottle  _throttle = new AuthFailThrottle(10, 60, 300);

        private static int _failBlockSeconds = 300;   // for the throttle log line

        public static bool Running => _running;

        private static bool RequireAuth => TransportConfig.Current.RequireKmhApiAuth;

        // The one port KMH asks an owner to open. The video relay shares it by default rather than needing a second.
        private static int _boundPort;
        public static int Port => _running ? _boundPort : 0;
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
                // Asked of the socket, not the config: port 0 means "any free one", and clients are told this number.
                _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                _running = true;
                Task.Run(() => AcceptLoop(_cts.Token));
                ServerLog.Success($"KMH API transport: listening on {bind}:{_boundPort} (auth {(cfg.RequireKmhApiAuth ? "required" : "OFF")}).");
                // Anyone who handshook before this point was told the transport was off, and would stay on chat all session.
                Comms.CommsStartup.PushHello();
                WarnOnUnsafeConfig(cfg);
            }
            catch (Exception ex)
            {
                _running = false;
                ServerLog.Error($"KMH API transport: could not bind {cfg.BindAddress}:{cfg.KmhApiPort} - staying on chat transport. ({ex.Message})");
            }
        }

        // Said out loud at boot, so an owner cannot drift into an exposed port or disabled auth without noticing.
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
                // Info rather than a warning: a public bind is the default posture and auth is still required.
                ServerLog.Info($"KMH API: public bind ({cfg.BindAddress}) with auth required - forward TCP {cfg.KmhApiPort} for remote KMH, or set BindAddress=127.0.0.1 for local-only.");
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            foreach (ApiConn c in _conns.Values) c.Close();
            _conns.Clear();
            _tokens.Clear();   // a credential must not survive the listener that would have honoured it
        }

        public static string IssueToken(ServerClient owner)
        {
            string username = owner?.GetData<UserFile>()?.Username;
            if (!_running || string.IsNullOrEmpty(username)) return "";
            ReapTokens();
            // A token is consumed on use, so an outstanding one means the client never arrived; reissuing it keeps a session to a single live credential.
            foreach (var kv in _tokens)
                if (ReferenceEquals(kv.Value.Owner, owner)) return kv.Key;
            byte[] raw = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string token = Convert.ToBase64String(raw);
            _tokens[token] = new TokenRecord { User = username, Owner = owner, Exp = (DateTime.UtcNow + TokenTtl).Ticks };
            return token;
        }

        // Matched on the exact session, never the username: a reconnect can already own the registry entry by the time this runs.
        public static void CloseForSession(ServerClient exact)
        {
            if (exact == null) return;
            RevokeTokensFor(exact);
            string user = exact.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            if (!_conns.TryGetValue(user, out ApiConn c) || !ReferenceEquals(c.Owner, exact)) return;
            _conns.TryRemove(new System.Collections.Generic.KeyValuePair<string, ApiConn>(user, c));
            c.Close();
            ServerLog.Protocol($"API: closed {user}'s connection with the RWT session that owned it.");
        }

        private static void RevokeTokensFor(ServerClient owner)
        {
            foreach (var kv in _tokens)
                if (ReferenceEquals(kv.Value.Owner, owner)) _tokens.TryRemove(kv.Key, out _);
        }

        // The registered connection for this exact session, or null - including when the username matches a newer one.
        private static ApiConn ConnFor(ServerClient client)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return null;
            return _conns.TryGetValue(user, out ApiConn c) && ReferenceEquals(c.Owner, client) ? c : null;
        }

        // "Not on the API" and "too big" need separate answers, or an oversized payload falls back to chat and fails there too.
        public enum SendResult { Sent, NotConnected, TooLarge, IoFailure, SerializationFailure }

        // Addressed by session, not by name, so a push never rides the socket a previous session left behind.
        public static SendResult Send(ServerClient client, KmhEnvelope env)
        {
            if (!_running || env == null) return SendResult.NotConnected;
            ApiConn c = ConnFor(client);
            return c != null ? c.Send(env) : SendResult.NotConnected;
        }

        // The negotiated frame budget for one peer, so fragments are cut to what that client can actually read.
        public static int SafeFrameBytesFor(ServerClient client) => ConnFor(client)?.FrameLimitBytes ?? _maxFrameBytes;

        public static bool PeerSupportsFragments(ServerClient client)
        {
            ApiConn c = ConnFor(client);
            return c == null || c.PeerFragments;
        }

        // Every peer on one frame budget gets the same cut, so a broadcast encodes the parts once and reuses them.
        public static System.Collections.Generic.List<Framed> EncodeAll(System.Collections.Generic.List<KmhEnvelope> parts)
        {
            if (parts == null || parts.Count == 0) return null;
            var outp = new System.Collections.Generic.List<Framed>(parts.Count);
            foreach (KmhEnvelope p in parts)
            {
                Framed f = Encode(p);
                if (f == null) return null;
                outp.Add(f);
            }
            return outp;
        }

        // Sent through the one connection resolved up front, or a mid-transfer reconnect splits the parts across two sockets and neither side holds a whole envelope.
        public static SendResult SendFragments(ServerClient client, System.Collections.Generic.List<Framed> parts)
        {
            if (parts == null || parts.Count == 0) return SendResult.SerializationFailure;
            ApiConn c = ConnFor(client);
            if (c == null) return SendResult.NotConnected;
            for (int i = 0; i < parts.Count; i++)
            {
                SendResult r = c.Send(parts[i]);
                if (r == SendResult.Sent) continue;
                // Earlier parts already on the wire cannot be recalled, so a late failure is ambiguous whatever caused it.
                return i == 0 ? r : SendResult.IoFailure;
            }
            return SendResult.Sent;
        }

        private static async Task AcceptLoop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { if (!_running) break; try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; } continue; }
                string ip = (client.Client?.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?";
                if (!Admit(ip, out string refusal)) { try { client.Close(); } catch { } ServerLog.Protocol($"API: refused {ip} - {refusal}."); continue; }
                _ = Task.Run(() => HandleClient(client, ip, ct));
            }
        }

        // Admit a socket only when it's not auth-blocked and under the global + per-IP caps; HandleClient must Release().
        private static bool Admit(string ip, out string why)
        {
            if (_throttle.IsBlocked(ip)) { why = "auth block"; return false; }
            if (_limiter.TryAdmit(ip)) { why = null; return true; }
            // Named, because "refused" alone sends an owner hunting the wrong setting.
            why = _limiter.PerIp(ip) >= TransportConfig.Current.MaxConnectionsPerIp
                ? $"per-IP cap ({TransportConfig.Current.MaxConnectionsPerIp}) - raise MaxConnectionsPerIp if these players share one address"
                : $"server cap ({TransportConfig.Current.MaxConnections})";
            return false;
        }

        // Players behind one address are indistinguishable to every per-IP guard, so one can exhaust the caps for all; reported once per address.
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _usersByIp
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        private static readonly ConcurrentDictionary<string, byte> _sharedIpReported
            = new ConcurrentDictionary<string, byte>();

        private static void NoteSharedIngress(string ip, string user)
        {
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user)) return;
            var users = _usersByIp.GetOrAdd(ip, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            users[user] = 0;
            if (users.Count < 2 || !_sharedIpReported.TryAdd(ip, 0)) return;
            TransportConfig cfg = TransportConfig.Current;
            ServerLog.Info($"KMH API: {users.Count} players reach this server from one address - they share this server's "
                         + $"per-IP limits (MaxConnectionsPerIp={cfg.MaxConnectionsPerIp}, "
                         + $"FailedAuthPerIp={cfg.FailedAuthPerIp}/{cfg.FailedAuthWindowSeconds}s). If more than "
                         + $"{cfg.MaxConnectionsPerIp} of them play at once, raise MaxConnectionsPerIp in Config/Transport.json.");
        }

        private static void Release(string ip) => _limiter.Release(ip);

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
            ServerClient owner = null;
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                // Lets video share this port instead of asking an owner to open a second one.
                byte[] peek = await PeekAsync(stream, 4, _authTimeoutMs, ct).ConfigureAwait(false);
                if (peek == null) return;
                if (LooksLikeHttp(peek))
                {
                    if (Features.Media.KmhVideoRelay.SharesApiPort)
                    {
                        // Admitted to the relay's limiter before this one is released, so no socket is held by neither.
                        if (!Features.Media.KmhVideoRelay.TryServeAccepted(client, peek, ip))
                        {
                            ServerLog.Protocol($"API: video relay at capacity - refused an HTTP connection from {ip}.");
                            return;
                        }
                        Release(ip);
                        client = null;
                        return;
                    }
                    ServerLog.Protocol($"API: HTTP request from {ip} but the video relay is not sharing this port - dropping.");
                    return;
                }

                // first frame must be an authenticated hello, bounded (async reads ignore NetworkStream.ReadTimeout)
                KmhEnvelope hello = await ReadFrameWithin(stream, peek, _authTimeoutMs, ct).ConfigureAwait(false);
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
                    // Both paths end on a live RWT session, so a socket can neither claim a player nor outlive the session whose authority it borrowed.
                    if (!string.IsNullOrEmpty(token) && TryConsumeToken(token, out user, out owner)) { /* authed by token */ }
                    else if (TryAuthBySession(claimed, client, out user, out owner))                { /* authed by RWT session+IP */ }
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
                    // Still session-bound: a connection with nothing to route to would answer heartbeats while dropping every request.
                    owner = KmhRouter.ResolveClient(user);
                }
                if (owner == null)
                {
                    await WriteFrame(stream, KindApiHelloAck, new { ok = false, reason = "no_session" }, ct).ConfigureAwait(false);
                    ServerLog.Protocol($"API: hello rejected for '{user}' - no live RWT session to own this connection.");
                    return;
                }

                // Rejected here so an incompatible client never reaches the feature router at all.
                int helloV = hello.GetInt("v", 0);
                if (helloV != KmhProtocol.CurrentVersion)
                {
                    await WriteFrame(stream, KindApiHelloAck, new { ok = false, reason = "version", v = KmhProtocol.CurrentVersion }, ct).ConfigureAwait(false);
                    ServerLog.Protocol($"API: hello rejected for '{user}' - client v{helloV} != server v{KmhProtocol.CurrentVersion}.");
                    return;
                }

                // The smaller end wins, or raising MaxFrameKb server-side alone would kill an older client's receive loop.
                int clientFrameKb = hello.GetInt("max_frame_kb", 0);
                int effectiveKb = clientFrameKb <= 0 ? 64 : Math.Min(clientFrameKb, _maxFrameBytes / 1024);
                bool clientFragments = hello.GetBool("supports_fragmentation", false);

                // ack before registering, so no other thread writes this socket mid-handshake
                await WriteFrame(stream, KindApiHelloAck, new
                {
                    ok = true, v = KmhProtocol.CurrentVersion, build = KmhProtocol.BuildVersion,
                    disabled = string.Join(",", FeaturesConfig.Current.DisabledList()),
                    capabilities = KmhCapabilities.Manifest,
                    max_frame_kb = effectiveKb, supports_fragmentation = true,
                    // The session's next one-use token, so a reconnect authenticates as itself rather than by a source-IP match a proxy or VPN breaks.
                    token = RequireAuth ? IssueToken(owner) : "",
                }, ct).ConfigureAwait(false);
                conn = new ApiConn(client, stream, user, owner) { FrameLimitBytes = effectiveKb * 1024, PeerFragments = clientFragments };
                if (!clientFragments)
                    ServerLog.Info($"KMH API: '{user}' is an older client without fragmentation - anything past {effectiveKb} KB cannot reach it.");
                if (_conns.TryGetValue(user, out ApiConn prev)) prev.Close();   // one live socket per user
                _conns[user] = conn;
                _throttle.Clear(ip);   // a clean auth clears this IP's failure streak
                NoteSharedIngress(ip, user);
                ServerLog.Success($"API: {user} connected over the KMH transport.");

                // steady state: heartbeat + feature traffic; drop after the idle timeout of silence
                while (_running && !ct.IsCancellationRequested)
                {
                    KmhEnvelope env = await ReadFrameWithin(stream, _idleTimeoutMs, ct).ConfigureAwait(false);
                    if (env == null) break; // closed or idle

                    // Checked before the heartbeat is answered: a socket whose RWT session has gone must not read as healthy while its requests are dropped.
                    if (!KmhRouter.IsLive(owner))
                    {
                        ServerLog.Protocol($"API: {user}'s RWT session has ended - closing the API connection that belonged to it.");
                        break;
                    }
                    if (env.Kind == KindPing)
                    {
                        // Capped separately: ping is exempt from the inbound window, so without this a modified client turns ping/pong into an uncapped reply channel.
                        if (AllowHeartbeat(user)) conn.TrySend(new KmhEnvelope(KindPong, null));
                        continue;
                    }
                    // Routed under the session this socket authenticated against, never anything the envelope claims - the API hello already authed and version-checked.
                    SubProtocol.KmhHandshakeHandler.MarkCompatible(owner);
                    // Guarded so the interpolated string is not built for every inbound packet.
                    if (ServerLog.DebugEnabled) ServerLog.Protocol($"API <= {user}: {env.Kind}");
                    KmhRouter.HandleInbound(owner, env);
                }
            }
            catch (Exception ex) { ServerLog.Protocol($"API: connection for {user ?? "?"} ended: {ex.Message}"); }
            finally
            {
                if (user != null && conn != null)
                    _conns.TryRemove(new System.Collections.Generic.KeyValuePair<string, ApiConn>(user, conn));
                conn?.Close();
                // null when the socket was handed to the video relay, which owns closing it from there.
                if (client != null) { try { client.Close(); } catch { } Release(ip); }
            }
        }

        // Test seams: a live handshake never produces two connections under one username, so the registry must be reachable to prove ownership holds.
        internal static void RegisterForTest(string user, ServerClient owner) => _conns[user] = new ApiConn(null, null, user, owner);
        internal static bool IsRegisteredFor(ServerClient owner) => ConnFor(owner) != null;
        internal static bool TryConsumeTokenForTest(string token, out string user, out ServerClient owner) => TryConsumeToken(token, out user, out owner);
        internal static int  OutstandingTokensForTest => _tokens.Count;
        internal static void SetRunningForTest(bool running) => _running = running;
        internal static void ResetForTest() { _conns.Clear(); _tokens.Clear(); }

        private static bool TryConsumeToken(string token, out string username, out ServerClient owner)
        {
            username = null; owner = null;
            if (string.IsNullOrEmpty(token)) return false;
            if (!_tokens.TryRemove(token, out TokenRecord v)) return false;
            if (DateTime.UtcNow.Ticks > v.Exp) return false;
            // The minting session may have ended while the client was dialing; a new session mints its own token.
            if (!KmhRouter.IsLive(v.Owner)) return false;
            username = v.User; owner = v.Owner;
            return true;
        }

        // The tokenless path for when RWT chat is down, so it demands a live verified session on a matching IP.
        private static bool TryAuthBySession(string username, TcpClient apiClient, out string user, out ServerClient owner)
        {
            user = null; owner = null;
            if (string.IsNullOrEmpty(username)) return false;
            var sc = KmhRouter.ResolveClient(username);   // verified + online, or null
            if (sc == null) return false;
            string apiIp = (apiClient.Client?.RemoteEndPoint as IPEndPoint)?.Address?.ToString();
            if (!IpMatches(apiIp, sc.IP))
            {
                ServerLog.Protocol($"API: session-auth IP mismatch for {username} (api {apiIp ?? "?"} vs session {sc.IP ?? "?"}).");
                return false;
            }
            user = username; owner = sc;
            return true;
        }

        // Heartbeats skip the router's inbound window, so they carry their own - sized well above the client's 15s interval: a ceiling on abuse, not a schedule.
        private const int MaxHeartbeatsPerWindow  = 12;
        private const int HeartbeatWindowSeconds  = 60;
        private static readonly object _beatLock = new object();
        private static readonly Util.KmhRateWindow _beats = new Util.KmhRateWindow();

        private static bool AllowHeartbeat(string user)
        {
            if (string.IsNullOrEmpty(user)) return true;
            lock (_beatLock) return _beats.Allow(user, DateTime.UtcNow.Ticks, MaxHeartbeatsPerWindow, HeartbeatWindowSeconds);
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
                if (now > kv.Value.Exp || !KmhRouter.IsLive(kv.Value.Owner)) _tokens.TryRemove(kv.Key, out _);
        }

        // A KMH frame opens with a big-endian length whose first byte is 0 under 16 MB; HTTP opens with a method name.
        private static bool LooksLikeHttp(byte[] first4)
        {
            if (first4 == null || first4.Length < 4) return false;
            string s = Encoding.ASCII.GetString(first4);
            return s.StartsWith("GET ", StringComparison.Ordinal) || s.StartsWith("HEAD", StringComparison.Ordinal)
                || s.StartsWith("POST", StringComparison.Ordinal) || s.StartsWith("OPTI", StringComparison.Ordinal);
        }

        private static async Task<byte[]> PeekAsync(NetworkStream stream, int count, int timeoutMs, CancellationToken ct)
        {
            Task<byte[]> read = ReadExactly(stream, count, ct);
            if (await Task.WhenAny(read, Task.Delay(timeoutMs, ct)).ConfigureAwait(false) != read) return null;
            return await read.ConfigureAwait(false);
        }

        // `lenBytes` carries a length prefix a caller already consumed while deciding what protocol this is.
        private static async Task<KmhEnvelope> ReadFrame(NetworkStream stream, byte[] lenBytes, CancellationToken ct)
        {
            byte[] lenBuf = lenBytes ?? await ReadExactly(stream, 4, ct).ConfigureAwait(false);
            if (lenBuf == null) return null;
            int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (len <= 0 || len > _maxFrameBytes) return null;
            byte[] body = await ReadExactly(stream, len, ct).ConfigureAwait(false);
            if (body == null) return null;
            return KmhEnvelope.TryParse(Encoding.UTF8.GetString(body));
        }

        // ReadFrame but give up after timeoutMs of silence (async reads ignore NetworkStream.ReadTimeout)
        private static Task<KmhEnvelope> ReadFrameWithin(NetworkStream stream, int timeoutMs, CancellationToken ct)
            => ReadFrameWithin(stream, null, timeoutMs, ct);

        private static async Task<KmhEnvelope> ReadFrameWithin(NetworkStream stream, byte[] lenBytes, int timeoutMs, CancellationToken ct)
        {
            Task<KmhEnvelope> read = ReadFrame(stream, lenBytes, ct);
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

        // One envelope's bytes, built once and written to every recipient: a snapshot is identical for all of them.
        public sealed class Framed
        {
            internal Framed(string kind, byte[] frame, int bodyBytes) { Kind = kind; Bytes = frame; BodyBytes = bodyBytes; }
            internal string Kind      { get; }
            internal byte[] Bytes     { get; }
            // The payload alone, which is what a peer's frame budget is measured against; Bytes carries 4 more.
            internal int    BodyBytes { get; }
        }

        // null means the payload could not be serialized at all, which is a fault in the payload, not in one peer.
        public static Framed Encode(KmhEnvelope env)
        {
            if (env == null) return null;
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(env.Serialize());
                return new Framed(env.Kind, FrameOf(body), body.Length);
            }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"KMH API: could not serialize '{env.Kind}': {ex.Message}");
                return null;
            }
        }

        public static SendResult Send(ServerClient client, Framed framed)
        {
            if (!_running || framed == null) return SendResult.NotConnected;
            ApiConn c = ConnFor(client);
            return c != null ? c.Send(framed) : SendResult.NotConnected;
        }

        // Asked BEFORE a payload is encoded, so a client on the chat fallback never pays for bytes it cannot receive.
        public static bool IsConnected(ServerClient client) => _running && ConnFor(client) != null;

        private sealed class ApiConn
        {
            private readonly TcpClient _client;
            private readonly object    _sendLock = new object();
            public readonly NetworkStream Stream;
            public readonly string Username;
            // The exact RWT session this socket authenticated against - the identity every accessor matches on.
            public readonly ServerClient Owner;
            public ApiConn(TcpClient c, NetworkStream s, string user, ServerClient owner)
            { _client = c; Stream = s; Username = user; Owner = owner; }

            // Negotiated at hello: the smaller of what this server sends and what this client can read.
            public int  FrameLimitBytes = 64 * 1024;
            public bool PeerFragments;

            public bool TrySend(KmhEnvelope env) => Send(env) == SendResult.Sent;

            // Encoded here only for a single recipient; a broadcast encodes once outside and calls the overload below.
            public SendResult Send(KmhEnvelope env)
            {
                Framed framed = Encode(env);
                return framed == null ? SendResult.SerializationFailure : Send(framed);
            }

            // Written under a lock, full-duplex with the receive task's read on the same socket.
            public SendResult Send(Framed framed)
            {
                if (framed == null) return SendResult.SerializationFailure;
                if (framed.BodyBytes > FrameLimitBytes)
                {
                    Diagnostics.ServerLog.Warn($"KMH API oversized outgoing envelope: user={Username} kind={framed.Kind} "
                        + $"serialized={framed.BodyBytes} bytes frameLimit={FrameLimitBytes} - "
                        + (PeerFragments ? "fragmenting." : "and this client cannot reassemble fragments."));
                    return SendResult.TooLarge;
                }

                try
                {
                    lock (_sendLock) { Stream.Write(framed.Bytes, 0, framed.Bytes.Length); }
                    return SendResult.Sent;
                }
                catch (Exception ex)
                {
                    Diagnostics.ServerLog.Verbose($"KMH API: send '{framed.Kind}' to {Username} failed: {ex.Message}");
                    return SendResult.IoFailure;
                }
            }

            public void Close() { try { Stream?.Close(); } catch { } try { _client?.Close(); } catch { } }
        }
    }
}
