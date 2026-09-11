using System;
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
        public ConsoleSettings  Console         { get; set; } = new ConsoleSettings();
        public KmhChatSettings  KmhChat         { get; set; } = new KmhChatSettings();
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
            public string DisplayName   { get; set; } = "KMH Server";
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
        }

        internal class ConsoleSettings
        {
            public bool Allow       { get; set; } = false;  // /kmh console run usable at all
            public bool RequireRole { get; set; } = true;   // ...and only for a ConsoleAccess role
            public bool LiveFeed    { get; set; } = true;   // stream the live server console into the Admin channel
        }

        // Separate from the RWT-chat bridge on Channels.Chat; blank turns this one off.
        internal class KmhChatSettings
        {
            public string Channel     { get; set; } = "";
            public bool   ToDiscord   { get; set; } = true;
            public bool   FromDiscord { get; set; } = true;

            // This bot and anything shaped like a KMH relay line are skipped regardless, or bridges feed each other.
            public bool   RelayBots   { get; set; } = true;
        }

        // KMH_DISCORD_BOT_TOKEN wins over the file, so a secret need never be stored in a shareable config.
        [JsonIgnore] public bool   IsEnabled     => Enabled && !string.IsNullOrWhiteSpace(BotToken);
        [JsonIgnore] public string BotToken      => DiscordTokenSource.Resolve(Bot?.Token);
        [JsonIgnore] public string CommandPrefix => "!";
        [JsonIgnore] public bool   UseSlashCommandsOn => Bot?.UseSlashCommands ?? true;
        [JsonIgnore] public bool   RequireMentionForCommands => Bot?.RequireMention ?? false;
        [JsonIgnore] public bool   SyncGuildRolesOn          => Roles?.SyncGuildRoles ?? false;

        [JsonIgnore] public ulong[] AllowedGuildIds
        {
            get { ulong g = ParseId(Bot?.GuildId); return g != 0 ? new[] { g } : Array.Empty<ulong>(); }
        }

        [JsonIgnore] public ulong ChatBridgeChannelId      => ParseId(Channels?.Chat);
        [JsonIgnore] public ulong KmhChatChannelId         => ParseId(KmhChat?.Channel);
        [JsonIgnore] public bool  KmhChatToDiscordOn       => KmhChat?.ToDiscord ?? true;
        [JsonIgnore] public bool  KmhChatFromDiscordOn     => KmhChat?.FromDiscord ?? true;
        [JsonIgnore] public ulong AnnouncementsChannelId   => ParseId(Channels?.Announcements);
        [JsonIgnore] public ulong PlayerAnnounceChannelId  => ParseId(Channels?.Announcements);
        [JsonIgnore] public ulong LinkAnnounceChannelId    => ParseId(Channels?.Announcements);
        [JsonIgnore] public ulong LeaderboardChannelId     => ParseId(Channels?.Leaderboard);
        [JsonIgnore] public ulong MarketplaceChannelId     => ParseId(Channels?.Marketplace);
        [JsonIgnore] public ulong ShowcaseChannelId        => ParseId(Channels?.Marketplace);
        [JsonIgnore] public ulong WtbChannelId             => ParseId(Channels?.Marketplace);
        [JsonIgnore] public ulong EffectiveWtbChannelId    => WtbChannelId;
        [JsonIgnore] public ulong SiteEventsChannelId      => ParseId(Channels?.SiteEvents);
        [JsonIgnore] public ulong AdminChannelId           => ParseId(Channels?.Admin);
        [JsonIgnore] public ulong ConsoleCommandsChannelId => ParseId(Channels?.Admin);
        [JsonIgnore] public ulong CommandsChannelId        => ParseId(Channels?.Commands);

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

        [JsonIgnore] public bool AllowConsoleCommands => Console?.Allow       ?? false;
        [JsonIgnore] public bool RequireConsoleRole   => Console?.RequireRole ?? true;

        // Also requires an Admin channel, since there is nowhere to stream a feed without one.
        [JsonIgnore] public bool ConsoleLiveFeed      => (Console?.LiveFeed ?? true) && AdminChannelId != 0;

        // Fixed rather than configurable, to keep the owner-facing file short.
        [JsonIgnore] public int LeaderboardIntervalMinutes   => 60;
        [JsonIgnore] public int LeaderboardTopCount          => 10;
        [JsonIgnore] public int LeaderboardRolloverHours      => 24;
        [JsonIgnore] public int ShowcaseSweepIntervalMinutes => 30;

        private static ulong ParseId(string s)
            => ulong.TryParse((s ?? "").Trim(), out ulong v) ? v : 0UL;

        public static DiscordConfig LoadOrDefault()
        {
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
                File.WriteAllText(path, DefaultJson);
            }
            catch (Exception ex) { ServerLog.Warn($"Could not generate DiscordConfig.json: {ex.Message}"); }
        }

        private const string DefaultJson =
@"{
  ""_readme"": ""Paste your bot Token, set GuildId to your Discord server id (right-click the server with Developer Mode on -> Copy ID), then fill in the channel ids you want each feed to post to (blank = off). Admin actions, console logs and /kmh console input all use the Admin channel. Restart the server after editing."",

  ""Enabled"": true,

  ""Bot"": {
    ""Token"": """",
    ""GuildId"": """",
    ""UseSlashCommands"": true,
    ""RequireMention"": false
  },

  ""Branding"": {
    ""DisplayName"": ""KMH Server"",
    ""EmbedColorHex"": ""#C88A2A""
  },

  ""Channels"": {
    ""Chat"": """",
    ""Announcements"": """",
    ""Leaderboard"": """",
    ""Marketplace"": """",
    ""SiteEvents"": """",
    ""Admin"": """"
  },

  ""Roles"": {
    ""Moderators"": [],
    ""ConsoleAccess"": [],
    ""SyncGuildRoles"": false
  },

  ""Events"": {
    ""Server"": true,
    ""Marketplace"": true,
    ""Sites"": true
  },

  ""Console"": {
    ""Allow"": false,
    ""RequireRole"": true,
    ""LiveFeed"": true
  },

  ""UseBundledIcons"": true
}
";
    }
}
