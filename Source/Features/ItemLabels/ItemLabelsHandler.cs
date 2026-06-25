using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.ItemLabels
{
    // Wire handler for kmh.item_labels: the client pushes its DefDatabase catalog at handshake and the server merges
    // it into the cache. Payload (snake_case): { labels: { defName: label, ... } }. Fire-and-forget (no response);
    // the cache persists to KMH-Data/Catalog/ItemLabels.json so labels survive restarts.
    internal static class ItemLabelsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemLabels, OnPush);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemValues, OnValues);
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

        // defName -> BaseMarketValue (separate envelope from labels - see KmhProtocol.Kind.ItemValues).
        private static void OnValues(ServerClient client, KmhEnvelope env)
        {
            ItemLabelsPush payload = env?.DataAs<ItemLabelsPush>();
            if (payload?.Values == null || payload.Values.Count == 0) return;
            ItemLabelCache.ApplyValues(payload.Values);
            ServerLog.Verbose(
                $"ItemLabels: received {payload.Values.Count} base values from {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
