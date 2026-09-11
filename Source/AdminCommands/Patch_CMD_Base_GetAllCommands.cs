using HarmonyLib;

namespace KMHServerAddon.AdminCommands
{
    // Appended after RWT's own discovery, or the list is rebuilt without KMH's entries.
    [HarmonyPatch(typeof(CMD_Base), nameof(CMD_Base.GetAllCommands))]
    internal static class Patch_CMD_Base_GetAllCommands
    {
        [HarmonyPostfix]
        private static void Postfix() => KmhHelpCommand.Register();
    }
}
