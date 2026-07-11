using System;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    // Deliberately lean on-disk shape (token + guild + a few channels); the [JsonIgnore] accessors below fan it out to
    // whatever each feature asks for.
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
        public bool             UseBundledIcons { get; set; } = true;

        internal class BotSettings
        {
            public string Token            { get; set; } = "";
            public string GuildId          { get; set; } = "";
            public bool   UseSlashCommands { get; set; } = true;
            // Multi-bot: require a player to @mention THIS bot before a !kmh-* text command runs, so several bots in
            // one channel don't all answer. Off by default (a single bot needs no mention). Slash commands are already
            // per-bot, so they're unaffected.
            public bool   RequireMention   { get; set; } = false;
        }

        internal class BrandingSettings
        {
            public string DisplayName   { get; set; } = "KMH Server";
            public string EmbedColorHex { get; set; } = "#C88A2A";
        }

        // One channel per visible feed, plus a single Admin channel for admin actions + console logs +
        // console-command input. Blank = that feed off
        internal class ChannelSettings
        {
            public string Chat          { get; set; } = "";
            public string Announcements { get; set; } = "";
            public string Leaderboard   { get; set; } = "";
            public string Marketplace   { get; set; } = "";
            public string SiteEvents    { get; set; } = "";
            public string Admin         { get; set; } = "";
            public string Commands      { get; set; } = "";   // where !kmh-* commands are allowed; blank = any configured channel
        }

        internal class RoleSettings
        {
            // Anyone with one of these (or guild-admin permission) can use the mod-tier slash commands
            public string[] Moderators    { get; set; } = Array.Empty<string>();
            // Required for /kmh console run when Console.RequireRole is on.
            public string[] ConsoleAccess { get; set; } = Array.Empty<string>();
            // Mirror KMH guild membership to Discord roles named "<Guild> (<Rank>)" on linked players, kept in sync on
            // join/leave/promote/demote/unlink. Off by default. Needs the bot to have Manage Roles, and its own role
            // above the roles it creates. No privileged intent required (uses REST).
            public bool     SyncGuildRoles { get; set; } = false;
        }

        // Which game events get auto-posted as embeds.
        internal class EventSettings
        {
            public bool Server      { get; set; } = true;   // server online, quest completed, guild created
            public bool Marketplace { get; set; } = true;   // new listing, item sold
            public bool Sites       { get; set; } = true;   // site built / removed
        }

        internal class ConsoleSettings
        {
            public bool Allow       { get; set; } = false;  // /kmh console run usable at all
            public bool RequireRole { get; set; } = true;   // ...and only for a ConsoleAccess role
            public bool LiveFeed    { get; set; } = true;   // stream the live server console into the Admin channel
        }

        // ---- flat accessors the features read ----

        [JsonIgnore] public bool   IsEnabled     => Enabled && !string.IsNullOrWhiteSpace(Bot?.Token);
        [JsonIgnore] public string BotToken      => Bot?.Token ?? "";
        [JsonIgnore] public string CommandPrefix => "!";   // legacy !kmh-* prefix, fixed
        [JsonIgnore] public bool   UseSlashCommandsOn => Bot?.UseSlashCommands ?? true;
        [JsonIgnore] public bool   RequireMentionForCommands => Bot?.RequireMention ?? false;
        [JsonIgnore] public bool   SyncGuildRolesOn          => Roles?.SyncGuildRoles ?? false;

        [JsonIgnore] public ulong[] AllowedGuildIds
        {
            get { ulong g = ParseId(Bot?.GuildId); return g != 0 ? new[] { g } : Array.Empty<ulong>(); }
        }

        // channels
        [JsonIgnore] public ulong ChatBridgeChannelId      => ParseId(Channels?.Chat);
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

        // True if !kmh-* commands may run from here. DMs always (linking). A set Commands channel locks them to it;
        // otherwise any configured KMH channel works - never an unrelated one like #general.
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

        // event toggles
        [JsonIgnore] public bool PostServerEvents      => Events?.Server      ?? true;
        [JsonIgnore] public bool PostMarketplaceEvents => Events?.Marketplace ?? true;
        [JsonIgnore] public bool PostSiteEvents        => Events?.Sites       ?? true;

        // console gating
        [JsonIgnore] public bool AllowConsoleCommands => Console?.Allow       ?? false;
        [JsonIgnore] public bool RequireConsoleRole   => Console?.RequireRole ?? true;
        // Live console feed only runs when its toggle is on AND an Admin channel is set (no channel = no feed).
        [JsonIgnore] public bool ConsoleLiveFeed      => (Console?.LiveFeed ?? true) && AdminChannelId != 0;

        // leaderboard / showcase cadence - sensible fixed defaults (kept out of the config to keep it lean; change
        // here if a server ever needs to)
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
