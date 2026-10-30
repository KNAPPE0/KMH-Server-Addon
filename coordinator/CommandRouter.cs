using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Kmh.Discord.Wire;

namespace KmhDiscordCoordinator
{
    // Acknowledges inside Discord's window first, then waits for the target server's neutral result.
    public static class CommandRouter
    {
        public static async Task HandleAsync(GatewayHost gateway, CoordinatorConfig cfg, ServerRegistry registry,
                                             SocketSlashCommand cmd)
        {
            if (cmd?.Data == null || cmd.Data.Name != KmhSlashTree.Root) return;
            try
            {
                KmhSlashTree.ReadPath(cmd.Data.Options, out string path, out Dictionary<string, string> options);
                if (string.IsNullOrEmpty(path))
                {
                    await cmd.RespondAsync("Try `/kmh help`.", ephemeral: true).ConfigureAwait(false);
                    return;
                }

                bool deferredPrivate = KmhSlashTree.IsPrivate(path);
                if (!await KmhInteractionResponder.DeferAsync(cmd, path).ConfigureAwait(false)) return;

                KmhDiscordCommandResult result = KmhSlashTree.IsCoordinatorPath(path)
                    ? Local(registry, cmd, path)
                    : await DispatchAsync(cfg, registry, cmd, path, options).ConfigureAwait(false);

                await KmhInteractionResponder.ApplyAsync(cmd, result, deferredPrivate, CoordLog.Warn).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CoordLog.Error("slash handling threw", ex);
            }
        }

        private static KmhDiscordCommandResult Local(ServerRegistry registry, SocketSlashCommand cmd, string path)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            switch (path)
            {
                case "servers":
                {
                    List<LiveServer> all = registry.All();
                    if (all.Count == 0) return KmhDiscordCommandResult.Text("No Colony Nexus servers are configured.");
                    KmhEmbedSpecBuilder eb = new KmhEmbedSpecBuilder()
                        .WithTitle($"Colony Nexus servers ({all.Count(s => s.Online(now))} online)")
                        .WithColor(0xC88A2A)
                        .WithCurrentTimestamp();
                    foreach (LiveServer s in all)
                    {
                        bool up = s.Online(now);
                        eb.AddField(GatewayHost.Label(s),
                            up ? $"`{s.Alias}` · online · {s.Players} player(s) · Colony Nexus {s.KmhVersion}"
                               : $"`{s.Alias}` · **offline**",
                            false);
                    }
                    return KmhDiscordCommandResult.Embed(eb.Build());
                }

                case "here":
                {
                    LiveServer owner = registry.ByChannel(cmd.Channel?.Id ?? 0);
                    return KmhDiscordCommandResult.Text(owner == null
                        ? "This channel is not mapped to a Colony Nexus server - pass `server:` to choose one."
                        : $"This channel talks to **{GatewayHost.Label(owner)}** (`{owner.Alias}`).", ephemeral: true);
                }

                default:
                    return KmhDiscordCommandResult.Error("Unknown command - try `/kmh help`.");
            }
        }

        private static async Task<KmhDiscordCommandResult> DispatchAsync(CoordinatorConfig cfg, ServerRegistry registry,
            SocketSlashCommand cmd, string path, Dictionary<string, string> options)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string asked = options != null && options.TryGetValue(KmhSlashTree.ServerOption, out string v) ? (v ?? "").Trim() : "";
            options?.Remove(KmhSlashTree.ServerOption);

            LiveServer target = Resolve(registry, cmd.Channel?.Id ?? 0, asked, now, out KmhDiscordCommandResult problem);
            if (problem != null) return problem;

            if (!target.Online(now))
                return KmhDiscordCommandResult.Error(
                    $"**{GatewayHost.Label(target)}** is not connected right now.", KmhDiscordError.Offline);

            string need = KmhSlashTree.CapabilityFor(path);
            if (need.Length > 0 && target.Capabilities.Count > 0 && !target.Capabilities.Contains(need))
                return KmhDiscordCommandResult.Error(
                    $"**{GatewayHost.Label(target)}** does not offer `{need}`.", KmhDiscordError.Unsupported);

            KmhDiscordCommandRequest req = Build(cmd, path, options, target);
            target.Enqueue(new KmhDiscordInbound
            {
                Kind = KmhDiscordInboundKind.Command,
                ChannelId = req.ChannelId,
                GuildId = req.GuildId,
                IsDirectMessage = req.IsDirectMessage,
                Command = req,
            });

            KmhDiscordCommandResult result = await registry
                .Await(req.RequestId, TimeSpan.FromSeconds(cfg.CommandTimeoutSeconds), CancellationToken.None)
                .ConfigureAwait(false);

            return Finish(result, target);
        }

        internal static KmhDiscordCommandResult Finish(KmhDiscordCommandResult result, LiveServer target)
        {
            if (result == null) return null;
            // Clicks route by channel, so a button in another server's answer would act on the channel's own server.
            result.Components = null;
            // Named, so a fleet answer says which world it came from even when several look alike.
            if (!result.IsError && result.Embeds != null)
                foreach (KmhEmbedSpec e in result.Embeds)
                    if (e != null && string.IsNullOrEmpty(e.Footer)) e.Footer = GatewayHost.Label(target);
            return result;
        }

        // Explicit option, then the channel's own server, then the only eligible one. Never an arbitrary pick.
        private static LiveServer Resolve(ServerRegistry registry, ulong channelId, string asked, DateTimeOffset now,
                                          out KmhDiscordCommandResult problem)
        {
            problem = null;

            if (asked.Length > 0)
            {
                LiveServer named = registry.ByAlias(asked) ?? registry.ByServerId(asked);
                if (named == null)
                    problem = KmhDiscordCommandResult.Error(
                        $"No Colony Nexus server called `{asked}`. Try `/kmh servers`.", KmhDiscordError.UnknownServer);
                return named;
            }

            LiveServer byChannel = registry.ByChannel(channelId);
            if (byChannel != null) return byChannel;

            List<LiveServer> live = registry.OnlineServers();
            if (live.Count == 1) return live[0];

            StringBuilder sb = new StringBuilder("Which server? Pass `server:` - ");
            sb.Append(live.Count == 0 ? "no Colony Nexus server is connected right now."
                                      : string.Join(", ", live.Select(s => "`" + s.Alias + "`")));
            problem = KmhDiscordCommandResult.Error(sb.ToString(), KmhDiscordError.Ambiguous);
            return null;
        }

        private static KmhDiscordCommandRequest Build(SocketSlashCommand cmd, string path,
                                                      Dictionary<string, string> options, LiveServer target)
        {
            KmhDiscordCommandRequest req = new KmhDiscordCommandRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Path = path,
                Options = options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ServerId = target.ServerId,
                UserId = cmd.User?.Id ?? 0,
                UserName = cmd.User?.Username ?? "",
                UserDisplay = cmd.User?.GlobalName ?? cmd.User?.Username ?? "",
                GuildId = cmd.GuildId ?? 0,
                ChannelId = cmd.Channel?.Id ?? 0,
                IsDirectMessage = cmd.GuildId == null,
                Locale = cmd.UserLocale ?? "",
                IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(),
            };

            // Discord's own permissions travel with the request; the target still applies its own policy.
            if (cmd.User is SocketGuildUser gu)
            {
                req.IsGuildAdministrator = gu.GuildPermissions.Administrator;
                foreach (SocketRole r in gu.Roles)
                {
                    if (r == null) continue;
                    req.RoleIds.Add(r.Id.ToString());
                    req.RoleNames.Add(r.Name ?? "");
                }
            }
            return req;
        }
    }
}
