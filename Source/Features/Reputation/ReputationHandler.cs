using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Reputation
{
    // Pushes the reputation roster to clients for tier badges. Snapshot is the full (small) roster - sent on
    // request, on handshake, and after any quest action that moves a score
    internal static class ReputationHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ReputationRequest, (c, e) => SendSnapshotTo(c));
        }

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ReputationSnapshot, ReputationStore.BuildSnapshot());
        }

        public static void BroadcastSnapshot()
        {
            Dto.ReputationSnapshot snap = ReputationStore.BuildSnapshot();
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                if (string.IsNullOrEmpty(c.GetData<UserFile>()?.Username)) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.ReputationSnapshot, snap);
            }
        }
    }
}
