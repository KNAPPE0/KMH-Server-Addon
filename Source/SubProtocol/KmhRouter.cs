using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol
{
    // Server-side router for KMH sub-protocol traffic. Security: handlers must trust ServerClient.UserFile.Username as
    // identity, never an envelope field - a client can fake KMH chat but only ever as themselves (no impersonation).
    public static class KmhRouter
    {
        private static readonly Dictionary<string, Action<ServerClient, KmhEnvelope>> Handlers
            = new Dictionary<string, Action<ServerClient, KmhEnvelope>>();

        // Generous-but-bounded cap. KMH envelopes can carry feature payloads (treasury entries, marketplace
        // listings) larger than the 512-char chat limit, but we still don't want a malicious or buggy client to
        // push multi-MB envelopes through us
        public const int MaxEnvelopeBytes = 64 * 1024;

        public static void RegisterHandler(string kind, Action<ServerClient, KmhEnvelope> handler)
        {
            if (string.IsNullOrEmpty(kind))
            {
                ServerLog.Warn("RegisterHandler called with null/empty kind");
                return;
            }
            // Surface accidental clobbers. Core handlers each register a distinct kind exactly once at bootstrap,
            // so a duplicate here means either a double-register bug or an extension reaching past the SDK guard -
            // either way the admin should see it
            if (Handlers.ContainsKey(kind))
                ServerLog.Warn($"Handler for kind '{kind}' is being overwritten - previous registration replaced");
            Handlers[kind] = handler;
            ServerLog.Verbose($"Registered handler '{kind}'");
        }

        // True if a handler is already registered for this kind. Used by the SDK host to refuse extension
        // registrations that would collide with a core kmh.* handler or with an earlier extension's kind
        public static bool IsRegistered(string kind)
            => !string.IsNullOrEmpty(kind) && Handlers.ContainsKey(kind);

        // Called from the chat-intercept Harmony patch when an inbound message is identified as KMH protocol. Never
        // throws - handler errors are caught and logged so a bad message can't take down the chat pipeline
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

            KmhEnvelope env = KmhEnvelope.TryParse(pkt.Message);
            if (env == null || string.IsNullOrEmpty(env.Kind))
            {
                ServerLog.Warn($"Malformed envelope from {client?.GetData<UserFile>()?.Username ?? "?"}");
                return;
            }
            HandleInbound(client, env);
        }

        // Dispatch a parsed envelope - shared by the chat path and the KMH API transport. Never throws.
        public static void HandleInbound(ServerClient client, KmhEnvelope env)
        {
            if (env == null || string.IsNullOrEmpty(env.Kind)) return;

            // Protocol enforcement: before a compatible handshake only ack + ping pass; all feature/economy traffic is
            // dropped, so a wrong-version or modified client that ignores its own gate still can't half-use v1.2.0 flows.
            if (env.Kind != KmhProtocol.Kind.HelloAck && env.Kind != KmhProtocol.Kind.Ping
                && !KmhHandshakeHandler.IsCompatible(client))
            {
                NoteEarlyDrop(client?.GetData<UserFile>()?.Username ?? "?");
                return;
            }

            // Owner feature switches: drop a disabled feature's requests. The client is told at handshake and shows it
            // as disabled, so a normal client won't reach here; this is the server-side block behind that.
            string feature = Features.FeaturesConfig.FeatureForKind(env.Kind);
            if (feature != null && !Features.FeaturesConfig.Current.IsEnabled(feature))
            {
                ServerLog.Verbose($"Blocked '{env.Kind}' - {feature} disabled by server config");
                return;
            }

            if (!Handlers.TryGetValue(env.Kind, out Action<ServerClient, KmhEnvelope> handler))
            {
                ServerLog.Verbose($"No handler for kind '{env.Kind}' from {client?.GetData<UserFile>()?.Username ?? "?"}");
                return;
            }

            // Note the client is active in this feature, so BroadcastToInterested pushes updates only to viewers.
            KmhInterest.Touch(client?.GetData<UserFile>()?.Username, env.Kind);

            try
            {
                handler(client, env);
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Handler '{env.Kind}' threw", ex);
            }
        }

        // Rate-limited pre-handshake drop accounting: count per user, log ONE compact line per 10s window instead of
        // a wall of per-packet warnings (a briefly-racing client can burst dozens of early packets).
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

        // Send a typed message to a specific client. Use for replies and per-client pushes (kmh.hello, snapshot
        // pushes, etc.)
        public static bool SendTo(ServerClient client, string kind, object data)
        {
            if (client?.Listener == null)
            {
                ServerLog.Verbose($"SendTo '{kind}' dropped - no active listener");
                return false;
            }

            try
            {
                KmhEnvelope env = new KmhEnvelope(kind, data);

                // prefer the API transport if this client is on it; else chat
                if (Features.Transport.KmhApiServer.TrySend(client.GetData<UserFile>()?.Username, env))
                    return true;

                PKT_Chat pkt = new PKT_Chat
                {
                    Username  = KmhProtocol.SystemUsername,
                    Message   = env.Serialize(),
                    IsCommand = false,
                };

                // Guard serialize + enqueue: a client dropping mid-send (or a payload that won't serialize) must
                // surface as a failed SendTo, not an exception escaping into a broadcast loop or sweeper
                client.Listener.EnqueuePacket(RwtCompat.ChatHeader, pkt);
                return true;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"SendTo '{kind}' failed: {ex.Message}");
                return false;
            }
        }

        // Push a per-caller snapshot only to verified clients currently interested in this kind's feature (they have
        // its dialog open and polling). Replaces broadcast-to-everyone on mutation: same instant update for viewers,
        // no wasted packets or client-side deserialization for the rest. buildFor returns each recipient's payload.
        public static void BroadcastToInterested(string kind, Func<string, object> buildFor)
        {
            if (buildFor == null) return;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u) || !KmhInterest.IsInterested(u, kind)) continue;
                SendTo(c, kind, buildFor(u));
            }
        }

        // Transient toast to a client. level is positive / negative / neutral.
        public static void Notify(ServerClient client, string level, string text)
            => SendTo(client, KmhProtocol.Kind.Notice, new { level, text });

        // Look up a verified connected client by username and send to them. Returns false silently if the named
        // player isn't currently online (most-common case for "tell the other party about a state change they
        // didn't initiate" - they'll catch up on the patch mod's 8s auto-refresh tick if they reconnect
        // mid-session)
        public static bool SendToUsername(string username, string kind, object data)
        {
            ServerClient c = ResolveClient(username);
            return c != null && SendTo(c, kind, data);
        }

        // verified online client by username, or null. Used by SendToUsername and the KMH API transport.
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
