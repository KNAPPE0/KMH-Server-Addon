using System;
using System.Collections.Generic;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Guilds.Patches
{
    // /kmh guild chat commands: create / join / leave / list / whoami / transfer / help sits alongside KmhIntercept
    // on PM_Chat.Receive; leading-slash check keeps the Prefixes apart
    [HarmonyPatch(typeof(PM_Chat), nameof(PM_Chat.Receive))]
    internal static class Patch_PM_Chat_GuildCommands
    {
        private const string CommandPrefix = "/kmh guild";

        [HarmonyPrefix]
        private static bool Prefix(ServerClient client, byte[] bytes)
        {
            PKT_Chat pkt;
            try { pkt = Serializer.ConvertBytesToObject<PKT_Chat>(bytes); }
            catch { return true; }
            if (pkt == null || !pkt.IsCommand) return true;

            string msg = (pkt.Message ?? "").Trim();
            // Word-boundary match so "/kmh guildfoo" doesn't get swallowed.
            if (!(msg.Equals(CommandPrefix, StringComparison.OrdinalIgnoreCase)
                  || msg.StartsWith(CommandPrefix + " ", StringComparison.OrdinalIgnoreCase)))
                return true;

            try
            {
                HandleCommand(client, msg);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Guild command handler threw", ex);
                try { PM_Chat.SendConsoleMessage(client, "[KMH] Guild command failed unexpectedly."); }
                catch { /* worst-case nothing we can do */ }
            }
            return false; // skip RWT's chat-command dispatch - we handled it
        }

        private static void HandleCommand(ServerClient client, string raw)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username))
            {
                Reply(client, "You must be logged in to use guild commands.");
                return;
            }

            // strip the two-word "/kmh guild" prefix, rebuild parts so handlers keep [1]=sub [2]=arg
            string rest = raw.Length > CommandPrefix.Length ? raw.Substring(CommandPrefix.Length).Trim() : "";
            string[] restParts = rest.Length == 0
                ? Array.Empty<string>()
                : rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] parts = new string[restParts.Length + 1];
            parts[0] = CommandPrefix;
            Array.Copy(restParts, 0, parts, 1, restParts.Length);
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";

            switch (sub)
            {
                case "create":   HandleCreate(client, username, parts);   return;
                case "join":     HandleJoin(client, username, parts);     return;
                case "leave":    HandleLeave(client, username);           return;
                case "list":     HandleList(client);                      return;
                case "whoami":   HandleWhoami(client, username);          return;
                case "transfer": HandleTransfer(client, username, parts); return;
                case "invite":   HandleInvite(client, username, parts);   return;
                case "setopen":  HandleSetOpen(client, username, parts);  return;
                case "deposit":  HandleDeposit(client, username, parts);  return;
                case "withdraw": HandleWithdraw(client, username, parts); return;
                case "perks":    HandlePerks(client, username);           return;
                case "buyperk":  HandleBuyPerk(client, username, parts);  return;
                case "help":
                default:
                    HandleHelp(client);
                    return;
            }
        }

        private static void HandleInvite(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3) { Reply(client, "Usage: /kmh guild invite <username>"); return; }
            string target = parts[2];
            if (GuildStore.Invite(username, target, out string gname, out string err))
            {
                Reply(client, $"Invited {target}. They can /kmh guild join now.");
                ServerLog.Info($"Guild: {username} invited {target}");
                Notifications.KmhMail.ToUser(target.Trim(), "positive", $"Guild invite: {gname}",
                    $"{username} invited you to join '{gname}'. Open the Guild Hall (KMH tab) to accept or decline.");
            }
            else Reply(client, $"Could not invite: {err}");
        }

        private static void HandleSetOpen(ServerClient client, string username, string[] parts)
        {
            bool open = parts.Length >= 3 && (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase));
            if (GuildStore.SetOpenJoin(username, open, out string err))
                Reply(client, open ? "Guild is now open - anyone can join." : "Guild is now invite-only.");
            else Reply(client, $"Could not change join mode: {err}");
        }

        private static void HandleDeposit(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3 || !TryParseAmount(parts[2], out int amount))
            {
                Reply(client, "Usage: /kmh guild deposit <amount>");
                return;
            }
            if (GuildStore.DepositToGuild(username, amount, out string err))
            {
                Reply(client, $"Contribution of {Util.SilverFmt.Format(amount)} pending - save your game to finalize.");
                ServerLog.Info($"Guild: {username} started a pending {amount}s guild contribution (chat)");
            }
            else Reply(client, $"Could not deposit: {err}");
        }

        private static void HandleWithdraw(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3 || !TryParseAmount(parts[2], out int amount))
            {
                Reply(client, "Usage: /kmh guild withdraw <amount>");
                return;
            }
            if (GuildStore.WithdrawFromGuild(username, amount, out string err))
            {
                Reply(client, $"Withdrew {Util.SilverFmt.Format(amount)} from your guild vault.");
                ServerLog.Info($"Guild: {username} withdrew {amount}s from guild vault");
            }
            else Reply(client, $"Could not withdraw: {err}");
        }

        private static void HandlePerks(ServerClient client, string username)
        {
            List<string> lines = GuildStore.DescribePerksFor(username);
            if (lines == null)
            {
                Reply(client, "You are not in a guild.");
                return;
            }
            long vault = Features.Treasury.TreasuryStore.GetGuildSilver(GuildStore.CurrentGuildOf(username));
            Reply(client, $"Guild perks (vault: {Util.SilverFmt.Format(vault)}):");
            foreach (string l in lines) Reply(client, l);
            Reply(client, "Buy with: /kmh guild buyperk <key>   (Admin only, paid from vault)");
        }

        private static void HandleBuyPerk(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3)
            {
                Reply(client, "Usage: /kmh guild buyperk <key>   (see /kmh guild perks)");
                return;
            }
            string perkKey = parts[2];
            if (GuildStore.BuyPerk(username, perkKey, out string reason, out int newLevel, out int cost))
            {
                Reply(client, $"Purchased {GuildStore.PerkLabel(perkKey)} (now Lv {newLevel}) for {Util.SilverFmt.Format(cost)}.");
                ServerLog.Info($"Guild: {username} bought perk '{perkKey}' -> Lv {newLevel}");
            }
            else
            {
                Reply(client, $"Could not buy {GuildStore.PerkLabel(perkKey)}: {reason}");
            }
        }

        private static bool TryParseAmount(string s, out int amount)
            => int.TryParse(s, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out amount) && amount > 0;

        private static void HandleCreate(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3)
            {
                Reply(client, "Usage: /kmh guild create <name>");
                return;
            }
            string name = string.Join(" ", new ArraySegment<string>(parts, 2, parts.Length - 2));
            if (GuildStore.CreateGuildAndJoinAsAdmin(username, name, out string err))
            {
                Reply(client, $"Guild '{name}' created. You are its Admin.");
                ServerLog.Info($"Guild: {username} created guild '{name}'");
            }
            else
            {
                Reply(client, $"Could not create guild: {err}");
            }
        }

        private static void HandleJoin(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3)
            {
                Reply(client, "Usage: /kmh guild join <name>");
                return;
            }
            string name = string.Join(" ", new ArraySegment<string>(parts, 2, parts.Length - 2));
            if (GuildStore.JoinGuild(username, name, out string err))
            {
                Reply(client, $"Joined guild '{name}'.");
                ServerLog.Info($"Guild: {username} joined guild '{name}'");
            }
            else
            {
                Reply(client, $"Could not join: {err}");
            }
        }

        private static void HandleLeave(ServerClient client, string username)
        {
            if (GuildStore.Leave(username, out string err))
            {
                Reply(client, "Left your guild.");
                ServerLog.Info($"Guild: {username} left their guild");
            }
            else
            {
                Reply(client, $"Could not leave: {err}");
            }
        }

        private static void HandleList(ServerClient client)
        {
            List<(string Name, int MemberCount)> guilds = GuildStore.ListGuilds();
            if (guilds.Count == 0)
            {
                Reply(client, "No guilds exist yet. Create one with /kmh guild create <name>.");
                return;
            }
            Reply(client, $"Guilds ({guilds.Count}):");
            foreach ((string name, int count) in guilds)
            {
                Reply(client, $"  • {name} - {count} member{(count == 1 ? "" : "s")}");
            }
        }

        private static void HandleWhoami(ServerClient client, string username)
        {
            string g = GuildStore.CurrentGuildOf(username);
            Reply(client, string.IsNullOrEmpty(g)
                ? "You are not in a guild."
                : $"You are in guild '{g}'.");
        }

        private static void HandleTransfer(ServerClient client, string username, string[] parts)
        {
            if (parts.Length < 3)
            {
                Reply(client, "Usage: /kmh guild transfer <username>");
                return;
            }
            string target = parts[2];
            if (GuildStore.TransferAdmin(username, target, out string err))
            {
                Reply(client, $"Ownership transferred to {target}. You are now an Admin.");
                ServerLog.Info($"Guild: {username} transferred guild ownership to {target}");
            }
            else
            {
                Reply(client, $"Could not transfer: {err}");
            }
        }

        private static void HandleHelp(ServerClient client)
        {
            Reply(client, "KMH guild commands:");
            Reply(client, "  /kmh guild create <name>      - start a new guild (you become Admin)");
            Reply(client, "  /kmh guild join <name>        - join an existing guild");
            Reply(client, "  /kmh guild leave              - leave your current guild");
            Reply(client, "  /kmh guild transfer <user>    - hand Admin to another member");
            Reply(client, "  /kmh guild invite <user>      - (Admin/Mod) invite a player to join");
            Reply(client, "  /kmh guild setopen <on|off>   - (Admin) allow anyone to join without an invite");
            Reply(client, "  /kmh guild deposit <amount>   - contribute personal silver to the guild vault");
            Reply(client, "  /kmh guild withdraw <amount>  - (Admin) take silver from the guild vault");
            Reply(client, "  /kmh guild perks              - show perk levels + next-level costs");
            Reply(client, "  /kmh guild buyperk <key>      - (Admin) buy a perk level from the vault");
            Reply(client, "  /kmh guild list               - list every guild + member count");
            Reply(client, "  /kmh guild whoami             - show your current guild");
            Reply(client, "  /kmh guild help               - show this list");
        }

        private static void Reply(ServerClient client, string text)
        {
            try { PM_Chat.SendConsoleMessage(client, $"[KMH] {text}"); }
            catch (Exception ex) { ServerLog.Error("Guild Reply send failed", ex); }
        }
    }
}
