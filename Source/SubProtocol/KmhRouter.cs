using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol
{
    // Server-side router for KMH sub-protocol traffic - counterpart to the patch mod's
    // KMHPatch.SubProtocol.KmhDispatcher
    //
    // Inbound flow: Patch_PM_Chat_KmhIntercept (Harmony Prefix on PM_Chat.Receive) identifies KMH-tagged chat by
    // Username and calls HandleInbound; we parse the envelope and dispatch to the registered handler. Handlers
    // receive both the ServerClient (for authenticated identity + reply target) and the envelope
    //
    // Outbound flow: feature code calls SendTo(client, kind, data); we serialize an envelope, wrap it in PKT_Chat
    // tagged with SystemUsername, and enqueue on that client's listener
    //
    // Security note: handlers must trust ServerClient.UserFile.Username as the authenticated identity, NOT anything
    // inside the envelope. A malicious client could fake KMH-tagged chat but they can only ever operate as
    // themselves - the protocol routing doesn't grant impersonation
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

            if (!Handlers.TryGetValue(env.Kind, out Action<ServerClient, KmhEnvelope> handler))
            {
                ServerLog.Verbose($"No handler for kind '{env.Kind}' from {client?.GetData<UserFile>()?.Username ?? "?"}");
                return;
            }

            try
            {
                handler(client, env);
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Handler '{env.Kind}' threw", ex);
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
                PKT_Chat    pkt = new PKT_Chat
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

        // Transient toast to a client. level is positive / negative / neutral.
        public static void Notify(ServerClient client, string level, string text)
            => SendTo(client, KmhProtocol.Kind.Notice, new { level, text });

        // Look up a verified connected client by username and send to them. Returns false silently if the named
        // player isn't currently online (most-common case for "tell the other party about a state change they
        // didn't initiate" - they'll catch up on the patch mod's 8s auto-refresh tick if they reconnect
        // mid-session)
        public static bool SendToUsername(string username, string kind, object data)
        {
            if (string.IsNullOrEmpty(username)) return false;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, username, System.StringComparison.OrdinalIgnoreCase))
                    return SendTo(c, kind, data);
            }
            return false;
        }
    }
}
