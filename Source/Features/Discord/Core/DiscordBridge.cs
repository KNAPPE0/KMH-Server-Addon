using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    // Discord bot lifecycle; skips cleanly without a token, and each sub-feature self-disables when its channel is unset.
    internal static class DiscordBridge
    {
        private static DiscordSocketClient _client;
        private static DiscordConfig       _config;
        private static CancellationTokenSource _presenceCts;

        public static bool IsEnabled => _config?.IsEnabled == true;

        internal static DiscordConfig Config => _config;

        // Null until the bot connects.
        internal static DiscordSocketClient Client => _client;

        public static string DescribeStatus()
        {
            if (_config == null || !_config.IsEnabled)
                return "disabled (no Bot.Token in Config/Discord/DiscordConfig.json)";
            if (_client == null)
                return "configured but client not yet initialized";
            if (_client.ConnectionState == ConnectionState.Connected)
            {
                string who = _client.CurrentUser?.Username ?? "?";
                ulong  id  = _client.CurrentUser?.Id ?? 0;
                return $"connected as {who} (id {id})";
            }
            return $"configured, state: {_client.ConnectionState}";
        }

        // Short enough to feel live without brushing Discord's rate limits.
        private static readonly TimeSpan PresenceRefreshInterval = TimeSpan.FromSeconds(30);

        // Safe even if the bot was never started.
        public static void Reload()
        {
            try { _presenceCts?.Cancel(); } catch { /* ignore */ }
            _presenceCts = null;

            DiscordSocketClient old = _client;
            _client = null;
            if (old != null)
            {
                Task.Run(async () =>
                {
                    try { await old.LogoutAsync().ConfigureAwait(false); } catch { /* best effort */ }
                    try { await old.StopAsync().ConfigureAwait(false); }   catch { /* best effort */ }
                    try { old.Dispose(); }                                  catch { /* best effort */ }
                });
            }

            _config = null;
            Start();
        }

        public static void Start()
        {
            try
            {
                _config = DiscordConfig.LoadOrDefault();
                if (!_config.IsEnabled)
                {
                    ServerLog.Info($"Discord: bridge disabled (no token - set Bot.Token in Config/Discord/DiscordConfig.json "
                                 + $"or the {DiscordTokenSource.EnvVar} environment variable)");
                    return;
                }

                DiscordSocketConfig socketConfig = new DiscordSocketConfig
                {
                    // MessageContent is required, or message.Content arrives empty and text commands never match.
                    GatewayIntents = GatewayIntents.Guilds
                                          | GatewayIntents.GuildMessages
                                          | GatewayIntents.DirectMessages
                                          | GatewayIntents.MessageContent,
                    LogLevel            = LogSeverity.Info,
                    MessageCacheSize    = 0,
                    AlwaysDownloadUsers = false,
                };

                _client                  = new DiscordSocketClient(socketConfig);
                _client.Log             += OnDiscordLog;
                _client.Ready           += OnReady;
                _client.MessageReceived    += OnMessage;
                _client.MessageDeleted     += OnMessageDeleted;
                _client.MessageUpdated     += OnMessageUpdated;
                _client.ButtonExecuted     += DiscordBuyButton.OnInteraction;
                _client.SlashCommandExecuted += DiscordSlashCommands.Handle;

                // Fire-and-forget, so a Discord outage never stops the server from starting.
                Task.Run(async () =>
                {
                    try
                    {
                        ServerLog.Info($"Discord: token source: {DiscordTokenSource.Describe(_config.Bot?.Token)}");
                        await _client.LoginAsync(TokenType.Bot, _config.BotToken).ConfigureAwait(false);
                        await _client.StartAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ServerLog.Error("Discord: bot start failed", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: bridge bootstrap threw", ex);
            }
        }

        private static Task OnDiscordLog(LogMessage msg)
        {
            string line = $"Discord: {msg.Source}: {msg.Message}";
            switch (msg.Severity)
            {
                case LogSeverity.Critical:
                case LogSeverity.Error:
                    if (msg.Exception != null) ServerLog.Error(line, msg.Exception);
                    else                       ServerLog.Error(line);
                    break;
                case LogSeverity.Warning:
                    ServerLog.Warn(line);
                    break;
                case LogSeverity.Info:
                    ServerLog.Info(line);
                    break;
                default:
                    ServerLog.Verbose(line);
                    break;
            }
            return Task.CompletedTask;
        }

        private static Task OnReady()
        {
            string who = _client?.CurrentUser?.Username ?? "?";
            ulong  id  = _client?.CurrentUser?.Id ?? 0;
            ServerLog.Info($"Discord: connected as {who} (id {id})");

            // Ready re-fires on reconnect, so the previous updater is cancelled to keep one per session.
            try { _presenceCts?.Cancel(); } catch { /* ignore */ }
            _presenceCts = new CancellationTokenSource();
            Task.Run(() => PresenceLoop(_presenceCts.Token));

            // Registered here because the guild cache is only populated once Ready fires.
            if (_config?.Bot?.UseSlashCommands == true)
                _ = DiscordSlashCommands.RegisterAsync(_client, _config);

            DiscordEventPublisher.OnBridgeReady();
            DiscordGuildRoleSync.OnBridgeReady();

            // Stated at boot because a shared channel is legal but looks like a misconfiguration.
            ulong rwtCh = _config?.ChatBridgeChannelId ?? 0;
            ulong kmhCh = _config?.KmhChatChannelId ?? 0;
            ServerLog.Info($"Discord: chat wiring - RWT bridge channel {(rwtCh == 0 ? "unset" : rwtCh.ToString())}, "
                         + $"KMH chat channel {(kmhCh == 0 ? "unset" : kmhCh.ToString())}, "
                         + $"KMH inbound {(KmhChatDiscordBridge.InboundEnabled ? "ON" : "OFF")}, "
                         + $"KMH outbound {(KmhChatDiscordBridge.OutboundEnabled ? "ON" : "OFF")}.");
            if (rwtCh != 0 && rwtCh == kmhCh)
                ServerLog.Info("Discord: the RWT chat bridge and KMH chat share one channel - both relays receive each message.");
            if (kmhCh == 0)
                ServerLog.Warn("Discord: KmhChat.Channel is unset - Discord messages will NOT reach KMH chat.");

            return Task.CompletedTask;
        }

        private static async Task PresenceLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _client != null)
                {
                    try
                    {
                        int count = 0;
                        foreach (TCPNetwork.ServerClient c in Network.ServerClients.Keys)
                        {
                            if (c?.IsVerified == true) count++;
                        }
                        string players = count == 1 ? "1 player online" : $"{count} players online";
                        // Name-prefixed so several bots on one host stay distinguishable.
                        string status = $"{KmhServerIdentity.Name} · {players}";
                        await _client.SetActivityAsync(new Game(status, ActivityType.Watching))
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ServerLog.Verbose($"Discord: presence update failed: {ex.Message}");
                    }
                    await Task.Delay(PresenceRefreshInterval, ct).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException) { /* clean shutdown */ }
            catch (Exception ex) { ServerLog.Warn($"Discord: presence loop ended: {ex.Message}"); }
        }

        public static void AnnounceLink(string username, string display, string kind)
        {
            if (_config == null || _config.LinkAnnounceChannelId == 0) return;
            if (string.IsNullOrEmpty(username))                        return;

            string text = (kind == "unlink")
                ? (string.IsNullOrEmpty(display)
                    ? $"**{username}** unlinked their Discord."
                    : $"**{username}** unlinked their Discord (was: **{display}**).")
                : $"**{username}** linked their KMH account to **{display}**.";

            PostToChannel(_config.LinkAnnounceChannelId, text);
        }

        // Strips player IPs and host paths, while leaving version-like strings such as 26.6.9.1 intact.
        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Handshake logs quote the IP, so the whole quoted value goes rather than just an IPv4 shape.
            text = Regex.Replace(text, @"(Handshake with ')[^']*(')", "$1<ip>$2");
            string src = text;
            text = Regex.Replace(src, @"(?:::ffff:)?\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", m =>
            {
                int i = m.Index;
                if (i > 0 && (src[i - 1] == 'v' || src[i - 1] == 'V')) return m.Value;
                if (i >= 8 && src.Substring(i - 8, 8).Equals("version ", StringComparison.OrdinalIgnoreCase)) return m.Value;
                return "<ip>";
            });
            text = Regex.Replace(text, @"[A-Za-z]:\\[^\r\n|]*",
                m => "…\\" + System.IO.Path.GetFileName(m.Value.Trim().TrimEnd('\\')));
            text = Regex.Replace(text, @"/(?:home|root|Users)/[^\r\n|]*",
                m => "…/" + System.IO.Path.GetFileName(m.Value.Trim().TrimEnd('/')));
            return text;
        }

        internal static void PostToChannel(ulong channelId, string text)
            => Task.Run(() => PostToChannelAsync(channelId, text));

        // An ordered stream must await this from one drain loop, or the posts arrive out of order.
        internal static async Task PostToChannelAsync(ulong channelId, string text)
            => await SendToChannelAsync(channelId, text).ConfigureAwait(false);

        // Returns the posted message id, or 0, so a caller can edit or delete it later.
        internal static async Task<ulong> SendToChannelAsync(ulong channelId, string text)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return 0;
            if (channelId == 0 || string.IsNullOrEmpty(text))             return 0;
            text = Redact(text);

            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel))
                {
                    ServerLog.Warn($"Discord: channel {channelId} not found / not a text channel");
                    return 0;
                }
                var sent = await channel.SendMessageAsync(text).ConfigureAwait(false);
                return sent?.Id ?? 0;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Discord: channel send failed: {ex.Message}");
                return 0;
            }
        }

        // Editing rather than deleting, so striking one relayed line does not take the batch with it.
        internal static async Task<bool> EditOwnMessageAsync(ulong channelId, ulong messageId, string newText)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return false;
            if (channelId == 0 || messageId == 0 || newText == null)      return false;
            newText = Redact(newText);

            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel)) return false;
                var msg = await channel.GetMessageAsync(messageId).ConfigureAwait(false);
                if (!(msg is IUserMessage own) || own.Author?.Id != _client.CurrentUser?.Id) return false;
                await own.ModifyAsync(m => m.Content = newText).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Discord: message edit failed: {ex.Message}");
                return false;
            }
        }

        internal static Task<ulong> PostOrEditEmbedAsync(ulong channelId, ulong prevMessageId, Embed embed)
            => PostOrEditEmbedsAsync(channelId, prevMessageId, embed == null ? null : new[] { embed });

        internal static async Task<ulong> PostOrEditEmbedsAsync(ulong channelId, ulong prevMessageId, Embed[] embeds)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return 0;
            if (channelId == 0 || embeds == null || embeds.Length == 0)   return 0;

            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel))
                {
                    ServerLog.Warn(
                        $"Discord: channel {channelId} not found / not a text channel");
                    return 0;
                }

                if (prevMessageId != 0)
                {
                    try
                    {
                        IMessage prev = await channel.GetMessageAsync(prevMessageId).ConfigureAwait(false);
                        if (prev is IUserMessage userMsg)
                        {
                            await userMsg.ModifyAsync(props => props.Embeds = embeds).ConfigureAwait(false);
                            return prevMessageId;
                        }
                    }
                    catch (Exception ex)
                    {
                        // A manually deleted message lands here, so it falls through to a fresh post.
                        ServerLog.Verbose($"Discord: edit fell back to post: {ex.Message}");
                    }
                }

                IUserMessage posted = await channel.SendMessageAsync(embeds: embeds).ConfigureAwait(false);
                return posted?.Id ?? 0;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Discord: PostOrEditEmbed failed: {ex.Message}");
                return 0;
            }
        }

        internal static async Task<bool> DeleteMessageAsync(ulong channelId, ulong messageId)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return false;
            if (channelId == 0 || messageId == 0)                         return false;

            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel)) return false;
                await channel.DeleteMessageAsync(messageId).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                ServerLog.Verbose($"Discord: DeleteMessage failed: {ex.Message}");
                return false;
            }
        }

        internal static void PostEmbedToChannel(ulong channelId, Embed embed)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return;
            if (channelId == 0 || embed == null)                          return;

            Task.Run(async () =>
            {
                try
                {
                    if (!(_client.GetChannel(channelId) is IMessageChannel channel))
                    {
                        ServerLog.Warn(
                            $"Discord: channel {channelId} not found / not a text channel");
                        return;
                    }
                    await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Discord: embed send failed: {ex.Message}");
                }
            });
        }

        internal static void PostEmbedToChannel(ulong channelId, EmbedBuilder builder, string iconFileName)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return;
            if (channelId == 0 || builder == null)                        return;

            string iconPath = DiscordIcons.ResolveExistingPath(_config, iconFileName);
            if (iconPath != null)
                builder.WithThumbnailUrl($"attachment://{Uri.EscapeDataString(System.IO.Path.GetFileName(iconPath))}");
            Embed embed = builder.Build();

            Task.Run(async () =>
            {
                try
                {
                    if (!(_client.GetChannel(channelId) is IMessageChannel channel))
                    {
                        ServerLog.Warn($"Discord: channel {channelId} not found / not a text channel");
                        return;
                    }
                    if (iconPath != null)
                        await channel.SendFileAsync(iconPath, embed: embed).ConfigureAwait(false);
                    else
                        await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Discord: embed+icon send failed: {ex.Message}");
                }
            });
        }

        // The relay index acts as the whitelist, so a deletion elsewhere can never remove a KMH chat line.
        private static Task OnMessageDeleted(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
        {
            try
            {
                if (!KmhChatRelayIndex.TryTakeInbound(message.Id, out long kmhId)) return Task.CompletedTask;
                if (Features.Chat.ChatHandler.RemoveRelayed(kmhId, "a moderator on Discord"))
                    ServerLog.Info($"Discord->KMH: message #{kmhId} removed in-game because its Discord original was deleted.");
            }
            catch (Exception ex) { ServerLog.Warn($"Discord: delete sync failed: {ex.Message}"); }
            return Task.CompletedTask;
        }

        // Size is judged here because Discord states the byte count up front; ChatImagePolicy still decides visibility.
        private static string FirstImageAttachment(IMessage message)
        {
            try
            {
                var cfg = Features.Chat.ChatConfig.Current;
                if (cfg == null || !cfg.AllowImagePreviews || message == null) return null;

                if (message.Attachments != null)
                    foreach (var a in message.Attachments)
                    {
                        if (a == null) continue;
                        string name = a.Filename ?? "";
                        // Only an image is ever fetched, so only an image has to fit under the cap.
                        if (Features.Chat.ChatImagePolicy.LooksLikeVideo(cfg, name))
                        {
                            MediaLog(message, $"attachment '{name}' is a video, offered as a link ({a.Size} bytes)");
                            return a.Url;
                        }
                        if (a.Size > cfg.MaxImageBytes)
                        {
                            MediaLog(message, $"attachment '{name}' skipped: {a.Size} bytes exceeds MaxImageBytes {cfg.MaxImageBytes}");
                            continue;
                        }
                        if (!Features.Chat.ChatImagePolicy.LooksLikeImage(cfg, name))
                        {
                            MediaLog(message, $"attachment '{name}' skipped: not a known image extension");
                            continue;
                        }
                        MediaLog(message, $"attachment '{name}' selected ({a.Size} bytes)");
                        return a.Url;
                    }

                string sticker = FirstSticker(message);
                if (!string.IsNullOrEmpty(sticker)) { MediaLog(message, $"sticker selected: {sticker}"); return sticker; }

                string embed = FirstEmbedImage(message);
                if (!string.IsNullOrEmpty(embed)) return embed;

                // Last, because an attachment or embed always says more about a message than an emoji in its caption.
                string emoji = Features.Chat.ChatDiscordEmoji.FirstEmojiUrl(message.Content);
                if (!string.IsNullOrEmpty(emoji)) { MediaLog(message, $"custom emoji selected: {emoji}"); return emoji; }
            }
            catch { }
            return null;
        }

        // A lottie sticker is JSON rather than a picture, so it yields nothing no decoder could read.
        private static string FirstSticker(IMessage message)
        {
            if (message?.Stickers == null || message.Stickers.Count == 0) return null;
            foreach (IStickerItem s in message.Stickers)
            {
                if (s == null) continue;
                string url = Features.Chat.ChatDiscordEmoji.StickerUrl(s.Id, s.Format.ToString());
                if (!string.IsNullOrEmpty(url)) return url;
                MediaLog(message, $"sticker '{s.Name}' is {s.Format} - no raster form to draw");
            }
            return null;
        }

        // ProxyUrl is preferred over Url because it keeps the fetch on Discord rather than a third-party host.
        private static string FirstEmbedImage(IMessage message)
        {
            if (message?.Embeds == null) return null;
            var cfg = Features.Chat.ChatConfig.Current;

            foreach (IEmbed e in message.Embeds)
            {
                if (e == null) continue;

                MediaLog(message, $"embed type='{e.Type}' image={Present(e.Image?.Url, e.Image?.ProxyUrl)}"
                                + $" thumb={Present(e.Thumbnail?.Url, e.Thumbnail?.ProxyUrl)}"
                                + $" video={Present(e.Video?.Url, null)}");

                string url = Pick(message, "image", e.Image?.ProxyUrl, e.Image?.Url)
                          ?? Pick(message, "thumbnail", e.Thumbnail?.ProxyUrl, e.Thumbnail?.Url);
                if (!string.IsNullOrEmpty(url)) return url;

                // A gifv embed has no image slot at all, only a Video pointing at an mp4.
                if (!string.IsNullOrEmpty(e.Video?.Url)
                    && Features.Chat.ChatImagePolicy.LooksLikeVideo(cfg, e.Video.Value.Url))
                {
                    MediaLog(message, $"video slot selected: {Features.Chat.ChatMediaUrl.Describe(cfg, e.Video.Value.Url)}");
                    return e.Video.Value.Url;
                }
            }
            MediaLog(message, "no embed carried usable media");
            return null;
        }

        private static string Present(string a, string b)
            => string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b) ? "-" : "y";

        // The decision lives in ChatMediaSelection so it can be exercised without a Discord.Net message object.
        private static string Pick(IMessage message, string slot, string proxy, string original)
        {
            string chosen = Features.Chat.ChatMediaSelection.Choose(
                Features.Chat.ChatConfig.Current, proxy, original, out string why);
            MediaLog(message, $"{slot}: {(string.IsNullOrEmpty(chosen) ? "nothing usable - " : "chose ")}{why}");
            return string.IsNullOrEmpty(chosen) ? null : chosen;
        }

        // Carries the message id so a player's report can be matched to the decision that produced it.
        private static void MediaLog(IMessage message, string what)
            => ServerLog.Debug($"Discord media [msg {(message == null ? "?" : message.Id.ToString())}]: {what}");

        // Discord attaches a link preview as a later edit, so the embed does not exist when the message arrives.
        private static Task OnMessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel channel)
        {
            try
            {
                if (after == null || _config == null) return Task.CompletedTask;
                if (_config.KmhChatChannelId == 0 || (channel?.Id ?? 0) != _config.KmhChatChannelId) return Task.CompletedTask;

                string image = FirstImageAttachment(after);
                if (string.IsNullOrEmpty(image)) return Task.CompletedTask;

                KmhChatDiscordBridge.AttachLateImage(after.Id, image);
            }
            catch (Exception ex) { ServerLog.Warn($"Discord: late embed update failed: {ex.Message}"); }
            return Task.CompletedTask;
        }
        private static async Task OnMessage(SocketMessage message)
        {
            try
            {
                if (message == null)                 return;
                if (message.Author == null)          return;
                // Our own bot is always skipped; other bots are an owner's choice because their posts often matter.
                ulong meId = _client?.CurrentUser?.Id ?? 0;
                if (message.Author.IsBot || message.Author.IsWebhook)
                {
                    if (meId != 0 && message.Author.Id == meId) return;
                    if (_config.KmhChat?.RelayBots != true)
                    {
                        MediaLog(message, $"bot '{message.Author.Username}' skipped - KmhChat.RelayBots is off");
                        return;
                    }
                    if (Features.Chat.ChatDiscordText.LooksLikeRelayEcho(message.Content))
                    {
                        MediaLog(message, $"bot '{message.Author.Username}' skipped - the message is a relay echo");
                        return;
                    }
                }

                string prefix = _config.CommandPrefix ?? "!";
                string text   = (message.Content ?? "").Trim();

                // The mention is stripped so "@Bot !kmh-x" parses like a plain command.
                bool mentionedMe = false;
                if (_config.RequireMentionForCommands)
                {
                    ulong myId = _client?.CurrentUser?.Id ?? 0;
                    if (myId != 0)
                    {
                        string mA = $"<@{myId}>", mB = $"<@!{myId}>";
                        if (text.Contains(mA) || text.Contains(mB))
                        {
                            mentionedMe = true;
                            text = text.Replace(mA, "").Replace(mB, "").Trim();
                        }
                    }
                }

                // Both bridges see every message, or a shared channel would silently starve one of them.
                bool notCommand = string.IsNullOrEmpty(text) || !text.StartsWith(prefix, StringComparison.Ordinal);
                bool hasText    = !string.IsNullOrEmpty(text) && notCommand;
                string image    = notCommand ? FirstImageAttachment(message) : null;

                // An attachment with no caption is still a message, so relayability cannot test the body alone.
                bool relayable  = hasText || !string.IsNullOrEmpty(image);
                bool relayed    = false;
                ulong chId      = message.Channel?.Id ?? 0;

                // The RWT bridge relays a line of text and has nowhere to put a picture.
                if (hasText && _config.ChatBridgeChannelId != 0 && chId == _config.ChatBridgeChannelId)
                {
                    DiscordChatBridge.RelayDiscordToInGame(ResolveDisplay(message.Author), text);
                    relayed = true;
                }

                if (relayable && _config.KmhChatChannelId != 0 && chId == _config.KmhChatChannelId)
                {
                    KmhChatDiscordBridge.Ingest(message.Author.Id, ResolveDisplay(message.Author), text, message.Id, image);
                    relayed = true;
                }

                if (relayed) return;


                if (!text.StartsWith(prefix, StringComparison.Ordinal)) return;

                // A DM is already per-bot, so it needs no mention.
                if (_config.RequireMentionForCommands && !mentionedMe && message.Channel is SocketGuildChannel) return;

                // DMs stay open so a player can always link.
                if (message.Channel is SocketGuildChannel gc
                    && _config.AllowedGuildIds != null
                    && _config.AllowedGuildIds.Length > 0)
                {
                    bool allowed = false;
                    foreach (ulong id in _config.AllowedGuildIds)
                    {
                        if (id == gc.Guild.Id) { allowed = true; break; }
                    }
                    if (!allowed) return;
                }

                if (!_config.CommandsAllowedIn(message.Channel?.Id ?? 0, !(message.Channel is SocketGuildChannel)))
                    return;

                string   body  = text.Substring(prefix.Length).Trim();
                string[] parts = body.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;

                string cmd = parts[0].ToLowerInvariant();
                switch (cmd)
                {
                    case "kmh-link":
                        await HandleLink(message, parts).ConfigureAwait(false);
                        break;
                    case "kmh-unlink":
                        await HandleUnlink(message).ConfigureAwait(false);
                        break;
                    case "kmh-whois":
                        await HandleWhois(message, parts).ConfigureAwait(false);
                        break;
                    case "kmh-rep":
                        await HandleReputation(message, parts).ConfigureAwait(false);
                        break;
                    case "kmh-rank":
                        await HandleRank(message, parts).ConfigureAwait(false);
                        break;
                    case "kmh-leaderboard":
                    case "kmh-lb":
                        await HandleLeaderboard(message, parts).ConfigureAwait(false);
                        break;
                    case "kmh-help":
                        await Reply(message,
                            "KMH bot commands:\n" +
                            $"  `{prefix}kmh-link <code>`        - bind a Discord identity to an in-game account\n" +
                            $"  `{prefix}kmh-link status`        - show your current link\n" +
                            $"  `{prefix}kmh-unlink`              - remove your link\n" +
                            $"  `{prefix}kmh-whois <player>`     - show who an in-game player is linked to\n" +
                            $"  `{prefix}kmh-rep <player>`       - a player's quest reputation + tier\n" +
                            $"  `{prefix}kmh-rank [player]`      - full stat card with per-metric ranks\n" +
                            $"  `{prefix}kmh-leaderboard [sort]` - top players (sort: " +
                            string.Join(", ", DiscordLeaderboardBuilder.SupportedSorts) + ")\n" +
                            $"  `{prefix}kmh-leaderboard guilds [sort]` - top guilds (sort: " +
                            string.Join(", ", DiscordLeaderboardBuilder.SupportedGuildSorts) + ")\n" +
                            $"  `{prefix}kmh-leaderboard rep`    - top players by quest reputation\n" +
                            $"  `{prefix}kmh-market [page|mine]` - browse open marketplace listings\n" +
                            $"  `{prefix}kmh-quests [page]`      - browse open quest board\n" +
                            $"  `{prefix}kmh-treasury`           - your treasury contents (requires link)\n" +
                            $"  `{prefix}kmh-buy <id> [qty]`     - buy from a listing (requires link)\n" +
                            $"  `{prefix}kmh-sell <item> <qty> <price>` - post a listing from your treasury\n" +
                            $"  `{prefix}kmh-cancel <id>`        - cancel your listing (refunds items)\n" +
                            $"  `{prefix}kmh-find <query>`       - search open listings by name\n" +
                            $"  `{prefix}kmh-compare <item>`     - price summary + cheapest listings\n" +
                            $"  `{prefix}kmh-history [n]`        - your last N treasury transactions (requires link)\n" +
                            $"  `{prefix}kmh-items [query]`      - browse the server's known item catalog\n" +
                            $"  `{prefix}kmh-showcase`            - publish your marketplace as a single live embed\n" +
                            $"  `{prefix}kmh-showcase tagline …`  - set the headline on your showcase\n" +
                            $"  `{prefix}kmh-showcase delete`     - remove your showcase post\n" +
                            $"  `{prefix}kmh-wtb add <item> <qty> <max-price>` - record what you want to buy\n" +
                            $"  `{prefix}kmh-wtb` / `tagline`/`delete` - publish/manage your Want-To-Buy board\n" +
                            (_config.ChatBridgeChannelId != 0
                                ? "_Chat bridge active - non-command messages in the bridge channel relay to in-game._"
                                : "_Chat bridge is not configured for this server._"))
                            .ConfigureAwait(false);
                        break;
                    default:
                        bool handled = await DiscordBrowseCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            handled = await DiscordTradeCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            handled = await DiscordShowcaseCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            handled = await DiscordWtbCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            // Last, so a core command wins any name collision.
                            DiscordExtensionCommands.TryDispatch(message, cmd, parts);
                        break;
                }
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: OnMessage threw", ex);
            }
        }

        private static async Task HandleLink(SocketMessage message, string[] parts)
        {
            if (parts.Length >= 2 && string.Equals(parts[1], "status", StringComparison.OrdinalIgnoreCase))
            {
                ulong  authorIdStatus = message.Author?.Id ?? 0;
                string bound   = LinkedAccountsStore.FindUsernameByDiscordId(authorIdStatus);   // id-only; display is impersonable
                if (string.IsNullOrEmpty(bound))
                {
                    await Reply(message, "Your Discord identity is not currently linked to any KMH account.")
                        .ConfigureAwait(false);
                }
                else
                {
                    await Reply(message, $"Your Discord identity is linked to in-game player **{bound}**.")
                        .ConfigureAwait(false);
                }
                return;
            }

            if (parts.Length < 2)
            {
                await Reply(message,
                    "Usage: `!kmh-link <code>` (get a code in-game with `/kmh link`) or `!kmh-link status`.")
                    .ConfigureAwait(false);
                return;
            }
            string code = parts[1];
            if (!DiscordLinkFlow.TryConsume(code, out string username))
            {
                await Reply(message,
                    "That code is invalid or expired. Run `/kmh link` in-game to get a new one.")
                    .ConfigureAwait(false);
                return;
            }

            string display2 = ResolveDisplay(message.Author);
            ulong  authorId = message.Author?.Id ?? 0;
            if (!LinkedAccountsStore.SetLink(username, display2, authorId, out string linkRefusal))
            {
                ServerLog.Warn($"Discord: link refused for '{username}' <-> '{display2}': {linkRefusal}");
                // The code was already spent getting here, so say plainly that a new one is needed.
                await Reply(message, linkRefusal + " Then run `/kmh link` in-game for a fresh code.")
                    .ConfigureAwait(false);
                return;
            }
            LinkedAccountsHandler.BroadcastSnapshot();
            AnnounceLink(username, display2, "link");

            ServerLog.Info($"Discord: linked '{username}' <-> '{display2}'");
            await Reply(message,
                $"Linked **{username}** to **{display2}**. Unlink anytime with `!kmh-unlink`.")
                .ConfigureAwait(false);
        }

        private static async Task HandleUnlink(SocketMessage message)
        {
            ulong authorId = message.Author?.Id ?? 0;
            string display  = ResolveDisplay(message.Author);
            // Id only: a display fallback would let someone unlink another player by copying their display name.
            string victim   = LinkedAccountsStore.FindUsernameByDiscordId(authorId);
            if (string.IsNullOrEmpty(victim))
            {
                await Reply(message,
                    "No KMH account is currently linked to your Discord identity.")
                    .ConfigureAwait(false);
                return;
            }
            LinkedAccountsStore.Unlink(victim);
            LinkedAccountsHandler.BroadcastSnapshot();
            AnnounceLink(victim, display, "unlink");
            ServerLog.Info($"Discord: unlinked '{victim}' (requested via Discord by '{display}')");
            await Reply(message, $"Unlinked **{victim}**.").ConfigureAwait(false);
        }

        // Shares the auto-poster's builder, so an on-demand board matches a posted one.
        private static async Task HandleLeaderboard(SocketMessage message, string[] parts)
        {
            int topN = _config?.LeaderboardTopCount ?? 10;
            if (topN < 1)  topN = 10;
            if (topN > 25) topN = 25;

            if (parts.Length >= 2
                && (string.Equals(parts[1], "guilds", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(parts[1], "guild",  StringComparison.OrdinalIgnoreCase)
                 || string.Equals(parts[1], "g",      StringComparison.OrdinalIgnoreCase)))
            {
                string gsort = parts.Length >= 3
                    ? DiscordLeaderboardBuilder.NormalizeGuildSort(parts[2])
                    : DiscordLeaderboardBuilder.DefaultGuildSort;
                Embed gembed = DiscordLeaderboardBuilder.BuildGuilds(topN, gsort);
                if (gembed == null)
                {
                    await Reply(message, "No guilds on this server yet - create one with `/kmh guild create <name>` in-game.")
                        .ConfigureAwait(false);
                    return;
                }
                try { await message.Channel.SendMessageAsync(embed: gembed).ConfigureAwait(false); }
                catch (Exception gex) { ServerLog.Warn($"Discord: guild leaderboard reply failed: {gex.Message}"); }
                return;
            }

            if (parts.Length >= 2
                && (string.Equals(parts[1], "rep",        StringComparison.OrdinalIgnoreCase)
                 || string.Equals(parts[1], "reputation", StringComparison.OrdinalIgnoreCase)))
            {
                Embed rembed = DiscordLeaderboardBuilder.BuildReputation(topN);
                if (rembed == null)
                {
                    await Reply(message, "No reputation recorded yet - players earn it by completing quests.")
                        .ConfigureAwait(false);
                    return;
                }
                try { await message.Channel.SendMessageAsync(embed: rembed).ConfigureAwait(false); }
                catch (Exception rex) { ServerLog.Warn($"Discord: reputation leaderboard reply failed: {rex.Message}"); }
                return;
            }

            string sort = parts.Length >= 2
                ? DiscordLeaderboardBuilder.NormalizeSort(parts[1])
                : DiscordLeaderboardBuilder.DefaultSort;
            Embed embed = DiscordLeaderboardBuilder.Build(topN, sort);
            if (embed == null)
            {
                await Reply(message, "No leaderboard data yet - no players have been seen on this server.")
                    .ConfigureAwait(false);
                return;
            }
            try
            {
                await message.Channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Discord: leaderboard reply failed: {ex.Message}");
            }
        }

        // With no name, the caller's own linked account is used.
        private static async Task HandleRank(SocketMessage message, string[] parts)
        {
            string query = parts.Length >= 2 ? string.Join(" ", parts, 1, parts.Length - 1).Trim() : "";
            if (query.Length == 0)
            {
                query = LinkedAccounts.LinkedAccountsStore.FindUsernameByDiscordId(message.Author.Id);   // id-only; no impersonable display fallback
                if (string.IsNullOrEmpty(query))
                {
                    await Reply(message, "Usage: `!kmh-rank <player>` (or link your account with `!kmh-link` to use it bare).")
                        .ConfigureAwait(false);
                    return;
                }
            }

            Embed card = DiscordLeaderboardBuilder.BuildPlayerCard(query, out List<string> matches);
            if (card == null)
            {
                string msg = matches != null && matches.Count > 0
                    ? $"Multiple players match \"{query}\": {string.Join(", ", matches)}"
                    : $"No player named \"{query}\" has been seen on this server.";
                await Reply(message, msg).ConfigureAwait(false);
                return;
            }
            try { await message.Channel.SendMessageAsync(embed: card).ConfigureAwait(false); }
            catch (Exception ex) { ServerLog.Warn($"Discord: rank reply failed: {ex.Message}"); }
        }

        // Stays quiet for an unknown or unlinked player rather than confirming the name exists.
        private static async Task HandleWhois(SocketMessage message, string[] parts)
        {
            if (parts.Length < 2)
            {
                await Reply(message, "Usage: `!kmh-whois <player>`").ConfigureAwait(false);
                return;
            }
            string player = parts[1];
            if (LinkedAccountsStore.TryGetLink(player, out string display) && !string.IsNullOrEmpty(display))
            {
                await Reply(message, $"**{player}** is linked to **{display}**.").ConfigureAwait(false);
            }
            else
            {
                await Reply(message, $"**{player}** is not linked (or doesn't exist).").ConfigureAwait(false);
            }
        }

        private static async Task HandleReputation(SocketMessage message, string[] parts)
        {
            if (parts.Length < 2)
            {
                await Reply(message, "Usage: `!kmh-rep <player>`").ConfigureAwait(false);
                return;
            }
            string player = parts[1];
            var (score, tier) = Features.Reputation.ReputationStore.Get(player);
            await Reply(message, $"**{player}** - {tier} ({score:+0;-0;0} reputation)").ConfigureAwait(false);
        }

        private static async Task Reply(SocketMessage message, string text)
        {
            try
            {
                await message.Channel.SendMessageAsync(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Discord: reply send failed: {ex.Message}");
            }
        }

        // A migrated account carries #0000, which is dropped rather than shown.
        private static string ResolveDisplay(IUser user)
        {
            string g = user?.GlobalName;
            if (!string.IsNullOrEmpty(g)) return g;
            string u = user?.Username;
            string d = user?.Discriminator;
            if (!string.IsNullOrEmpty(d) && d != "0" && d != "0000") return $"{u}#{d}";
            return u ?? "(unknown)";
        }
    }
}
