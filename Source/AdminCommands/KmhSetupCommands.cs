using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Features.Identity;

namespace KMHServerAddon.AdminCommands
{
    internal enum SetupState { Done, Todo, Optional }

    internal sealed class SetupStep
    {
        public string Name = "";
        public SetupState State;
        public string Detail = "";
        public string Next = "";     // the exact command to run, empty when there is nothing to do
    }

    // Derived from live config, so a step cannot claim to be done when the thing it describes is not.
    internal static class KmhSetupCommands
    {
        public static void Run(string[] args, string actorName, Action<string> reply, bool fromConsole)
        {
            string action = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            switch (action)
            {
                case "owner": Owner(args, actorName, reply, fromConsole); return;
                case "info":
                case "privacy":
                case "data":  Features.Global.KmhGlobalDisclosure.Describe(reply); return;
                default:      Status(reply); return;
            }
        }

        public static List<SetupStep> Steps()
        {
            var steps = new List<SetupStep>();

            StaffConfig staff = StaffConfig.Current;
            int owners = staff?.Owners?.Count ?? 0;
            steps.Add(new SetupStep
            {
                Name   = "Owner",
                State  = owners > 0 ? SetupState.Done : SetupState.Todo,
                Detail = owners > 0 ? string.Join(", ", (staff.Owners ?? new List<string>()).ToArray())
                                    : "nobody is listed as the owner",
                Next   = owners > 0 ? "" : "kmh setup owner <your in-game name>",
            });

            GlobalConfig g = GlobalConfig.Current;
            bool registered = GlobalSetupState.Current_.IsOwnerRegistered;
            steps.Add(new SetupStep
            {
                Name   = "Connected Worlds",
                State  = !g.Enabled ? SetupState.Optional : registered ? SetupState.Done : SetupState.Todo,
                Detail = !g.Enabled ? "disabled - this server is not in the Colony Nexus browser"
                       : registered ? $"registered as {KmhServerIdentity.Id}"
                                    : $"setup is at '{GlobalSetupState.Current_.Current}'",
                Next   = !g.Enabled || registered ? "" : "kmh global setup",
            });

            // Federation needs a Global identity but never a listing; a private one is made for it automatically.
            string fedWhy = Features.Global.Federation.FederationService.UnavailableReason();
            bool fedOn = Features.FeaturesConfig.Current.GuildFederation;
            steps.Add(new SetupStep
            {
                Name   = "Guild Federation",
                State  = !fedOn ? SetupState.Optional : fedWhy == "" ? SetupState.Done : SetupState.Todo,
                Detail = !fedOn ? "off in Config/Features.json"
                       : fedWhy == "" ? (GlobalSetupState.Current_.BackendRegistered
                                         ? "on, through a private Global identity (not listed)" : "on")
                       : fedWhy == "global.off" ? "paused - Colony Nexus Global is switched off here"
                       : fedWhy == "global.setup" ? "waiting for this server's Global identity" + (string.IsNullOrEmpty(Features.Global.KmhGlobalBackend.LastError) ? "" : $" ({Features.Global.KmhGlobalBackend.LastError})")
                       : fedWhy == "global.unsupported" ? "Colony Nexus Global does not offer Federation yet"
                       : fedWhy == "global.revoked" ? "closed - Colony Nexus Global revoked this server's registration"
                       : "Colony Nexus Global is unreachable right now",
                Next   = !fedOn || fedWhy == "" ? ""
                       : fedWhy == "global.off" ? "kmh global setup"
                       : fedWhy == "global.setup" || fedWhy == "global.revoked" ? "kmh global status"
                       : "kmh federation status",
            });

            // Invisible from in game: a listing with no address still shows its row, but nobody can join it.
            if (g.Enabled && registered)
                steps.Add(new SetupStep
                {
                    Name   = "Join address",
                    State  = string.IsNullOrWhiteSpace(g.PublicHost) ? SetupState.Todo : SetupState.Done,
                    Detail = string.IsNullOrWhiteSpace(g.PublicHost)
                           ? "no public host - your listing publishes no address to join"
                           : g.PublicHost,
                    Next   = string.IsNullOrWhiteSpace(g.PublicHost)
                           ? "set PublicHost in KMH-Data/Config/Global.json, then: kmh reload global" : "",
                });

            Features.Discord.DiscordConfig d = Features.Discord.DiscordBridge.Config;
            bool discordOn = d != null && d.IsEnabled;
            steps.Add(new SetupStep
            {
                Name   = "Discord",
                State  = d == null || !d.Enabled ? SetupState.Optional
                       : discordOn ? SetupState.Done : SetupState.Todo,
                Detail = d == null || !d.Enabled ? "off"
                       : discordOn ? "connected"
                       : "enabled but no bot token - set KMH_DISCORD_BOT_TOKEN or Bot.Token",
                Next   = d != null && d.Enabled && !discordOn
                       ? "set the bot token, then restart the server" : "",
            });

            if (discordOn)
            {
                bool anyChannel = d.ChatBridgeChannelId != 0 || d.KmhChatChannelId != 0
                               || d.WorldChatChannelId != 0 || d.AdminChannelId != 0;
                steps.Add(new SetupStep
                {
                    Name   = "Discord channels",
                    State  = anyChannel ? SetupState.Done : SetupState.Todo,
                    Detail = anyChannel ? Channels(d) : "no channels set - the bot is connected but wired to nothing",
                    Next   = anyChannel ? "" : "set Channels.* in KMH-Data/Config/Discord/DiscordConfig.json, then restart",
                });
            }

            steps.Add(new SetupStep
            {
                Name   = "In-game settings access",
                State  = SetupState.Optional,
                Detail = "console-only, and it closes on its own",
                Next   = owners > 0 ? "kmh config-editor allow <owner> [minutes]" : "",
            });

            return steps;
        }

        private static string Channels(Features.Discord.DiscordConfig d)
        {
            var set = new List<string>();
            if (d.ChatBridgeChannelId != 0) set.Add("chat");
            if (d.KmhChatChannelId != 0)    set.Add("kmh-chat");
            if (d.WorldChatChannelId != 0)  set.Add("world-chat");
            if (d.AdminChannelId != 0)      set.Add("admin");
            return string.Join(", ", set.ToArray());
        }

        private static void Status(Action<string> reply)
        {
            List<SetupStep> steps = Steps();
            int todo = 0;
            foreach (SetupStep s in steps) if (s.State == SetupState.Todo) todo++;

            reply(todo == 0 ? "Colony Nexus setup: everything required is done." : $"Colony Nexus setup: {todo} step(s) left.");
            reply("");
            foreach (SetupStep s in steps)
            {
                string mark = s.State == SetupState.Done ? "[x]" : s.State == SetupState.Todo ? "[ ]" : "[-]";
                reply($"  {mark} {s.Name}: {s.Detail}");
                if (s.Next.Length > 0) reply($"        -> {s.Next}");
            }
            reply("");
            reply("[x] done   [ ] still to do   [-] optional");
            reply("What this server sends to the directory, and when: kmh setup info");
        }

        // Console only: every authority check reads the owner, so chat or Discord must never be able to set it.
        private static void Owner(string[] args, string actorName, Action<string> reply, bool fromConsole)
        {
            if (!fromConsole)
            {
                reply("'setup owner' can only be run at the server console - it decides who the server answers to.");
                return;
            }

            string who = args != null && args.Length > 2 ? args[2].Trim() : "";
            if (who.Length == 0) { reply("Usage: kmh setup owner <in-game name>"); return; }

            StaffConfig cfg = StaffConfig.Current;
            if (cfg == null) { reply("Staff config is unavailable."); return; }

            if (StaffRegistry.IsOwner(who)) { reply($"'{who}' is already the owner."); return; }

            cfg.Owners ??= new List<string>();
            cfg.Owners.Add(who);
            cfg.Normalize();
            if (!Persistence.JsonFileStore.Save(Persistence.KmhDataPaths.StaffConfigFile, cfg))
            {
                reply("Could not save Staff.json - check free space and permissions on KMH-Data.");
                return;
            }
            StaffConfig.Reload();
            Features.Comms.CommsStartup.ApplyStaff(push: true);

            Diagnostics.ServerLog.Warn($"Setup: {actorName} added '{who}' as an owner.");
            reply($"'{who}' is now an owner.");
            reply($"Open their in-game settings access with: kmh config-editor allow {who}");
        }
    }
}
