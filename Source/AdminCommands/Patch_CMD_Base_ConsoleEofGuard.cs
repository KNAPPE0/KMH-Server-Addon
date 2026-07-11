using System.Reflection;
using System.Threading;
using GameServer.PacketManager;
using HarmonyLib;

namespace KMHServerAddon.AdminCommands
{
    // Closed/detached stdin makes RWT's console loop NPE-spin (gigabytes of log) - swallow null input + throttle.
    [HarmonyPatch]
    internal static class Patch_CMD_Base_ConsoleEofGuard
    {
        private static MethodBase Target()
            => AccessTools.Method(typeof(CMD_Base), "ParseCommand", new[] { typeof(string) });

        private static bool Prepare() => Target() != null;   // skip cleanly on a generation without this method

        private static MethodBase TargetMethod() => Target();

        [HarmonyPrefix]
        private static bool Prefix(string input)
        {
            if (input != null) return true;
            Thread.Sleep(1000);   // EOF'd stdin - idle the listener thread instead of spinning
            return false;
        }
    }
}
