using System;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // Two-way chat relay between Discord (DiscordChatChannelId) and in-game (PKT_Chat). Loops are broken by
    // filtering Author.IsBot on the Discord side, filtering "[Discord] " usernames on the in-game side, and by the
    // KmhIntercept Prefix already filtering protocol envelopes
    internal static class DiscordChatBridge
    {
        // Prefix applied to in-game chat that originated on Discord.
        private const string DiscordTagPrefix = "[Discord] ";

        public static bool IsEnabled
        {
            get
            {
                DiscordConfig cfg = DiscordBridge.Config;
                return cfg != null && cfg.IsEnabled && cfg.ChatBridgeChannelId != 0;
            }
        }

        // in-game -> Discord. Skip messages that are themselves relayed Discord chat (username starts with
        // "[Discord] "), commands, and empty/protocol messages.
        public static void RelayInGameToDiscord(string username, string message)
        {
            if (!IsEnabled)                                                                return;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(message))           return;
            if (username.StartsWith(DiscordTagPrefix, StringComparison.Ordinal))           return;
            if (message.Length > 1500) message = message.Substring(0, 1497) + "…";

            // Markdown-escape minimal - Discord renders **bold** and other markup, and a malicious in-game player
            // could ping @everyone. Escape the @ to prevent role/everyone mentions; leave the rest readable since
            // marketplace links and short emphasis are useful
            string safe = message.Replace("@", "@​"); // zero-width space breaks pings

            DiscordBridge.PostToChannel(
                DiscordBridge.Config.ChatBridgeChannelId,
                $"**{username}**: {safe}");
        }

        // Discord -> in-game. Constructs a PKT_Chat tagged "[Discord] X" and broadcasts to every verified client.
        // Stops if the message exceeds the chat-cap (server's own rate limit handles spammy sources; we just clamp
        // display length here)
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
