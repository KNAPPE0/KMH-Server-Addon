using System.Collections.Generic;
using KMHServerAddon.Features.Notifications.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Notifications
{
    internal static class NotificationHandler
    {
        public static void Register() => KmhRouter.RegisterHandler(KmhProtocol.Kind.NotifyAck, OnAck);

        public static void DeliverQueuedTo(ServerClient client)
        {
            if (client == null) return;
            string user = client.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;

            // One that cannot ack is served the old way, or an older client re-reads the same notices for ever.
            bool acks = KmhHandshakeHandler.ClientDeclares(client, KmhClientCapabilities.DurableNotifications);

            List<NotificationDto> queued = acks ? NotificationStore.PeekForUser(user) : NotificationStore.Drain(user);
            if (queued.Count == 0) return;

            if (!KmhRouter.SendTo(client, KmhProtocol.Kind.NotifyQueued, new NotificationBatch { Notifications = queued }))
            {
                // Drain already removed them, so a failed hand-off has to put them back or they are lost silently.
                if (!acks) NotificationStore.Restore(user, queued);
                return;
            }
            Diagnostics.ServerLog.Verbose($"Delivered {queued.Count} queued notice(s) to {user}"
                                        + (acks ? " - held until acknowledged" : ""));
        }

        private static void OnAck(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            List<string> ids = env?.GetStringList("ids");
            int removed = NotificationStore.AckDelivered(user, ids);
            if (removed > 0) Diagnostics.ServerLog.Verbose($"{user} acknowledged {removed} notice(s)");
        }
    }
}
