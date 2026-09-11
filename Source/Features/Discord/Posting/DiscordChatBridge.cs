using System;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // The bot filter one way and the "[Discord] " username filter the other are what stop the relay looping.
    internal static class DiscordChatBridge
    {
        private const string DiscordTagPrefix = "[Discord] ";

        public static bool IsEnabled
        {
            get
            {
                DiscordConfig cfg = DiscordBridge.Config;
                return cfg != null && cfg.IsEnabled && cfg.ChatBridgeChannelId != 0;
            }
        }

        public static void RelayInGameToDiscord(string username, string message)
        {
            if (!IsEnabled)                                                                return;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(message))           return;
            if (username.StartsWith(DiscordTagPrefix, StringComparison.Ordinal))           return;
            if (message.Length > 1500) message = message.Substring(0, 1497) + "…";

            // Only mentions are defused; markdown stays so links and emphasis still read naturally.
            string safe = message.Replace("@", "@​");

            DiscordBridge.PostToChannel(
                DiscordBridge.Config.ChatBridgeChannelId,
                $"**{username}**: {safe}");
        }

        public static void RelayDiscordToInGame(string displayName, string message)
        {
            if (!IsEnabled)                                                          return;
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(message))  return;
            if (message.Length > 256) message = message.Substring(0, 253) + "…";

            try
            {
                PKT_Chat pkt = new PKT_Chat
                {
                    Username  = DiscordTagPrefix + displayName,
                    Message   = message,
                    IsCommand = false,
                };

                int sent = 0;
                foreach (TCPNetwork.ServerClient c in Network.ServerClients.Keys)
                {
                    if (c?.IsVerified != true || c.Listener == null) continue;
                    try
                    {
                        c.Listener.EnqueuePacket(RwtCompat.ChatHeader, pkt);
                        sent++;
                    }
                    catch { /* per-client send failure shouldn't abort the broadcast */ }
                }
                ServerLog.Verbose($"ChatBridge: relayed Discord -> in-game to {sent} client(s)");
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"ChatBridge: Discord -> in-game relay threw: {ex.Message}");
            }
        }
    }
}
