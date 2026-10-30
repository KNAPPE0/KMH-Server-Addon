using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    // Skips cleanly without a token, and each sub-feature self-disables when its channel is unset.
    internal static class DiscordBridge
    {
        private static DiscordSocketClient _client;
        private static DiscordConfig       _config;
        private static CancellationTokenSource _presenceCts;

        public static bool IsEnabled => _config?.IsEnabled == true;

        internal static DiscordConfig Config => _config;
        internal static void SetConfigForTest(DiscordConfig cfg) => _config = cfg;

        // Null until the bot connects.
        internal static DiscordSocketClient Client => _client;

        public static string DescribeStatus()
        {
            if (_config == null) return "disabled (not started)";
            // Switched off says nothing about whether a token is stored, so the two are reported apart.
            if (!_config.Enabled) return "disabled (Enabled is false in Config/Discord/DiscordConfig.json)";
            if (!_config.IsEnabled)
                return $"disabled (no Bot.Token in Config/Discord/DiscordConfig.json and no {DiscordTokenSource.EnvVar})";
            if (_config.CoordinatorMode) return DiscordCoordinatorLink.Describe();
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

        // Each reload is a fresh gateway login, and Discord resets a bot's token past 1000 of those in a day.
        internal static readonly TimeSpan ReloadCooldown = TimeSpan.FromMinutes(2);
        private static long _lastReloadTicks;

        internal static bool ReloadAllowed(long lastTicks, long nowTicks)
            => lastTicks == 0 || nowTicks - lastTicks >= ReloadCooldown.Ticks;

        // Claims the slot for the caller, so two requests in the same moment cannot both log in again.
        internal static bool TryClaimReload(out TimeSpan wait)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = System.Threading.Interlocked.Read(ref _lastReloadTicks);
            wait = TimeSpan.FromTicks(Math.Max(0, last + ReloadCooldown.Ticks - now));
            if (!ReloadAllowed(last, now)) return false;
            return System.Threading.Interlocked.CompareExchange(ref _lastReloadTicks, now, last) == last;
        }

        // Safe even if the bot was never started.
        public static void Reload()
        {
            try { _presenceCts?.Cancel(); } catch { }
            _presenceCts = null;
            DiscordCoordinatorLink.Stop();

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
            // The live messages read Enabled at start, so a reload that turns Discord on must restart them too.
            DiscordLive.Start();
        }

        // Live loops skip their tick until this holds, rather than logging "channel not found" before Ready.
        internal static bool Ready
            => _config?.IsEnabled == true
            && (_config.CoordinatorMode ? DiscordCoordinatorLink.Active : _client?.ConnectionState == ConnectionState.Connected);

        // The coordinator wire returns no message ids, so nothing posted through it can be edited later.
        internal static bool CanEdit => _config?.IsEnabled == true && !_config.CoordinatorMode;

        private static long _sendFailures;
        private static long _lastSendFailureTicks;
        internal static long SendFailures => System.Threading.Interlocked.Read(ref _sendFailures);
        internal static DateTime LastSendFailureUtc
        {
            get { long t = System.Threading.Interlocked.Read(ref _lastSendFailureTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); }
        }

        private static void NoteSendFailure()
        {
            System.Threading.Interlocked.Increment(ref _sendFailures);
            System.Threading.Interlocked.Exchange(ref _lastSendFailureTicks, DateTime.UtcNow.Ticks);
        }

        // Every post and edit names its mentions: none, unless an alert is told to ping a role.
        internal static AllowedMentions Mentions(IReadOnlyList<ulong> roles)
            => roles == null || roles.Count == 0
                ? AllowedMentions.None
                : new AllowedMentions(AllowedMentionTypes.None) { RoleIds = new List<ulong>(roles) };

        internal static void Once(string key, string message)
        {
            if (Diagnostics.KmhLogThrottle.Allow("discord.once." + key, TimeSpan.FromDays(3650), DateTime.UtcNow.Ticks, out _))
                ServerLog.Info(message);
        }

        public static void Start()
        {
            try
            {
                _config = DiscordConfig.LoadOrDefault();
                foreach (string named in DiscordConfig.NamedRoleEntries(_config.Roles))
                    ServerLog.Warn($"Discord: role \"{named}\" in DiscordConfig.json Roles is a name, not an id - it is ignored. "
                                 + "Copy the role's id (Developer Mode, right-click the role) instead.");
                if (!_config.IsEnabled)
                {
                    ServerLog.Diag($"Discord: bridge disabled (no token - set Bot.Token in Config/Discord/DiscordConfig.json "
                                 + $"or the {DiscordTokenSource.EnvVar} environment variable)");
                    return;
                }

                // Coordinator mode creates no gateway client, so a leftover token cannot start a second bot session.
                if (_config.CoordinatorMode)
                {
                    if (!string.IsNullOrWhiteSpace(_config.Bot?.Token) || DiscordTokenSource.FromEnvironment)
                        ServerLog.Warn("Discord: coordinator mode is on, so the local bot token is ignored - "
                                     + "remove it from this server once migration is done.");
                    ServerLog.Info($"Discord: coordinator mode - connecting to {_config.Coordinator?.Url}");
                    DiscordCoordinatorLink.Start(_config);
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
                _client.ButtonExecuted     += OnButton;
                _client.SlashCommandExecuted += DiscordDirectSlash.HandleAsync;

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
                    // Discord.Net's "Serializer Error" carries the payload it could not read only in the exception.
                    ServerLog.Warn(msg.Exception == null ? line
                        : line + " (" + Util.KmhSafe.Cap(msg.Exception.GetType().Name + ": " + (msg.Exception.InnerException ?? msg.Exception).Message.Replace('\n', ' ').Replace('\r', ' '), 240) + ")");
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
            try { _presenceCts?.Cancel(); } catch { }
            _presenceCts = new CancellationTokenSource();
            Task.Run(() => PresenceLoop(_presenceCts.Token));

            // Registered here because the guild cache is only populated once Ready fires.
            if (_config?.Bot?.UseSlashCommands == true)
                _ = DiscordDirectSlash.RegisterAsync(_client, _config);

            DiscordEventPublisher.OnBridgeReady();
            DiscordGuildRoleSync.OnBridgeReady();

            // Stated at boot because a shared channel is legal but looks like a misconfiguration.
            ulong rwtCh = _config?.ChatBridgeChannelId ?? 0;
            ulong kmhCh = _config?.KmhChatChannelId ?? 0;
            ServerLog.Info($"Discord: chat wiring - RWT bridge channel {(rwtCh == 0 ? "unset" : rwtCh.ToString())}, "
                         + $"Colony Nexus chat channel {(kmhCh == 0 ? "unset" : kmhCh.ToString())}, "
                         + $"Colony Nexus inbound {(KmhChatDiscordBridge.InboundEnabled ? "ON" : "OFF")}, "
                         + $"Colony Nexus outbound {(KmhChatDiscordBridge.OutboundEnabled ? "ON" : "OFF")}.");
            ulong worldCh = _config?.WorldChatChannelId ?? 0;
            ServerLog.Info($"Discord: world chat channel {(worldCh == 0 ? "unset" : worldCh.ToString())}, "
                         + $"inbound {(KmhWorldChatDiscordBridge.InboundEnabled ? "ON" : "OFF")}, "
                         + $"outbound {(KmhWorldChatDiscordBridge.OutboundEnabled ? "ON" : "OFF")}.");

            if (rwtCh != 0 && rwtCh == kmhCh)
                ServerLog.Info("Discord: the RWT chat bridge and Colony Nexus chat share one channel - an inbound line is "
                             + "delivered by the RWT bridge only, so nobody reads it twice. Images still go to Colony Nexus "
                             + "chat, which is the only one that can show them.");
            if (worldCh != 0 && worldCh == kmhCh)
                ServerLog.Info("Discord: world chat and Colony Nexus chat share one channel - inbound messages go to world "
                             + "chat only, so players do not see each one twice. In-game chat still reaches Discord.");
            if (kmhCh == 0)
                ServerLog.Warn("Discord: KmhChat.Channel is unset - Discord messages will NOT reach Colony Nexus chat.");

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

            string who = DiscordText.SafeName(username), was = DiscordText.SafeName(display);
            string text = (kind == "unlink")
                ? (string.IsNullOrEmpty(display)
                    ? $"🔓 **{who}** unlinked their Discord."
                    : $"🔓 **{who}** unlinked their Discord (was **{was}**).")
                : $"🔗 **{who}** linked their Colony Nexus account to **{was}**.";

            PostToChannel(_config.LinkAnnounceChannelId, text);
        }

        // What world chat will not carry stays with KMH chat, or on a shared channel it reaches nobody.
        internal static bool WorldCarries(DiscordConfig cfg, ulong chId, bool hasText, bool fromBot, bool relayUp)
            => hasText && cfg.WorldChatChannelId != 0 && chId == cfg.WorldChatChannelId && cfg.WorldChatFromDiscordOn
            && relayUp && (!fromBot || cfg.WorldChatRelayBotsOn);

        // Strips registered secrets, player IPs and host paths, while leaving version-like strings such as 26.6.9.1 intact.
        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = KMHServerAddon.Maintenance.KmhRedact.Text(text);
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
            // IPv6 too, parsed rather than shape-matched so a clock like 12:34:56 survives.
            text = Regex.Replace(text, @"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:])", m =>
                System.Net.IPAddress.TryParse(m.Value, out System.Net.IPAddress a)
                && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "<ip>" : m.Value);
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
        internal static async Task<ulong> SendToChannelAsync(ulong channelId, string text, IReadOnlyList<ulong> pingRoles = null)
        {
            if (_config == null || !_config.IsEnabled)        return 0;
            if (channelId == 0 || string.IsNullOrEmpty(text)) return 0;
            text = Redact(text);

            if (DiscordCoordinatorLink.Active)
            {
                // The wire cannot say which mentions are allowed, so none survive it.
                if (pingRoles != null && pingRoles.Count > 0)
                    Once("coordinator-ping", "Discord: coordinator mode cannot ping a role, so alerts arrive without the mention.");
                DiscordCoordinatorLink.Enqueue(new KmhDiscordOutbound
                {
                    Op = KmhDiscordOp.Post,
                    ChannelRole = DiscordCoordinatorLink.RoleForChannel(channelId),
                    Content = DiscordText.NoMentions(text),
                });
                return 0;
            }
            if (_client == null) return 0;

            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel))
                {
                    ServerLog.Warn($"Discord: channel {channelId} not found / not a text channel");
                    return 0;
                }
                var sent = await channel.SendMessageAsync(text, allowedMentions: Mentions(pingRoles)).ConfigureAwait(false);
                return sent?.Id ?? 0;
            }
            catch (Exception ex)
            {
                NoteSendFailure();
                ServerLog.Warn($"Discord: channel send failed: {ex.Message}");
                return 0;
            }
        }

        // Editing rather than deleting, so striking one relayed line does not take the batch with it.
        internal static async Task<bool> EditOwnMessageAsync(ulong channelId, ulong messageId, string newText)
        {
            if (_config == null || !_config.IsEnabled)              return false;
            if (channelId == 0 || messageId == 0 || newText == null) return false;
            newText = Redact(newText);

            if (DiscordCoordinatorLink.Active)
            {
                DiscordCoordinatorLink.Enqueue(new KmhDiscordOutbound
                {
                    Op = KmhDiscordOp.Edit,
                    ChannelRole = DiscordCoordinatorLink.RoleForChannel(channelId),
                    MessageId = messageId,
                    Content = DiscordText.NoMentions(newText),
                });
                return true;
            }
            if (_client == null) return false;

            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel)) return false;
                var msg = await channel.GetMessageAsync(messageId).ConfigureAwait(false);
                if (!(msg is IUserMessage own) || own.Author?.Id != _client.CurrentUser?.Id) return false;
                await own.ModifyAsync(m => { m.Content = newText; m.AllowedMentions = AllowedMentions.None; }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                NoteSendFailure();
                ServerLog.Warn($"Discord: message edit failed: {ex.Message}");
                return false;
            }
        }

        internal static Task<ulong> PostOrEditTextAsync(ulong channelId, ulong prevMessageId, string content, bool postIfMissing = true)
            => PostOrEditFilesAsync(channelId, prevMessageId, content, null, postIfMissing);

        // No files keeps the attachments; any old-build embed is dropped; a deleted message is re-posted unless told not to.
        internal static async Task<ulong> PostOrEditFilesAsync(ulong channelId, ulong prevMessageId, string content,
                                                               IReadOnlyList<(string Name, byte[] Bytes)> files, bool postIfMissing = true)
        {
            if (_config == null || !_config.IsEnabled || channelId == 0) return 0;
            content = Redact(content ?? "");
            bool hasFiles = files != null && files.Count > 0;
            if (content.Length == 0 && !hasFiles) return 0;

            if (DiscordCoordinatorLink.Active)
            {
                if (hasFiles) Once("coordinator-files", "Discord: coordinator mode carries no files, so images are sent as their text line only.");
                if (prevMessageId == 0 && !postIfMissing) return 0;
                DiscordCoordinatorLink.Enqueue(new KmhDiscordOutbound
                {
                    Op = prevMessageId == 0 ? KmhDiscordOp.Post : KmhDiscordOp.Edit,
                    ChannelRole = DiscordCoordinatorLink.RoleForChannel(channelId),
                    MessageId = prevMessageId,
                    Content = DiscordText.NoMentions(content),
                });
                return prevMessageId;
            }
            if (_client == null) return 0;

            List<FileAttachment> attach = null;
            try
            {
                if (!(_client.GetChannel(channelId) is IMessageChannel channel))
                {
                    if (Diagnostics.KmhLogThrottle.Allow("discord.live.channel." + channelId, TimeSpan.FromMinutes(30), DateTime.UtcNow.Ticks, out _))
                        ServerLog.Warn($"Discord: channel {channelId} not found / not a text channel");
                    return 0;
                }

                if (prevMessageId != 0)
                {
                    try
                    {
                        IMessage prev = await channel.GetMessageAsync(prevMessageId).ConfigureAwait(false);
                        if (prev is IUserMessage own && own.Author?.Id == _client.CurrentUser?.Id)
                        {
                            attach = hasFiles ? Attachments(files) : null;
                            await own.ModifyAsync(p =>
                            {
                                p.Content = content;
                                p.Embeds = Array.Empty<Embed>();
                                p.AllowedMentions = AllowedMentions.None;
                                if (attach != null) p.Attachments = attach;
                            }).ConfigureAwait(false);
                            return prevMessageId;
                        }
                    }
                    catch (Exception ex)
                    {
                        // A manually deleted message lands here, and so does a refused edit.
                        ServerLog.Verbose($"Discord: edit of {prevMessageId} failed: {ex.Message}");
                    }
                    finally { Dispose(attach); attach = null; }
                    if (!postIfMissing) return 0;
                }

                IUserMessage posted;
                if (hasFiles)
                {
                    attach = Attachments(files);
                    posted = await channel.SendFilesAsync(attach, content, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                }
                else posted = await channel.SendMessageAsync(content, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                return posted?.Id ?? 0;
            }
            catch (Exception ex)
            {
                NoteSendFailure();
                ServerLog.Warn($"Discord: live post failed: {ex.Message}");
                return 0;
            }
            finally { Dispose(attach); }
        }

        private static List<FileAttachment> Attachments(IReadOnlyList<(string Name, byte[] Bytes)> files)
        {
            var list = new List<FileAttachment>();
            foreach ((string name, byte[] bytes) in files)
                if (bytes != null && bytes.Length > 0) list.Add(new FileAttachment(new System.IO.MemoryStream(bytes), name));
            return list;
        }

        private static void Dispose(List<FileAttachment> files)
        {
            if (files == null) return;
            foreach (FileAttachment f in files) { try { f.Dispose(); } catch { /* already closed */ } }
        }

        internal static async Task<bool> DeleteMessageAsync(ulong channelId, ulong messageId)
        {
            if (_config == null || !_config.IsEnabled)  return false;
            if (channelId == 0 || messageId == 0)       return false;

            if (DiscordCoordinatorLink.Active)
            {
                DiscordCoordinatorLink.Enqueue(new KmhDiscordOutbound
                {
                    Op = KmhDiscordOp.Delete,
                    ChannelRole = DiscordCoordinatorLink.RoleForChannel(channelId),
                    MessageId = messageId,
                });
                return true;
            }
            if (_client == null) return false;

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

        private static Task OnMessageDeleted(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
        {
            try { RemoveDeletedOriginal(message.Id); }
            catch (Exception ex) { ServerLog.Warn($"Discord: delete sync failed: {ex.Message}"); }
            return Task.CompletedTask;
        }

        // The relay index acts as the whitelist, so a deletion elsewhere can never remove a KMH chat line.
        private static void RemoveDeletedOriginal(ulong discordMessageId)
        {
            if (KmhChatRelayIndex.TryTakeInbound(discordMessageId, out long kmhId)
             && Features.Chat.ChatHandler.RemoveRelayed(kmhId, "a moderator on Discord"))
                ServerLog.Info($"Discord->Colony Nexus: message #{kmhId} removed in-game because its Discord original was deleted.");
        }

        // Size is judged here because Discord states the byte count up front; ChatImagePolicy still decides visibility.
        private static string FirstImageAttachment(KmhDiscordMessage message)
        {
            try
            {
                var cfg = Features.Chat.ChatConfig.Current;
                if (cfg == null || message == null) return null;
                if (!cfg.AllowImagePreviews)
                {
                    // Previews being off is a policy, but a picture silently not arriving looks like a broken bridge.
                    if (HasAnyMedia(message)) MediaLogOff();
                    return null;
                }

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
        private static string FirstSticker(KmhDiscordMessage message)
        {
            if (message?.Stickers == null || message.Stickers.Count == 0) return null;
            foreach (KmhDiscordSticker s in message.Stickers)
            {
                if (s == null) continue;
                string url = Features.Chat.ChatDiscordEmoji.StickerUrl(s.Id, s.Format);
                if (!string.IsNullOrEmpty(url)) return url;
                MediaLog(message, $"sticker '{s.Name}' is {s.Format} - no raster form to draw");
            }
            return null;
        }

        // ProxyUrl is preferred over Url because it keeps the fetch on Discord rather than a third-party host.
        private static string FirstEmbedImage(KmhDiscordMessage message)
        {
            if (message?.Embeds == null) return null;
            var cfg = Features.Chat.ChatConfig.Current;

            foreach (KmhDiscordEmbed e in message.Embeds)
            {
                if (e == null) continue;

                MediaLog(message, $"embed type='{e.Type}' image={Present(e.ImageUrl, e.ImageProxyUrl)}"
                                + $" thumb={Present(e.ThumbUrl, e.ThumbProxyUrl)}"
                                + $" video={Present(e.VideoUrl, null)}");

                string url = Pick(message, "image", e.ImageProxyUrl, e.ImageUrl)
                          ?? Pick(message, "thumbnail", e.ThumbProxyUrl, e.ThumbUrl);
                if (!string.IsNullOrEmpty(url)) return url;

                // A gifv embed has no image slot at all, only a Video pointing at an mp4.
                if (!string.IsNullOrEmpty(e.VideoUrl)
                    && Features.Chat.ChatImagePolicy.LooksLikeVideo(cfg, e.VideoUrl))
                {
                    MediaLog(message, $"video slot selected: {Features.Chat.ChatMediaUrl.Describe(cfg, e.VideoUrl)}");
                    return e.VideoUrl;
                }
            }
            MediaLogQuiet("no-embed-media", "an embed carried no usable media");
            return null;
        }

        private static string Present(string a, string b)
            => string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b) ? "-" : "y";

        // The decision lives in ChatMediaSelection so it can be exercised without a Discord.Net message object.
        private static string Pick(KmhDiscordMessage message, string slot, string proxy, string original)
        {
            string chosen = Features.Chat.ChatMediaSelection.Choose(
                Features.Chat.ChatConfig.Current, proxy, original, out string why);
            if (string.IsNullOrEmpty(chosen)) { MediaLogQuiet("pick-empty." + slot, $"{slot}: nothing usable - {why}"); return null; }
            MediaLog(message, $"{slot}: chose {why}");
            return chosen;
        }

        // Carries the message id so a player's report can be matched to the decision that produced it.
        private static void MediaLog(KmhDiscordMessage message, string what)
            => ServerLog.Debug($"Discord media [msg {(message == null ? "?" : message.Id.ToString())}]: {what}");

        private static bool HasAnyMedia(KmhDiscordMessage message)
        {
            try
            {
                if (message.Attachments != null) foreach (var a in message.Attachments) if (a != null) return true;
                if (message.Embeds != null)
                    foreach (var e in message.Embeds)
                        if (!string.IsNullOrEmpty(e.ImageUrl) || !string.IsNullOrEmpty(e.ThumbUrl)
                            || !string.IsNullOrEmpty(e.VideoUrl)) return true;
            }
            catch { }
            return false;
        }

        // Said at Info, not Debug: the owner who wonders why a gif never appears is not running with debug on.
        private static void MediaLogOff()
        {
            if (!Diagnostics.KmhLogThrottle.Allow("discord.media.previews-off", TimeSpan.FromMinutes(30),
                                                  DateTime.UtcNow.Ticks, out int suppressed)) return;
            ServerLog.Info("Discord: a message carried a picture but chat image previews are off - set "
                         + $"AllowImagePreviews=true in Config/Chat.json to show them{(suppressed > 0 ? $" ({suppressed} more since)" : "")}.");
        }

        // "No picture here" is the ordinary case, not a diagnostic; one line per message buried everything else.
        private static void MediaLogQuiet(string key, string what)
        {
            if (!Diagnostics.KmhLogThrottle.Allow("discord.media." + key, TimeSpan.FromMinutes(5),
                                                  DateTime.UtcNow.Ticks, out int suppressed)) return;
            ServerLog.Debug($"Discord media: {what}{(suppressed > 0 ? $" (+{suppressed} more since)" : "")}");
        }

        // Discord attaches a link preview as a later edit, so the embed does not exist when the message arrives.
        private static Task OnMessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel channel)
        {
            try
            {
                if (after == null || _config == null) return Task.CompletedTask;
                if (_config.KmhChatChannelId == 0 || (channel?.Id ?? 0) != _config.KmhChatChannelId) return Task.CompletedTask;

                string image = FirstImageAttachment(Adapt(after));
                if (string.IsNullOrEmpty(image)) return Task.CompletedTask;

                KmhChatDiscordBridge.AttachLateImage(after.Id, image);
            }
            catch (Exception ex) { ServerLog.Warn($"Discord: late embed update failed: {ex.Message}"); }
            return Task.CompletedTask;
        }
        // Adapted to KMH's neutral message here, so everything past this point runs the same under a coordinator.
        private static Task OnMessage(SocketMessage message)
            => message == null ? Task.CompletedTask : HandleMessageAsync(Adapt(message));

        internal static KmhDiscordMessage Adapt(SocketMessage message)
        {
            List<KmhDiscordAttachment> atts = new List<KmhDiscordAttachment>();
            if (message.Attachments != null)
                foreach (IAttachment a in message.Attachments)
                    if (a != null) atts.Add(new KmhDiscordAttachment { Filename = a.Filename ?? "", Url = a.Url ?? "", Size = a.Size });

            List<KmhDiscordSticker> stickers = new List<KmhDiscordSticker>();
            if (message.Stickers != null)
                foreach (IStickerItem s in message.Stickers)
                    if (s != null) stickers.Add(new KmhDiscordSticker { Id = s.Id, Name = s.Name ?? "", Format = s.Format.ToString() });

            List<KmhDiscordEmbed> embeds = new List<KmhDiscordEmbed>();
            if (message.Embeds != null)
                foreach (IEmbed e in message.Embeds)
                {
                    if (e == null) continue;
                    embeds.Add(new KmhDiscordEmbed
                    {
                        Type          = e.Type.ToString(),
                        ImageUrl      = e.Image?.Url,
                        ImageProxyUrl = e.Image?.ProxyUrl,
                        ThumbUrl      = e.Thumbnail?.Url,
                        ThumbProxyUrl = e.Thumbnail?.ProxyUrl,
                        VideoUrl      = e.Video?.Url,
                    });
                }

            ulong channelId = message.Channel?.Id ?? 0;
            bool  isDm      = !(message.Channel is SocketGuildChannel);
            return new KmhDiscordMessage
            {
                Id      = message.Id,
                Content = message.Content ?? "",
                GuildId = (message.Channel as SocketGuildChannel)?.Guild?.Id ?? 0,
                Author  = new KmhDiscordUser
                {
                    Id        = message.Author?.Id ?? 0,
                    Username  = message.Author?.Username ?? "",
                    Display   = ResolveDisplay(message.Author),
                    IsBot     = message.Author?.IsBot ?? false,
                    IsWebhook = message.Author?.IsWebhook ?? false,
                },
                Channel     = new KmhDiscordChannel(channelId, isDm, SendViaGateway, SendFilesViaGateway),
                Attachments = atts,
                Stickers    = stickers,
                Embeds      = embeds,
            };
        }

        // The coordinator's neutral events enter the same handlers a local gateway would have fed.
        internal static async Task OnCoordinatorEvent(KmhDiscordInbound ev)
        {
            switch (ev.Kind)
            {
                case KmhDiscordInboundKind.Message:
                    await HandleMessageAsync(FromWire(ev)).ConfigureAwait(false);
                    return;

                case KmhDiscordInboundKind.MessageDeleted:
                    RemoveDeletedOriginal(ev.MessageId);
                    return;

                case KmhDiscordInboundKind.MessageUpdated:
                {
                    if (_config == null || _config.KmhChatChannelId == 0) return;
                    if (ev.ChannelId != _config.KmhChatChannelId) return;
                    string image = FirstImageAttachment(FromWire(ev));
                    if (!string.IsNullOrEmpty(image)) KmhChatDiscordBridge.AttachLateImage(ev.MessageId, image);
                    return;
                }
            }
        }

        private static KmhDiscordMessage FromWire(KmhDiscordInbound ev)
        {
            List<KmhDiscordAttachment> atts = new List<KmhDiscordAttachment>();
            foreach (KmhWireAttachment a in ev.Attachments ?? new List<KmhWireAttachment>())
                if (a != null) atts.Add(new KmhDiscordAttachment { Filename = a.Filename, Url = a.Url, Size = a.Size });

            List<KmhDiscordSticker> stickers = new List<KmhDiscordSticker>();
            foreach (KmhWireSticker s in ev.Stickers ?? new List<KmhWireSticker>())
                if (s != null) stickers.Add(new KmhDiscordSticker { Id = s.Id, Name = s.Name, Format = s.Format });

            List<KmhDiscordEmbed> embeds = new List<KmhDiscordEmbed>();
            foreach (KmhWireEmbedMedia e in ev.Embeds ?? new List<KmhWireEmbedMedia>())
                if (e != null) embeds.Add(new KmhDiscordEmbed
                {
                    Type = e.Type, ImageUrl = e.ImageUrl, ImageProxyUrl = e.ImageProxyUrl,
                    ThumbUrl = e.ThumbUrl, ThumbProxyUrl = e.ThumbProxyUrl, VideoUrl = e.VideoUrl,
                });

            return new KmhDiscordMessage
            {
                Id = ev.MessageId,
                Content = ev.Content ?? "",
                GuildId = ev.GuildId,
                Author = new KmhDiscordUser
                {
                    Id = ev.AuthorId, Username = ev.AuthorName, Display = ev.AuthorDisplay,
                    IsBot = ev.AuthorIsBot, IsWebhook = ev.AuthorIsWebhook,
                },
                Channel = new KmhDiscordChannel(ev.ChannelId, ev.IsDirectMessage, SendViaCoordinator),
                Attachments = atts,
                Stickers = stickers,
                Embeds = embeds,
            };
        }

        // KmhDiscordChannel has already turned any card into text, so nothing here carries an embed.
        private static Task SendViaCoordinator(ulong channelId, string text, KmhEmbedSpec[] specs,
                                               KmhComponentSpec components)
        {
            DiscordCoordinatorLink.Enqueue(new KmhDiscordOutbound
            {
                Op = KmhDiscordOp.Post,
                ChannelRole = DiscordCoordinatorLink.RoleForChannel(channelId),
                Content = text == null ? null : DiscordText.NoMentions(Redact(text)),
                Components = components,
            });
            return Task.CompletedTask;
        }

        private static Task OnButton(SocketMessageComponent component)
            => component == null ? Task.CompletedTask : DiscordBuyButton.OnInteraction(Adapt(component));

        internal static KmhDiscordInteraction Adapt(SocketMessageComponent c)
            => new KmhDiscordInteraction
            {
                CustomId  = c.Data?.CustomId ?? "",
                ChannelId = c.Channel?.Id ?? 0,
                GuildId   = c.GuildId ?? 0,
                User      = new KmhDiscordUser
                {
                    Id       = c.User?.Id ?? 0,
                    Username = c.User?.Username ?? "",
                    Display  = ResolveDisplay(c.User),
                },
                Responder         = (text, eph) => c.HasResponded ? c.FollowupAsync(text, ephemeral: eph, allowedMentions: AllowedMentions.None)
                                                                  : c.RespondAsync(text, ephemeral: eph, allowedMentions: AllowedMentions.None),
                Deferrer          = () => c.DeferAsync(ephemeral: true),
                FollowupResponder = (text, eph) => c.FollowupAsync(text, ephemeral: eph, allowedMentions: AllowedMentions.None),
            };

        private static async Task SendViaGateway(ulong channelId, string text, KmhEmbedSpec[] specs,
                                                 KmhComponentSpec components)
        {
            if (_client?.GetChannel(channelId) is IMessageChannel ch)
                await ch.SendMessageAsync(text == null ? null : Redact(text), allowedMentions: AllowedMentions.None,
                        components: KmhEmbedRender.ToComponents(components)).ConfigureAwait(false);
        }

        private static async Task SendFilesViaGateway(ulong channelId, string text, IReadOnlyList<(string Name, byte[] Bytes)> files)
        {
            if (!(_client?.GetChannel(channelId) is IMessageChannel ch)) return;
            List<FileAttachment> attach = Attachments(files);
            try
            {
                await ch.SendFilesAsync(attach, text == null ? null : Redact(text), allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
            finally { Dispose(attach); }
        }

        private static async Task HandleMessageAsync(KmhDiscordMessage message)
        {
            try
            {
                if (message == null)                 return;
                if (message.Author == null)          return;
                // This bot is always skipped; other bots are an owner's choice because their posts often matter.
                ulong meId = _client?.CurrentUser?.Id ?? 0;
                bool fromBot = message.Author.IsBot || message.Author.IsWebhook;
                if (fromBot)
                {
                    if (meId != 0 && message.Author.Id == meId) return;
                    if (!_config.KmhChatRelayBotsOn && !_config.WorldChatRelayBotsOn)
                    {
                        MediaLog(message, $"bot '{message.Author.Username}' skipped - relaying bots is off");
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
                bool rwtTookIt = false;
                if (hasText && _config.ChatBridgeChannelId != 0 && chId == _config.ChatBridgeChannelId)
                {
                    DiscordChatBridge.RelayDiscordToInGame(message.Author.Display, text);
                    rwtTookIt = true;
                    relayed = true;
                }

                // A channel on both KMH bridges would deliver twice; world chat wins, but only for what it really carries.
                bool worldOwnsChannel = WorldCarries(_config, chId, hasText, fromBot, KmhWorldChatDiscordBridge.InboundEnabled);

                // A line the RWT bridge carried already reached every client; a picture it cannot carry is still ingested.
                if (relayable && !worldOwnsChannel && !rwtTookIt
                    && _config.KmhChatChannelId != 0 && chId == _config.KmhChatChannelId)
                {
                    if (fromBot && !_config.KmhChatRelayBotsOn)
                        MediaLog(message, "bot skipped for KmhChat - KmhChat.RelayBots is off");
                    else
                    {
                        KmhChatDiscordBridge.Ingest(message.Author.Id, message.Author.Display, text, message.Id, image);
                        relayed = true;
                    }
                }

                // Text only: an attachment url here would be fetched by players on servers whose owners never agreed to it.
                if (hasText && _config.WorldChatChannelId != 0 && chId == _config.WorldChatChannelId)
                {
                    if (fromBot && !_config.WorldChatRelayBotsOn)
                        MediaLog(message, "bot skipped for WorldChat - WorldChat.RelayBots is off");
                    else
                    {
                        KmhWorldChatDiscordBridge.Ingest(message.Author.Display, text);
                        relayed = true;
                    }
                }

                if (relayed) return;

                if (!text.StartsWith(prefix, StringComparison.Ordinal)) return;

                // A DM is already per-bot, so it needs no mention.
                if (_config.RequireMentionForCommands && !mentionedMe && !message.Channel.IsDirectMessage) return;

                // DMs stay open so a player can always link.
                if (!message.Channel.IsDirectMessage
                    && _config.AllowedGuildIds != null
                    && _config.AllowedGuildIds.Length > 0)
                {
                    bool allowed = false;
                    foreach (ulong id in _config.AllowedGuildIds)
                    {
                        if (id == message.GuildId) { allowed = true; break; }
                    }
                    if (!allowed) return;
                }

                if (!_config.CommandsAllowedIn(chId, message.Channel.IsDirectMessage))
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
                            "Colony Nexus bot commands:\n" +
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
                            $"  `{prefix}kmh-showcase`            - publish your marketplace as one live message\n" +
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

        private static async Task HandleLink(KmhDiscordMessage message, string[] parts)
        {
            if (parts.Length >= 2 && string.Equals(parts[1], "status", StringComparison.OrdinalIgnoreCase))
            {
                ulong  authorIdStatus = message.Author?.Id ?? 0;
                string bound   = LinkedAccountsStore.FindUsernameByDiscordId(authorIdStatus);   // id-only; display is impersonable
                if (string.IsNullOrEmpty(bound))
                {
                    await Reply(message, "Your Discord identity is not currently linked to any Colony Nexus account.")
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

            string display2 = message.Author.Display;
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

        private static async Task HandleUnlink(KmhDiscordMessage message)
        {
            ulong authorId = message.Author?.Id ?? 0;
            string display  = message.Author.Display;
            // Id only: a display fallback would let someone unlink another player by copying their display name.
            string victim   = LinkedAccountsStore.FindUsernameByDiscordId(authorId);
            if (string.IsNullOrEmpty(victim))
            {
                await Reply(message,
                    "No Colony Nexus account is currently linked to your Discord identity.")
                    .ConfigureAwait(false);
                return;
            }
            bool saved = LinkedAccountsStore.Unlink(victim);
            LinkedAccountsHandler.BroadcastSnapshot();
            AnnounceLink(victim, display, "unlink");
            ServerLog.Info($"Discord: unlinked '{victim}' (requested via Discord by '{display}')");
            await Reply(message, $"Unlinked **{victim}**." + (saved ? "" : LinkedAccountsStore.UnlinkNotSavedNote)).ConfigureAwait(false);
        }

        // The same picture the live board posts, with one line; the rows as text where no file can go (the coordinator).
        private static async Task HandleLeaderboard(KmhDiscordMessage message, string[] parts)
        {
            string first = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
            string key, empty;
            if (first == "guilds" || first == "guild" || first == "g")
            {
                key = parts.Length >= 3 ? DiscordLeaderboardBuilder.NormalizeGuildSort(parts[2]) : DiscordLeaderboardBuilder.DefaultGuildSort;
                empty = "No guilds on this server yet - create one with `/kmh guild create <name>` in-game.";
            }
            else if (first == "rep" || first == "reputation")
            {
                key = Leaderboards.LeaderboardCatalog.Reputation;
                empty = "No reputation recorded yet - players earn it by completing quests.";
            }
            else
            {
                key = parts.Length >= 2 ? DiscordLeaderboardBuilder.NormalizeSort(parts[1]) : DiscordLeaderboardBuilder.DefaultSort;
                empty = "Nobody is on that board yet - a zero ranks nobody.";
            }

            Leaderboards.LeaderboardEngine.Inputs inputs = Leaderboards.LeaderboardEngine.Live();
            Leaderboards.LeaderboardBoardDto board = Leaderboards.LeaderboardEngine.Build(Leaderboards.LeaderboardCatalog.Find(key), inputs);
            KmhDiscordCommandResult answer = DiscordLeaderboardPoster.CommandResult(board, _config ?? new DiscordConfig(), inputs,
                                                                                    message.Channel.CanSendFiles);
            if (answer == null)
            {
                await Reply(message, empty).ConfigureAwait(false);
                return;
            }
            try
            {
                if (answer.Files != null) await message.Channel.SendFilesAsync(answer.Content, answer.Files).ConfigureAwait(false);
                else await message.Channel.SendMessageAsync(answer.Content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Discord: leaderboard reply failed: {ex.Message}");
            }
        }

        private static async Task HandleRank(KmhDiscordMessage message, string[] parts)
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

            KmhEmbedSpec card = DiscordLeaderboardBuilder.BuildPlayerCard(query, out List<string> matches);
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
        private static async Task HandleWhois(KmhDiscordMessage message, string[] parts)
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

        private static async Task HandleReputation(KmhDiscordMessage message, string[] parts)
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

        private static async Task Reply(KmhDiscordMessage message, string text)
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
