using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.PlayerStats
{
    // Server-side handler for kmh.player_stats.* (counterpart to the client's PlayerStatsHandler). Answers requests
    // from PlayerStatsStore; the client's 8s auto-refresh covers updates until unsolicited push is wired.
    internal static class PlayerStatsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.PlayerStatsRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonyReport,       OnColonyReport);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonistRequest,    OnColonistRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonistRosterRequest, OnColonistRosterRequest);
        }

        // The Colonist Records board asks for the flattened roster of every colony's colonists.
        private static void OnColonistRosterRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ColonistRoster, PlayerStatsStore.BuildColonistRoster());
        }

        // Optional: broadcast a fresh snapshot to every connected verified client. Called from feature code that
        // mutates the store. (Wired up when the first mutating feature lands.)
        public static void BroadcastSnapshot()
        {
            PlayerStatsSnapshotAll();
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            // Always EnsurePlayer the requester first so a brand-new player who just connected and clicked Player
            // Leaderboard sees themselves in the list rather than an empty board
            PlayerStatsStore.EnsurePlayer(client?.GetData<UserFile>()?.Username);

            Dto.PlayerStatsSnapshot snapshot = PlayerStatsStore.BuildSnapshot();
            KmhRouter.SendTo(client, KmhProtocol.Kind.PlayerStatsSnapshot, snapshot);
            ServerLog.Verbose($"Sent player_stats.snapshot ({snapshot.Entries.Count} entries) to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }

        // Per-user rate limit so a modified client can't spam reports to force constant disk writes / work.
        private static readonly object _reportGate = new object();
        private static readonly Dictionary<string, long> _lastReportUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly long MinReportIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        // Client uploaded its colony summary + colonist. Attribute to the authenticated session, never the envelope.
        private static void OnColonyReport(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            long now = DateTime.UtcNow.Ticks;
            lock (_reportGate)
            {
                if (_lastReportUtcTicks.TryGetValue(username, out long last) && now - last < MinReportIntervalTicks) return;
                _lastReportUtcTicks[username] = now;
            }

            Dto.ColonyReport report = env?.DataAs<Dto.ColonyReport>();
            if (report == null) return;
            PlayerStatsStore.ApplyColonyReport(username, report);
            // Deliberately NO broadcast: open leaderboards auto-refresh on their own ~8s tick and on open.
            // Broadcasting on every client-timed report would amplify one client's uploads into a full snapshot
            // pushed to everyone - a needless fan-out / DoS lever.
        }

        // A client opened someone's card - send that player's full colonist profile (or an empty one if none).
        private static void OnColonistRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            string target = env?.GetString("username");
            if (string.IsNullOrEmpty(target)) return;
            Dto.ColonistProfile detail = PlayerStatsStore.GetColonist(target);
            KmhRouter.SendTo(client, KmhProtocol.Kind.ColonistProfile,
                new Dto.ColonistProfileEnvelope { Username = target, Detail = detail });
        }

        // Push the current snapshot to every connected verified client. Used by mutating features that want every
        // viewer to see a fresh leaderboard immediately (no polling)
        private static void PlayerStatsSnapshotAll()
        {
            Dto.PlayerStatsSnapshot snapshot = PlayerStatsStore.BuildSnapshot();
            foreach (ServerClient c in TCPNetwork.Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.PlayerStatsSnapshot, snapshot);
            }
        }
    }
}
