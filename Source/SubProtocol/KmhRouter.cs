using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using SR = KMHServerAddon.Features.Transport.KmhApiServer.SendResult;

namespace KMHServerAddon.SubProtocol
{
    // A handler must take identity from ServerClient.UserFile.Username, never from an envelope field.
    public static class KmhRouter
    {
        private static readonly Dictionary<string, Action<ServerClient, KmhEnvelope>> Handlers
            = new Dictionary<string, Action<ServerClient, KmhEnvelope>>();

        // Well above the 512-char chat limit a feature payload needs, but still short of a multi-MB envelope.
        public const int MaxEnvelopeBytes = 64 * 1024;

        public static void RegisterHandler(string kind, Action<ServerClient, KmhEnvelope> handler)
        {
            if (string.IsNullOrEmpty(kind))
            {
                ServerLog.Warn("RegisterHandler called with null/empty kind");
                return;
            }
            // A duplicate means a double-register bug or an extension past the SDK guard, so the owner sees it.
            if (Handlers.ContainsKey(kind))
                ServerLog.Warn($"Handler for kind '{kind}' is being overwritten - previous registration replaced");
            Handlers[kind] = handler;
            ServerLog.Verbose($"Registered handler '{kind}'");
        }

        public static bool IsRegistered(string kind)
            => !string.IsNullOrEmpty(kind) && Handlers.ContainsKey(kind);

        // Never throws, or one bad message would take down the whole chat pipeline.
        public static void HandleInbound(ServerClient client, PKT_Chat pkt)
        {
            if (pkt == null || string.IsNullOrEmpty(pkt.Message)) return;
            if (pkt.Message.Length > MaxEnvelopeBytes)
            {
                ServerLog.Warn(
                    $"Dropping oversized envelope from {client?.GetData<UserFile>()?.Username ?? "?"} " +
                    $"({pkt.Message.Length} > {MaxEnvelopeBytes} bytes)");
                return;
            }

            KmhEnvelope parsed = KmhEnvelope.TryParse(pkt.Message);
            if (parsed != null && KmhFragments.IsFragment(parsed.Kind)) { HandleInbound(client, parsed, overChat: true); return; }

            KmhEnvelope env = parsed;
            if (env == null || string.IsNullOrEmpty(env.Kind))
            {
                ServerLog.Warn($"Malformed envelope from {client?.GetData<UserFile>()?.Username ?? "?"}");
                return;
            }
            HandleInbound(client, env, overChat: true);
        }

        // KmhRateWindow is not self-synchronizing, so both windows are locked.
        public const int MaxInboundPerWindow  = 120;
        public const int InboundWindowSeconds = 10;
        private static readonly object _inboundLock = new object();
        private static readonly Util.KmhRateWindow _inbound    = new Util.KmhRateWindow();
        private static readonly Util.KmhRateWindow _floodLog   = new Util.KmhRateWindow();

        // Named and pinned by a test, because a third exemption would hand a client an uncapped channel.
        internal static bool CountsTowardInboundCap(string kind)
            => !string.IsNullOrEmpty(kind)
            && kind != KmhProtocol.Kind.Ping
            && kind != KmhProtocol.Kind.HelloAck;

        // The exempt kinds still answer, so they need a cap of their own - sized well clear of the client's 15s heartbeat: a ceiling on abuse, not a schedule.
        public const int MaxHandshakePerWindow  = 12;
        public const int HandshakeWindowSeconds = 60;
        private static readonly Util.KmhRateWindow _handshake = new Util.KmhRateWindow();

        private static bool AllowHandshake(ServerClient client)
        {
            string who = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(who)) return true;   // pre-auth traffic is already gated above
            lock (_inboundLock)
                return _handshake.Allow(who, DateTime.UtcNow.Ticks, MaxHandshakePerWindow, HandshakeWindowSeconds);
        }

        private static bool AllowInbound(ServerClient client)
        {
            string who = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(who)) return true;   // pre-auth traffic is already gated above
            long now = DateTime.UtcNow.Ticks;
            lock (_inboundLock)
            {
                if (_inbound.Allow(who, now, MaxInboundPerWindow, InboundWindowSeconds)) return true;
                if (_floodLog.Allow(who, now, 1, 30))
                    ServerLog.Warn($"Inbound flood from {who}: over {MaxInboundPerWindow} msgs/{InboundWindowSeconds}s - dropping until it settles.");
            }
            return false;
        }

        // One per session, so no other connection can consume this one's budget or hand it a half-assembled envelope.
        private static readonly Dictionary<ServerClient, KmhFragments.Assembler> _assemblers =
            new Dictionary<ServerClient, KmhFragments.Assembler>();

        public static void ForgetPeer(ServerClient client)
        {
            if (client == null) return;
            lock (_assemblers) _assemblers.Remove(client);
        }

        // Reference identity against RWT's live set - the only thing that separates a session from its successor under the same username.
        public static bool IsLive(ServerClient client)
            => client != null && client.IsVerified && Network.ServerClients.ContainsKey(client);

        public static void HandleInbound(ServerClient client, KmhEnvelope env) => HandleInbound(client, env, overChat: false);

        public static void HandleInbound(ServerClient client, KmhEnvelope env, bool overChat)
        {
            if (env == null || string.IsNullOrEmpty(env.Kind)) return;

            // Enforced on arrival: a modified client that ignores the advertised policy must not run the economy over chat anyway.
            if (overChat && !ChatMayCarry(env.Kind))
            {
                ServerLog.Verbose($"Blocked '{env.Kind}' from {client?.GetData<UserFile>()?.Username ?? "?"} - feature traffic over RWT chat is off on this server.");
                return;
            }

            // Counted as one logical message, or a large legitimate transfer would trip the flood cap.
            if (KmhFragments.IsFragment(env.Kind))
            {
                if (client == null) return;
                KmhFragments.Assembler asm;
                lock (_assemblers)
                {
                    if (!_assemblers.TryGetValue(client, out asm))
                        _assemblers[client] = asm = new KmhFragments.Assembler();
                }
                KmhEnvelope whole = asm.Accept(env, out string why);
                if (why != null) ServerLog.Warn($"Transport: dropped fragment from {client.GetData<UserFile>()?.Username ?? "?"} - {why}");
                if (whole != null) HandleInbound(client, whole, overChat);
                return;
            }

            // Enforced server-side, so a modified client that ignores its own version gate still cannot proceed.
            if (env.Kind != KmhProtocol.Kind.HelloAck && env.Kind != KmhProtocol.Kind.Ping
                && !KmhHandshakeHandler.IsCompatible(client))
            {
                NoteEarlyDrop(client?.GetData<UserFile>()?.Username ?? "?");
                return;
            }

            // Dropped silently, since every request rebuilds a whole snapshot and the client's next refresh recovers.
            if (CountsTowardInboundCap(env.Kind))
            {
                if (!AllowInbound(client)) return;
            }
            else if (!AllowHandshake(client)) return;

            // The server-side block behind the disabled state the client already shows.
            string feature = Features.FeaturesConfig.FeatureForKind(env.Kind);
            if (feature != null && !Features.FeaturesConfig.Current.IsEnabled(feature))
            {
                ServerLog.Verbose($"Blocked '{env.Kind}' - {feature} disabled by server config");
                return;
            }

            // Mutations only, reads still pass: handlers are registered before their stores load, and files rewritten underneath the economy must not be crossed.
            bool mutation = Maintenance.KmhMaintenanceGate.WouldBlock(env.Kind);
            if (mutation && !Maintenance.KmhReadiness.IsReady)
            {
                ServerLog.Verbose($"Blocked '{env.Kind}' - {Maintenance.KmhReadiness.Describe()}");
                Notify(client, "negative", Results.KmhErrorText.SafeMessage(Results.KmhErrorCode.InvalidState));
                return;
            }
            if (Maintenance.KmhMaintenanceGate.ShouldBlock(env.Kind))
            {
                ServerLog.Verbose($"Blocked '{env.Kind}' - {Maintenance.KmhMaintenanceGate.Describe()}");
                Notify(client, "negative", Results.KmhErrorText.SafeMessage(Results.KmhErrorCode.InvalidState));
                return;
            }

            if (!Handlers.TryGetValue(env.Kind, out Action<ServerClient, KmhEnvelope> handler))
            {
                ServerLog.Verbose($"No handler for kind '{env.Kind}' from {client?.GetData<UserFile>()?.Username ?? "?"}");
                return;
            }

            KmhInterest.Touch(client?.GetData<UserFile>()?.Username, env.Kind);

            try
            {
                handler(client, env);
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Handler '{env.Kind}' threw", ex);
            }
            finally
            {
                // Sent from here rather than per handler: a client that never hears this cannot tell a deliberate repeat from a retry.
                if (!string.IsNullOrEmpty(env.OpId))
                    SendTo(client, KmhProtocol.Kind.OpResult, new { op = env.OpId });
            }
        }

        // One line per window, because a briefly-racing client can burst dozens of early packets.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long WindowTicks)> _earlyDrops
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (int, long)>(StringComparer.OrdinalIgnoreCase);
        private static void NoteEarlyDrop(string username)
        {
            long now = DateTime.UtcNow.Ticks;
            var e = _earlyDrops.AddOrUpdate(username, (1, now), (_, cur) => (cur.Count + 1, cur.WindowTicks));
            if (e.Count == 1 || now - e.WindowTicks > TimeSpan.FromSeconds(10).Ticks)
            {
                if (e.Count > 1) ServerLog.Verbose($"Dropped {e.Count} early KMH packet(s) from {username} before handshake completed.");
                _earlyDrops[username] = (0, now);
            }
        }

        // The handshake always rides chat - it is how a client discovers the API at all; AllowChatTransportFallback governs FEATURE traffic only.
        internal static bool IsTransportControlKind(string kind)
            => kind == KmhProtocol.Kind.Hello || kind == KmhProtocol.Kind.HelloAck
            || kind == KmhProtocol.Kind.Ping  || kind == KmhProtocol.Kind.Pong;

        internal static bool ChatMayCarry(string kind)
            => IsTransportControlKind(kind) || Features.Transport.TransportConfig.Current.AllowChatTransportFallback;

        public static bool SendTo(ServerClient client, string kind, object data)
            => SendPrepared(client, kind, new Prepared(new KmhEnvelope(kind, data)));

        // One payload bound for many clients: serializing, encoding and framing happen here once, not once per client.
        private sealed class Prepared
        {
            internal Prepared(KmhEnvelope env) { Env = env; }
            internal readonly KmhEnvelope Env;
            internal string Wire;                                  // only a chat fallback or a fragment cut needs the string
            internal Features.Transport.KmhApiServer.Framed Frame; // the API path's encoded frame
            internal bool   FrameTried;                            // a payload that will not serialize must not be retried per client
            internal int    SplitLimit;                            // the frame budget Parts were cut to; 0 = nothing cut yet
            internal List<Features.Transport.KmhApiServer.Framed> Parts;

            internal string WireOnce() => Wire ?? (Wire = Env.Serialize());

            internal Features.Transport.KmhApiServer.Framed FrameOnce()
            {
                if (!FrameTried) { FrameTried = true; Frame = Features.Transport.KmhApiServer.Encode(Env); }
                return Frame;
            }

            // Peers negotiate the same budget in practice, so the cut is reused; a different one re-cuts and replaces it.
            internal List<Features.Transport.KmhApiServer.Framed> PartsFor(string kind, int limit)
            {
                if (Parts != null && SplitLimit == limit) return Parts;
                List<KmhEnvelope> cut = KmhFragments.Split(kind, WireOnce(), limit);
                if (cut == null) return null;
                Parts      = Features.Transport.KmhApiServer.EncodeAll(cut);
                SplitLimit = Parts == null ? 0 : limit;
                return Parts;
            }
        }

        private static bool SendPrepared(ServerClient client, string kind, Prepared prep)
        {
            if (client?.Listener == null)
            {
                ServerLog.Verbose($"SendTo '{kind}' dropped - no active listener");
                return false;
            }

            try
            {
                string user = client.GetData<UserFile>()?.Username;

                // Connection first, THEN encode: a peer on the chat fallback must not pay to build API bytes.
                SR api = SR.NotConnected;
                if (Features.Transport.KmhApiServer.IsConnected(client))
                {
                    Features.Transport.KmhApiServer.Framed framed = prep.FrameOnce();
                    api = framed == null ? SR.SerializationFailure
                                         : Features.Transport.KmhApiServer.Send(client, framed);
                }
                if (api == SR.Sent) return true;

                // Fragmenting is what lets a feature outgrow the frame ceiling without inventing its own chunking.
                if (api == SR.TooLarge)
                {
                    if (!Features.Transport.KmhApiServer.PeerSupportsFragments(client))
                    {
                        ServerLog.Warn($"Transport: '{kind}' is too large for {user}, whose client cannot reassemble fragments - update the client.");
                        return false;
                    }
                    var parts = prep.PartsFor(kind, Features.Transport.KmhApiServer.SafeFrameBytesFor(client));
                    if (parts == null) return false;
                    api = Features.Transport.KmhApiServer.SendFragments(client, parts);
                    if (api == SR.Sent)
                    {
                        ServerLog.Verbose($"Transport: '{kind}' to {user} sent as {parts.Count} fragment(s), {prep.Wire.Length} logical bytes.");
                        return true;
                    }
                }

                // A write that threw may still have reached the peer, so nothing is retried: snapshots return on the next refresh, and owed value is recovered by its own record.
                if (api != SR.NotConnected)
                {
                    ServerLog.Verbose($"Transport: '{kind}' to {user} failed mid-write ({api}) - not resent over chat, since the peer may already have it.");
                    return false;
                }

                if (!ChatMayCarry(kind))
                {
                    ServerLog.Verbose($"Transport: '{kind}' to {user} dropped - the API link is down and this server does not allow feature traffic over RWT chat.");
                    return false;
                }

                string wire = prep.WireOnce();
                // The chat fallback carries the same ceiling, so an oversized envelope must not go there either.
                if (wire.Length > MaxEnvelopeBytes)
                {
                    List<KmhEnvelope> chatParts = KmhFragments.Split(kind, wire, MaxEnvelopeBytes);
                    if (chatParts == null) return false;
                    foreach (KmhEnvelope part in chatParts)
                    {
                        client.Listener.EnqueuePacket(RwtCompat.ChatHeader, new PKT_Chat
                        { Username = KmhProtocol.SystemUsername, Message = part.Serialize(), IsCommand = false });
                    }
                    ServerLog.Verbose($"Transport: '{kind}' to {user} sent as {chatParts.Count} chat fragment(s).");
                    return true;
                }
                PKT_Chat pkt = new PKT_Chat
                {
                    Username  = KmhProtocol.SystemUsername,
                    Message   = wire,
                    IsCommand = false,
                };

                // A client dropping mid-send must fail this call, not throw out into a broadcast loop.
                client.Listener.EnqueuePacket(RwtCompat.ChatHeader, pkt);
                return true;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"SendTo '{kind}' failed: {ex.Message}");
                return false;
            }
        }

        // For state the player sees without opening a KMH window, where interest gating leaves the map stale.
        public static void BroadcastToVerified(string kind, Func<string, object> buildFor)
        {
            if (buildFor == null) return;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u)) continue;
                SendTo(c, kind, buildFor(u));
            }
        }

        // Only clients with the feature open, so a mutation costs no packets for everyone else.
        public static void BroadcastToInterested(string kind, Func<string, object> buildFor)
        {
            BroadcastToInterested(kind, null, buildFor);
        }

        // A shared key promises identical bytes, so the payload is built once per key; null falls back to reference equality.
        public static void BroadcastToInterested(string kind, Func<string, string> shareKey, Func<string, object> buildFor)
        {
            if (buildFor == null) return;

            object lastData = null; Prepared last = null; string lastKey = null;

            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u) || !KmhInterest.IsInterested(u, kind)) continue;

                string key = shareKey?.Invoke(u);
                bool reuse = last != null && key != null && key == lastKey;
                if (!reuse)
                {
                    object data = buildFor(u);
                    // A new payload object needs its own encoding; the same one back again reuses everything.
                    if (last == null || !ReferenceEquals(data, lastData))
                    {
                        lastData = data;
                        last     = new Prepared(new KmhEnvelope(kind, data));
                    }
                    lastKey = key;
                }
                SendPrepared(c, kind, last);
            }
        }

        // level is positive, negative or neutral.
        public static void Notify(ServerClient client, string level, string text)
            => SendTo(client, KmhProtocol.Kind.Notice, new { level, text });

        // False when offline, which is ordinary: they catch up on the client's next refresh.
        public static bool SendToUsername(string username, string kind, object data)
        {
            ServerClient c = ResolveClient(username);
            return c != null && SendTo(c, kind, data);
        }

        public static ServerClient ResolveClient(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, username, System.StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }
    }
}
