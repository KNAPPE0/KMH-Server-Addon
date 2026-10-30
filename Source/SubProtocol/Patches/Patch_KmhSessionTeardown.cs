using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // Matched on the exact ServerClient: a username-keyed teardown would kill the player's newer reconnected session.
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
                Features.Global.KmhVerifiedPlayers.Forget(client);
                Features.Global.Federation.FederationHandler.Forget(client);
            }
            catch (System.Exception ex) { ServerLog.Error("Session teardown threw", ex); }
        }
    }
}
