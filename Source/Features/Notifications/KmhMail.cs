using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Notifications
{
    // Notify a user online or queue offline mail, so player-facing events never get silently dropped.
    internal static class KmhMail
    {
        public static void ToUser(string username, string tone, string title, string body)
        {
            if (string.IsNullOrEmpty(username)) return;
            bool online = KmhRouter.SendToUsername(username, KmhProtocol.Kind.Notice,
                                                   new { level = string.IsNullOrEmpty(tone) ? "neutral" : tone, text = body });
            if (!online) NotificationStore.Enqueue(username, tone, title, body);
        }
    }
}
