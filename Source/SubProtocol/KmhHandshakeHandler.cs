using System.Collections.Generic;
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

        internal static void HelloAckForTest(ServerClient client, KmhEnvelope env) => OnHelloAck(client, env);
        internal static void MarkCompatible(ServerClient client) { if (client != null) _compatible.AddOrUpdate(client, CompatMarker); }

        // What the CLIENT says it can do; weak-keyed like the table above so it dies with the session.
        private static readonly ConditionalWeakTable<ServerClient, HashSet<string>> _clientCaps =
            new ConditionalWeakTable<ServerClient, HashSet<string>>();

        internal static bool ClientDeclares(ServerClient client, string capability)
            => client != null && !string.IsNullOrEmpty(capability)
               && _clientCaps.TryGetValue(client, out HashSet<string> caps) && caps.Contains(capability);

        private static void RecordClientCaps(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            // An older client sends no list at all, which reads as declaring nothing.
            StoreClientCaps(client, env?.GetStringList("client_capabilities") ?? new List<string>());
        }

        internal static void StoreClientCaps(ServerClient client, IEnumerable<string> declared)
        {
            var caps = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string c in declared)
                if (!string.IsNullOrWhiteSpace(c) && caps.Count < 64) caps.Add(c.Trim());
            // Two hello acks for one connection can race in from the chat path and the API link.
            _clientCaps.AddOrUpdate(client, caps);
        }

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.HelloAck, OnHelloAck);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.Ping,     OnPing);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.Incompatible, OnIncompatible);
        }

        // A stock RWT client sees this as an invisible chat line rather than an error, which is why it rides chat.
        public static void SendHelloTo(ServerClient client)
        {
            // Checked first: a banned player must not learn this server's capabilities, mod list or API port.
            if (Features.Moderation.BanGate.RefuseIfBanned(client)) return;

            if (Features.Enforcement.EnforcementAdmission.IsHeld(client))
            {
                KmhRouter.SendTo(client, KmhProtocol.Kind.Hello, Features.Enforcement.EnforcementAdmission.Bind(client, new
                {
                    v = KmhProtocol.CurrentVersion, build = KmhProtocol.BuildVersion,
                    server_id = KmhServerIdentity.Id, server_name = KmhServerIdentity.Name,
                    capabilities = KmhCapabilities.Manifest, admission_required = true, api_enabled = false,
                    enforcement_enabled = Features.Enforcement.EnforcementConfig.Current.Enabled
                }));
                return;
            }
            string username = client?.GetData<UserFile>()?.Username ?? "?";

            // Apply the authoritative config before any hello, or the client holds defaults until a reload.
            if (!Features.Comms.CommsStartup.Ready)
            {
                ServerLog.Warn($"Handshake from {username} arrived before Communications was ready - applying its "
                             + "configuration first so the hello carries real values, not defaults.");
                Features.Comms.CommsStartup.ApplyAtBoot();
            }

            // The token goes only over this authenticated channel, is never logged, and belongs to this session.
            Features.Transport.TransportConfig tcfg = Features.Transport.TransportConfig.Current;
            Features.Chat.ChatConfig           ccfg = Features.Chat.ChatConfig.Current;
            // The listener that exists, not the configured one: a failed bind would send clients to a dead socket.
            bool   apiOn    = tcfg.EnableKmhApiTransport && Features.Transport.KmhApiServer.Running;
            string apiToken = apiOn ? Features.Transport.KmhApiServer.IssueToken(client) : "";

            bool sent = KmhRouter.SendTo(client, KmhProtocol.Kind.Hello, new
            {
                v           = KmhProtocol.CurrentVersion,
                build       = KmhProtocol.BuildVersion,
                server_name = KmhServerIdentity.Name,
                // The identity this server already had; a browser-specific one would be a second truth to reconcile.
                server_id   = KmhServerIdentity.Id,
                api_enabled = apiOn,
                api_host    = apiOn ? (tcfg.PublicApiHost ?? "") : "",   // "" -> client dials the RWT IP it connected to
                api_port    = apiOn ? Features.Transport.KmhApiServer.Port : 0,   // bound, not configured
                api_token   = apiToken,
                allow_chat_fallback = tcfg.AllowChatTransportFallback,   // owner policy: false = clients must not tunnel features over chat

                disabled    = string.Join(",", Features.FeaturesConfig.Current.DisabledList()),
                capabilities = KmhCapabilities.Manifest,   // additive; older clients ignore it
                debug_uplink = tcfg.Advanced.DebugLogging,   // a request only: each player's own sharing setting decides

                // Additive: an older client ignores anything it does not know, so fields are only ever appended.
                theme_accent  = ccfg.Theme.Accent     ?? "",
                theme_server  = ccfg.Theme.ServerChat ?? "",
                theme_guild   = ccfg.Theme.GuildChat  ?? "",
                theme_dm      = ccfg.Theme.DirectMsg  ?? "",
                theme_discord = ccfg.Theme.Discord    ?? "",
                theme_name       = ccfg.Theme.NameNormal  ?? "",
                theme_text       = ccfg.Theme.TextNormal  ?? "",
                theme_name_dc    = ccfg.Theme.NameDiscord ?? "",
                theme_text_dc    = ccfg.Theme.TextDiscord ?? "",
                marker_mine      = ccfg.Theme.MarkerMine   ?? "",
                marker_theirs    = ccfg.Theme.MarkerTheirs ?? "",
                discord_marker   = Features.Chat.ChatConfig.ClampMarker(ccfg.Theme.DiscordMarker),
                staff_badges     = Features.Simulation.SimulatedAccounts.WithBadge(Features.Identity.StaffConfig.Current.BadgeWire()),
                staff_roles      = Features.Simulation.SimulatedAccounts.WithRoles(Features.Identity.StaffConfig.Current.RoleWire()),
                sim_ranked       = !Features.Simulation.SimulatedAccounts.ExcludesFromRankings,
                // A client ignores an older revision, so a slow transport cannot overwrite newer presentation.
                comms_rev        = Features.Comms.CommsStartup.Revision,
                // One ceiling, decided here and enforced on the client, or the two disagree and uploads fail silently.
                max_image_bytes  = ccfg.MaxImageBytes,
                // The plain url still rides alongside the media id, so an older client that ignores these still works.
                media_resolver     = Features.Media.MediaConfig.Current.Images.ResolverEnabled,
                max_video_seconds  = Features.Media.MediaConfig.Current.Video.MaxSeconds,
                yt_playback        = Features.Media.MediaConfig.Current.Watch.PagePlayback,
                yt_max_height      = Features.Media.MediaConfig.Current.Watch.MaxHeight,
                yt_max_seconds     = Features.Media.MediaConfig.Current.Watch.MaxSeconds,
                yt_server          = Features.Media.KmhVideoRelay.Running,
                // "off", "local" or "global", so the client dialog states where a report goes before it is written.
                reports            = Features.Reports.ReportHandler.Mode,
                // An older client ignores it; a newer one sends destinations only to a server that reads them.
                reports_v          = Features.Reports.ReportHandler.WireVersion,
                // The owner-stated game version, so a client on another one stays off before anything is applied.
                rimworld           = Features.Global.GlobalStatusProbe.RimWorldVersion(),
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
            // Refused at login: never compatible, and owed deliveries are not replayed to a connection on its way out.
            if (Features.Enforcement.EnforcementAdmission.WasRefused(client)) return;

            // A client that names no version is a KMH from before this field, built for 1.6 only: a 1.6 server takes it
            // as before, any other server refuses it. RWT's own login carries no RimWorld version to check instead.
            string clientGame = env?.GetString("rimworld");
            if (RefusesGame(clientGame, Features.Global.GlobalStatusProbe.RimWorldVersion()))
            {
                RefuseGameVersion(client, username, clientVersion, clientGame);
                return;
            }

            if (Judge(clientVersion, Features.Enforcement.EnforcementAdmission.IsHeld(client))
                == HandshakeVerdict.Compatible)
            {
                RecordClientCaps(client, env);
                // Several acks per connection are normal (admission, full hello, API link); only the first logs at Info.
                bool firstAck = !IsCompatible(client);
                _compatible.AddOrUpdate(client, CompatMarker);   // unlocks feature traffic in the router gate
                // Logged before the admission hand-off, or a held client's handshake is never reported at all.
                if (firstAck)
                    ServerLog.Info($"Colony Nexus compatibility handshake complete with {username} (client v{clientVersion})");
                else
                    ServerLog.Verbose($"Colony Nexus transport ready for {username} - a further link acked on a connection already handshaken");
                if (Features.Enforcement.EnforcementAdmission.HandleHello(client)) return;

                // The first hello goes out before any ack exists, so a slow client can miss it entirely.
                if (firstAck) SendHelloTo(client);

                // Every dialog formats usernames through the linked-accounts cache, so an empty one shows raw names.
                try
                {
                    Features.FeaturesConfig f = Features.FeaturesConfig.Current;
                    // With the new-save reset on, a first link waits for the save to be known: its colony report replays
                    // them after the reset decision. A replay arriving twice costs a message, never goods.
                    if (!Features.Economy.EconomyConfig.Current.ResetEconomyOnNewSave || Features.Delivery.DeliveryHandler.SaveKnownOn(client))
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
            else RefuseIncompatible(client, username, clientVersion);
        }

        // Ends the hold on the client's own word. Guarded, or refusing twice re-enters Fail on a disconnecting client.
        private static void OnIncompatible(ServerClient client, KmhEnvelope env)
        {
            if (client == null || IsCompatible(client)) return;

            // Any number but this server's means the hello it judged was not the hello this server sent.
            int heard = env?.GetInt("server_v", 0) ?? 0;
            if (heard != 0 && heard != KmhProtocol.CurrentVersion)
                ServerLog.Warn($"Colony Nexus: a client reports hearing protocol v{heard} from this server, which announces "
                             + $"v{KmhProtocol.CurrentVersion} - something between the two rewrote the handshake.");

            // The client refused on the game version itself; it says so rather than leave a protocol line in the log.
            if (env?.GetString("reason") == GameVersionReason)
            {
                RefuseGameVersion(client, client.GetData<UserFile>()?.Username ?? "?", env.GetInt("client_v", 0),
                                  env.GetString("client_game") ?? "");
                return;
            }

            // Echo the version the client HEARD, not ours, or the refusal reads "server requires v3; you have v3".
            RefuseIncompatible(client, client.GetData<UserFile>()?.Username ?? "?",
                               env?.GetInt("client_v", 0) ?? 0, heard);
        }

        internal const string GameVersionReason = "game_version";

        // Both sides must name a version for it to count: an unreadable or missing one is never a refusal.
        internal static bool GameVersionDiffers(string clientGame, string serverGame)
        {
            System.Text.RegularExpressions.Match c = System.Text.RegularExpressions.Regex.Match(clientGame ?? "", @"^\s*(\d+)\.(\d+)");
            System.Text.RegularExpressions.Match s = System.Text.RegularExpressions.Regex.Match(serverGame ?? "", @"^\s*(\d+)\.(\d+)");
            if (!c.Success || !s.Success) return false;
            return c.Groups[1].Value != s.Groups[1].Value || c.Groups[2].Value != s.Groups[2].Value;
        }

        // Every KMH that does not state a version predates 1.7 support and was only ever built and tested for 1.6.
        internal const string PresumedForUnstated = "1.6";

        internal static bool RefusesGame(string clientGame, string serverGame)
            => GameVersionDiffers(StatedOrPresumed(clientGame), serverGame);

        internal static string StatedOrPresumed(string clientGame)
            => System.Text.RegularExpressions.Regex.IsMatch(clientGame ?? "", @"^\s*\d+\.\d+") ? clientGame : PresumedForUnstated;

        // Disconnects even without enforcement: two RimWorld versions are not known to share RWT's world safely.
        internal static void RefuseGameVersion(ServerClient client, string username, int clientVersion, string clientGame)
        {
            bool enforced = Features.Enforcement.EnforcementAdmission.IsHeld(client);
            string serverGame = Features.Global.GlobalStatusProbe.RimWorldVersion();
            bool presumed = StatedOrPresumed(clientGame) != clientGame;
            string theirs = presumed ? $"{PresumedForUnstated} (presumed: this Colony Nexus client predates the version check)" : clientGame;
            ServerLog.Warn($"RimWorld version mismatch with {username}: client RimWorld {theirs}, this server is RimWorld "
                         + $"{serverGame} (Config/Mods.json RimWorldVersion) - refused and disconnected");
            KmhRouter.SendTo(client, KmhProtocol.Kind.Incompatible,
                new { server_v = KmhProtocol.CurrentVersion, client_v = clientVersion, enforced,
                      reason = GameVersionReason, server_game = serverGame });
            string why = $"client runs RimWorld {theirs}; this server is RimWorld {serverGame}";
            if (enforced) Features.Enforcement.EnforcementAdmission.Fail(client, why);
            else client?.Listener?.MarkForDisconnect();
        }

        internal enum HandshakeVerdict { Compatible, RefuseAndHold, RefuseAndDisable }

        // Declared version only, never a timeout, capability list or hydration progress; pure so the matrix tests offline.
        internal static HandshakeVerdict Judge(int clientVersion, bool enforced)
            => clientVersion == KmhProtocol.CurrentVersion ? HandshakeVerdict.Compatible
             : enforced ? HandshakeVerdict.RefuseAndHold      // fail closed: the owner asked for enforcement
                        : HandshakeVerdict.RefuseAndDisable;  // KMH off for the session, RWT unaffected

        // heardVersion (0 = unsaid) is echoed back, so the refusal names the number the decision was made on.
        internal static void RefuseIncompatible(ServerClient client, string username, int clientVersion,
                                                int heardVersion = 0)
        {
            bool enforced = Features.Enforcement.EnforcementAdmission.IsHeld(client);
            string heardNote = heardVersion != 0 && heardVersion != KmhProtocol.CurrentVersion
                ? $", and reports hearing v{heardVersion} from it" : "";
            ServerLog.Warn($"Colony Nexus protocol mismatch with {username}: client v{clientVersion}, server "
                         + $"v{KmhProtocol.CurrentVersion}{heardNote}"
                         + $"{(enforced ? " - Config Enforcement requires a compatible client, refusing admission" : " - Colony Nexus stays off for this session")}");

            // Before any disconnect, so the player reads a reason rather than an unexplained drop.
            KmhRouter.SendTo(client, KmhProtocol.Kind.Incompatible,
                new { server_v = heardVersion != 0 ? heardVersion : KmhProtocol.CurrentVersion,
                      client_v = clientVersion, enforced });

            // Fail-closed only where the owner asked for it: a non-enforcing server has no reason to lock them out.
            if (Judge(clientVersion, enforced) == HandshakeVerdict.RefuseAndHold)
                Features.Enforcement.EnforcementAdmission.Fail(client,
                    $"client protocol v{clientVersion} cannot satisfy Config Enforcement on protocol v{KmhProtocol.CurrentVersion}");
        }

        private static void OnPing(ServerClient client, KmhEnvelope env)
        {
            KmhRouter.SendTo(client, KmhProtocol.Kind.Pong, null);
            ServerLog.Verbose($"Pong sent to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
