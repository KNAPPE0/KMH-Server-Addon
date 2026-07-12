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
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemLabels,    OnPush);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemValues,    OnValues);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConditionDefs, OnConditions);
        }

        // GameConditionDef defName -> label (same payload shape as labels) - feeds the discovered-weather pool.
        private static void OnConditions(ServerClient client, KmhEnvelope env)
        {
            ItemLabelsPush payload = env?.DataAs<ItemLabelsPush>();
            if (payload?.Labels == null || payload.Labels.Count == 0) return;
            WeatherDefCache.Apply(payload.Labels);
            ServerLog.Verbose(
                $"WeatherDefs: received {payload.Labels.Count} condition defs from {client?.GetData<UserFile>()?.Username ?? "?"}");
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
            ItemLabelCache.MarkFungible(payload.Fungible);
            ServerLog.Verbose(
                $"ItemLabels: received {payload.Labels.Count} entries from {client?.GetData<UserFile>()?.Username ?? "?"}");
            // On the final catalog chunk, consolidate legacy treasury payloads that predate the mergeable flag.
            // Idempotent: a no-op after the first run.
            if (payload.ChunkTotal <= 0 || payload.ChunkIndex >= payload.ChunkTotal)
                Features.Treasury.TreasuryStore.CompactFungiblePayloads();
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
