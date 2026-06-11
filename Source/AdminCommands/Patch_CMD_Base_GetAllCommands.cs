using HarmonyLib;

namespace KMHServerAddon.AdminCommands
{
    // After RWT discovers its own chat commands (Program.cs -> GetAllCommands), append KMH's so they show up in
    // /help alongside the official ones
    [HarmonyPatch(typeof(CMD_Base), nameof(CMD_Base.GetAllCommands))]
    internal static class Patch_CMD_Base_GetAllCommands
    {
        [HarmonyPostfix]
        private static void Postfix() => KmhHelpCommand.Register();
    }
}
