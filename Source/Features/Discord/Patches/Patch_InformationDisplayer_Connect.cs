using System;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord.Patches
{
    // DisplayLogin rather than the pre-auth DisplayConnect, which only ever sees an IP.
    [HarmonyPatch(typeof(InformationDisplayer), nameof(InformationDisplayer.DisplayLogin))]
    internal static class Patch_InformationDisplayer_Connect
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                DiscordPlayerAnnouncer.AnnounceJoined(client?.GetData<UserFile>()?.Username);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: AnnounceJoined Postfix threw", ex);
            }
        }
    }

    // The client is already out of Network.ServerClients here, but its UserFile still carries the username.
    [HarmonyPatch(typeof(InformationDisplayer), nameof(InformationDisplayer.DisplayDisconnect))]
    internal static class Patch_InformationDisplayer_Disconnect
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                DiscordPlayerAnnouncer.AnnounceLeft(client?.GetData<UserFile>()?.Username);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: AnnounceLeft Postfix threw", ex);
            }
        }
    }
}
