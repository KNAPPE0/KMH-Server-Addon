using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Kmh.Discord.Wire;

namespace KmhDiscordCoordinator
{
    // The fleet's only Discord gateway connection. Game servers reach Discord exclusively through this process.
    public sealed class GatewayHost
    {
        private readonly CoordinatorConfig _cfg;
        private readonly ServerRegistry _registry;
        private DiscordSocketClient _client;
        private CancellationTokenSource _presenceCts;

        public GatewayHost(CoordinatorConfig cfg, ServerRegistry registry)
        {
            _cfg = cfg;
            _registry = registry;
        }

        public DiscordSocketClient Client => _client;
        public ulong SelfUserId => _client?.CurrentUser?.Id ?? 0;
        public bool Connected => _client?.ConnectionState == ConnectionState.Connected;

        public int SlashRegistrations { get; private set; }

        public async Task StartAsync(string token)
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
                               | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
                LogLevel = LogSeverity.Info,
                MessageCacheSize = 0,
                AlwaysDownloadUsers = false,
            });

            _client.Log += OnLog;
            _client.Ready += OnReady;
            _client.MessageReceived += OnMessage;
            _client.MessageDeleted += OnMessageDeleted;
            _client.MessageUpdated += OnMessageUpdated;
            _client.ButtonExecuted += OnButton;
            _client.SlashCommandExecuted += OnSlash;
            _client.AutocompleteExecuted += OnAutocomplete;

            await _client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            try { _presenceCts?.Cancel(); } catch (Exception) { }
            if (_client == null) return;
            try { await _client.LogoutAsync().ConfigureAwait(false); } catch (Exception) { }
            try { await _client.StopAsync().ConfigureAwait(false); }   catch (Exception) { }
        }

        private Task OnLog(LogMessage m)
        {
            string line = $"discord: {m.Source}: {m.Message}";
            if (m.Severity <= LogSeverity.Error) CoordLog.Error(line, m.Exception);
            else if (m.Severity == LogSeverity.Warning) CoordLog.Warn(line);
            else CoordLog.Info(line);
            return Task.CompletedTask;
        }

        private async Task OnReady()
        {
            CoordLog.Info($"gateway ready as {_client.CurrentUser?.Username} ({SelfUserId})");

            // Once per process, not once per server: the runtime server option keeps the schema stable.
            await RegisterCommandsAsync().ConfigureAwait(false);

            try { _presenceCts?.Cancel(); } catch (Exception) { }
            _presenceCts = new CancellationTokenSource();
            _ = PresenceLoop(_presenceCts.Token);
        }

        private async Task RegisterCommandsAsync()
        {
            try
            {
                SlashCommandProperties tree = KmhSlashTree.Build();
                if (ulong.TryParse((_cfg.GuildId ?? "").Trim(), out ulong guildId) && guildId != 0)
                {
                    SocketGuild g = _client.GetGuild(guildId);
                    if (g != null)
                    {
                        await g.CreateApplicationCommandAsync(tree).ConfigureAwait(false);
                        SlashRegistrations++;
                        CoordLog.Info($"registered /kmh to guild {guildId} (registration #{SlashRegistrations} this process)");
                        return;
                    }
                    CoordLog.Warn($"guild {guildId} not visible - registering /kmh globally instead");
                }
                await _client.CreateGlobalApplicationCommandAsync(tree).ConfigureAwait(false);
                SlashRegistrations++;
                CoordLog.Info($"registered /kmh globally (registration #{SlashRegistrations} this process)");
            }
            catch (Exception ex) { CoordLog.Error("slash registration failed", ex); }
        }

        private string _lastPresence = "";

        private async Task PresenceLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _client != null)
                {
                    try
                    {
                        List<LiveServer> live = _registry.OnlineServers();
                        int players = live.Sum(s => s.Players);
                        string status = $"{live.Count} server{(live.Count == 1 ? "" : "s")} · {players} player{(players == 1 ? "" : "s")}";
                        // Debounced: Discord rate-limits presence, and an unchanged string is not worth a call.
                        if (status != _lastPresence)
                        {
                            await _client.SetActivityAsync(new Game(status, ActivityType.Watching)).ConfigureAwait(false);
                            _lastPresence = status;
                        }
                    }
                    catch (Exception ex) { CoordLog.Warn($"presence update failed: {ex.Message}"); }
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private Task OnMessage(SocketMessage m)
        {
            if (m?.Author == null) return Task.CompletedTask;
            if (m.Author.Id == SelfUserId) return Task.CompletedTask;

            ulong chId = m.Channel?.Id ?? 0;
            LiveServer target = _registry.ByChannel(chId);
            if (target == null) return Task.CompletedTask;   // a channel KMH does not own is not KMH's business

            KmhDiscordInbound ev = new KmhDiscordInbound
            {
                Kind = KmhDiscordInboundKind.Message,
                ChannelId = chId,
                ChannelRole = RoleOf(target, chId),
                GuildId = (m.Channel as SocketGuildChannel)?.Guild?.Id ?? 0,
                IsDirectMessage = !(m.Channel is SocketGuildChannel),
                MessageId = m.Id,
                Content = m.Content ?? "",
                AuthorId = m.Author.Id,
                AuthorName = m.Author.Username ?? "",
                AuthorDisplay = Display(m.Author),
                AuthorIsBot = m.Author.IsBot,
                AuthorIsWebhook = m.Author.IsWebhook,
                Attachments = Attachments(m),
                Stickers = Stickers(m),
                Embeds = EmbedMedia(m),
            };
            target.Enqueue(ev);
            return Task.CompletedTask;
        }

        private Task OnMessageDeleted(Cacheable<IMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch)
        {
            LiveServer target = _registry.ByChannel(ch.Id);
            target?.Enqueue(new KmhDiscordInbound
            {
                Kind = KmhDiscordInboundKind.MessageDeleted,
                ChannelId = ch.Id,
                MessageId = msg.Id,
            });
            return Task.CompletedTask;
        }

        private Task OnMessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel ch)
        {
            if (after == null) return Task.CompletedTask;
            LiveServer target = _registry.ByChannel(ch?.Id ?? 0);
            target?.Enqueue(new KmhDiscordInbound
            {
                Kind = KmhDiscordInboundKind.MessageUpdated,
                ChannelId = ch?.Id ?? 0,
                MessageId = after.Id,
                Content = after.Content ?? "",
                Attachments = Attachments(after),
                Stickers = Stickers(after),
                Embeds = EmbedMedia(after),
            });
            return Task.CompletedTask;
        }

        private async Task OnButton(SocketMessageComponent c)
        {
            LiveServer target = _registry.ByChannel(c.Channel?.Id ?? 0);
            if (target == null || !target.Online(DateTimeOffset.UtcNow))
            {
                try { await c.RespondAsync("That Colony Nexus server is not connected right now.", ephemeral: true).ConfigureAwait(false); }
                catch (Exception) { }
                return;
            }

            // Acknowledged here first, because the owning server's reply cannot meet Discord's window.
            string requestId = Guid.NewGuid().ToString("N");
            try { await c.DeferAsync(ephemeral: true).ConfigureAwait(false); }
            catch (Exception) { return; }

            target.Enqueue(new KmhDiscordInbound
            {
                Kind = KmhDiscordInboundKind.Component,
                ChannelId = c.Channel?.Id ?? 0,
                GuildId = c.GuildId ?? 0,
                CustomId = c.Data?.CustomId ?? "",
                InteractionId = requestId,
                AuthorId = c.User?.Id ?? 0,
                AuthorName = c.User?.Username ?? "",
                AuthorDisplay = Display(c.User),
            });

            KmhDiscordCommandResult result = await _registry
                .Await(requestId, TimeSpan.FromSeconds(_cfg.CommandTimeoutSeconds), CancellationToken.None)
                .ConfigureAwait(false);
            await KmhInteractionResponder.ApplyAsync(c, result, deferredPrivate: true, CoordLog.Warn).ConfigureAwait(false);
        }

        private Task OnSlash(SocketSlashCommand cmd) => CommandRouter.HandleAsync(this, _cfg, _registry, cmd);

        private async Task OnAutocomplete(SocketAutocompleteInteraction ac)
        {
            try
            {
                KmhSlashTree.ReadAutocomplete(ac, out string focused, out string typed);
                if (!string.Equals(focused, KmhSlashTree.ServerOption, StringComparison.Ordinal))
                {
                    await ac.RespondAsync(Array.Empty<AutocompleteResult>()).ConfigureAwait(false);
                    return;
                }

                // Answered from the coordinator's own registry: no game server is contacted for a keystroke.
                DateTimeOffset now = DateTimeOffset.UtcNow;
                List<AutocompleteResult> choices = _registry.All()
                    .Where(s => s.Online(now))
                    .Where(s => string.IsNullOrEmpty(typed)
                             || s.Alias.Contains(typed, StringComparison.OrdinalIgnoreCase)
                             || (s.DisplayName ?? "").Contains(typed, StringComparison.OrdinalIgnoreCase))
                    .Take(25)
                    .Select(s => new AutocompleteResult(Label(s), s.Alias))
                    .ToList();
                await ac.RespondAsync(choices).ConfigureAwait(false);
            }
            catch (Exception ex) { CoordLog.Warn($"autocomplete failed: {ex.Message}"); }
        }

        // A server names itself, so only the coordinator's config can make it Official, and a self-chosen name always
        // appears beside the alias the coordinator gave it.
        internal static string Label(LiveServer s)
        {
            string name = CleanName(s.DisplayName);
            if (s.Official) return (name.Length > 0 ? name : s.Alias) + " (Official)";
            return name.Length == 0 || string.Equals(name, s.Alias, StringComparison.OrdinalIgnoreCase) ? s.Alias : $"{name} [{s.Alias}]";
        }

        internal static string CleanName(string raw)
        {
            string t = System.Text.RegularExpressions.Regex.Replace(raw ?? "", @"[\p{C}*_`~|>\[\]()@#:<]", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, "official", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > 40 ? t.Substring(0, 40).TrimEnd() : t;
        }

        private static string RoleOf(LiveServer s, ulong channelId)
        {
            foreach (KeyValuePair<string, ulong> kv in s.Channels)
                if (kv.Value == channelId) return kv.Key;
            return "";
        }

        private static string Display(IUser u)
        {
            string g = u?.GlobalName;
            if (!string.IsNullOrEmpty(g)) return g;
            string name = u?.Username;
            string d = u?.Discriminator;
            if (!string.IsNullOrEmpty(d) && d != "0" && d != "0000") return $"{name}#{d}";
            return name ?? "(unknown)";
        }

        private static List<KmhWireAttachment> Attachments(IMessage m)
        {
            List<KmhWireAttachment> list = new List<KmhWireAttachment>();
            if (m?.Attachments == null) return list;
            foreach (IAttachment a in m.Attachments)
                if (a != null) list.Add(new KmhWireAttachment { Filename = a.Filename ?? "", Url = a.Url ?? "", Size = a.Size });
            return list;
        }

        private static List<KmhWireSticker> Stickers(IMessage m)
        {
            List<KmhWireSticker> list = new List<KmhWireSticker>();
            if (m?.Stickers == null) return list;
            foreach (IStickerItem s in m.Stickers)
                if (s != null) list.Add(new KmhWireSticker { Id = s.Id, Name = s.Name ?? "", Format = s.Format.ToString() });
            return list;
        }

        private static List<KmhWireEmbedMedia> EmbedMedia(IMessage m)
        {
            List<KmhWireEmbedMedia> list = new List<KmhWireEmbedMedia>();
            if (m?.Embeds == null) return list;
            foreach (IEmbed e in m.Embeds)
            {
                if (e == null) continue;
                list.Add(new KmhWireEmbedMedia
                {
                    Type = e.Type.ToString(),
                    ImageUrl = e.Image?.Url,
                    ImageProxyUrl = e.Image?.ProxyUrl,
                    ThumbUrl = e.Thumbnail?.Url,
                    ThumbProxyUrl = e.Thumbnail?.ProxyUrl,
                    VideoUrl = e.Video?.Url,
                });
            }
            return list;
        }

        public async Task<KmhDiscordOutboundResult> ApplyAsync(LiveServer from, KmhDiscordOutbound op)
        {
            if (op == null) return new KmhDiscordOutboundResult { Error = "empty" };
            try
            {
                switch (op.Op)
                {
                    case KmhDiscordOp.Presence:
                        _registry.Renew(from, op.Players);
                        return new KmhDiscordOutboundResult { Ok = true };

                    case KmhDiscordOp.Post:   return await PostAsync(from, op).ConfigureAwait(false);
                    case KmhDiscordOp.Edit:   return await EditAsync(from, op).ConfigureAwait(false);
                    case KmhDiscordOp.Delete: return await DeleteAsync(from, op).ConfigureAwait(false);
                    // No game server sends this, and creating or granting guild roles is never one server's call.
                    case KmhDiscordOp.Role:   return new KmhDiscordOutboundResult { Error = KmhDiscordError.Unsupported };
                    default: return new KmhDiscordOutboundResult { Error = KmhDiscordError.Unsupported };
                }
            }
            catch (Exception ex)
            {
                CoordLog.Warn($"{from?.Alias}: {op.Op} failed: {ex.Message}");
                return new KmhDiscordOutboundResult { Error = ex.Message };
            }
        }

        // A server may only post to a channel the coordinator mapped to it, whatever it asks for.
        private IMessageChannel Resolve(LiveServer from, KmhDiscordOutbound op, out string error)
        {
            error = "";
            ulong id = from.Channel(op.ChannelRole);
            if (id == 0) { error = KmhDiscordError.NoSuchChannel; return null; }
            if (!(_client?.GetChannel(id) is IMessageChannel ch)) { error = KmhDiscordError.NoSuchChannel; return null; }
            return ch;
        }

        private async Task<KmhDiscordOutboundResult> PostAsync(LiveServer from, KmhDiscordOutbound op)
        {
            IMessageChannel ch = Resolve(from, op, out string error);
            if (ch == null) return new KmhDiscordOutboundResult { Error = error };

            // No mentions from a server's post, whatever it sends: honest servers defuse them, a hostile one would not.
            IUserMessage sent = await ch.SendMessageAsync(
                op.Content,
                allowedMentions: AllowedMentions.None,
                embeds: KmhEmbedRender.ToEmbeds(op.Embeds),
                components: KmhEmbedRender.ToComponents(op.Components)).ConfigureAwait(false);
            return new KmhDiscordOutboundResult { Ok = true, MessageId = sent?.Id ?? 0 };
        }

        private async Task<KmhDiscordOutboundResult> EditAsync(LiveServer from, KmhDiscordOutbound op)
        {
            IMessageChannel ch = Resolve(from, op, out string error);
            if (ch == null) return new KmhDiscordOutboundResult { Error = error };
            if (op.MessageId == 0) return new KmhDiscordOutboundResult { Error = "no message id" };

            IMessage msg = await ch.GetMessageAsync(op.MessageId).ConfigureAwait(false);
            // Only KMH's own posts are ever edited, whichever server asked.
            if (!(msg is IUserMessage own) || own.Author?.Id != SelfUserId)
                return new KmhDiscordOutboundResult { Error = "not our message" };

            Embed[] embeds = KmhEmbedRender.ToEmbeds(op.Embeds);
            await own.ModifyAsync(m =>
            {
                if (op.Content != null) m.Content = op.Content;
                if (embeds != null) m.Embeds = embeds;
                m.AllowedMentions = AllowedMentions.None;
            }).ConfigureAwait(false);
            return new KmhDiscordOutboundResult { Ok = true, MessageId = op.MessageId };
        }

        private async Task<KmhDiscordOutboundResult> DeleteAsync(LiveServer from, KmhDiscordOutbound op)
        {
            IMessageChannel ch = Resolve(from, op, out string error);
            if (ch == null) return new KmhDiscordOutboundResult { Error = error };

            IMessage msg = await ch.GetMessageAsync(op.MessageId).ConfigureAwait(false);
            if (!(msg is IUserMessage own) || own.Author?.Id != SelfUserId)
                return new KmhDiscordOutboundResult { Error = "not our message" };
            await own.DeleteAsync().ConfigureAwait(false);
            return new KmhDiscordOutboundResult { Ok = true };
        }
    }
}
