using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.ItemLabels
{
    // Wire handler for kmh.item_labels - patch mod pushes its local DefDatabase catalog at handshake completion,
    // server merges into the cache
    //
    // Envelope payload (snake_case): { labels: { defName: label, ... } }
    //
    // No response - this is a fire-and-forget push. If the cache is empty the server's first market-browse output
    // may still show raw defNames until at least one client has connected. Once a client reports its catalog, all
    // subsequent Discord commands have friendly labels available across server restarts (cache persists to
    // KMH-Data/Catalog/ItemLabels.json)
    internal static class ItemLabelsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemLabels, OnPush);
        }

        private static void OnPush(ServerClient client, KmhEnvelope env)
        {
            ItemLabelsPush payload = env?.DataAs<ItemLabelsPush>();
            if (payload?.Labels == null || payload.Labels.Count == 0)
            {
                ServerLog.Verbose($"ItemLabels: push from {client?.GetData<UserFile>()?.Username ?? "?"} had no labels");
                return;
            }
            ItemLabelCache.Apply(payload.Labels);
            ServerLog.Verbose(
                $"ItemLabels: received {payload.Labels.Count} entries from {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
