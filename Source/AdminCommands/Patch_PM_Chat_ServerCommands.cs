using System;
using GameServer.PacketManager;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    // In-game /kmh server chat command. Thin wrapper around KmhServerCommands - the same logic backs the
    // server-console "kmh server" command. Read subcommands (status / extensions / help) are open; mutating ones
    // are gated on the caller's IsAdmin flag
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_ServerCommands
    {
        private const string CommandPrefix = "/kmh server";

        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            PKT_Chat pkt;
            try { pkt = Shared.Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return true; }
            if (pkt == null || !pkt.IsCommand) return true;

            string msg = (pkt.Message ?? "").Trim();
            // Word-boundary match so "/kmh serverfoo" falls through.
            if (!(msg.Equals(CommandPrefix, StringComparison.OrdinalIgnoreCase)
                  || msg.StartsWith(CommandPrefix + " ", StringComparison.OrdinalIgnoreCase)))
                return true;

            try
            {
                // Drop the two-word "/kmh server" prefix; parts[2..] is [subcommand, args...] (parts[0]="/kmh",
                // parts[1]="server")
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
                catch { /* nothing we can do */ }
            }
            return false; // handled
        }
    }
}
