using GameServer.PacketManager;
using HarmonyLib;

namespace KMHServerAddon.Features.PlayerStats.Patches
{
    // Login hook for PlayerStats. Harmony postfix on the same RWT method KmhSendHello uses
    // (PM_Chat.SendLoginChatMessages) - both postfixes run, order doesn't matter. Keeping the PlayerStats hook in
    // its own patch class lets the feature stay self-contained: removing the feature is a single-folder delete
    //
    // Every login Ensures the player exists in PlayerStatsStore so the leaderboard reflects everyone who's
    // connected this server-process lifetime, not just everyone who's clicked the Player Leaderboard dialog
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
