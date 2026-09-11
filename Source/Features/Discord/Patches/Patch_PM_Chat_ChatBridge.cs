using System;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Discord.Patches
{
    // A postfix, so it only sees messages KmhIntercept's prefix already let through to RWT.
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

            // Redundant today, but a Harmony ordering change would otherwise leak JSON envelopes into Discord.
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
