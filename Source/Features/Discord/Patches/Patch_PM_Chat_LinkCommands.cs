using System;
using GameServer.PacketManager;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord.Patches
{
    // /kmh link + /kmh unlink chat-command handler.
    //
    // /kmh link issues a fresh single-use code via DiscordLinkFlow and tells the player how to redeem it on the
    // Discord side. Works even when the Discord bridge is disabled - the code just won't get redeemed, which is
    // loud enough to debug
    //
    // /kmh unlink wipes the current link for the calling user, if any. Symmetric with !kmh-unlink on the Discord
    // side so a player can drop the link from whichever end they're already typing on
    //
    // second Prefix on PM_Chat.Receive, returns false on match so RWT's own chat pipeline doesn't also handle the
    // unknown slash command
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_LinkCommands
    {
        private const string CmdLink   = "/kmh link";
        private const string CmdUnlink = "/kmh unlink";

        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            PKT_Chat pkt;
            try { pkt = Shared.Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return true; }
            if (pkt == null || !pkt.IsCommand) return true;

            string msg      = (pkt.Message ?? "").Trim();
            bool   isLink   = msg.StartsWith(CmdLink,   StringComparison.OrdinalIgnoreCase);
            bool   isUnlink = msg.StartsWith(CmdUnlink, StringComparison.OrdinalIgnoreCase);
            if (!isLink && !isUnlink) return true;

            // Exact-prefix guard - "/kmh link " (space) is fine, "/kmh linker" must fall through to RWT
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
                catch { /* worst-case nothing more we can do */ }
            }
            return false; // skip RWT's chat-command dispatch - we handled it
        }

        private static void HandleLink(ServerClient client, string raw)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username))
            {
                Reply(client, "You must be logged in to use /kmh link.");
                return;
            }

            // Subcommand parse: /kmh link status -> show current binding without issuing a fresh code. Anything
            // else = issue. Strip the two-word prefix first, since the subcommand now sits after it
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
            // Capture the previous display BEFORE removing so the announce line can include it ("X unlinked - was:
            // Y"). TryGetLink doubles as the IsLinked check, so we don't probe twice
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
