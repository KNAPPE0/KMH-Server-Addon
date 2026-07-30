using System;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Discord.Patches
{
    // Harmony Postfix on PM_Chat.Receive - relays regular in-game chat to the configured Discord chat-bridge
    // channel
    //
    // Postfix timing: runs only when KmhIntercept's Prefix returned true (let RWT handle the message). That means
    // KMH protocol traffic is already filtered out before we get here
    //
    // We then filter further:
    //   - Skip commands (pkt.IsCommand) - RWT itself doesn't broadcast
    //     them, and our /kmh-* commands are noisy if relayed.
    //   - Skip empty messages.
    //   - Skip messages from the Discord-tag prefix (would loop).
    //
    // No-op when the chat bridge is disabled (config check inside DiscordChatBridge.RelayInGameToDiscord) so the
    // Postfix can stay installed without cost on bridge-less servers
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_ChatBridge
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client, byte[] bytes)
        {
            if (!DiscordChatBridge.IsEnabled) return;

            PKT_Chat pkt;
            try { pkt = Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return; }
            if (pkt == null) return;
            if (pkt.IsCommand) return;
            if (string.IsNullOrEmpty(pkt.Message)) return;

            // Drop KMH protocol usernames as a belt-and-braces - KmhIntercept Prefix already suppresses them, but
            // if Harmony ordering ever changes this guard stops the relay from leaking JSON envelopes into Discord
            // chat
            if (pkt.Username == KmhProtocol.ClientUsername) return;
            if (pkt.Username == KmhProtocol.SystemUsername) return;

            try
            {
                DiscordChatBridge.RelayInGameToDiscord(client?.GetData<UserFile>()?.Username ?? "(unknown)", pkt.Message);
            }
            catch (Exception ex)
            {
                ServerLog.Verbose($"ChatBridge: Postfix relay threw: {ex.Message}");
            }
        }
    }
}
