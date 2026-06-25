using System.Collections.Generic;
using KMHServerAddon.Features.Notifications.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Notifications
{
    // Server -> client only: there's no inbound kind to register. On login the handshake calls DeliverQueuedTo,
    // which drains the user's mailbox and pushes it as one batch for the client to render as letters.
    internal static class NotificationHandler
    {
        public static void DeliverQueuedTo(ServerClient client)
        {
            if (client == null) return;
            string user = client.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;

            List<NotificationDto> queued = NotificationStore.Drain(user);
            if (queued.Count == 0) return;

            // Drain removed them from the mailbox - if the hand-off fails (client dropped mid-login), put them back
            // so they're not silently lost.
            if (!KmhRouter.SendTo(client, KmhProtocol.Kind.NotifyQueued, new NotificationBatch { Notifications = queued }))
            {
                NotificationStore.Restore(user, queued);
                return;
            }
            Diagnostics.ServerLog.Verbose($"Delivered {queued.Count} queued notice(s) to {user}");
        }
    }
}
