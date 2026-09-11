using HarmonyLib;

namespace KMHServerAddon.Features.PlayerStats.Patches
{
    // Shares its target method with KmhSendHello's postfix; both run and the order does not matter.
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.SendLoginChatMessages))]
    internal static class Patch_PM_Chat_PlayerStatsEnsure
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                PlayerStatsStore.EnsurePlayer(client?.GetData<UserFile>()?.Username);
            }
            catch (System.Exception ex)
            {
                Diagnostics.ServerLog.Error("PlayerStats EnsurePlayer threw", ex);
            }
        }
    }
}
