using System.Collections.Generic;
using KMHServerAddon.Features.Roadworks.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Roadworks
{
    // Adjacency is never checked here - the client asserts the route.
    internal static class RoadworksHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.RoadworksRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.RoadworksStart,   OnStart);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.RoadworksCancel,  OnCancel);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env) => SendSnapshotTo(client);

        private static void OnStart(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user) || env == null) return;

            var op = new Security.KmhOpClaim("roadworks.start", user, env);
            if (!op.Begin()) { SendSnapshotTo(client); SendTreasuryTo(client, user); return; }

            List<RoadTile> route = ParseRoute(env);
            bool ok = RoadworksStore.TryStartProject(
                user, env.GetInt("site_tile", -1), env.GetString("tier", ""),
                route, out RoadProject project, out string reason);
            if (!ok) op.Release();

            KmhRouter.Notify(client, ok ? "positive" : "negative",
                ok ? $"Road project #{project.Id} started." : reason);
            if (ok) BroadcastSnapshot(); else SendSnapshotTo(client);
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user) || env == null) return;

            bool ok = RoadworksStore.CancelProject(user, env.GetLong("project_id", 0), out int refunded, out string reason);
            KmhRouter.Notify(client, ok ? "neutral" : "negative",
                ok ? $"Road project cancelled - {Util.SilverFmt.Format(refunded)} silver returned; finished road stays."
                   : reason);
            if (ok) { BroadcastSnapshot(); SendTreasuryTo(client, user); } else SendSnapshotTo(client);
        }

        // Flat parallel arrays: no shape negotiation across client versions.
        private static List<RoadTile> ParseRoute(KmhEnvelope env)
        {
            var route = new List<RoadTile>();
            int[] tiles  = env.GetIntArray("route_tiles");
            int[] layers = env.GetIntArray("route_layers");
            if (tiles == null) return route;
            for (int i = 0; i < tiles.Length; i++)
                route.Add(new RoadTile { TileId = tiles[i], LayerId = layers != null && i < layers.Length ? layers[i] : 0 });
            return route;
        }

        internal static void SendSnapshotTo(ServerClient client)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.RoadworksSnapshot, RoadworksStore.BuildSnapshotFor(user));
        }

        private static long _lastGlobalRevision = -1;

        // A segment is world map state and reaches everyone; an unfinished project moves no segment.
        internal static void BroadcastSnapshot()
        {
            long rev = RoadworksStore.Revision;
            bool segmentsChanged = System.Threading.Interlocked.Exchange(ref _lastGlobalRevision, rev) != rev;
            if (segmentsChanged)
                KmhRouter.BroadcastToVerified(KmhProtocol.Kind.RoadworksSnapshot, u => RoadworksStore.BuildSnapshotFor(u));
            else
                KmhRouter.BroadcastToInterested(KmhProtocol.Kind.RoadworksSnapshot, u => RoadworksStore.BuildSnapshotFor(u));
        }

        private static void SendTreasuryTo(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));
    }
}
