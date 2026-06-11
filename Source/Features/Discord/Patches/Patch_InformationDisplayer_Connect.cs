using System;
using GameServer.Misc;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord.Patches
{
    // Harmony Postfix on InformationDisplayer.DisplayLogin - RWT's stock "this client just authenticated" log call.
    // Stock RWT calls it from PM_Logins right after the auth handshake succeeds, which is exactly when we want to
    // announce a join: the username is final, the client is verified, and we haven't fired earlier (the pre-auth
    // DisplayConnect sees only an IP).
    //
    // We Postfix DisplayLogin so the announce lands at the right moment without touching any RWT source.
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

    // Harmony Postfix on InformationDisplayer.DisplayDisconnect - RWT's stock disconnect log call, invoked from the
    // NetworkRuleset's OnDisconnect delegate (ServerNetwork.OnDisconnect). At this point the client has been
    // removed from Network.ServerClients but client.GetData<UserFile>() is still populated, so we can read the
    // username for the announce
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
