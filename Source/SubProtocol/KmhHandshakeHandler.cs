using System.Runtime.CompilerServices;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol
{
    // Server-side handshake: sends kmh.hello on connect (via Patch_PM_Chat_KmhSendHello, a postfix on
    // PM_Chat.SendLoginChatMessages) and handles the client's kmh.hello.ack. A successful handshake means the client
    // has KMH-Patch loaded.
    internal static class KmhHandshakeHandler
    {
        // Per-connection compatible-handshake set (leak-free: entries drop when the ServerClient is GC'd). The router
        // gates all feature traffic on this so a wrong-version/modified client can't half-use v1.2.0 flows.
        private static readonly ConditionalWeakTable<ServerClient, object> _compatible = new ConditionalWeakTable<ServerClient, object>();
        private static readonly object CompatMarker = new object();
        internal static bool IsCompatible(ServerClient client) => client != null && _compatible.TryGetValue(client, out _);
        // Marked by the chat hello.ack (version-matched) and by the API transport handshake (also version-checked).
        internal static void MarkCompatible(ServerClient client) { if (client != null) _compatible.AddOrUpdate(client, CompatMarker); }

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.HelloAck, OnHelloAck);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.Ping,     OnPing);
        }

        // Called from PM_Logins.FinishPostLogin via the Harmony postfix on PM_Chat.SendLoginChatMessages. Stock-RWT
        // clients see this as an invisible chat message under a zero-width-prefixed username - cosmetically odd,
        // functionally harmless. Patched clients silently intercept it
        public static void SendHelloTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username ?? "?";

            // When the API transport is on, advertise it + mint this user's one-time token (sent only over this
            // authenticated channel, never logged). Off by default -> fields are empty and inert.
            Features.Transport.TransportConfig tcfg = Features.Transport.TransportConfig.Current;
            bool   apiOn    = tcfg.EnableKmhApiTransport;
            string apiToken = apiOn ? Features.Transport.KmhApiServer.IssueToken(username) : "";

            bool sent = KmhRouter.SendTo(client, KmhProtocol.Kind.Hello, new
            {
                v           = KmhProtocol.CurrentVersion,
                build       = KmhProtocol.BuildVersion,
                server_name = KmhServerIdentity.Name,
                api_enabled = apiOn,
                api_host    = apiOn ? (tcfg.PublicApiHost ?? "") : "",   // "" -> client dials the RWT IP it connected to
                api_port    = apiOn ? tcfg.KmhApiPort : 0,
                api_token   = apiToken,
                allow_chat_fallback = tcfg.AllowChatTransportFallback,   // owner policy: false = clients must not tunnel features over chat

                disabled    = string.Join(",", Features.FeaturesConfig.Current.DisabledList()),
                debug_uplink = tcfg.DebugLogging,   // server debug on -> clients auto-send their KMH logs back
            });

            if (sent)
            {
                ServerLog.Verbose($"Sent kmh.hello (v{KmhProtocol.CurrentVersion}{(apiOn ? ", api" : "")}) to {username}");
            }
        }

        private static void OnHelloAck(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username ?? "?";
            int    clientVersion = env?.GetInt("v", 0) ?? 0;

            if (clientVersion == KmhProtocol.CurrentVersion)
            {
                _compatible.AddOrUpdate(client, CompatMarker);   // unlocks feature traffic in the router gate
                ServerLog.Info($"Handshake complete with {username} (client v{clientVersion})");

                // Push initial state for features the patch mod would otherwise need to request explicitly.
                // LinkedAccounts is the best example - every dialog renders usernames via
                // LinkedAccountsCache.Format, and we want that cache populated from frame one rather than after the
                // first 8s auto-refresh tick
                try
                {
                    Features.FeaturesConfig f = Features.FeaturesConfig.Current;
                    Features.LinkedAccounts.LinkedAccountsHandler.SendSnapshotTo(client);
                    Features.Reputation.ReputationHandler.SendSnapshotTo(client);
                    Features.Enforcement.EnforcementHandler.SendSnapshotTo(client);
                    if (f.LivingWorld) Features.World.WorldHandler.SendSnapshotTo(client);
                    if (f.Auctions)    Features.Auctions.AuctionHandler.SendSnapshotTo(client);
                    if (f.WantBoard)   Features.WantBoard.WantHandler.SendSnapshotTo(client);
                    // Deliver anything that piled up while they were offline (auction/marketplace outcomes).
                    Features.Notifications.NotificationHandler.DeliverQueuedTo(client);
                }
                catch (System.Exception ex)
                {
                    ServerLog.Error("Initial snapshot push failed", ex);
                }
            }
            else
            {
                ServerLog.Warn(
                    $"Version mismatch with {username}: client v{clientVersion}, server v{KmhProtocol.CurrentVersion}");
            }
        }

        // Diagnostic round-trip - client sends Ping, server replies Pong ("is the protocol alive?").
        private static void OnPing(ServerClient client, KmhEnvelope env)
        {
            KmhRouter.SendTo(client, KmhProtocol.Kind.Pong, null);
            ServerLog.Verbose($"Pong sent to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
