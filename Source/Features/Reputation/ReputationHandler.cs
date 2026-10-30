using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Reputation
{
    internal static class ReputationHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ReputationRequest, (c, e) => SendSnapshotTo(c));
        }

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ReputationSnapshot, ClientSnapshot());
        }

        private static Dto.ReputationSnapshot ClientSnapshot()
            => Leaderboards.LeaderboardEngine.ForClients(ReputationStore.BuildSnapshot(), Leaderboards.LeaderboardEngine.BannedNow());

        public static void BroadcastSnapshot()
        {
            Dto.ReputationSnapshot snap = ClientSnapshot();
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                if (string.IsNullOrEmpty(c.GetData<UserFile>()?.Username)) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.ReputationSnapshot, snap);
            }
        }
    }
}
