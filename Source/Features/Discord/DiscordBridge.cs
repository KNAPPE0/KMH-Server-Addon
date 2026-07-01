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

        // Internal accessor - lets sibling Discord features (player announcer, leaderboard poster, etc.) read
        // channel ids and command prefix without each one re-loading the config file
        internal static DiscordConfig Config => _config;

        // Short human-readable status line for /kmh server status. Cheap - just reads in-memory state, no Discord
        // round-trip
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

        // Refresh bot status every 30s: feels live without brushing Discord's rate limits.
        private static readonly TimeSpan PresenceRefreshInterval = TimeSpan.FromSeconds(30);

        // Restart Discord bridge and reload DiscordConfig.json; safe even if the bot was never started.
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
                    ServerLog.Info("Discord: bridge disabled (no Bot.Token in Config/Discord/DiscordConfig.json)");
                    return;
                }

                DiscordSocketConfig socketConfig = new DiscordSocketConfig
                {
                    // Requires Discord MessageContent intent, or message.Content is empty and text commands won't be seen.
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
                _client.ButtonExecuted     += DiscordBuyButton.OnInteraction;
                _client.SlashCommandExecuted += DiscordSlashCommands.Handle;

                // Fire-and-forget Discord login; startup failures log and KMH keeps running without the bridge.
                Task.Run(async () =>
                {
                    try
                    {
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

            // Restart the presence updater on every Ready - Discord may re-fire Ready after reconnects, and we want
            // a single live updater per connected session
            try { _presenceCts?.Cancel(); } catch { /* ignore */ }
            _presenceCts = new CancellationTokenSource();
            Task.Run(() => PresenceLoop(_presenceCts.Token));

            // (Re-)register the /kmh slash command tree now that the guild cache is populated. Fire-and-forget -
            // failure logs and the legacy !kmh-* commands still work
            if (_config?.Bot?.UseSlashCommands == true)
                _ = DiscordSlashCommands.RegisterAsync(_client, _config);

            // One-time "server online" announcement (guards against Ready re-firing on reconnects)
            DiscordEventPublisher.OnBridgeReady();

            return Task.CompletedTask;
        }

        // Bot status updater. Reads the verified-client count out of RWT's Network.ServerClients and pushes it as a
        // Discord activity. The cancellation token gets tripped on every Ready so we never leak overlapping loops across reconnects
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
                        string status = count == 1 ? "1 player online" : $"{count} players online";
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

        // Posts link/unlink announcements when configured; safely no-ops if Discord or the channel is disabled.
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

        // Shared Discord post helper with gating, fire-and-forget sends, and host-sensitive text scrubbing.

        // Scrub IPs and absolute paths before Discord posts, while preserving version-like IPv4 strings.
        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Handshake logs quote IPs, including ::ffff: mapped forms, so scrub the whole quoted value.
            text = Regex.Replace(text, @"(Handshake with ')[^']*(')", "$1<ip>$2");
            // Scrub other IPv4s, but leave version-like strings such as 26.6.9.1 intact.
            string src = text;
            text = Regex.Replace(src, @"(?:::ffff:)?\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", m =>
            {
                int i = m.Index;
                if (i > 0 && (src[i - 1] == 'v' || src[i - 1] == 'V')) return m.Value;
                if (i >= 8 && src.Substring(i - 8, 8).Equals("version ", StringComparison.OrdinalIgnoreCase)) return m.Value;
                return "<ip>";
            });
            // Absolute paths -> keep only the leaf name (handles spaces in folder names; stops at line end or '|').
            text = Regex.Replace(text, @"[A-Za-z]:\\[^\r\n|]*",
                m => "…\\" + System.IO.Path.GetFileName(m.Value.Trim().TrimEnd('\\')));
            text = Regex.Replace(text, @"/(?:home|root|Users)/[^\r\n|]*",
                m => "…/" + System.IO.Path.GetFileName(m.Value.Trim().TrimEnd('/')));
            return text;
        }

        internal static void PostToChannel(ulong channelId, string text)
        {
            if (_client == null || _config == null || !_config.IsEnabled) return;
            if (channelId == 0 || string.IsNullOrEmpty(text))             return;
            text = Redact(text); // scrub player IPs + host file paths before anything leaves for Discord

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
                    await channel.SendMessageAsync(text).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Discord: channel send failed: {ex.Message}");
                }
            });
        }

        // Post or edit a single live embed, returning the message id for caller persistence or 0 on failure.
        internal static Task<ulong> PostOrEditEmbedAsync(ulong channelId, ulong prevMessageId, Embed embed)
            => PostOrEditEmbedsAsync(channelId, prevMessageId, embed == null ? null : new[] { embed });

        // Same contract for multiple embeds in one message (Discord allows 10) - the leaderboard live post carries
        // the player + guild boards together
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

                // Try edit-in-place first when we have a prior id. Missing / deleted message returns null from
                // GetMessageAsync - fall through to fresh post in that case
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
                        // Most common: message was deleted manually. Fall through to fresh post so the user gets a
                        // working showcase rather than a silent failure
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

        // Delete a posted message; success or already-missing both let the caller clear state.
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

        // Embed variant of PostToChannel. Same gating, same fire-and-forget semantics. Used by
        // DiscordLeaderboardPoster - anything richer than a plain status line goes through here
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

        // Send embed with bundled thumbnail when available; otherwise fall back to a plain fire-and-forget embed.
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

        private static async Task OnMessage(SocketMessage message)
        {
            try
            {
                if (message == null)                 return;
                if (message.Author == null)          return;
                if (message.Author.IsBot)            return;

                string prefix = _config.CommandPrefix ?? "!";
                string text   = (message.Content ?? "").Trim();

                // Chat bridge relays normal messages in-game, while prefixed messages still fall through to commands.
                if (_config.ChatBridgeChannelId != 0
                    && message.Channel?.Id == _config.ChatBridgeChannelId
                    && !text.StartsWith(prefix, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(text))
                {
                    string display = ResolveDisplay(message.Author);
                    DiscordChatBridge.RelayDiscordToInGame(display, text);
                    return;
                }

                if (!text.StartsWith(prefix, StringComparison.Ordinal)) return;

                // Optional guild allowlist for commands; DMs always work so players can still link.
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

                // Commands only in their designated channels (DMs always allowed for linking) - never spam #general.
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
                        // Delegate command families in order so each feature owns its own surface without bloating this switch.
                        bool handled = await DiscordBrowseCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            handled = await DiscordTradeCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            handled = await DiscordShowcaseCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            handled = await DiscordWtbCommands.TryHandleAsync(message, cmd, parts).ConfigureAwait(false);
                        if (!handled)
                            // Extension commands run last; core commands win any name collision.
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
            // !kmh-link status shows which KMH account this Discord user is linked to.
            if (parts.Length >= 2 && string.Equals(parts[1], "status", StringComparison.OrdinalIgnoreCase))
            {
                ulong  authorIdStatus = message.Author?.Id ?? 0;
                string display = ResolveDisplay(message.Author);
                string bound   = LinkedAccountsStore.FindUsernameByDiscordId(authorIdStatus)
                              ?? LinkedAccountsStore.FindUsernameByDiscord(display);
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
            LinkedAccountsStore.SetLink(username, display2, authorId);
            LinkedAccountsHandler.BroadcastSnapshot();
            AnnounceLink(username, display2, "link");

            ServerLog.Info($"Discord: linked '{username}' <-> '{display2}'");
            await Reply(message,
                $"Linked **{username}** to **{display2}**. Unlink anytime with `!kmh-unlink`.")
                .ConfigureAwait(false);
        }

        private static async Task HandleUnlink(SocketMessage message)
        {
            // Prefer Discord ID unlink so renames can't dodge it; fall back to display name for legacy links.
            ulong authorId = message.Author?.Id ?? 0;
            string display  = ResolveDisplay(message.Author);
            string victim   = LinkedAccountsStore.FindUsernameByDiscordId(authorId)
                           ?? LinkedAccountsStore.FindUsernameByDiscord(display);
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

        // !kmh-leaderboard uses the same builder as the auto-poster, so on-demand output matches posted boards.
        private static async Task HandleLeaderboard(SocketMessage message, string[] parts)
        {
            int topN = _config?.LeaderboardTopCount ?? 10;
            if (topN < 1)  topN = 10;
            if (topN > 25) topN = 25;

            // Guild leaderboard branch.
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

            // Reputation board branch.
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

            // Default: player leaderboard.
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

        // !kmh-rank [player] - per-player stat card with per-metric ranks. No name = the caller's own linked account
        private static async Task HandleRank(SocketMessage message, string[] parts)
        {
            string query = parts.Length >= 2 ? string.Join(" ", parts, 1, parts.Length - 1).Trim() : "";
            if (query.Length == 0)
            {
                query = LinkedAccounts.LinkedAccountsStore.FindUsernameByDiscordId(message.Author.Id)
                     ?? LinkedAccounts.LinkedAccountsStore.FindUsernameByDiscord(message.Author.Username);
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

        // !kmh-whois shows the linked Discord identity for a player, staying quiet when unknown or unlinked.
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

        // !kmh-rep <player> - quest reputation score + tier.
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

        // Prefer GlobalName, falling back to legacy Username#Discriminator; drop #0000 for migrated accounts.
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
