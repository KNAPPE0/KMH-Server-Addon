using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Kmh.Discord.Wire
{
    // Compiled by both sides, so the two halves of the contract cannot drift apart.
    public static class KmhDiscordProtocol
    {
        public const string Name = "kmh.discord";
        public const int    Version = 1;

        // Signed over by the game server on every call; see KmhDiscordAuth.
        public const string SignatureDomain = "kmh.discord.link.v1";

        public const int MaxContentChars = 1900;
        public const int MaxEmbedsPerMessage = 10;
        public const int MaxFollowups = 5;
    }

    // Named by role, not channel id, so a game server never learns another server's channels.
    public static class KmhChannelRole
    {
        public const string Chat          = "chat";
        public const string KmhChat       = "kmh-chat";
        public const string WorldChat     = "world-chat";
        public const string Announcements = "announcements";
        public const string Leaderboard   = "leaderboard";
        public const string Marketplace   = "marketplace";
        public const string SiteEvents    = "site-events";
        public const string Admin         = "admin";
        public const string Commands      = "commands";
    }

    // Advertised at registration, so an unsupported command is refused instead of timing out.
    public static class KmhDiscordCapability
    {
        public const string Status        = "status";
        public const string Players       = "players";
        public const string Leaderboard   = "leaderboard";
        public const string Marketplace   = "marketplace";
        public const string Sites         = "sites";
        public const string Announcements = "announcements";
        public const string ChatRelay     = "chat-relay";
        public const string Console       = "console";
        public const string Moderation    = "moderation";

        public static readonly string[] All =
        {
            Status, Players, Leaderboard, Marketplace, Sites, Announcements, ChatRelay, Console, Moderation,
        };
    }

    public sealed class KmhEmbedField
    {
        [JsonProperty("name")]   public string Name   { get; set; } = "";
        [JsonProperty("value")]  public string Value  { get; set; } = "";
        [JsonProperty("inline")] public bool   Inline { get; set; }
    }

    // Only what KMH actually renders. Anything Discord offers beyond this is deliberately absent.
    public sealed class KmhEmbedSpec
    {
        [JsonProperty("title")]        public string Title        { get; set; }
        [JsonProperty("description")]  public string Description  { get; set; }
        [JsonProperty("color")]        public uint   ColorRgb     { get; set; }
        [JsonProperty("footer")]       public string Footer       { get; set; }
        [JsonProperty("thumbnail")]    public string ThumbnailUrl { get; set; }
        [JsonProperty("image")]        public string ImageUrl     { get; set; }
        // 0 means no timestamp at all, which is different from the epoch.
        [JsonProperty("ts")]           public long   TimestampUnix { get; set; }
        [JsonProperty("fields")]       public List<KmhEmbedField> Fields { get; set; } = new List<KmhEmbedField>();

        // An icon shipped with KMH, resolved to a real file by whichever side actually talks to Discord.
        [JsonProperty("icon")]         public string IconFileName { get; set; }
    }

    public sealed class KmhButtonSpec
    {
        [JsonProperty("label")]    public string Label    { get; set; } = "";
        [JsonProperty("custom")]   public string CustomId { get; set; } = "";
        // success | primary | secondary | danger
        [JsonProperty("style")]    public string Style    { get; set; } = "secondary";
    }

    public sealed class KmhComponentSpec
    {
        [JsonProperty("buttons")] public List<KmhButtonSpec> Buttons { get; set; } = new List<KmhButtonSpec>();
    }

    // Only the coordinator builds one, from a real interaction a game server never sees.
    public sealed class KmhDiscordCommandRequest
    {
        [JsonProperty("request_id")]   public string RequestId { get; set; } = "";
        [JsonProperty("command")]      public string Command   { get; set; } = "kmh";
        // "status", "leaderboard.show" - the group and subcommand already resolved.
        [JsonProperty("path")]         public string Path      { get; set; } = "";
        [JsonProperty("options")]      public Dictionary<string, string> Options { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";

        [JsonProperty("user_id")]      public ulong  UserId      { get; set; }
        [JsonProperty("user_name")]    public string UserName    { get; set; } = "";
        [JsonProperty("user_display")] public string UserDisplay { get; set; } = "";

        [JsonProperty("guild_id")]     public ulong  GuildId   { get; set; }
        [JsonProperty("channel_id")]   public ulong  ChannelId { get; set; }
        [JsonProperty("dm")]           public bool   IsDirectMessage { get; set; }

        // Both forms, because an owner may have written either a role id or a role name into DiscordConfig.
        [JsonProperty("role_ids")]     public List<string> RoleIds   { get; set; } = new List<string>();
        [JsonProperty("role_names")]   public List<string> RoleNames { get; set; } = new List<string>();
        [JsonProperty("guild_admin")]  public bool IsGuildAdministrator { get; set; }

        [JsonProperty("locale")]       public string Locale { get; set; } = "";
        [JsonProperty("issued_at")]    public long   IssuedAtUnix  { get; set; }
        [JsonProperty("expires_at")]   public long   ExpiresAtUnix { get; set; }

        public string Option(string name, string fallback = "")
            => Options != null && Options.TryGetValue(name, out string v) && v != null ? v : fallback;

        public long OptionInt(string name, long fallback)
            => long.TryParse(Option(name, ""), out long v) ? v : fallback;
    }

    public sealed class KmhDiscordFollowup
    {
        [JsonProperty("content")]   public string Content   { get; set; } = "";
        [JsonProperty("ephemeral")] public bool   Ephemeral { get; set; } = true;
    }

    public sealed class KmhDiscordCommandResult
    {
        [JsonProperty("request_id")] public string RequestId { get; set; } = "";
        [JsonProperty("content")]    public string Content   { get; set; }
        [JsonProperty("embeds")]     public List<KmhEmbedSpec> Embeds { get; set; } = new List<KmhEmbedSpec>();
        [JsonProperty("components")] public KmhComponentSpec Components { get; set; }
        [JsonProperty("ephemeral")]  public bool   Ephemeral { get; set; }
        [JsonProperty("error")]      public bool   IsError   { get; set; }
        [JsonProperty("error_code")] public string ErrorCode { get; set; } = "";
        [JsonProperty("followups")]  public List<KmhDiscordFollowup> Followups { get; set; }
            = new List<KmhDiscordFollowup>();

        // Never on the wire: only a gateway held by this process can upload a file, so the coordinator sees Content alone.
        [JsonIgnore] public List<(string Name, byte[] Bytes)> Files { get; set; }

        public static KmhDiscordCommandResult Text(string content, bool ephemeral = false)
            => new KmhDiscordCommandResult { Content = content, Ephemeral = ephemeral };

        public static KmhDiscordCommandResult Error(string content, string code = "")
            => new KmhDiscordCommandResult { Content = content, Ephemeral = true, IsError = true, ErrorCode = code };

        public static KmhDiscordCommandResult Embed(KmhEmbedSpec embed, bool ephemeral = false)
        {
            KmhDiscordCommandResult r = new KmhDiscordCommandResult { Ephemeral = ephemeral };
            if (embed != null) r.Embeds.Add(embed);
            return r;
        }
    }

    public static class KmhDiscordOp
    {
        public const string Post     = "post";
        public const string Edit     = "edit";
        public const string Delete   = "delete";
        public const string Presence = "presence";
        public const string Role     = "role";
    }

    public sealed class KmhDiscordOutbound
    {
        [JsonProperty("op")]           public string Op { get; set; } = KmhDiscordOp.Post;
        [JsonProperty("op_id")]        public string OpId { get; set; } = "";
        [JsonProperty("channel_role")] public string ChannelRole { get; set; } = "";
        [JsonProperty("message_id")]   public ulong  MessageId { get; set; }

        [JsonProperty("content")]      public string Content { get; set; }
        [JsonProperty("embeds")]       public List<KmhEmbedSpec> Embeds { get; set; }
        [JsonProperty("components")]   public KmhComponentSpec Components { get; set; }

        [JsonProperty("players")]      public int Players { get; set; }

        [JsonProperty("role_op")]      public string RoleOp { get; set; }
        [JsonProperty("role_name")]    public string RoleName { get; set; }
        [JsonProperty("role_color")]   public uint   RoleColorRgb { get; set; }
        [JsonProperty("role_id")]      public ulong  RoleId { get; set; }
        [JsonProperty("target_user")]  public ulong  TargetUserId { get; set; }
    }

    public sealed class KmhDiscordOutboundResult
    {
        [JsonProperty("ok")]         public bool   Ok { get; set; }
        [JsonProperty("message_id")] public ulong  MessageId { get; set; }
        [JsonProperty("role_id")]    public ulong  RoleId { get; set; }
        [JsonProperty("error")]      public string Error { get; set; } = "";
    }

    public static class KmhDiscordInboundKind
    {
        public const string Message        = "message";
        public const string MessageDeleted = "message-deleted";
        public const string MessageUpdated = "message-updated";
        public const string Component      = "component";
        public const string Command        = "command";
    }

    public sealed class KmhWireAttachment
    {
        [JsonProperty("name")] public string Filename { get; set; } = "";
        [JsonProperty("url")]  public string Url      { get; set; } = "";
        [JsonProperty("size")] public int    Size     { get; set; }
    }

    public sealed class KmhWireSticker
    {
        [JsonProperty("id")]     public ulong  Id     { get; set; }
        [JsonProperty("name")]   public string Name   { get; set; } = "";
        [JsonProperty("format")] public string Format { get; set; } = "";
    }

    public sealed class KmhWireEmbedMedia
    {
        [JsonProperty("type")]        public string Type          { get; set; } = "";
        [JsonProperty("image")]       public string ImageUrl      { get; set; }
        [JsonProperty("image_proxy")] public string ImageProxyUrl { get; set; }
        [JsonProperty("thumb")]       public string ThumbUrl      { get; set; }
        [JsonProperty("thumb_proxy")] public string ThumbProxyUrl { get; set; }
        [JsonProperty("video")]       public string VideoUrl      { get; set; }
    }

    public sealed class KmhDiscordInbound
    {
        [JsonProperty("seq")]  public long   Seq  { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; } = "";

        [JsonProperty("channel_id")]   public ulong  ChannelId { get; set; }
        [JsonProperty("channel_role")] public string ChannelRole { get; set; } = "";
        [JsonProperty("guild_id")]     public ulong  GuildId { get; set; }
        [JsonProperty("dm")]           public bool   IsDirectMessage { get; set; }

        [JsonProperty("message_id")] public ulong  MessageId { get; set; }
        [JsonProperty("content")]    public string Content { get; set; } = "";

        [JsonProperty("author_id")]      public ulong  AuthorId { get; set; }
        [JsonProperty("author_name")]    public string AuthorName { get; set; } = "";
        [JsonProperty("author_display")] public string AuthorDisplay { get; set; } = "";
        [JsonProperty("author_bot")]     public bool   AuthorIsBot { get; set; }
        [JsonProperty("author_webhook")] public bool   AuthorIsWebhook { get; set; }

        [JsonProperty("attachments")] public List<KmhWireAttachment> Attachments { get; set; }
        [JsonProperty("stickers")]    public List<KmhWireSticker>    Stickers { get; set; }
        [JsonProperty("embeds")]      public List<KmhWireEmbedMedia> Embeds { get; set; }

        // Component press: the custom id KMH minted when it built the button.
        [JsonProperty("custom_id")]      public string CustomId { get; set; } = "";
        [JsonProperty("interaction_id")] public string InteractionId { get; set; } = "";

        [JsonProperty("command")] public KmhDiscordCommandRequest Command { get; set; }
    }

    public sealed class KmhDiscordRegisterRequest
    {
        [JsonProperty("protocol")]     public int    Protocol { get; set; } = KmhDiscordProtocol.Version;
        [JsonProperty("server_id")]    public string ServerId { get; set; } = "";
        [JsonProperty("generation")]   public long   Generation { get; set; }
        [JsonProperty("instance_id")]  public string InstanceId { get; set; } = "";
        [JsonProperty("display_name")] public string DisplayName { get; set; } = "";
        [JsonProperty("kmh_version")]  public string KmhVersion { get; set; } = "";
        [JsonProperty("capabilities")] public List<string> Capabilities { get; set; } = new List<string>();
        [JsonProperty("nonce")]        public string Nonce { get; set; } = "";
        [JsonProperty("issued_at")]    public long   IssuedAtUnix { get; set; }
        [JsonProperty("signature")]    public string Signature { get; set; } = "";
    }

    public sealed class KmhDiscordRegisterResponse
    {
        [JsonProperty("ok")]            public bool   Ok { get; set; }
        [JsonProperty("error")]         public string Error { get; set; } = "";
        [JsonProperty("session")]       public string SessionToken { get; set; } = "";
        [JsonProperty("lease_seconds")] public int    LeaseSeconds { get; set; }
        [JsonProperty("bot_user_id")]   public ulong  BotUserId { get; set; }
        [JsonProperty("alias")]         public string Alias { get; set; } = "";
        [JsonProperty("channels")]      public Dictionary<string, ulong> Channels { get; set; }
            = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    }

    public static class KmhDiscordError
    {
        public const string Unauthorized   = "KMH-DSC-401";
        public const string UnknownServer  = "KMH-DSC-404";
        public const string Revoked        = "KMH-DSC-403";
        public const string Replay         = "KMH-DSC-409";
        public const string Offline        = "KMH-DSC-503";
        public const string Timeout        = "KMH-DSC-504";
        public const string NoSuchChannel  = "KMH-DSC-412";
        public const string Unsupported    = "KMH-DSC-501";
        public const string Ambiguous      = "KMH-DSC-300";
    }
}
