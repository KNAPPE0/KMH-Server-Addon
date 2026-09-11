using System;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord.Patches
{
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_LinkCommands
    {
        private const string CmdLink   = "/kmh link";
        private const string CmdUnlink = "/kmh unlink";

        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            PKT_Chat pkt;
            try { pkt = Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return true; }
            if (pkt == null || !pkt.IsCommand) return true;

            string msg      = (pkt.Message ?? "").Trim();
            bool   isLink   = msg.StartsWith(CmdLink,   StringComparison.OrdinalIgnoreCase);
            bool   isUnlink = msg.StartsWith(CmdUnlink, StringComparison.OrdinalIgnoreCase);
            if (!isLink && !isUnlink) return true;

            // Word-boundary match, so "/kmh linker" falls through to RWT.
            int cmdLen = isLink ? CmdLink.Length : CmdUnlink.Length;
            if (msg.Length > cmdLen && msg[cmdLen] != ' ') return true;

            try
            {
                if (isLink) HandleLink(client, msg);
                else        HandleUnlink(client);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Link command handler threw", ex);
                try { PM_Chat.SendConsoleMessage(client, "[KMH] Link command failed unexpectedly."); }
                catch { }
            }
            return false; // handled
        }

        private static void HandleLink(ServerClient client, string raw)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username))
            {
                Reply(client, "You must be logged in to use /kmh link.");
                return;
            }

            // "status" reports the binding without issuing a code, since issuing one invalidates the last.
            string after = raw.Length > CmdLink.Length ? raw.Substring(CmdLink.Length).Trim() : "";
            if (after.StartsWith("status", StringComparison.OrdinalIgnoreCase))
            {
                if (LinkedAccountsStore.TryGetLink(username, out string display) && !string.IsNullOrEmpty(display))
                {
                    Reply(client, $"Your KMH account is linked to Discord: {display}");
                }
                else
                {
                    Reply(client, "Your KMH account is not currently linked to Discord.");
                    Reply(client, "Run /kmh link to get a code, then use it on Discord with !kmh-link.");
                }
                return;
            }

            string code = DiscordLinkFlow.IssueCodeFor(username);
            int    mins = (int)DiscordLinkFlow.TimeToLive.TotalMinutes;
            Reply(client, $"Your link code: {code}");
            Reply(client, $"In Discord, DM the bot or post in an allowed channel: !kmh-link {code}");
            Reply(client, $"This code expires in {mins} minutes and can only be used once.");
            Reply(client, "(See your current link any time with: /kmh link status)");
        }

        private static void HandleUnlink(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username))
            {
                Reply(client, "You must be logged in to use /kmh unlink.");
                return;
            }
            // Read before unlinking, because the announce below still needs the display name.
            if (!LinkedAccountsStore.TryGetLink(username, out string previousDisplay))
            {
                Reply(client, "You don't currently have a Discord link.");
                return;
            }
            LinkedAccountsStore.Unlink(username);
            LinkedAccountsHandler.BroadcastSnapshot();
            DiscordBridge.AnnounceLink(username, previousDisplay, "unlink");
            ServerLog.Info($"Link: {username} unlinked their Discord (via /kmh unlink)");
            Reply(client, "Your Discord link has been removed.");
        }

        private static void Reply(ServerClient client, string text)
        {
            try { PM_Chat.SendConsoleMessage(client, $"[KMH] {text}"); }
            catch (Exception ex) { ServerLog.Error("Link Reply send failed", ex); }
        }
    }
}
