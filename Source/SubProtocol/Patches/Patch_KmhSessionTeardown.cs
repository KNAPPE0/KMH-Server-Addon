using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // Matched on the exact ServerClient: the player may already be back on a new session, and a username-keyed teardown would tear down the reconnection.
    [HarmonyPatch(typeof(InformationDisplayer), nameof(InformationDisplayer.DisplayDisconnect))]
    internal static class Patch_KmhSessionTeardown
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                Features.Transport.KmhApiServer.CloseForSession(client);
                KmhRouter.ForgetPeer(client);
            }
            catch (System.Exception ex) { ServerLog.Error("Session teardown threw", ex); }
        }
    }
}
