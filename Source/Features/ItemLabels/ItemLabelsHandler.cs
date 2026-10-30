using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.ItemLabels
{
    // Fire-and-forget: a client pushes its catalog at handshake and never waits for an answer.
    internal static class ItemLabelsHandler
    {
        // The client sends 800 entries per envelope and pushes once per join; this is a few joins' worth.
        internal const int MaxEntriesPerPush = 1_000;
        internal const int MaxPushesPerWindow = 200, PushWindowSeconds = 600;
        private static readonly Util.KmhRateWindow _pushes = new Util.KmhRateWindow();
        private static readonly object _pushLock = new object();

        internal static int CompactionsRequested;
        internal static void PushForTest(ServerClient client, KmhEnvelope env) => OnPush(client, env);

        internal static bool Admit(ServerClient client, int entries)
        {
            string who = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(who) || entries > MaxEntriesPerPush) return false;
            lock (_pushLock) return _pushes.Allow(who, System.DateTime.UtcNow.Ticks, MaxPushesPerWindow, PushWindowSeconds);
        }

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemLabels,    OnPush);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ItemValues,    OnValues);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConditionDefs, OnConditions);
        }

        private static void OnConditions(ServerClient client, KmhEnvelope env)
        {
            ItemLabelsPush payload = env?.DataAs<ItemLabelsPush>();
            if (payload?.Labels == null || payload.Labels.Count == 0) return;
            if (!Admit(client, payload.Labels.Count)) return;
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
            if (!Admit(client, payload.Labels.Count + (payload.Fungible?.Count ?? 0))) return;
            ItemLabelCache.Apply(payload.Labels);
            bool newFungible = ItemLabelCache.MarkFungible(payload.Fungible);
            ServerLog.Verbose(
                $"ItemLabels: received {payload.Labels.Count} entries from {client?.GetData<UserFile>()?.Username ?? "?"}");
            // Only when a def became fungible: the compaction walks every vault under the treasury lock.
            if (newFungible) { CompactionsRequested++; Features.Treasury.TreasuryStore.CompactFungiblePayloads(); }
        }

        private static void OnValues(ServerClient client, KmhEnvelope env)
        {
            ItemLabelsPush payload = env?.DataAs<ItemLabelsPush>();
            if (payload?.Values == null || payload.Values.Count == 0) return;
            if (!Admit(client, payload.Values.Count)) return;
            ItemLabelCache.ApplyValues(payload.Values);
            ServerLog.Verbose(
                $"ItemLabels: received {payload.Values.Count} base values from {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
