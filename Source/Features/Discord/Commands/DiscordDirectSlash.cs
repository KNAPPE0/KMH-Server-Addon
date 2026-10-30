using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // Legacy direct mode only; in coordinator mode none of this runs.
    internal static class DiscordDirectSlash
    {
        public static async Task RegisterAsync(DiscordSocketClient client, DiscordConfig cfg)
        {
            if (client == null || cfg == null || cfg.Bot == null || !cfg.Bot.UseSlashCommands) return;
            try
            {
                SlashCommandProperties built = KmhSlashTree.Build();

                ulong guildId = ParseId(cfg.Bot.GuildId);
                if (guildId != 0)
                {
                    SocketGuild g = client.GetGuild(guildId);
                    if (g != null)
                    {
                        await g.CreateApplicationCommandAsync(built).ConfigureAwait(false);
                        ServerLog.Info($"Discord: registered /kmh slash commands to guild {guildId}");
                        return;
                    }
                    ServerLog.Warn($"Discord: Bot.GuildId {guildId} not found - registering /kmh globally instead (up to 1h to appear)");
                }

                await client.CreateGlobalApplicationCommandAsync(built).ConfigureAwait(false);
                ServerLog.Info("Discord: registered /kmh slash commands globally (can take up to 1h to appear)");
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: slash-command registration failed", ex);
            }
        }

        public static async Task HandleAsync(SocketSlashCommand cmd)
        {
            if (cmd?.Data == null || cmd.Data.Name != KmhSlashTree.Root) return;

            KmhSlashTree.ReadPath(cmd.Data.Options, out string path, out Dictionary<string, string> options);
            if (string.IsNullOrEmpty(path))
            {
                try { await cmd.RespondAsync("Try `/kmh help`.", ephemeral: true).ConfigureAwait(false); } catch (Exception) { }
                return;
            }

            bool deferredPrivate = KmhSlashTree.IsPrivate(path);
            if (!await KmhInteractionResponder.DeferAsync(cmd, path).ConfigureAwait(false)) return;

            KmhDiscordCommandResult result;
            if (path == "servers" || path == "here")
                result = OneServerAnswer(path);
            else
                result = await DiscordSlashCommands.ExecuteAsync(ToRequest(cmd, path, options)).ConfigureAwait(false);

            await KmhInteractionResponder.ApplyAsync(cmd, result, deferredPrivate, ServerLog.Warn).ConfigureAwait(false);
        }

        // Direct mode is a fleet of one, so these answer locally rather than pretending a coordinator exists.
        private static KmhDiscordCommandResult OneServerAnswer(string path)
            => KmhDiscordCommandResult.Text(
                $"This bot serves one Colony Nexus server: **{KmhServerIdentity.Name}**.", ephemeral: true);

        internal static KmhDiscordCommandRequest ToRequest(SocketSlashCommand cmd, string path,
                                                           Dictionary<string, string> options)
        {
            KmhDiscordCommandRequest req = new KmhDiscordCommandRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Path        = path,
                Options     = options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ServerId    = KmhDiscordIdentity.ServerId,
                UserId      = cmd.User?.Id ?? 0,
                UserName    = cmd.User?.Username ?? "",
                UserDisplay = cmd.User?.GlobalName ?? cmd.User?.Username ?? "",
                GuildId     = cmd.GuildId ?? 0,
                ChannelId   = cmd.Channel?.Id ?? 0,
                IsDirectMessage = cmd.GuildId == null,
                Locale      = cmd.UserLocale ?? "",
                IssuedAtUnix  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(),
            };

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

        private static ulong ParseId(string s) => ulong.TryParse((s ?? "").Trim(), out ulong v) ? v : 0UL;
    }
}
