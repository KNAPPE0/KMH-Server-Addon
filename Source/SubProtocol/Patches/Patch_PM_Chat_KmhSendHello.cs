using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol.Patches
{
    // Patches the public SendLoginChatMessages rather than the private FinishPostLogin that calls it.
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
