using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kmh.Discord.Wire;

namespace KMHServerAddon.Features.Discord
{
    // The same shapes arrive whether this process holds the gateway or a coordinator holds it for the fleet.
    internal sealed class KmhDiscordUser
    {
        public ulong  Id        { get; set; }
        public string Username  { get; set; } = "";
        // What a relayed line is signed with: a global name where there is one, otherwise the old name#discriminator.
        public string Display   { get; set; } = "";
        public bool   IsBot     { get; set; }
        public bool   IsWebhook { get; set; }
    }

    internal sealed class KmhDiscordAttachment
    {
        public string Filename { get; set; } = "";
        public string Url      { get; set; } = "";
        public int    Size     { get; set; }
    }

    internal sealed class KmhDiscordSticker
    {
        public ulong  Id     { get; set; }
        public string Name   { get; set; } = "";
        public string Format { get; set; } = "";
    }

    // Only the slots the media picker looks at; an embed carries far more than KMH ever reads.
    internal sealed class KmhDiscordEmbed
    {
        public string Type          { get; set; } = "";
        public string ImageUrl      { get; set; }
        public string ImageProxyUrl { get; set; }
        public string ThumbUrl      { get; set; }
        public string ThumbProxyUrl { get; set; }
        public string VideoUrl      { get; set; }
    }

    // Replies go back through whichever gateway delivered the message.
    internal sealed class KmhDiscordChannel
    {
        private readonly Func<ulong, string, KmhEmbedSpec[], KmhComponentSpec, Task> _send;
        private readonly Func<ulong, string, IReadOnlyList<(string Name, byte[] Bytes)>, Task> _sendFiles;

        public KmhDiscordChannel(ulong id, bool isDirectMessage,
            Func<ulong, string, KmhEmbedSpec[], KmhComponentSpec, Task> send,
            Func<ulong, string, IReadOnlyList<(string Name, byte[] Bytes)>, Task> sendFiles = null)
        {
            Id = id;
            IsDirectMessage = isDirectMessage;
            _send = send;
            _sendFiles = sendFiles;
        }

        public ulong Id { get; }
        public bool  IsDirectMessage { get; }

        // Only a gateway this process holds can upload a file; the coordinator wire carries text.
        public bool CanSendFiles => _sendFiles != null;

        // A card is written out as compact text here, so no reply reaches Discord as an embed.
        public Task SendMessageAsync(string text = null, KmhEmbedSpec embed = null, KmhEmbedSpec[] embeds = null,
                                     KmhComponentSpec components = null)
        {
            if (_send == null) return Task.CompletedTask;
            KmhEmbedSpec[] all = embeds ?? (embed == null ? null : new[] { embed });
            string body = text;
            if (all != null && all.Length > 0)
            {
                string card = DiscordCompactText.Render(all);
                body = string.IsNullOrEmpty(text) ? card : DiscordCompactText.Cap(text + "\n" + card, DiscordCompactText.MaxChars);
            }
            try { return _send(Id, body, null, components) ?? Task.CompletedTask; }
            catch (Exception) { return Task.CompletedTask; }
        }

        public Task SendFilesAsync(string text, IReadOnlyList<(string Name, byte[] Bytes)> files)
        {
            if (_sendFiles == null || files == null || files.Count == 0) return SendMessageAsync(text);
            try { return _sendFiles(Id, text, files) ?? Task.CompletedTask; }
            catch (Exception) { return Task.CompletedTask; }
        }
    }

    internal sealed class KmhDiscordMessage
    {
        public ulong             Id      { get; set; }
        public string            Content { get; set; } = "";
        public KmhDiscordUser    Author  { get; set; }
        public KmhDiscordChannel Channel { get; set; }

        // 0 for a direct message, which is what separates "anyone may link here" from a guild allow-list.
        public ulong GuildId { get; set; }

        public IReadOnlyList<KmhDiscordAttachment> Attachments { get; set; } = Array.Empty<KmhDiscordAttachment>();
        public IReadOnlyList<KmhDiscordSticker>    Stickers    { get; set; } = Array.Empty<KmhDiscordSticker>();
        public IReadOnlyList<KmhDiscordEmbed>      Embeds      { get; set; } = Array.Empty<KmhDiscordEmbed>();
    }

    // Discord gives an interaction three seconds, which is why its reply path is not an ordinary channel post.
    internal sealed class KmhDiscordInteraction
    {
        public string          CustomId  { get; set; } = "";
        public ulong           ChannelId { get; set; }
        public ulong           GuildId   { get; set; }
        public KmhDiscordUser  User      { get; set; }

        // Set by whichever gateway produced it; a game server never touches the interaction behind them.
        internal Func<string, bool, Task> Responder;
        internal Func<Task>               Deferrer;
        internal Func<string, bool, Task> FollowupResponder;

        public Task Respond(string text, bool ephemeral = true)
            => Responder == null ? Task.CompletedTask : (Responder(text, ephemeral) ?? Task.CompletedTask);

        public Task Defer()
            => Deferrer == null ? Task.CompletedTask : (Deferrer() ?? Task.CompletedTask);

        public Task Followup(string text, bool ephemeral = true)
            => FollowupResponder == null ? Respond(text, ephemeral) : (FollowupResponder(text, ephemeral) ?? Task.CompletedTask);
    }
}
