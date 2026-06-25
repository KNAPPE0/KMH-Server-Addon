using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Seasons
{
    // kmh.archive.season handler: serves the season archive snapshot on request, and broadcasts a fresh one after
    // an admin rolls the season.
    internal static class SeasonHandler
    {
        public static void Register()
            => KmhRouter.RegisterHandler(KmhProtocol.Kind.SeasonArchiveRequest, OnRequest);

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.SeasonArchive, SeasonStore.BuildSnapshot());
        }

        public static void Broadcast()
        {
            Dto.SeasonArchiveSnapshot snap = SeasonStore.BuildSnapshot();
            foreach (ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true) KmhRouter.SendTo(c, KmhProtocol.Kind.SeasonArchive, snap);
        }
    }
}
