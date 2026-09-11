using System.Collections.Generic;
using KMHServerAddon.Features.Notifications.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Notifications
{
    // No Register(): this direction is server-to-client only, so there is no inbound kind to route.
    internal static class NotificationHandler
    {
        public static void DeliverQueuedTo(ServerClient client)
        {
            if (client == null) return;
            string user = client.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;

            List<NotificationDto> queued = NotificationStore.Drain(user);
            if (queued.Count == 0) return;

            // Drain already removed them, so a failed hand-off has to put them back or they are lost silently.
            if (!KmhRouter.SendTo(client, KmhProtocol.Kind.NotifyQueued, new NotificationBatch { Notifications = queued }))
            {
                NotificationStore.Restore(user, queued);
                return;
            }
            Diagnostics.ServerLog.Verbose($"Delivered {queued.Count} queued notice(s) to {user}");
        }
    }
}
