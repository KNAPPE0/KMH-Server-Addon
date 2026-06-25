using System;
using GameServer.PacketManager;
using HarmonyLib;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    // Surfaces the KMH command family in RWT's /help. The real KMH commands are Harmony-intercepted on
    // PM_Chat.Receive (swallowed before dispatch), so they never self-register; we add one "/kmh" entry for the
    // listing plus an intercept (below) so bare "/kmh" and "/kmh help" always print the command list.
    internal static class KmhHelpCommand
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                // One umbrella entry for /help. Its Action is a fallback - the root intercept normally handles
                // "/kmh" and "/kmh help" first
                CMD_Base.ChatCommands.Add(new KmhChatCommand(
                    "/kmh", "KMH commands - guild / link / server (type /kmh help)",
                    () => SendHelp(PM_Chat.TargetClient)));

                // Console command (operator types "kmh ..." in the server console) - same admin actions as the
                // in-game chat command
                CMD_Base.Commands.Add(new KmhServerConsoleCommand());
                ServerLog.Info("Registered KMH commands into /help + server console");
            }
            catch (Exception ex) { ServerLog.Warn($"Could not register KMH commands into /help: {ex.Message}"); }
        }

        public static void SendHelp(ServerClient client)
        {
            if (client == null) return;
            PM_Chat.SendConsoleMessage(client, "KMH commands:");
            PM_Chat.SendConsoleMessage(client, "  /kmh guild help    - guilds: create / join / invite / deposit / perks / ...");
            PM_Chat.SendConsoleMessage(client, "  /kmh link [status] - link your Discord account (get a code from the bot)");
            PM_Chat.SendConsoleMessage(client, "  /kmh unlink        - unlink your Discord account");
            PM_Chat.SendConsoleMessage(client, "  /kmh server help   - server status / admin");
            PM_Chat.SendConsoleMessage(client, "  Most KMH features live in the in-game KMH tab.");
        }
    }

    // Owns the bare "/kmh" root and explicit "/kmh help". The guild / link /
    // server intercepts own their own sub-namespaces and run independently;
    // this one only fires when no subcommand (or "help") was given, so it never steals a real subcommand
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_KmhRoot
    {
        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            PKT_Chat pkt;
            try { pkt = Shared.Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return true; }
            if (pkt == null || !pkt.IsCommand) return true;

            string msg    = (pkt.Message ?? "").Trim();
            bool   isRoot = msg.Equals("/kmh", StringComparison.OrdinalIgnoreCase);
            bool   isHelp = msg.Equals("/kmh help", StringComparison.OrdinalIgnoreCase)
                         || msg.StartsWith("/kmh help ", StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !isHelp) return true;

            try { KmhHelpCommand.SendHelp(client); }
            catch (Exception ex) { ServerLog.Error("/kmh help handler threw", ex); }
            return false; // handled - skip RWT's chat-command dispatch
        }
    }

    // A CMD_Base entry for the shared ChatCommands list. Optional handler runs on dispatch; used only for the
    // umbrella /kmh entry's fallback
    internal sealed class KmhChatCommand : CMD_Base
    {
        private readonly Action _onAction;

        public KmhChatCommand(string prefix, string description, Action onAction = null)
        {
            Prefix        = prefix;
            Description   = description;
            IsChatCommand = true;
            _onAction     = onAction;
        }

        public override void Action() => _onAction?.Invoke();
    }
}
