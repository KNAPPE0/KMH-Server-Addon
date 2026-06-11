using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.PlayerStats
{
    // Server-side handler for kmh.player_stats.* - counterpart to the patch-mod's
    // KMHPatch.Features.PlayerStats.PlayerStatsHandler
    //
    // Answers requests with whatever's in PlayerStatsStore. Future work: push snapshots unsolicited when stats
    // actually change (on treasury deposit / marketplace sale / quest completion / etc.) so clients don't have to
    // poll. The patch mod's 8s auto-refresh is the failover until we wire that
    internal static class PlayerStatsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.PlayerStatsRequest, OnRequest);
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
