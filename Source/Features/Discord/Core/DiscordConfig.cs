using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    internal class DiscordConfig
    {
        public int              SchemaVersion   { get; set; } = 1;
        public bool             Enabled         { get; set; } = true;
        public BotSettings      Bot             { get; set; } = new BotSettings();
        public BrandingSettings Branding        { get; set; } = new BrandingSettings();
        public ChannelSettings  Channels        { get; set; } = new ChannelSettings();
        public RoleSettings     Roles           { get; set; } = new RoleSettings();
        public EventSettings    Events          { get; set; } = new EventSettings();
        public LiveSettings     Live            { get; set; } = new LiveSettings();
        public ConsoleSettings  Console         { get; set; } = new ConsoleSettings();
        public KmhChatSettings  KmhChat         { get; set; } = new KmhChatSettings();
        public WorldChatSettings WorldChat      { get; set; } = new WorldChatSettings();
        public CoordinatorSettings Coordinator  { get; set; } = new CoordinatorSettings();
        public bool             UseBundledIcons { get; set; } = true;

        internal class BotSettings
        {
            public string Token            { get; set; } = "";
            public string GuildId          { get; set; } = "";
            public bool   UseSlashCommands { get; set; } = true;
            // For a channel holding several KMH bots, so they do not all answer the same text command.
            public bool   RequireMention   { get; set; } = false;
        }

        internal class BrandingSettings
        {
            public string DisplayName   { get; set; } = "Colony Nexus Server";
            public string EmbedColorHex { get; set; } = "#C88A2A";
        }

        // Blank turns that feed off.
        internal class ChannelSettings
        {
            public string Chat          { get; set; } = "";
            public string Announcements { get; set; } = "";
            public string Leaderboard   { get; set; } = "";
            public string Marketplace   { get; set; } = "";
            public string SiteEvents    { get; set; } = "";
            public string Admin         { get; set; } = "";
            public string Commands      { get; set; } = "";
        }

        internal class RoleSettings
        {
            public string[] Moderators    { get; set; } = Array.Empty<string>();
            public string[] ConsoleAccess { get; set; } = Array.Empty<string>();

            // Needs Manage Roles, and the bot's own role sitting above the roles it creates.
            public bool     SyncGuildRoles { get; set; } = false;
        }

        internal class EventSettings
        {
            public bool Server      { get; set; } = true;
            public bool Marketplace { get; set; } = true;
            public bool Sites       { get; set; } = true;
            public bool Reports     { get; set; } = true;
            public bool Status      { get; set; } = true;
            public bool Alerts      { get; set; } = true;
            public bool AlertsPingModerators { get; set; } = false;
        }

        // Messages edited in place; each one is finished, and a new one started, when the day turns over.
        internal class LiveSettings
        {
            public int      DayStartsAtUtcHour        { get; set; } = 0;
            public int      LeaderboardEveryMinutes   { get; set; } = 15;
            public int      LeaderboardRolloverHours  { get; set; } = 24;
            // "auto" is the board the website opens on, "guilds" the guild board; any leaderboard key works too.
            public string[] LeaderboardBoards         { get; set; } = { "auto", "guilds" };
            public int      MarketBoardEveryMinutes   { get; set; } = 10;
        }

        internal class ConsoleSettings
        {
            public bool Allow       { get; set; } = false;
            public bool RequireRole { get; set; } = true;
            public bool LiveFeed    { get; set; } = true;
        }

        // With this on the server opens no gateway of its own and takes its channel ids from the coordinator.
        internal class CoordinatorSettings
        {
            public bool   Enabled { get; set; }
            public string Url     { get; set; } = "";
        }

        // Separate from the RWT-chat bridge on Channels.Chat; blank turns this one off.
        internal class KmhChatSettings
        {
            public string Channel     { get; set; } = "";
            public bool   ToDiscord   { get; set; } = true;
            public bool   FromDiscord { get; set; } = true;

            // Off by default: other bots spam the game, and two bridges relaying each other echo in an unbounded loop.
            public bool   RelayBots   { get; set; } = false;
        }

        // Cross-server world chat, owned by Global rather than by this server. Blank turns it off.
        internal class WorldChatSettings
        {
            public string Channel     { get; set; } = "";
            public bool   ToDiscord   { get; set; } = true;
            public bool   FromDiscord { get; set; } = true;

            // Never defaults on: world chat reaches every KMH server, far beyond the guild whose owner enabled it.
            public bool   RelayBots   { get; set; } = false;

            // In-game chat is carried to world chat as well, so a player with no Discord still takes part.
            public bool   RelayInGame { get; set; } = false;
        }

        [JsonIgnore] public bool CoordinatorMode => Coordinator?.Enabled == true;

        // KMH_DISCORD_BOT_TOKEN wins over the file, so a secret need never be stored in a shareable config.
        // Coordinator mode ignores the local token entirely: that is the guard against two live bot sessions.
        [JsonIgnore] public bool   IsEnabled     => Enabled && (CoordinatorMode || !string.IsNullOrWhiteSpace(BotToken));
        [JsonIgnore] public string BotToken      => CoordinatorMode ? "" : DiscordTokenSource.Resolve(Bot?.Token);
        [JsonIgnore] public string CommandPrefix => "!";
        [JsonIgnore] public bool   RequireMentionForCommands => Bot?.RequireMention ?? false;
        [JsonIgnore] public bool   SyncGuildRolesOn          => Roles?.SyncGuildRoles ?? false;

        [JsonIgnore] public ulong[] AllowedGuildIds
        {
            get { ulong g = ParseId(Bot?.GuildId); return g != 0 ? new[] { g } : Array.Empty<ulong>(); }
        }

        [JsonIgnore] public ulong ChatBridgeChannelId      => Chan(Kmh.Discord.Wire.KmhChannelRole.Chat, Channels?.Chat);
        [JsonIgnore] public ulong KmhChatChannelId         => Chan(Kmh.Discord.Wire.KmhChannelRole.KmhChat, KmhChat?.Channel);
        [JsonIgnore] public ulong WorldChatChannelId       => Chan(Kmh.Discord.Wire.KmhChannelRole.WorldChat, WorldChat?.Channel);
        [JsonIgnore] public bool  KmhChatToDiscordOn       => KmhChat?.ToDiscord ?? true;
        [JsonIgnore] public bool  KmhChatFromDiscordOn     => KmhChat?.FromDiscord ?? true;
        [JsonIgnore] public bool  KmhChatRelayBotsOn       => KmhChat?.RelayBots ?? false;
        [JsonIgnore] public bool  WorldChatToDiscordOn     => WorldChat?.ToDiscord ?? true;
        [JsonIgnore] public bool  WorldChatFromDiscordOn   => WorldChat?.FromDiscord ?? true;
        [JsonIgnore] public bool  WorldChatRelayBotsOn     => WorldChat?.RelayBots ?? false;
        [JsonIgnore] public bool  WorldChatRelayInGameOn   => WorldChat?.RelayInGame ?? false;
        [JsonIgnore] public ulong AnnouncementsChannelId   => Chan(Kmh.Discord.Wire.KmhChannelRole.Announcements, Channels?.Announcements);
        [JsonIgnore] public ulong PlayerAnnounceChannelId  => AnnouncementsChannelId;
        [JsonIgnore] public ulong LinkAnnounceChannelId    => AnnouncementsChannelId;
        [JsonIgnore] public ulong LeaderboardChannelId     => Chan(Kmh.Discord.Wire.KmhChannelRole.Leaderboard, Channels?.Leaderboard);
        [JsonIgnore] public ulong MarketplaceChannelId     => Chan(Kmh.Discord.Wire.KmhChannelRole.Marketplace, Channels?.Marketplace);
        [JsonIgnore] public ulong ShowcaseChannelId        => MarketplaceChannelId;
        [JsonIgnore] public ulong WtbChannelId             => MarketplaceChannelId;
        [JsonIgnore] public ulong EffectiveWtbChannelId    => WtbChannelId;
        [JsonIgnore] public ulong SiteEventsChannelId      => Chan(Kmh.Discord.Wire.KmhChannelRole.SiteEvents, Channels?.SiteEvents);
        [JsonIgnore] public ulong AdminChannelId           => Chan(Kmh.Discord.Wire.KmhChannelRole.Admin, Channels?.Admin);
        [JsonIgnore] public ulong ConsoleCommandsChannelId => AdminChannelId;
        [JsonIgnore] public ulong CommandsChannelId        => Chan(Kmh.Discord.Wire.KmhChannelRole.Commands, Channels?.Commands);

        // In coordinator mode the fleet's channel map is authoritative, so an owner never writes ids here twice.
        private static ulong Chan(string role, string configured)
        {
            if (DiscordCoordinatorLink.Active
             && DiscordCoordinatorLink.Channels.TryGetValue(role, out ulong fromFleet)) return fromFleet;
            return ParseId(configured);
        }

        // A channel id belongs to exactly one Discord server, so a command sent in one of these came from ours.
        public bool IsOwnChannel(ulong channelId)
        {
            if (channelId == 0) return false;
            foreach (ulong c in new[] { ChatBridgeChannelId, KmhChatChannelId, WorldChatChannelId, AnnouncementsChannelId,
                                        LeaderboardChannelId, MarketplaceChannelId, SiteEventsChannelId, AdminChannelId,
                                        CommandsChannelId })
                if (c == channelId) return true;
            return false;
        }

        // Role names are not unique and anyone with Manage Roles can make one, so only numeric ids count.
        internal static List<string> NamedRoleEntries(RoleSettings roles)
        {
            List<string> named = new List<string>();
            if (roles == null) return named;
            foreach (string[] list in new[] { roles.Moderators, roles.ConsoleAccess })
                foreach (string s in list ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(s) && ParseId(s) == 0) named.Add(s.Trim());
            return named;
        }

        // Falls back to the configured KMH channels rather than anywhere, so commands never run in an unrelated channel.
        public bool CommandsAllowedIn(ulong channelId, bool isDm)
        {
            if (isDm) return true;
            if (CommandsChannelId != 0) return channelId == CommandsChannelId;
            return channelId != 0 && (channelId == MarketplaceChannelId
                                   || channelId == LeaderboardChannelId
                                   || channelId == AdminChannelId
                                   || channelId == ChatBridgeChannelId
                                   || channelId == AnnouncementsChannelId
                                   || channelId == SiteEventsChannelId);
        }

        [JsonIgnore] public bool PostServerEvents      => Events?.Server      ?? true;
        [JsonIgnore] public bool PostMarketplaceEvents => Events?.Marketplace ?? true;
        [JsonIgnore] public bool PostSiteEvents        => Events?.Sites       ?? true;
        [JsonIgnore] public bool PostReportEvents      => Events?.Reports     ?? true;
        [JsonIgnore] public bool PostStatus            => (Events?.Status ?? true) && AdminChannelId != 0;
        [JsonIgnore] public bool PostAlerts            => (Events?.Alerts ?? true) && AdminChannelId != 0;
        [JsonIgnore] public bool AlertPingModerators   => Events?.AlertsPingModerators ?? false;

        [JsonIgnore] public bool AllowConsoleCommands => Console?.Allow       ?? false;
        [JsonIgnore] public bool RequireConsoleRole   => Console?.RequireRole ?? true;

        // Also requires an Admin channel, since there is nowhere to stream a feed without one.
        [JsonIgnore] public bool ConsoleLiveFeed      => (Console?.LiveFeed ?? true) && AdminChannelId != 0;

        // Under one minute (or no boards) turns it off; the upper bounds stop a typo parking it for a year.
        [JsonIgnore] public int LeaderboardIntervalMinutes   => Math.Min(Live?.LeaderboardEveryMinutes ?? 15, 1440);
        [JsonIgnore] public int LeaderboardRolloverHours     => Math.Clamp(Live?.LeaderboardRolloverHours ?? 24, 1, 168);
        [JsonIgnore] public int LiveDayStartUtcHour          => Math.Clamp(Live?.DayStartsAtUtcHour ?? 0, 0, 23);
        [JsonIgnore] public int MarketBoardIntervalMinutes   => Math.Min(Live?.MarketBoardEveryMinutes ?? 10, 1440);
        [JsonIgnore] public string[] LiveLeaderboardBoards
        {
            get
            {
                var keys = new List<string>();
                foreach (string s in Live?.LeaderboardBoards ?? Array.Empty<string>())
                {
                    string k = (s ?? "").Trim().ToLowerInvariant();
                    if (k.Length > 0 && k.Length <= 40 && !keys.Contains(k) && keys.Count < 4) keys.Add(k);
                }
                return keys.ToArray();
            }
        }

        // Fixed rather than configurable, to keep the owner-facing file short.
        [JsonIgnore] public int LeaderboardTopCount          => 10;
        [JsonIgnore] public int ShowcaseSweepIntervalMinutes => 30;

        internal static ulong ParseId(string s)
            => ulong.TryParse((s ?? "").Trim(), out ulong v) ? v : 0UL;

        public static DiscordConfig LoadOrDefault()
        {
            // It holds the bot token: owner-only like the signing key, or every account on the machine could read it.
            if (File.Exists(KmhDataPaths.DiscordConfigFile)) Global.KmhServerKey.Restrict(KmhDataPaths.DiscordConfigFile);
            if (JsonFileStore.TryLoad(KmhDataPaths.DiscordConfigFile, out DiscordConfig cfg) && cfg != null)
                return cfg;
            return new DiscordConfig();
        }

        public static void EnsureGenerated()
        {
            string path = KmhDataPaths.DiscordConfigFile;
            if (File.Exists(path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                JsonFileStore.Save(path, BuildDefaultFile());
            }
            catch (Exception ex) { ServerLog.Warn($"Could not generate DiscordConfig.json: {ex.Message}"); }
        }

        // Built from the type: a hand-written literal falls behind silently, and a fresh install ships a stale config.
        internal static Newtonsoft.Json.Linq.JObject BuildDefaultFile()
        {
            var file = Newtonsoft.Json.Linq.JObject.FromObject(new DiscordConfig());
            file.AddFirst(new Newtonsoft.Json.Linq.JProperty("_readme", Readme));
            return file;
        }

        private const string Readme =
            "Paste your bot Token, set GuildId to your Discord server id (right-click the server with Developer Mode "
          + "on -> Copy ID), then fill in the channel ids you want each feed to post to (blank = off). Admin actions, "
          + "console logs and /kmh console input all use the Admin channel. KmhChat bridges the Colony Nexus chat and is "
          + "separate from Channels.Chat, which bridges RWT chat. Roles.Moderators and Roles.ConsoleAccess take role "
          + "ids, not names. The leaderboard image, the market board, the activity logs and the Admin status are single "
          + "messages edited live, and each starts afresh when the day turns over at Live.DayStartsAtUtcHour (UTC). "
          + "Restart the server after editing.";
    }
}
