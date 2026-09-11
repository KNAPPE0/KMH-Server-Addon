using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.World
{
    internal static class WorldHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WorldRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WorldContribute, OnContribute);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WorldDeliver, OnDeliver);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.WorldSnapshot, WorldStore.BuildSnapshot());

        private static void OnContribute(ServerClient client, KmhEnvelope env)
        {
            string user  = client?.GetData<UserFile>()?.Username;
            long   id    = env?.GetInt("quest_id", 0) ?? 0;
            int    total = env?.GetInt("total", 0) ?? 0;
            if (string.IsNullOrEmpty(user) || id <= 0 || total < 0) return;
            WorldEngine.ApplyContribution(user, id, total);
        }

        private static void OnDeliver(ServerClient client, KmhEnvelope env)
        {
            string user    = client?.GetData<UserFile>()?.Username;
            long   id      = env?.GetInt("quest_id", 0) ?? 0;
            string itemDef = env?.GetString("item_def_name", "") ?? "";
            int    qty     = env?.GetInt("qty", 0) ?? 0;
            if (string.IsNullOrEmpty(user) || id <= 0 || string.IsNullOrEmpty(itemDef) || qty <= 0) return;

            var op = new Security.KmhOpClaim("world.deliver", user, env);
            if (!op.Begin()) { SendSnapshotTo(client); return; }

            if (!WorldEngine.ApplyDelivery(user, id, itemDef, qty)) op.Release();
        }

        public static void SendSnapshotTo(ServerClient client)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.WorldSnapshot, WorldStore.BuildSnapshot());

        public static void BroadcastSnapshot()
        {
            Dto.WorldSnapshot snap = WorldStore.BuildSnapshot();
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.WorldSnapshot, snap);
            }
        }
    }
}
