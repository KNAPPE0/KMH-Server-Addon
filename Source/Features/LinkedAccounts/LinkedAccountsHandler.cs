using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.LinkedAccounts
{
    // Server-side handler for kmh.linked_accounts.* - counterpart to
    // KMHPatch.Features.LinkedAccounts.LinkedAccountsHandler
    //
    // Only the request kind is wire-driven in v1. Future link/unlink mutations (driven by a Discord bot bridge or
    // admin commands) call LinkedAccountsStore.SetLink/Unlink directly, then invoke BroadcastSnapshot to push the
    // change to every connected patch-mod client
    internal static class LinkedAccountsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.LinkedAccountsRequest, OnRequest);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        // Send the snapshot to a specific client. Used on request + when a freshly-handshaken client first
        // connects
        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            Dto.LinkedAccountsSnapshot snapshot = LinkedAccountsStore.BuildSnapshot();
            KmhRouter.SendTo(client, KmhProtocol.Kind.LinkedAccountsSnapshot, snapshot);
        }

        // Push to every verified client. Call from SetLink / Unlink wrappers in future link-management features
        public static void BroadcastSnapshot()
        {
            Dto.LinkedAccountsSnapshot snapshot = LinkedAccountsStore.BuildSnapshot();
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.LinkedAccountsSnapshot, snapshot);
            }
            ServerLog.Verbose($"LinkedAccounts: broadcast snapshot ({snapshot.Links.Count} links) to all verified clients");
        }
    }
}
