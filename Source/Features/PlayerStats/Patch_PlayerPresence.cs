using System;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.PlayerStats.Patches
{
    // Stamped from the connection, not the activity heartbeat: a client that never sends one still has a last seen.
    [HarmonyPatch(typeof(InformationDisplayer), nameof(InformationDisplayer.DisplayLogin))]
    internal static class Patch_PlayerPresence_Login
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                string username = client?.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(username)) return;
                PlayerStatsStore.EnsurePlayer(username);
                PlayerStatsStore.TouchSeen(username);
                PlayerStatsStore.BeginSession(username);
            }
            catch (Exception ex) { ServerLog.Error("Presence: login stamp threw", ex); }
        }
    }

    [HarmonyPatch(typeof(InformationDisplayer), nameof(InformationDisplayer.DisplayDisconnect))]
    internal static class Patch_PlayerPresence_Disconnect
    {
        [HarmonyPostfix]
        private static void Postfix(ServerClient client)
        {
            try
            {
                string username = client?.GetData<UserFile>()?.Username;
                PlayerStatsStore.RollSession(username, ending: true);
                PlayerStatsStore.TouchSeen(username);
            }
            catch (Exception ex) { ServerLog.Error("Presence: disconnect stamp threw", ex); }
        }
    }
}
