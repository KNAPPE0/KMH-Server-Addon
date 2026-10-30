using System;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Discord.WebSocket;

namespace Kmh.Discord.Wire
{
    // A free-text `server` option resolved at runtime, so adding or removing a server never re-registers /kmh.
    public static class KmhSlashTree
    {
        public const string Root = "kmh";
        public const string ServerOption = "server";

        // Paths the coordinator answers itself; everything else is dispatched to a game server.
        public static readonly string[] CoordinatorPaths = { "servers", "here", "help" };

        // A private result on a public path is still delivered privately - see KmhInteractionResponder.
        private static readonly HashSet<string> Private = new HashSet<string>(StringComparer.Ordinal)
        {
            "help", "here", "whois", "console.run",
            "ban", "ipban", "unban", "bans",
            "leaderboard.post", "leaderboard.refresh",
            "announcement.send", "discord.reload", "config.reload",
        };

        public static bool IsPrivate(string path) => path != null && Private.Contains(path);

        public static bool IsCoordinatorPath(string path)
            => path != null && Array.IndexOf(CoordinatorPaths, path) >= 0;

        // Which capability a path needs; absent means any registered server can service it.
        private static readonly Dictionary<string, string> Needs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"]              = KmhDiscordCapability.Status,
            ["players"]             = KmhDiscordCapability.Players,
            ["whois"]               = KmhDiscordCapability.Moderation,
            ["ban"]                 = KmhDiscordCapability.Moderation,
            ["ipban"]               = KmhDiscordCapability.Moderation,
            ["unban"]               = KmhDiscordCapability.Moderation,
            ["bans"]                = KmhDiscordCapability.Moderation,
            ["leaderboard.show"]    = KmhDiscordCapability.Leaderboard,
            ["leaderboard.rank"]    = KmhDiscordCapability.Leaderboard,
            ["leaderboard.post"]    = KmhDiscordCapability.Leaderboard,
            ["leaderboard.refresh"] = KmhDiscordCapability.Leaderboard,
            ["market.list"]         = KmhDiscordCapability.Marketplace,
            ["market.search"]       = KmhDiscordCapability.Marketplace,
            ["site.list"]           = KmhDiscordCapability.Sites,
            ["site.info"]           = KmhDiscordCapability.Sites,
            ["site.roads"]          = KmhDiscordCapability.Sites,
            ["announcement.send"]   = KmhDiscordCapability.Announcements,
            ["console.run"]         = KmhDiscordCapability.Console,
        };

        public static string CapabilityFor(string path)
            => path != null && Needs.TryGetValue(path, out string c) ? c : "";

        public static SlashCommandProperties Build()
        {
            SlashCommandBuilder kmh = new SlashCommandBuilder()
                .WithName(Root)
                .WithDescription("Colony Nexus server commands");

            kmh.AddOption(Routed(Sub("status",  "Server health + counts")));
            kmh.AddOption(Routed(Sub("players", "Who is online right now")));
            kmh.AddOption(Sub("servers", "Every Colony Nexus server this bot serves"));
            kmh.AddOption(Sub("here",    "Which Colony Nexus server this channel talks to"));
            kmh.AddOption(Sub("help",    "List the Colony Nexus Discord commands"));
            kmh.AddOption(Routed(Sub("whois", "(owner) An online player's IP for moderation - the reply is private to you")
                .AddOption(Str("player", "In-game name (must be online)", true))));

            kmh.AddOption(Routed(Sub("ban", "(owner) Ban a player by name")
                .AddOption(Str("player", "In-game name", true))
                .AddOption(Str("reason", "Shown in the ban list, not to the player", false))
                .AddOption(IntOpt("days", "Days until it lifts (blank = permanent)", false))));
            kmh.AddOption(Routed(Sub("ipban", "(owner) Ban a player by name and current address - they must be online")
                .AddOption(Str("player", "In-game name (must be online)", true))
                .AddOption(Str("reason", "Shown in the ban list, not to the player", false))
                .AddOption(IntOpt("days", "Days until it lifts (blank = permanent)", false))));
            kmh.AddOption(Routed(Sub("unban", "(owner) Lift a ban by player name or address")
                .AddOption(Str("player", "In-game name or address", true))));
            kmh.AddOption(Routed(Sub("bans", "(owner) Who is currently banned")));

            SlashCommandOptionBuilder lb = Group("leaderboard", "Top players / guilds / reputation");
            lb.AddOption(Routed(Sub("show", "Show the leaderboard")
                .AddOption(new SlashCommandOptionBuilder().WithName("board").WithDescription("Which board")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(false)
                    .AddChoice("players", "players").AddChoice("guilds", "guilds").AddChoice("rep", "rep"))
                .AddOption(Str("sort", "Sort key (see /kmh help)", false))));
            lb.AddOption(Routed(Sub("rank", "A player's full stat card")
                .AddOption(Str("player", "In-game name (blank = your linked account)", false))));
            lb.AddOption(Routed(Sub("post",    "(mod) Post the leaderboard to its channel now")));
            lb.AddOption(Routed(Sub("refresh", "(mod) Re-post the leaderboard now")));
            kmh.AddOption(lb);

            SlashCommandOptionBuilder mk = Group("market", "Browse the marketplace");
            mk.AddOption(Routed(Sub("list", "Open listings").AddOption(IntOpt("page", "Page number", false))));
            mk.AddOption(Routed(Sub("search", "Search listings").AddOption(Str("query", "Item or seller", true))));
            kmh.AddOption(mk);

            SlashCommandOptionBuilder st = Group("site", "Custom sites");
            st.AddOption(Routed(Sub("list", "All active sites")));
            st.AddOption(Routed(Sub("info", "One site by world tile").AddOption(IntOpt("tile", "World tile id", true))));
            st.AddOption(Routed(Sub("roads", "Road network built by Roadworks sites")));
            kmh.AddOption(st);

            SlashCommandOptionBuilder an = Group("announcement", "Server announcements");
            an.AddOption(Routed(Sub("send", "(mod) Post an announcement").AddOption(Str("message", "Text to post", true))));
            kmh.AddOption(an);

            SlashCommandOptionBuilder dc = Group("discord", "Bot admin");
            dc.AddOption(Routed(Sub("reload", "(mod) Reload Discord config + restart the bridge")));
            kmh.AddOption(dc);

            SlashCommandOptionBuilder cf = Group("config", "Game config admin");
            cf.AddOption(Routed(Sub("reload", "(mod) Reload Economy + Sites config")));
            kmh.AddOption(cf);

            SlashCommandOptionBuilder co = Group("console", "Owner console");
            co.AddOption(Routed(Sub("run", "(owner) Run any server console command")
                .AddOption(Str("command", "e.g. help / players / kmh status / kmh enforce on", true))));
            kmh.AddOption(co);

            return kmh.Build();
        }

        private static SlashCommandOptionBuilder Routed(SlashCommandOptionBuilder sub)
            => sub.AddOption(new SlashCommandOptionBuilder()
                .WithName(ServerOption).WithDescription("Which Colony Nexus server")
                .WithType(ApplicationCommandOptionType.String).WithRequired(false).WithAutocomplete(true));

        private static SlashCommandOptionBuilder Sub(string name, string desc)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.SubCommand);

        private static SlashCommandOptionBuilder Group(string name, string desc)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.SubCommandGroup);

        private static SlashCommandOptionBuilder Str(string name, string desc, bool required)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.String).WithRequired(required);

        private static SlashCommandOptionBuilder IntOpt(string name, string desc, bool required)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.Integer).WithRequired(required);

        // The only place an interaction's own shape is read.
        public static void ReadPath(IReadOnlyCollection<IApplicationCommandInteractionDataOption> top,
                                    out string path, out Dictionary<string, string> options)
        {
            path = "";
            options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IApplicationCommandInteractionDataOption first = top?.FirstOrDefault();
            if (first == null) return;

            IReadOnlyCollection<IApplicationCommandInteractionDataOption> args;
            if (first.Type == ApplicationCommandOptionType.SubCommandGroup)
            {
                IApplicationCommandInteractionDataOption s = first.Options?.FirstOrDefault();
                path = s == null ? first.Name : first.Name + "." + s.Name;
                args = s?.Options;
            }
            else
            {
                path = first.Name;
                args = first.Options;
            }

            if (args == null) return;
            foreach (IApplicationCommandInteractionDataOption a in args)
                if (a?.Name != null) options[a.Name] = a.Value?.ToString() ?? "";
        }

        public static void ReadAutocomplete(SocketAutocompleteInteraction ac, out string focusedName, out string typed)
        {
            focusedName = ac?.Data?.Current?.Name ?? "";
            typed = ac?.Data?.Current?.Value?.ToString() ?? "";
        }
    }
}
