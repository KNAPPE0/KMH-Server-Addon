using System;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_ServerCommands
    {
        private const string CommandPrefix = "/kmh server";

        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            PKT_Chat pkt;
            try { pkt = Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return true; }
            if (pkt == null || !pkt.IsCommand) return true;

            string msg = (pkt.Message ?? "").Trim();
            // Word-boundary match so "/kmh serverfoo" falls through.
            if (!(msg.Equals(CommandPrefix, StringComparison.OrdinalIgnoreCase)
                  || msg.StartsWith(CommandPrefix + " ", StringComparison.OrdinalIgnoreCase)))
                return true;

            try
            {
                // Index 2 because the prefix is two words: parts[0]="/kmh", parts[1]="server".
                string[] parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string[] args  = parts.Length > 2 ? parts[2..] : Array.Empty<string>();

                bool   isAdmin   = client?.GetData<UserFile>()?.IsAdmin == true;
                string actorName = client?.GetData<UserFile>()?.Username ?? "(unknown)";
                KmhServerCommands.Dispatch(args, isAdmin, actorName,
                    line => PM_Chat.SendConsoleMessage(client, $"[KMH] {line}"));
            }
            catch (Exception ex)
            {
                ServerLog.Error("/kmh server command handler threw", ex);
                try { PM_Chat.SendConsoleMessage(client, "[KMH] /kmh server command failed unexpectedly."); }
                catch { }
            }
            return false; // handled
        }
    }
}
