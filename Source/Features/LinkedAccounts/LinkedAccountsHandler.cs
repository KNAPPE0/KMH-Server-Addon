using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.LinkedAccounts
{
    // Link and unlink are deliberately not wire-driven - only the Discord bridge and admin commands mutate the store.
    internal static class LinkedAccountsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.LinkedAccountsRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.LinkRequest,           OnLinkRequest);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        private static void OnLinkRequest(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            if (Discord.DiscordBridge.Config?.IsEnabled != true)
            {
                KmhRouter.Notify(client, "negative", "Discord linking isn't set up on this server.");
                return;
            }
            string code = Discord.DiscordLinkFlow.IssueCodeFor(username);
            int    mins = (int)Discord.DiscordLinkFlow.TimeToLive.TotalMinutes;
            KmhRouter.SendTo(client, KmhProtocol.Kind.LinkCode, new { code = code, ttl_minutes = mins });
        }

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            Dto.LinkedAccountsSnapshot snapshot = LinkedAccountsStore.BuildSnapshot();
            KmhRouter.SendTo(client, KmhProtocol.Kind.LinkedAccountsSnapshot, snapshot);
        }

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
