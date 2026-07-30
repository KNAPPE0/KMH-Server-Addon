using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // Fire the KMH handshake right after the client receives RWT's normal post-login chat messages.
    // PM_Chat.SendLoginChatMessages is called from PM_Logins.FinishPostLogin and is itself public static - patching
    // it (rather than the private FinishPostLogin) keeps us on documented
    // public API.
    //
    // SendHelloTo is idempotent and safe to fire repeatedly across reconnects. For stock-RWT clients (no patch
    // loaded), the kmh.hello packet renders as an invisible chat entry under a zero-width-prefixed username -
    // cosmetically odd but harmless
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.SendLoginChatMessages))]
    internal static class Patch_PM_Chat_KmhSendHello
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                KmhHandshakeHandler.SendHelloTo(client);
            }
            catch (System.Exception ex)
            {
                ServerLog.Error("SendHelloTo threw", ex);
            }
        }
    }
}
