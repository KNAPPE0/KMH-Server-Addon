using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KMHServerAddon.AdminCommands;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Util;

namespace KMHServerAddon.Features.Discord
{
    // The /kmh slash-command tree. Registered on Ready (guild-scoped when Bot.GuildId is set, else global) and
    // dispatched from DiscordBridge. Slash commands don't need the privileged MessageContent intent, so they work on
    // bots that never enabled it. Tiers: player open; mod needs guild-admin or a Roles.Moderators role; console run
    // is gated by the Console block and runs in the Admin channel. The legacy !kmh-* commands keep working in parallel.
    internal static class DiscordSlashCommands
    {
        // -------- registration --------

        public static async Task RegisterAsync(DiscordSocketClient client, DiscordConfig cfg)
        {
            if (client == null || cfg == null || cfg.Bot == null || !cfg.Bot.UseSlashCommands) return;
            try
            {
                ApplicationCommandProperties built = BuildTree().Build();

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

        private static SlashCommandBuilder BuildTree()
        {
            SlashCommandBuilder kmh = new SlashCommandBuilder()
                .WithName("kmh")
                .WithDescription("KMH server commands");

            kmh.AddOption(Sub("status",  "Server health + counts"));
            kmh.AddOption(Sub("players", "Who is online right now"));
            kmh.AddOption(Sub("help",    "List the KMH Discord commands"));
            kmh.AddOption(Sub("whois",   "(owner) An online player's IP for moderation - the reply is private to you")
                .AddOption(Str("player", "In-game name (must be online)", true)));

            // leaderboard
            SlashCommandOptionBuilder lb = Group("leaderboard", "Top players / guilds / reputation");
            lb.AddOption(Sub("show", "Show the leaderboard")
                .AddOption(new SlashCommandOptionBuilder().WithName("board").WithDescription("Which board")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(false)
                    .AddChoice("players", "players").AddChoice("guilds", "guilds").AddChoice("rep", "rep"))
                .AddOption(Str("sort", "Sort key (see /kmh help)", false)));
            lb.AddOption(Sub("rank", "A player's full stat card")
                .AddOption(Str("player", "In-game name (blank = your linked account)", false)));
            lb.AddOption(Sub("post",    "(mod) Post the leaderboard to its channel now"));
            lb.AddOption(Sub("refresh", "(mod) Re-post the leaderboard now"));
            kmh.AddOption(lb);

            // market
            SlashCommandOptionBuilder mk = Group("market", "Browse the marketplace");
            mk.AddOption(Sub("list", "Open listings").AddOption(IntOpt("page", "Page number", false)));
            mk.AddOption(Sub("search", "Search listings").AddOption(Str("query", "Item or seller", true)));
            kmh.AddOption(mk);

            // site
            SlashCommandOptionBuilder st = Group("site", "Custom sites");
            st.AddOption(Sub("list", "All active sites"));
            st.AddOption(Sub("info", "One site by world tile").AddOption(IntOpt("tile", "World tile id", true)));
            kmh.AddOption(st);

            // announcement
            SlashCommandOptionBuilder an = Group("announcement", "Server announcements");
            an.AddOption(Sub("send", "(mod) Post an announcement").AddOption(Str("message", "Text to post", true)));
            kmh.AddOption(an);

            // discord
            SlashCommandOptionBuilder dc = Group("discord", "Bot admin");
            dc.AddOption(Sub("reload", "(mod) Reload Discord config + restart the bridge"));
            kmh.AddOption(dc);

            // config
            SlashCommandOptionBuilder cf = Group("config", "Game config admin");
            cf.AddOption(Sub("reload", "(mod) Reload Economy + Sites config"));
            kmh.AddOption(cf);

            // console
            SlashCommandOptionBuilder co = Group("console", "Owner console");
            co.AddOption(Sub("run", "(owner) Run any server console command")
                .AddOption(Str("command", "e.g. help / players / kmh status / kmh enforce on", true)));
            kmh.AddOption(co);

            return kmh;
        }

        private static SlashCommandOptionBuilder Sub(string name, string desc)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.SubCommand);

        private static SlashCommandOptionBuilder Group(string name, string desc)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.SubCommandGroup);

        private static SlashCommandOptionBuilder Str(string name, string desc, bool required)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.String).WithRequired(required);

        private static SlashCommandOptionBuilder IntOpt(string name, string desc, bool required)
            => new SlashCommandOptionBuilder().WithName(name).WithDescription(desc)
                .WithType(ApplicationCommandOptionType.Integer).WithRequired(required);

        // -------- dispatch --------

        public static async Task Handle(SocketSlashCommand cmd)
        {
            try
            {
                if (cmd?.Data == null || cmd.Data.Name != "kmh") return;
                DiscordConfig cfg = DiscordBridge.Config ?? new DiscordConfig();

                SocketSlashCommandDataOption first = cmd.Data.Options?.FirstOrDefault();
                if (first == null) { await Ephemeral(cmd, "Try `/kmh help`."); return; }

                string group, sub;
                IReadOnlyCollection<SocketSlashCommandDataOption> args;
                if (first.Type == ApplicationCommandOptionType.SubCommandGroup)
                {
                    group = first.Name;
                    SocketSlashCommandDataOption s = first.Options?.FirstOrDefault();
                    sub  = s?.Name ?? "";
                    args = s?.Options;
                }
                else
                {
                    group = "";
                    sub   = first.Name;
                    args  = first.Options;
                }

                string path = string.IsNullOrEmpty(group) ? sub : $"{group}.{sub}";
                await Route(cmd, cfg, path, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Discord: slash handler threw", ex);
                try { if (!cmd.HasResponded) await cmd.RespondAsync("Command failed - see the server log.", ephemeral: true).ConfigureAwait(false); }
                catch { /* interaction may have expired */ }
            }
        }

        private static async Task Route(SocketSlashCommand cmd, DiscordConfig cfg, string path,
                                        IReadOnlyCollection<SocketSlashCommandDataOption> args)
        {
            switch (path)
            {
                case "status":  await DoStatus(cmd, cfg);  return;
                case "players": await DoPlayers(cmd, cfg); return;
                case "help":    await DoHelp(cmd, cfg);    return;

                case "leaderboard.show": await DoLeaderboard(cmd, cfg, args); return;
                case "leaderboard.rank": await DoRank(cmd, cfg, args);        return;
                case "leaderboard.post":
                case "leaderboard.refresh":
                    if (!await RequireMod(cmd, cfg)) return;
                    await DoLeaderboardPost(cmd, cfg); return;

                case "market.list":   await DoMarket(cmd, cfg, args, search: false); return;
                case "market.search": await DoMarket(cmd, cfg, args, search: true);  return;

                case "site.list": await DoSites(cmd, cfg, -1); return;
                case "site.info": await DoSites(cmd, cfg, (int)GetInt(args, "tile", -1)); return;

                case "announcement.send":
                    if (!await RequireMod(cmd, cfg)) return;
                    await DoAnnounce(cmd, cfg, GetStr(args, "message")); return;

                case "discord.reload":
                    if (!await RequireMod(cmd, cfg)) return;
                    await DoDiscordReload(cmd); return;

                case "config.reload":
                    if (!await RequireMod(cmd, cfg)) return;
                    await DoConfigReload(cmd); return;

                case "whois":
                    await DoWhois(cmd, cfg, GetStr(args, "player")); return;

                case "console.run":
                    await DoConsoleRun(cmd, cfg, GetStr(args, "command")); return;

                default:
                    await Ephemeral(cmd, "Unknown command - try `/kmh help`."); return;
            }
        }

        // -------- handlers --------

        private static async Task DoStatus(SocketSlashCommand cmd, DiscordConfig cfg)
        {
            StringBuilder sb = new StringBuilder();
            KmhServerCommands.Dispatch(new[] { "status" }, isAdmin: false, actorName: "discord", line => sb.AppendLine(line));
            await RespondBlock(cmd, sb.ToString(), ephemeral: false).ConfigureAwait(false);
        }

        private static async Task DoPlayers(SocketSlashCommand cmd, DiscordConfig cfg)
        {
            List<string> names = new List<string>();
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (!string.IsNullOrEmpty(u)) names.Add(u);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);

            string body = names.Count == 0
                ? "_Nobody is online right now._"
                : string.Join("\n", names.Select(n => "• " + n));
            await cmd.RespondAsync(embed: Brand(cfg, $"Players online ({names.Count})", body).Build()).ConfigureAwait(false);
        }

        // Owner-only, ephemeral: an online player's IP for moderation. The reply is private to the requesting admin
        // (and RequireConsole keeps it in the admin channel), and it's built directly - NOT routed through
        // DiscordBridge.Redact - so the admin sees the real IP while the channel never does. Online players only
        // (their live connection IP); offline IP history lives in the local server console / `banlist`.
        private static async Task DoWhois(SocketSlashCommand cmd, DiscordConfig cfg, string player)
        {
            if (!await RequireConsole(cmd, cfg)) return;
            player = (player ?? "").Trim();
            if (player.Length == 0) { await Ephemeral(cmd, "Usage: `/kmh whois <player>`"); return; }

            ServerClient match = null;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, player, StringComparison.OrdinalIgnoreCase)) { match = c; break; }
            }
            if (match == null)
            {
                await Ephemeral(cmd, $"`{player}` isn't online. This shows live connection IPs only - for offline players use the local server console / `banlist`.");
                return;
            }

            string name = match.GetData<UserFile>()?.Username ?? player;
            string ip   = string.IsNullOrEmpty(match.IP) ? "(unknown)" : match.IP;
            await cmd.RespondAsync($"**{name}** (online)\nIP: `{ip}`", ephemeral: true).ConfigureAwait(false);
            ServerLog.Info($"Discord: whois {name} by {cmd.User?.Username} (ephemeral reply)");
        }

        private static async Task DoLeaderboard(SocketSlashCommand cmd, DiscordConfig cfg,
                                                IReadOnlyCollection<SocketSlashCommandDataOption> args)
        {
            string board = GetStr(args, "board", "players").ToLowerInvariant();
            string sort  = GetStr(args, "sort", "");
            int topN     = Clamp(cfg.LeaderboardTopCount, 1, 25);

            Embed e;
            if (board == "guilds")
                e = DiscordLeaderboardBuilder.BuildGuilds(topN,
                        string.IsNullOrEmpty(sort) ? DiscordLeaderboardBuilder.DefaultGuildSort : DiscordLeaderboardBuilder.NormalizeGuildSort(sort));
            else if (board == "rep")
                e = DiscordLeaderboardBuilder.BuildReputation(topN);
            else
                e = DiscordLeaderboardBuilder.Build(topN,
                        string.IsNullOrEmpty(sort) ? DiscordLeaderboardBuilder.DefaultSort : DiscordLeaderboardBuilder.NormalizeSort(sort));

            if (e == null) { await Ephemeral(cmd, "No leaderboard data yet."); return; }
            await cmd.RespondAsync(embed: e).ConfigureAwait(false);
        }

        private static async Task DoRank(SocketSlashCommand cmd, DiscordConfig cfg,
                                         IReadOnlyCollection<SocketSlashCommandDataOption> args)
        {
            string query = GetStr(args, "player", "").Trim();
            if (query.Length == 0)
            {
                query = LinkedAccountsStore.FindUsernameByDiscordId(cmd.User.Id)
                     ?? LinkedAccountsStore.FindUsernameByDiscord(cmd.User.Username);
                if (string.IsNullOrEmpty(query))
                {
                    await Ephemeral(cmd, "Give a player name, or link your account (`!kmh-link`) to use it bare.");
                    return;
                }
            }

            Embed card = DiscordLeaderboardBuilder.BuildPlayerCard(query, out List<string> matches);
            if (card == null)
            {
                await Ephemeral(cmd, matches != null && matches.Count > 0
                    ? $"Multiple players match \"{query}\": {string.Join(", ", matches)}"
                    : $"No player named \"{query}\" has been seen on this server.");
                return;
            }
            await cmd.RespondAsync(embed: card).ConfigureAwait(false);
        }

        private static async Task DoLeaderboardPost(SocketSlashCommand cmd, DiscordConfig cfg)
        {
            ulong ch = cfg.LeaderboardChannelId;
            if (ch == 0) { await Ephemeral(cmd, "No leaderboard channel set (Channels.Leaderboard)."); return; }
            int n = Clamp(cfg.LeaderboardTopCount, 1, 25);
            Embed e = DiscordLeaderboardBuilder.Build(n, DiscordLeaderboardBuilder.DefaultSort);
            if (e == null) { await Ephemeral(cmd, "No leaderboard data yet."); return; }
            Embed g = DiscordLeaderboardBuilder.BuildGuilds(n, DiscordLeaderboardBuilder.DefaultGuildSort);
            await DiscordBridge.PostOrEditEmbedsAsync(ch, 0, g == null ? new[] { e } : new[] { e, g }).ConfigureAwait(false);
            await cmd.RespondAsync("Posted the leaderboard.", ephemeral: true).ConfigureAwait(false);
        }

        private static async Task DoMarket(SocketSlashCommand cmd, DiscordConfig cfg,
                                           IReadOnlyCollection<SocketSlashCommandDataOption> args, bool search)
        {
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot("");
            List<MarketplaceListing> rows = snap?.Listings ?? new List<MarketplaceListing>();

            string title = "Marketplace";
            if (search)
            {
                string q = GetStr(args, "query", "").Trim();
                title = $"Marketplace · \"{q}\"";
                rows = rows.Where(l =>
                        ItemLabelCache.LabelFor(l.ItemDefName).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                     || (l.ItemDefName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                     || (l.SellerUsername ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            rows.Sort((a, b) =>
            {
                int byItem = string.Compare(a.ItemDefName, b.ItemDefName, StringComparison.OrdinalIgnoreCase);
                return byItem != 0 ? byItem : a.UnitPriceSilver.CompareTo(b.UnitPriceSilver);
            });

            EmbedBuilder eb = Brand(cfg, title, null);
            if (rows.Count == 0)
            {
                eb.WithDescription(search ? "_No listings match._" : "_No open listings right now._");
                await cmd.RespondAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            const int pageSize = 8;
            int totalPages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
            int page = (int)Clamp(GetInt(args, "page", 1), 1, totalPages);
            int skip = (page - 1) * pageSize;
            int end  = Math.Min(skip + pageSize, rows.Count);
            for (int i = skip; i < end; i++)
            {
                MarketplaceListing l = rows[i];
                eb.AddField($"#{l.Id} · {DiscordText.Escape(ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex))}",
                    $"**{l.RemainingQty}**× @ `{SilverFmt.Format(l.UnitPriceSilver)}/ea` · by **{DiscordText.Escape(l.SellerUsername)}**",
                    inline: false);
            }
            eb.WithFooter(totalPages > 1 ? $"Page {page}/{totalPages} · {rows.Count} listings" : $"{rows.Count} listing(s)");
            await cmd.RespondAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        private static async Task DoSites(SocketSlashCommand cmd, DiscordConfig cfg, int tile)
        {
            SiteSnapshot snap = SiteStore.BuildSnapshotFor("");
            List<SiteEntry> sites = snap?.Sites ?? new List<SiteEntry>();

            if (tile >= 0)
            {
                SiteEntry s = sites.FirstOrDefault(x => x.Tile == tile);
                if (s == null) { await Ephemeral(cmd, $"No site at tile {tile}."); return; }
                EmbedBuilder eb = Brand(cfg, $"Site · tile {s.Tile}", null)
                    .AddField("Owner",   string.IsNullOrEmpty(s.OwnerGuild) ? DiscordText.Escape(s.OwnerUsername) : $"{DiscordText.Escape(s.OwnerUsername)} ({DiscordText.Escape(s.OwnerGuild)})", true)
                    .AddField("Produces", $"{s.BaseAmountPerCycle}× {DiscordText.Escape(ItemLabelCache.LabelFor(s.ItemDefName))}", true)
                    .AddField("Access",  AccessLabel(s.AccessMode), true)
                    .AddField("Workers", $"{s.Workers?.Count ?? 0}/{s.MaxWorkers}", true);
                await cmd.RespondAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            EmbedBuilder list = Brand(cfg, $"Sites ({sites.Count})", null);
            if (sites.Count == 0)
            {
                list.WithDescription("_No custom sites built yet._");
                await cmd.RespondAsync(embed: list.Build()).ConfigureAwait(false);
                return;
            }
            foreach (SiteEntry s in sites.Take(15))
            {
                string owner = string.IsNullOrEmpty(s.OwnerGuild) ? DiscordText.Escape(s.OwnerUsername) : $"{DiscordText.Escape(s.OwnerUsername)} ({DiscordText.Escape(s.OwnerGuild)})";
                list.AddField($"Tile {s.Tile} · {DiscordText.Escape(ItemLabelCache.LabelFor(s.ItemDefName))}",
                    $"{s.BaseAmountPerCycle}×/cycle · {AccessLabel(s.AccessMode)} · by **{owner}** · workers {s.Workers?.Count ?? 0}/{s.MaxWorkers}",
                    inline: false);
            }
            if (sites.Count > 15) list.WithFooter($"Showing 15 of {sites.Count}");
            await cmd.RespondAsync(embed: list.Build()).ConfigureAwait(false);
        }

        private static async Task DoAnnounce(SocketSlashCommand cmd, DiscordConfig cfg, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) { await Ephemeral(cmd, "Provide a message."); return; }
            ulong ch = cfg.AnnouncementsChannelId;
            if (ch == 0) { await Ephemeral(cmd, "No announcements channel set (Channels.Announcements)."); return; }

            EmbedBuilder eb = Brand(cfg, "📢 Announcement", message)
                .WithFooter(cfg.Branding?.DisplayName ?? "KMH Server")
                .WithCurrentTimestamp();
            DiscordBridge.PostEmbedToChannel(ch, eb.Build());
            ServerLog.Info($"Discord: announcement by {cmd.User?.Username}: {message}");
            await cmd.RespondAsync("Announcement posted.", ephemeral: true).ConfigureAwait(false);
        }

        private static async Task DoDiscordReload(SocketSlashCommand cmd)
        {
            // Respond first - Reload tears down the client handling this very interaction, so confirm before
            // pulling the rug
            await cmd.RespondAsync("Reloading the Discord bridge...", ephemeral: true).ConfigureAwait(false);
            DiscordBridge.Reload();
        }

        private static async Task DoConfigReload(SocketSlashCommand cmd)
        {
            Features.Economy.EconomyConfig.Reload();
            Features.Sites.SitesConfig.Reload();
            await cmd.RespondAsync("Reloaded Economy + Sites config.", ephemeral: true).ConfigureAwait(false);
        }

        private static async Task DoConsoleRun(SocketSlashCommand cmd, DiscordConfig cfg, string command)
        {
            if (!await RequireConsole(cmd, cfg)) return;
            // ConsoleExecutor.Run is synchronous and can run past Discord's 3-second interaction window (big help/
            // list output), which throws "Cannot respond after 3 seconds". Acknowledge immediately with DeferAsync
            // (buys ~15 min), then follow up once the captured output is ready
            await cmd.DeferAsync(ephemeral: true).ConfigureAwait(false);
            string output;
            try { output = ConsoleExecutor.Run(command); }
            catch (Exception ex) { output = $"(command threw) {ex.Message}"; }
            output = DiscordBridge.Redact(output); // scrub player IPs + host paths from console output before Discord
            ServerLog.Info($"Discord: console-run by {cmd.User?.Username}: {command}");
            await FollowupBlocks(cmd, output).ConfigureAwait(false);
        }

        private static async Task DoHelp(SocketSlashCommand cmd, DiscordConfig cfg)
        {
            EmbedBuilder eb = Brand(cfg, "KMH Discord commands", null)
                .AddField("Anyone",
                    "`/kmh status` · `/kmh players` · `/kmh leaderboard show` · `/kmh market list|search` · `/kmh site list|info` · `/kmh help`", false)
                .AddField("Moderators",
                    "`/kmh leaderboard post|refresh` · `/kmh announcement send` · `/kmh discord reload` · `/kmh config reload`", false)
                .AddField("Owners",
                    "`/kmh console run <command>` _(gated by the Console block in DiscordConfig.json)_", false)
                .WithFooter("Account linking still uses /kmh link in-game + !kmh-link here.");
            await cmd.RespondAsync(embed: eb.Build(), ephemeral: true).ConfigureAwait(false);
        }

        // -------- permissions --------

        private static async Task<bool> RequireMod(SocketSlashCommand cmd, DiscordConfig cfg)
        {
            if (IsMod(cmd.User, cfg)) return true;
            await cmd.RespondAsync("That command needs a moderator or admin role.", ephemeral: true).ConfigureAwait(false);
            return false;
        }

        private static bool IsMod(SocketUser user, DiscordConfig cfg)
        {
            if (!(user is SocketGuildUser gu)) return false;
            if (gu.GuildPermissions.Administrator) return true;
            foreach (SocketRole r in gu.Roles)
                if (HasRole(cfg.Roles?.Moderators, r)) return true;
            return false;
        }

        private static async Task<bool> RequireConsole(SocketSlashCommand cmd, DiscordConfig cfg)
        {
            if (!cfg.AllowConsoleCommands)
            {
                await cmd.RespondAsync("Console commands are disabled (Console.Allow = false).", ephemeral: true).ConfigureAwait(false);
                return false;
            }
            ulong wantChannel = cfg.ConsoleCommandsChannelId;
            if (wantChannel != 0 && cmd.Channel?.Id != wantChannel)
            {
                await cmd.RespondAsync("Console commands must be used in the Admin channel.", ephemeral: true).ConfigureAwait(false);
                return false;
            }
            if (cfg.RequireConsoleRole)
            {
                bool ok = false;
                if (cmd.User is SocketGuildUser gu)
                    foreach (SocketRole r in gu.Roles)
                        if (HasRole(cfg.Roles?.ConsoleAccess, r)) { ok = true; break; }
                if (!ok)
                {
                    await cmd.RespondAsync("You need a console-access role for that.", ephemeral: true).ConfigureAwait(false);
                    return false;
                }
            }
            return true;
        }

        private static bool HasRole(string[] cfgRoles, SocketRole role)
        {
            if (cfgRoles == null || role == null) return false;
            foreach (string s in cfgRoles)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string t = s.Trim();
                if (t == role.Id.ToString()) return true;
                if (string.Equals(t, role.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // -------- helpers --------

        private static EmbedBuilder Brand(DiscordConfig cfg, string title, string description)
        {
            EmbedBuilder eb = new EmbedBuilder().WithTitle(title).WithColor(BrandColor(cfg));
            if (!string.IsNullOrEmpty(description)) eb.WithDescription(description);
            return eb;
        }

        private static Color BrandColor(DiscordConfig cfg) => KmhEmbedBuilder.BrandColor(cfg);

        private static string AccessLabel(string mode)
            => mode == SiteEntry.AccessPublic ? "Public"
             : mode == SiteEntry.AccessPrivate ? "Private"
             : "Guild + allies";

        private static Task Ephemeral(SocketSlashCommand cmd, string text)
            => cmd.RespondAsync(text, ephemeral: true);

        // Wrap multi-line console/status output in a code block, clamped to Discord's 2000-char message ceiling
        private static Task RespondBlock(SocketSlashCommand cmd, string text, bool ephemeral)
        {
            string body = string.IsNullOrWhiteSpace(text) ? "(no output)" : text.TrimEnd();
            if (body.Length > 1900) body = body.Substring(0, 1900) + "\n…(truncated)";
            return cmd.RespondAsync($"```\n{body}\n```", ephemeral: ephemeral);
        }

        // Send captured output as one or more ```code``` followups after a DeferAsync, split on line boundaries so
        // big results (help / deeplist) come through in full instead of being truncated to one message
        private static async Task FollowupBlocks(SocketSlashCommand cmd, string text)
        {
            string body = string.IsNullOrWhiteSpace(text) ? "(no output)" : text.TrimEnd();
            List<string> chunks = SplitForBlocks(body, 1900);
            const int maxMsgs = 5; // never flood the channel - cap the followups
            for (int i = 0; i < chunks.Count && i < maxMsgs; i++)
            {
                string c = chunks[i];
                if (i == maxMsgs - 1 && chunks.Count > maxMsgs) c += "\n…(truncated)";
                await cmd.FollowupAsync($"```\n{c}\n```", ephemeral: true).ConfigureAwait(false);
            }
        }

        // Split text into chunks no larger than cap, breaking on newlines (a single over-long line is hard-split).
        private static List<string> SplitForBlocks(string text, int cap)
        {
            List<string> chunks = new List<string>();
            StringBuilder sb = new StringBuilder();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw;
                while (line.Length > cap) { chunks.Add(line.Substring(0, cap)); line = line.Substring(cap); }
                if (sb.Length + line.Length + 1 > cap) { chunks.Add(sb.ToString().TrimEnd('\n')); sb.Clear(); }
                sb.Append(line).Append('\n');
            }
            if (sb.Length > 0) chunks.Add(sb.ToString().TrimEnd('\n'));
            return chunks;
        }

        private static string GetStr(IReadOnlyCollection<SocketSlashCommandDataOption> args, string name, string def = "")
        {
            if (args == null) return def;
            foreach (SocketSlashCommandDataOption a in args)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    return a.Value?.ToString() ?? def;
            return def;
        }

        private static long GetInt(IReadOnlyCollection<SocketSlashCommandDataOption> args, string name, long def)
        {
            if (args == null) return def;
            foreach (SocketSlashCommandDataOption a in args)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    try { return Convert.ToInt64(a.Value, CultureInfo.InvariantCulture); }
                    catch { return def; }
                }
            return def;
        }

        private static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int  Clamp(int  v, int  lo, int  hi) => v < lo ? lo : (v > hi ? hi : v);

        private static ulong ParseId(string s) => ulong.TryParse((s ?? "").Trim(), out ulong v) ? v : 0UL;
    }
}
