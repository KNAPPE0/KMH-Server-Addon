using System.Runtime.CompilerServices;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol
{
    // A completed handshake is the only proof that a client has KMH-Patch loaded.
    internal static class KmhHandshakeHandler
    {
        // The router gates every feature on this, so a wrong-version or modified client cannot half-use newer flows.
        private static readonly ConditionalWeakTable<ServerClient, object> _compatible = new ConditionalWeakTable<ServerClient, object>();
        private static readonly object CompatMarker = new object();
        internal static bool IsCompatible(ServerClient client) => client != null && _compatible.TryGetValue(client, out _);
        internal static void MarkCompatible(ServerClient client) { if (client != null) _compatible.AddOrUpdate(client, CompatMarker); }

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.HelloAck, OnHelloAck);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.Ping,     OnPing);
        }

        // A stock RWT client sees this as an invisible chat line rather than an error, which is why it rides chat.
        public static void SendHelloTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username ?? "?";

            // Never build a hello before the authoritative config is applied, or the client holds defaults until a reload.
            if (!Features.Comms.CommsStartup.Ready)
            {
                ServerLog.Warn($"Handshake from {username} arrived before Communications was ready - applying its "
                             + "configuration first so the hello carries real values, not defaults.");
                Features.Comms.CommsStartup.ApplyAtBoot();
            }

            // The token goes only over this authenticated channel, is never logged, and belongs to this session.
            Features.Transport.TransportConfig tcfg = Features.Transport.TransportConfig.Current;
            Features.Chat.ChatConfig           ccfg = Features.Chat.ChatConfig.Current;
            // The listener that exists, not the one config asked for: a failed bind still sent clients at a dead socket.
            bool   apiOn    = tcfg.EnableKmhApiTransport && Features.Transport.KmhApiServer.Running;
            string apiToken = apiOn ? Features.Transport.KmhApiServer.IssueToken(client) : "";

            bool sent = KmhRouter.SendTo(client, KmhProtocol.Kind.Hello, new
            {
                v           = KmhProtocol.CurrentVersion,
                build       = KmhProtocol.BuildVersion,
                server_name = KmhServerIdentity.Name,
                api_enabled = apiOn,
                api_host    = apiOn ? (tcfg.PublicApiHost ?? "") : "",   // "" -> client dials the RWT IP it connected to
                api_port    = apiOn ? Features.Transport.KmhApiServer.Port : 0,   // bound, not configured
                api_token   = apiToken,
                allow_chat_fallback = tcfg.AllowChatTransportFallback,   // owner policy: false = clients must not tunnel features over chat

                disabled    = string.Join(",", Features.FeaturesConfig.Current.DisabledList()),
                capabilities = KmhCapabilities.Manifest,   // additive; older clients ignore it
                debug_uplink = tcfg.DebugLogging,   // server debug on -> clients auto-send their KMH logs back

                // Additive: an older client ignores anything it does not know, so fields are only ever appended.
                theme_accent  = ccfg.ThemeAccent     ?? "",
                theme_server  = ccfg.ThemeServerChat ?? "",
                theme_guild   = ccfg.ThemeGuildChat  ?? "",
                theme_dm      = ccfg.ThemeDirectMsg  ?? "",
                theme_discord = ccfg.ThemeDiscord    ?? "",
                theme_name       = ccfg.ThemeNameNormal  ?? "",
                theme_text       = ccfg.ThemeTextNormal  ?? "",
                theme_name_dc    = ccfg.ThemeNameDiscord ?? "",
                theme_text_dc    = ccfg.ThemeTextDiscord ?? "",
                marker_mine      = ccfg.MarkerMine   ?? "",
                marker_theirs    = ccfg.MarkerTheirs ?? "",
                discord_marker   = Features.Chat.ChatConfig.ClampMarker(ccfg.DiscordMarker),
                staff_badges     = Features.Identity.StaffConfig.Current.BadgeWire(),
                staff_roles      = Features.Identity.StaffConfig.Current.RoleWire(),
                // A client ignores an older revision, so a slow transport cannot overwrite newer presentation.
                comms_rev        = Features.Comms.CommsStartup.Revision,
                // One ceiling, decided here and enforced on the client, or the two disagree and uploads fail silently.
                max_image_bytes  = ccfg.MaxImageBytes,
                // The plain url still rides alongside the media id, so an older client that ignores these still works.
                media_resolver     = Features.Media.MediaConfig.Current.ServerMediaResolverEnabled,
                max_video_seconds  = Features.Media.MediaConfig.Current.MaxVideoSeconds,
                yt_playback        = Features.Media.MediaConfig.Current.WatchPagePlayback,
                yt_max_height      = Features.Media.MediaConfig.Current.WatchMaxHeight,
                yt_max_seconds     = Features.Media.MediaConfig.Current.WatchMaxSeconds,
                yt_server          = Features.Media.KmhVideoRelay.Running,
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
                // Two acks on one connection is normal, not a bug: the chat carrier first, then the API link.
                bool firstAck = !IsCompatible(client);
                _compatible.AddOrUpdate(client, CompatMarker);   // unlocks feature traffic in the router gate
                if (firstAck)
                    ServerLog.Info($"KMH compatibility handshake complete with {username} (client v{clientVersion})");
                else
                    ServerLog.Info($"KMH transport ready for {username} - a second link acked on a connection already handshaken");

                // The first hello goes out before any ack exists, so a slow client can miss it entirely.
                if (firstAck) SendHelloTo(client);

                // Every dialog formats usernames through the linked-accounts cache, so an empty one shows raw names.
                try
                {
                    Features.FeaturesConfig f = Features.FeaturesConfig.Current;
                    Features.Delivery.DeliveryHandler.ReplayOwed(client);
                    Features.LinkedAccounts.LinkedAccountsHandler.SendSnapshotTo(client);
                    Features.Reputation.ReputationHandler.SendSnapshotTo(client);
                    Features.Enforcement.EnforcementHandler.SendSnapshotTo(client);
                    if (f.LivingWorld) Features.World.WorldHandler.SendSnapshotTo(client);
                    if (f.Auctions)    Features.Auctions.AuctionHandler.SendSnapshotTo(client);
                    if (f.WantBoard)   Features.WantBoard.WantHandler.SendSnapshotTo(client);
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

        private static void OnPing(ServerClient client, KmhEnvelope env)
        {
            KmhRouter.SendTo(client, KmhProtocol.Kind.Pong, null);
            ServerLog.Verbose($"Pong sent to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
