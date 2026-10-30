using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        // The owning session is part of the credential, or a later connection could inherit a stale token's authority.
        private struct TokenRecord { public string User; public ServerClient Owner; public long Exp; }
        private static readonly ConcurrentDictionary<string, TokenRecord> _tokens
            = new ConcurrentDictionary<string, TokenRecord>();
        // Indexed by username for lookup only; ApiConn.Owner is the authority, and every accessor checks it.
        private static readonly ConcurrentDictionary<string, ApiConn> _conns
            = new ConcurrentDictionary<string, ApiConn>(StringComparer.OrdinalIgnoreCase);

        private static ConnectionLimiter _limiter = new ConnectionLimiter(200, 6);
        private static AuthFailThrottle  _throttle = new AuthFailThrottle(10, 60, 300);

        private static int _failBlockSeconds = 300;

        public static bool Running => _running;

        // Snapshotted at Start with the bind: a reload must never turn auth off on a listener other machines can reach.
        private static volatile bool _requireAuth = true;
        private static bool RequireAuth => _requireAuth;

        public static string StartRefusal { get; private set; }

        // The video relay shares this port by default rather than asking an owner to open a second.
        private static int _boundPort;
        public static int Port => _running ? _boundPort : 0;
        public static int  ConnectedCount => _conns.Count;

        public static void Start()
        {
            if (_running) return;
            StartRefusal = null;
            TransportConfig cfg = TransportConfig.Current;
            if (!cfg.EnableKmhApiTransport)
            {
                ServerLog.Info("Colony Nexus API transport: disabled (Config/Transport.json EnableKmhApiTransport=false) - using RWT chat.");
                return;
            }
            // Fail closed: an unreadable bind or a reachable port without auth never opens a socket.
            string unsafeReason = cfg.UnsafeReason;
            if (unsafeReason != null)
            {
                StartRefusal = unsafeReason;
                ServerLog.Error("Colony Nexus API transport NOT started: " + unsafeReason + " Players stay on the chat transport until it is fixed (Config/Transport.json, then restart).");
                return;
            }
            try
            {
                _maxFrameBytes  = cfg.Advanced.MaxFrameKb * 1024;
                _authTimeoutMs  = cfg.Advanced.AuthTimeoutSeconds * 1000;
                _idleTimeoutMs  = cfg.Advanced.IdleTimeoutSeconds * 1000;
                _failBlockSeconds = cfg.Advanced.FailedAuthBlockSeconds;
                _limiter  = new ConnectionLimiter(cfg.Advanced.MaxConnections, cfg.Advanced.MaxConnectionsPerIp);
                _throttle = new AuthFailThrottle(cfg.Advanced.FailedAuthPerIp, cfg.Advanced.FailedAuthWindowSeconds, cfg.Advanced.FailedAuthBlockSeconds);
                _requireAuth = cfg.RequireKmhApiAuth;

                TransportConfig.TryResolveBind(cfg.BindAddress, out IPAddress bind);
                _listener = new TcpListener(bind, cfg.KmhApiPort);
                _listener.Start();
                // Asked of the socket, not the config: port 0 means "any free one", and clients are told this number.
                _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                _running = true;
                Task.Run(() => AcceptLoop(_cts.Token));
                ServerLog.Success($"Colony Nexus API transport: listening on {bind}:{_boundPort} (auth {(cfg.RequireKmhApiAuth ? "required" : "OFF")}).");
                // Anyone who handshook before this point was told the transport was off, and would stay on chat all session.
                Comms.CommsStartup.PushHello();
                WarnOnUnsafeConfig(cfg);
            }
            catch (Exception ex)
            {
                _running = false;
                ServerLog.Error($"Colony Nexus API transport: could not bind {cfg.BindAddress}:{cfg.KmhApiPort} - staying on chat transport. ({ex.Message})");
            }
        }

        // Logged at boot so an owner cannot drift into disabled auth unnoticed; public + auth off never gets here.
        private static void WarnOnUnsafeConfig(TransportConfig cfg)
        {
            if (!cfg.RequireKmhApiAuth)
            {
                ServerLog.Warn("Colony Nexus API: auth is OFF on a loopback-only bind (testing only) - any local program can claim any player. Set RequireKmhApiAuth=true.");
            }
            else if (cfg.IsPublicBind)
            {
                // Info rather than a warning: a public bind is the default posture and auth is still required.
                ServerLog.Info($"Colony Nexus API: public bind ({cfg.BindAddress}) with auth required - forward TCP {cfg.KmhApiPort} for remote players, or set BindAddress=127.0.0.1 for local-only.");
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
            // A token is consumed on use, so reissuing an outstanding one keeps a session to a single live credential.
            foreach (var kv in _tokens)
                if (ReferenceEquals(kv.Value.Owner, owner)) return kv.Key;
            byte[] raw = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string token = Convert.ToBase64String(raw);
            _tokens[token] = new TokenRecord { User = username, Owner = owner, Exp = (DateTime.UtcNow + TokenTtl).Ticks };
            return token;
        }

        // Matched on the exact session, not the username: a reconnect may already own the registry entry.
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

        private static ApiConn ConnFor(ServerClient client)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return null;
            return _conns.TryGetValue(user, out ApiConn c) && ReferenceEquals(c.Owner, client) && !c.IsDead ? c : null;
        }

        // Until this runs, IsConnected keeps saying yes and every later send retries the same dead socket.
        private static void Invalidate(ApiConn conn)
        {
            if (conn == null || !conn.TryMarkDead()) return;
            _conns.TryRemove(new System.Collections.Generic.KeyValuePair<string, ApiConn>(conn.Username, conn));
            conn.Close();
            ServerLog.Protocol($"API: dropped {conn.Username}'s connection after a failed write.");
        }

        // "Not on the API" and "too big" differ, or an oversized payload falls back to chat and fails there too.
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

        // Defaults to false, unlike fragments: a peer that never said so gets raw bytes it can certainly read.
        public static bool PeerSupportsDeflate(ServerClient client) => ConnFor(client)?.PeerDeflate == true;

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

        // One connection resolved up front, or a mid-transfer reconnect splits the parts across two sockets.
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

        // HandleClient must Release() every admitted socket, or the limiter leaks a slot.
        private static bool Admit(string ip, out string why)
        {
            if (_throttle.IsBlocked(ip)) { why = "auth block"; return false; }
            if (_limiter.TryAdmit(ip)) { why = null; return true; }
            // Named, because "refused" alone sends an owner hunting the wrong setting.
            why = _limiter.PerIp(ip) >= TransportConfig.Current.Advanced.MaxConnectionsPerIp
                ? $"per-IP cap ({TransportConfig.Current.Advanced.MaxConnectionsPerIp}) - raise Advanced.MaxConnectionsPerIp in Config/Transport.json if these players share one address"
                : $"server cap ({TransportConfig.Current.Advanced.MaxConnections})";
            return false;
        }

        // Players behind one address share every per-IP cap; tracked only as a bounded, expiring hint for the owner.
        private sealed class Ingress { public readonly HashSet<string> Users = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public long LastSeen; public bool Reported; }
        private static readonly ConcurrentDictionary<string, Ingress> _ingress = new ConcurrentDictionary<string, Ingress>();
        internal const int MaxIngressTracked = 4096;
        internal static readonly TimeSpan IngressTtl = TimeSpan.FromHours(6);
        private static long _ingressSweptAt;

        internal static int IngressTrackedForTest => _ingress.Count;

        internal static void NoteSharedIngress(string ip, string user, long nowTicks = 0)
        {
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user)) return;
            long now = nowTicks > 0 ? nowTicks : DateTime.UtcNow.Ticks;
            SweepIngress(now);
            Ingress seen = _ingress.GetOrAdd(ip, _ => new Ingress());
            int count;
            lock (seen)
            {
                seen.Users.Add(user);
                seen.LastSeen = now;
                count = seen.Users.Count;
                if (count < 2 || seen.Reported) return;
                seen.Reported = true;
            }
            TransportConfig cfg = TransportConfig.Current;
            ServerLog.Info($"Colony Nexus API: {count} players reach this server from one address - they share this server's "
                         + $"per-IP limits (MaxConnectionsPerIp={cfg.Advanced.MaxConnectionsPerIp}, "
                         + $"FailedAuthPerIp={cfg.Advanced.FailedAuthPerIp}/{cfg.Advanced.FailedAuthWindowSeconds}s). If more than "
                         + $"{cfg.Advanced.MaxConnectionsPerIp} of them play at once, raise Advanced.MaxConnectionsPerIp in Config/Transport.json.");
        }

        // At most once a minute, or at once when full: quiet addresses go, then the longest quiet if still over the cap.
        private static void SweepIngress(long now)
        {
            long last = Interlocked.Read(ref _ingressSweptAt);
            bool full = _ingress.Count >= MaxIngressTracked;
            if (!full && now - last < TimeSpan.TicksPerMinute) return;
            if (Interlocked.CompareExchange(ref _ingressSweptAt, now, last) != last) return;
            var quiet = new List<(long At, string Ip)>();
            foreach (var kv in _ingress)
            {
                long at;
                lock (kv.Value) at = kv.Value.LastSeen;
                if (now - at > IngressTtl.Ticks) _ingress.TryRemove(kv.Key, out _);
                else quiet.Add((at, kv.Key));
            }
            int over = _ingress.Count - MaxIngressTracked * 9 / 10;
            if (over <= 0) return;
            quiet.Sort((a, b) => a.At.CompareTo(b.At));
            for (int i = 0; i < over && i < quiet.Count; i++) _ingress.TryRemove(quiet[i].Ip, out _);
        }

        internal static void ResetIngressForTest() { _ingress.Clear(); Interlocked.Exchange(ref _ingressSweptAt, 0); }

        private static void Release(string ip) => _limiter.Release(ip);

        // A peer that held a slot the whole auth window without a byte counts against its address, or silent sockets fill
        // every slot; one that closes at once (a port check) does not.
        internal static bool HeldSilent(long elapsedMs, int timeoutMs) => timeoutMs > 0 && elapsedMs >= timeoutMs * 9L / 10;

        private static void NoteAuthFail(string ip)
        {
            if (_throttle.NoteFailure(ip, out int total))
            {
                ServerLog.Warn($"Colony Nexus API: an IP tripped the failed-auth throttle - blocked for {_failBlockSeconds}s (enable transport debug to see which).");
                ServerLog.Protocol($"Colony Nexus API: blocked {ip} for {_failBlockSeconds}s after {total} failed auths.");
            }
        }

        internal const int SendTimeoutMs = 10_000;

        private static async Task HandleClient(TcpClient client, string ip, CancellationToken ct)
        {
            ApiConn conn = null;
            string user = null;
            ServerClient owner = null;
            try
            {
                client.NoDelay = true;
                // A peer that stops reading would otherwise hold the write, and everyone queued behind it, forever.
                client.SendTimeout = SendTimeoutMs;
                NetworkStream stream = client.GetStream();

                // Lets video share this port instead of asking an owner to open a second one.
                long started = Environment.TickCount64;
                byte[] peek = await PeekAsync(stream, 4, _authTimeoutMs, ct).ConfigureAwait(false);
                if (peek == null)
                {
                    if (HeldSilent(Environment.TickCount64 - started, _authTimeoutMs)) NoteAuthFail(ip);
                    return;
                }
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

                // Bounded here because async reads ignore NetworkStream.ReadTimeout.
                KmhEnvelope hello = await ReadFrameWithin(stream, peek, _authTimeoutMs, ct).ConfigureAwait(false);
                if (hello == null || hello.Kind != KindApiHello)
                {
                    // Counted as a failed auth, or a peer could send junk and reconnect forever unthrottled.
                    NoteAuthFail(ip);
                    ServerLog.Protocol("API: first frame was not kmh.api.hello - dropping.");
                    return;
                }

                if (_requireAuth)
                {
                    string token   = hello.GetString("token") ?? "";
                    string claimed = hello.GetString("username") ?? "";
                    // Only the token proves identity; everyone behind one NAT or tunnel shares an address.
                    if (!string.IsNullOrEmpty(token) && TryConsumeToken(token, out user, out owner)) { /* authed by token */ }
                    else
                    {
                        await WriteFrame(stream, KindApiHelloAck, new { ok = false, reason = "auth" }, ct).ConfigureAwait(false);
                        ServerLog.Protocol($"API: hello rejected for '{claimed}' (no valid token).");
                        NoteAuthFail(ip);
                        return;
                    }
                }
                else
                {
                    user = hello.GetString("username") ?? "";  // unauth mode (testing only)
                    if (string.IsNullOrEmpty(user)) { await WriteFrame(stream, KindApiHelloAck, new { ok = false, reason = "no_user" }, ct).ConfigureAwait(false); return; }
                    // Still session-bound: with nothing to route to it would ack heartbeats and drop every request.
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
                bool clientDeflate   = hello.GetBool("supports_deflate", false);
                int  inflateBytes    = InflateLimitFor(hello.GetInt("max_inflate_kb", 0), effectiveKb);

                // ack before registering, so no other thread writes this socket mid-handshake
                await WriteFrame(stream, KindApiHelloAck, new
                {
                    ok = true, v = KmhProtocol.CurrentVersion, build = KmhProtocol.BuildVersion,
                    disabled = string.Join(",", FeaturesConfig.Current.DisabledList()),
                    capabilities = KmhCapabilities.Manifest,
                    max_frame_kb = effectiveKb, supports_fragmentation = true, supports_deflate = true,
                    max_inflate_kb = MaxInflateBytes / 1024,
                    // Next one-use token: a reconnect proves itself by it, not by a source IP a proxy or VPN changes.
                    token = RequireAuth ? IssueToken(owner) : "",
                }, ct).ConfigureAwait(false);
                conn = new ApiConn(client, stream, user, owner)
                       { FrameLimitBytes = effectiveKb * 1024, InflateLimitBytes = inflateBytes,
                         PeerFragments = clientFragments, PeerDeflate = clientDeflate };
                if (!clientFragments)
                    ServerLog.Info($"Colony Nexus API: '{user}' is an older client without fragmentation - anything past {effectiveKb} KB cannot reach it.");
                if (_conns.TryGetValue(user, out ApiConn prev)) prev.Close();
                _conns[user] = conn;
                _throttle.Clear(ip);
                KmhTransportStats.Connected();
                NoteSharedIngress(ip, user);
                ServerLog.Success($"API: {user} connected over the Colony Nexus transport.");

                InflateBudget inflateBudget = new InflateBudget();
                while (_running && !ct.IsCancellationRequested)
                {
                    KmhEnvelope env = await ReadFrameWithin(stream, null, _idleTimeoutMs, ct, inflateBudget).ConfigureAwait(false);
                    if (env == null) break;

                    // Checked before the heartbeat: a socket whose RWT session is gone must not read as healthy.
                    if (!KmhRouter.IsLive(owner))
                    {
                        ServerLog.Protocol($"API: {user}'s RWT session has ended - closing the API connection that belonged to it.");
                        break;
                    }
                    if (env.Kind == KindPing)
                    {
                        // Ping skips the inbound window; this cap stops it becoming an unlimited reply channel.
                        if (AllowHeartbeat(user)) conn.TrySend(new KmhEnvelope(KindPong, null));
                        continue;
                    }
                    // Routed under the session this socket authenticated, never anything the envelope claims.
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

        // A client that never said only ever inflated up to its frame size.
        internal static int InflateLimitFor(int advertisedKb, int frameKb)
        {
            int frame = Math.Max(1, frameKb) * 1024;
            if (advertisedKb <= 0) return frame;
            long ceiling = SubProtocol.KmhFragments.MaxTransferBytes;
            return (int)Math.Max(frame, Math.Min((long)advertisedKb * 1024, ceiling));
        }

        // Test seams: a live handshake never registers two connections under one username, so ownership needs these.
        internal static void RegisterForTest(string user, ServerClient owner) => _conns[user] = new ApiConn(null, null, user, owner);
        internal static bool IsRegisteredFor(ServerClient owner) => ConnFor(owner) != null;
        internal static bool TryConsumeTokenForTest(string token, out string user, out ServerClient owner) => TryConsumeToken(token, out user, out owner);
        internal static int  OutstandingTokensForTest => _tokens.Count;
        // An authenticated connection's read, which is the only one allowed to carry compressed frames.
        internal static Task<KmhEnvelope> ReadFrameForTest(NetworkStream stream, CancellationToken ct) => ReadFrame(stream, null, ct, new InflateBudget());
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

        // Heartbeats skip the router's inbound window, so they get their own: well above the 15s interval, an abuse ceiling.
        private const int MaxHeartbeatsPerWindow  = 12;
        private const int HeartbeatWindowSeconds  = 60;
        private static readonly object _beatLock = new object();
        private static readonly Util.KmhRateWindow _beats = new Util.KmhRateWindow();

        private static bool AllowHeartbeat(string user)
        {
            if (string.IsNullOrEmpty(user)) return true;
            lock (_beatLock) return _beats.Allow(user, DateTime.UtcNow.Ticks, MaxHeartbeatsPerWindow, HeartbeatWindowSeconds);
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
            if (!await FinishesWithin(read, timeoutMs, ct).ConfigureAwait(false)) return null;
            return await read.ConfigureAwait(false);
        }

        // The timer is cancelled with the wait; left running, every read would park one for the whole idle window.
        internal static async Task<bool> FinishesWithin(Task read, int timeoutMs, CancellationToken ct)
        {
            using (CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task first = await Task.WhenAny(read, Task.Delay(timeoutMs, delay.Token)).ConfigureAwait(false);
                delay.Cancel();
                return first == read;
            }
        }

        // lenBytes is a length prefix a caller already consumed. A null budget means an unauthenticated peer, which may
        // not send compressed frames: a 2 KB frame could inflate to 2 MB before the hello was checked.
        internal static async Task<KmhEnvelope> ReadFrame(System.IO.Stream stream, byte[] lenBytes, CancellationToken ct, InflateBudget budget = null)
        {
            byte[] lenBuf = lenBytes ?? await ReadExactly(stream, 4, ct).ConfigureAwait(false);
            if (lenBuf == null) return null;
            SubProtocol.KmhFrameCodec.ReadHeader(lenBuf, out int len, out bool compressed);
            if (len <= 0 || len > _maxFrameBytes) return null;
            if (compressed && budget == null) return null;
            byte[] body = await ReadExactly(stream, len, ct).ConfigureAwait(false);
            if (body == null) return null;
            KmhTransportStats.FrameIn(4 + len);
            if (compressed)
            {
                // Our advertised cap, so a few KB cannot inflate without limit.
                body = SubProtocol.KmhFrameCodec.Inflate(body, MaxInflateBytes);
                if (body == null || !budget.Take(body.Length)) return null;
            }
            return KmhEnvelope.TryParse(Encoding.UTF8.GetString(body));
        }

        // Inflated bytes one connection may produce per window; past it the connection is dropped.
        internal sealed class InflateBudget
        {
            internal const long Bytes = 16L * 1024 * 1024;
            internal const long WindowMs = 10_000;
            private long _windowStart = Environment.TickCount64, _used;

            internal bool Take(int bytes)
            {
                long now = Environment.TickCount64;
                if (now - _windowStart > WindowMs) { _windowStart = now; _used = 0; }
                _used += bytes;
                return _used <= Bytes;
            }
        }

        // Advertised in the ack and above the frame size, so a compressed catalog may unpack past one frame.
        internal const int MaxInflateBytes = 2 * 1024 * 1024;

        // Async reads ignore NetworkStream.ReadTimeout, so silence is bounded here.
        private static Task<KmhEnvelope> ReadFrameWithin(NetworkStream stream, int timeoutMs, CancellationToken ct)
            => ReadFrameWithin(stream, null, timeoutMs, ct);

        private static async Task<KmhEnvelope> ReadFrameWithin(NetworkStream stream, byte[] lenBytes, int timeoutMs, CancellationToken ct,
                                                               InflateBudget budget = null)
        {
            Task<KmhEnvelope> read = ReadFrame(stream, lenBytes, ct, budget);
            if (!await FinishesWithin(read, timeoutMs, ct).ConfigureAwait(false)) return null;
            return await read.ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactly(System.IO.Stream stream, int count, CancellationToken ct)
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

        // Handshake ack only: the single writer at that point, so a direct async write is safe.
        private static async Task WriteFrame(NetworkStream stream, string kind, object data, CancellationToken ct)
        {
            byte[] frame = FrameOf(Encoding.UTF8.GetBytes(new KmhEnvelope(kind, data).Serialize()));
            await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
        }

        private static byte[] FrameOf(byte[] body) => SubProtocol.KmhFrameCodec.Frame(body, false);

        // One envelope's bytes, built once and written to every recipient: a snapshot is identical for all of them.
        public sealed class Framed
        {
            internal Framed(string kind, byte[] frame, int bodyBytes, int rawBytes = -1)
            { Kind = kind; Bytes = frame; BodyBytes = bodyBytes; RawBytes = rawBytes < 0 ? bodyBytes : rawBytes; }
            internal string Kind      { get; }
            internal byte[] Bytes     { get; }
            // The payload alone, which is what a peer's frame budget is measured against; Bytes carries 4 more.
            internal int    BodyBytes { get; }
            internal int    RawBytes  { get; }
            internal bool   Compressed => RawBytes != BodyBytes;
        }

        // null means the payload could not be serialized at all, which is a fault in the payload, not in one peer.
        public static Framed Encode(KmhEnvelope env) => Encode(env, false);

        // BodyBytes is what goes on the wire; the body is written behind four bytes of room so the header needs no copy.
        public static Framed Encode(KmhEnvelope env, bool compress)
        {
            if (env == null) return null;
            try
            {
                byte[] withHead = env.SerializeUtf8(4);
                int raw = withHead.Length - 4;
                byte[] packed = compress ? SubProtocol.KmhFrameCodec.Deflate(withHead, 4, raw, 4) : null;
                if (packed != null)
                {
                    SubProtocol.KmhFrameCodec.WriteHeader(packed, packed.Length - 4, true);
                    return new Framed(env.Kind, packed, packed.Length - 4, raw);
                }
                SubProtocol.KmhFrameCodec.WriteHeader(withHead, raw, false);
                return new Framed(env.Kind, withHead, raw);
            }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Warn($"Colony Nexus API: could not serialize '{env.Kind}': {ex.Message}");
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

        internal sealed class ApiConn
        {
            private readonly TcpClient _client;
            public readonly NetworkStream Stream;
            public readonly string Username;
            // The exact RWT session this socket authenticated against - the identity every accessor matches on.
            public readonly ServerClient Owner;
            public ApiConn(TcpClient c, NetworkStream s, string user, ServerClient owner)
            { _client = c; Stream = s; Username = user; Owner = owner; }

            // Negotiated at hello: the smaller of what this server sends and what this client can read.
            public int  FrameLimitBytes = 64 * 1024;
            public int  InflateLimitBytes = 64 * 1024;
            public bool PeerFragments;
            public bool PeerDeflate;

            private int _dead;
            public bool IsDead => System.Threading.Volatile.Read(ref _dead) != 0;

            // True to exactly one caller, so a burst of failing writes drops and logs the connection once.
            internal bool TryMarkDead() => System.Threading.Interlocked.Exchange(ref _dead, 1) == 0;

            public bool TrySend(KmhEnvelope env) => Send(env) == SendResult.Sent;

            // Encoded here only for a single recipient; a broadcast encodes once outside and calls the overload below.
            public SendResult Send(KmhEnvelope env)
            {
                Framed framed = Encode(env);
                return framed == null ? SendResult.SerializationFailure : Send(framed);
            }

            // A peer this far behind is not reading, and waiting for it would hold every broadcast behind it.
            internal const long MaxQueuedBytes = 8L * 1024 * 1024;
            internal const int CoalesceBytes = 64 * 1024;

            private readonly ConcurrentQueue<Framed> _outbox = new ConcurrentQueue<Framed>();
            private long _queuedBytes;
            private int  _draining;
            private byte[] _batch;

            internal long QueuedBytes => Interlocked.Read(ref _queuedBytes);

            // Queued, not written: Sent still means handed to the transport, and a later failure drops the connection.
            public SendResult Send(Framed framed)
            {
                if (framed == null) return SendResult.SerializationFailure;
                if (framed.BodyBytes > FrameLimitBytes || framed.Compressed && framed.RawBytes > InflateLimitBytes)
                {
                    string what = $"Colony Nexus API oversized outgoing envelope: user={Username} kind={framed.Kind} "
                                + $"serialized={framed.BodyBytes} bytes (unpacked {framed.RawBytes}) "
                                + $"frameLimit={FrameLimitBytes} inflateLimit={InflateLimitBytes} - ";

                    // Fragmenting is the designed path, so a warning here would fire on every refresh of a large snapshot.
                    if (PeerFragments) Diagnostics.ServerLog.Verbose(what + "fragmenting.");
                    else Diagnostics.ServerLog.Warn(what + "and this client cannot reassemble fragments.");

                    return SendResult.TooLarge;
                }
                if (IsDead) return SendResult.IoFailure;

                long queued = Interlocked.Add(ref _queuedBytes, framed.Bytes.Length);
                if (queued > MaxQueuedBytes)
                {
                    Interlocked.Add(ref _queuedBytes, -framed.Bytes.Length);
                    KmhTransportStats.SlowPeerDropped();
                    Diagnostics.ServerLog.Warn($"Colony Nexus API: {Username} is not reading - {queued / 1024} KB is waiting to be sent. "
                                             + "Dropping the connection so it cannot hold up everyone else; the game reconnects.");
                    Invalidate(this);
                    return SendResult.IoFailure;
                }
                KmhTransportStats.QueueDepth(queued);
                _outbox.Enqueue(framed);
                if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) _ = DrainAsync();
                return SendResult.Sent;
            }

            // The one writer; the re-check after standing down covers a frame queued at that moment.
            private async Task DrainAsync()
            {
                try
                {
                    while (true)
                    {
                        if (IsDead) return;
                        if (!_outbox.TryDequeue(out Framed first))
                        {
                            Volatile.Write(ref _draining, 0);
                            if (_outbox.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
                            continue;
                        }

                        byte[] buffer = first.Bytes;
                        int length = first.Bytes.Length, frames = 1, raw = first.RawBytes;
                        int compressed = first.Compressed ? 1 : 0;
                        // Only this writer dequeues, so a peeked frame is still next.
                        if (length < CoalesceBytes && _outbox.TryPeek(out Framed next) && length + next.Bytes.Length <= CoalesceBytes)
                        {
                            buffer = _batch ??= new byte[CoalesceBytes];
                            Buffer.BlockCopy(first.Bytes, 0, buffer, 0, length);
                            while (_outbox.TryPeek(out next) && length + next.Bytes.Length <= CoalesceBytes
                                   && _outbox.TryDequeue(out next))
                            {
                                Buffer.BlockCopy(next.Bytes, 0, buffer, length, next.Bytes.Length);
                                length += next.Bytes.Length;
                                raw += next.RawBytes;
                                if (next.Compressed) compressed++;
                                frames++;
                            }
                        }

                        // Bounded, so a shut receive window drops the peer.
                        using (var timeout = new CancellationTokenSource(SendTimeoutMs))
                            await Stream.WriteAsync(buffer, 0, length, timeout.Token).ConfigureAwait(false);
                        Interlocked.Add(ref _queuedBytes, -length);
                        KmhTransportStats.Written(frames, length, raw + 4L * frames, compressed);
                    }
                }
                catch (Exception ex)
                {
                    // TCP has no transient write failure: this socket is finished.
                    KmhTransportStats.WriteFailed();
                    Diagnostics.ServerLog.Verbose($"Colony Nexus API: write to {Username} failed: {ex.Message}");
                    Invalidate(this);
                    Volatile.Write(ref _draining, 0);
                }
            }

            internal bool WaitDrained(TimeSpan max)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!IsDead && (Interlocked.Read(ref _queuedBytes) > 0 || Volatile.Read(ref _draining) != 0))
                {
                    if (sw.Elapsed >= max) return false;
                    Thread.Sleep(5);
                }
                return true;
            }

            public void Close() { try { Stream?.Close(); } catch { } try { _client?.Close(); } catch { } }
        }
    }
}
