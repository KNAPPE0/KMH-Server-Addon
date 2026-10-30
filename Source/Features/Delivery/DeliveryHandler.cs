using System.Collections.Generic;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Delivery
{
    internal static class DeliveryHandler
    {
        public static void Register()
            => KmhRouter.RegisterHandler(KmhProtocol.Kind.DeliveryAck, OnAck);

        private static void OnAck(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            string id = env?.GetString("delivery_id") ?? "";
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(id)) return;
            if (DeliveryStore.Ack(username, id)) Transactions.KmhTransactionRepository.ConfirmDelivered(id);
            else Diagnostics.ServerLog.Verbose($"Delivery: ignored an ack for '{id}' from {username} (unknown, not theirs, or already settled).");
        }

        // Owed deliveries wait for the first colony report: it resets a new save's economy, and replaying any earlier
        // would land the old save's withdrawals in the new one.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, object> _saveKnown =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, object>();
        private static readonly object Known = new object();

        internal static bool SaveKnownOn(ServerClient client) => client != null && _saveKnown.TryGetValue(client, out _);

        // True only the first time, so exactly one caller replays.
        internal static bool MarkSaveKnown(ServerClient client)
        {
            if (client == null) return false;
            lock (_saveKnown)
            {
                if (_saveKnown.TryGetValue(client, out _)) return false;
                _saveKnown.Add(client, Known);
                return true;
            }
        }

        // Re-sent every join: the client's durable receipt decides, so a replay costs a message, not duplicated goods.
        public static void ReplayOwed(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            // Owed to the save a reset is destroying; a stalled reset purges them when it lands.
            if (Economy.KmhEconomyReset.Resetting(username)) return;
            List<OutboundDelivery> owed = DeliveryStore.OwedFor(username);
            if (owed.Count == 0) return;
            Diagnostics.ServerLog.Info($"Delivery: replaying {owed.Count} unacknowledged delivery(ies) to {username}.");
            foreach (OutboundDelivery d in owed) Send(client, d);
        }

        public static void Send(ServerClient client, OutboundDelivery d)
        {
            if (client == null || d == null) return;
            if (d.Payloads != null && d.Payloads.Count > 0)
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant,
                    new { kind = "item_payloads", payloads = d.Payloads, delivery_id = d.Id });
            else if (d.Items != null && d.Items.Count > 0)
                foreach (KeyValuePair<string, int> kv in d.Items)
                    KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant,
                        new { kind = "item", def_name = kv.Key, amount = kv.Value, delivery_id = d.Id });
            else if (d.Silver > 0)
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant,
                    new { kind = "silver", amount = (int)System.Math.Min(d.Silver, int.MaxValue), delivery_id = d.Id });
        }
    }
}
