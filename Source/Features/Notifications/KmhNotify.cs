using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Notifications
{
    // Not Player Mail (Features.Mail): this carries no value and is never player-to-player.
    internal static class KmhNotify
    {
        public static void ToUser(string username, string tone, string title, string body)
        {
            if (string.IsNullOrEmpty(username)) return;
            bool online = KmhRouter.SendToUsername(username, KmhProtocol.Kind.Notice,
                                                   new { level = string.IsNullOrEmpty(tone) ? "neutral" : tone, text = body });
            if (!online) NotificationStore.Enqueue(username, tone, title, body);
        }

        // Unlike ToUser this never queues for the offline, so a player who must not miss it needs ToUser.
        public static void ToEveryoneOnline(string tone, string text)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true) KmhRouter.Notify(c, string.IsNullOrEmpty(tone) ? "neutral" : tone, text);
        }
    }
}
