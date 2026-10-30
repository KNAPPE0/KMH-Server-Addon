using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
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
    // Nothing here knows whether the caller reached Discord through this process or the fleet coordinator.
    internal static class DiscordSlashCommands
    {
        public static async Task<KmhDiscordCommandResult> ExecuteAsync(KmhDiscordCommandRequest req)
        {
            if (req == null) return KmhDiscordCommandResult.Error("Empty command.");
            DiscordConfig cfg = DiscordBridge.Config ?? new DiscordConfig();
            if (!FromAllowedGuild(req, cfg)) return KmhDiscordCommandResult.Error("This bot does not serve that Discord server.");
            try
            {
                return RedactPublic(AsText(await Route(req, cfg).ConfigureAwait(false)));
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Discord: /kmh {req.Path} threw", ex);
                return KmhDiscordCommandResult.Error("Command failed - see the server log.");
            }
        }

        // Cards are answered as compact text, so no slash reply arrives as an embed, through either gateway.
        internal static KmhDiscordCommandResult AsText(KmhDiscordCommandResult r)
        {
            if (r?.Embeds == null || r.Embeds.Count == 0) return r;
            string card = DiscordCompactText.Render(r.Embeds);
            r.Content = string.IsNullOrEmpty(r.Content) ? card : DiscordCompactText.Cap(r.Content + "\n" + card, DiscordCompactText.MaxChars);
            r.Embeds = new List<KmhEmbedSpec>();
            return r;
        }

        // A reply the whole channel sees gets the same redaction as a channel post.
        internal static KmhDiscordCommandResult RedactPublic(KmhDiscordCommandResult r)
        {
            if (r == null) return null;
            if (!r.Ephemeral) r.Content = DiscordBridge.Redact(r.Content);
            if (r.Followups != null)
                foreach (KmhDiscordFollowup f in r.Followups)
                    if (f != null && !f.Ephemeral) f.Content = DiscordBridge.Redact(f.Content);
            return r;
        }

        private static async Task<KmhDiscordCommandResult> Route(KmhDiscordCommandRequest req, DiscordConfig cfg)
        {
            switch (req.Path)
            {
                case "status":  return DoStatus();
                case "players": return DoPlayers(cfg);
                case "help":    return DoHelp(cfg);

                case "leaderboard.show": return DoLeaderboard(cfg, req);
                case "leaderboard.rank": return DoRank(req);
                case "leaderboard.post":
                case "leaderboard.refresh":
                {
                    KmhDiscordCommandResult denied = RequireMod(req, cfg);
                    return denied ?? await DoLeaderboardPost(cfg).ConfigureAwait(false);
                }

                case "market.list":   return DoMarket(cfg, req, search: false);
                case "market.search": return DoMarket(cfg, req, search: true);

                case "site.list":  return DoSites(cfg, -1);
                case "site.info":  return DoSites(cfg, (int)req.OptionInt("tile", -1));
                case "site.roads": return DoRoads(cfg);

                case "announcement.send":
                {
                    KmhDiscordCommandResult denied = RequireMod(req, cfg);
                    return denied ?? DoAnnounce(cfg, req, req.Option("message"));
                }

                case "discord.reload":
                {
                    KmhDiscordCommandResult denied = RequireMod(req, cfg);
                    return denied ?? DoDiscordReload();
                }

                case "config.reload":
                {
                    KmhDiscordCommandResult denied = RequireMod(req, cfg);
                    return denied ?? DoConfigReload();
                }

                case "ban":    return DoBan(cfg, req, alsoAddress: false);
                case "ipban":  return DoBan(cfg, req, alsoAddress: true);
                case "unban":  return DoUnban(cfg, req);
                case "bans":   return DoBanList(cfg, req);

                case "whois":       return DoWhois(cfg, req, req.Option("player"));
                case "console.run": return DoConsoleRun(cfg, req, req.Option("command"));

                default: return KmhDiscordCommandResult.Error("Unknown command - try `/kmh help`.");
            }
        }

        private static KmhDiscordCommandResult DoStatus()
        {
            StringBuilder sb = new StringBuilder();
            KmhServerCommands.Dispatch(new[] { "status" }, isAdmin: false, actorName: "discord", line => sb.AppendLine(line));
            return Block(sb.ToString(), ephemeral: false);
        }

        private static KmhDiscordCommandResult DoPlayers(DiscordConfig cfg)
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
            return KmhDiscordCommandResult.Embed(Brand(cfg, $"Players online ({names.Count})", body).Build());
        }

        // Admin channel only, like the console: a public ban names the player, and an address ban their address.
        private static KmhDiscordCommandResult DoBan(DiscordConfig cfg, KmhDiscordCommandRequest req, bool alsoAddress)
        {
            KmhDiscordCommandResult denied = RequireModeration(req, cfg);
            if (denied != null) return denied;

            string player = (req.Option("player") ?? "").Trim();
            if (player.Length == 0)
                return KmhDiscordCommandResult.Error($"Usage: `/kmh {(alsoAddress ? "ipban" : "ban")} <player> [reason] [days]`");

            string reason = (req.Option("reason") ?? "").Trim();
            long days = req.OptionInt("days", 0);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long expires = days > 0 ? now + days * 86400L : 0;

            ServerClient online = FindOnline(player);
            string ip = alsoAddress ? (online?.IP ?? "") : "";
            if (alsoAddress && string.IsNullOrEmpty(ip))
                return KmhDiscordCommandResult.Error(
                    $"`{player}` isn't online, so there is no address to ban. Use `/kmh ban {player}` for a name ban.");

            bool added = Features.Moderation.BanStore.Add(new Features.Moderation.BanEntry
            {
                Username = player, Ip = ip, Reason = reason,
                By = req.UserName ?? "discord", AtUnix = now, ExpiresUnix = expires,
            }, out bool saved);
            if (!added) return KmhDiscordCommandResult.Error("Nothing to ban - give a player name.");

            if (online != null) online.Listener?.MarkForDisconnect();

            string how = alsoAddress ? "name and address" : "name";
            string when = days > 0 ? $"for {days} day(s)" : "permanently";
            ServerLog.Warn($"Discord: {req.UserName} banned {player} by {how} {when}"
                         + (reason.Length == 0 ? "." : $" - {reason}")
                         + (saved ? "" : " NOT SAVED: Bans.json could not be written; the next flush retries."));
            return KmhDiscordCommandResult.Text(
                $"Banned **{player}** by {how}, {when}." + (online != null ? " They were disconnected." : "")
                + (saved ? "" : " **The ban could not be saved yet.** It holds now and Colony Nexus keeps retrying, but a restart before that succeeds would lift it."),
                ephemeral: true);
        }

        private static KmhDiscordCommandResult DoUnban(DiscordConfig cfg, KmhDiscordCommandRequest req)
        {
            KmhDiscordCommandResult denied = RequireModeration(req, cfg);
            if (denied != null) return denied;

            string subject = (req.Option("player") ?? "").Trim();
            if (subject.Length == 0) return KmhDiscordCommandResult.Error("Usage: `/kmh unban <player or address>`");

            int removed = Features.Moderation.BanStore.Remove(subject, out bool saved);
            if (!saved)
                return KmhDiscordCommandResult.Error(
                    $"The server could not record lifting the ban on `{subject}`, so it is still in place. Try again shortly.");
            if (removed == 0) return KmhDiscordCommandResult.Text($"`{subject}` was not banned.", ephemeral: true);

            ServerLog.Warn($"Discord: {req.UserName} lifted {removed} ban entr(y/ies) for {subject}.");
            return KmhDiscordCommandResult.Text($"Lifted {removed} ban entr(y/ies) for **{subject}**.", ephemeral: true);
        }

        private static KmhDiscordCommandResult DoBanList(DiscordConfig cfg, KmhDiscordCommandRequest req)
        {
            KmhDiscordCommandResult denied = RequireModeration(req, cfg);
            if (denied != null) return denied;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Features.Moderation.BanEntry> bans = Features.Moderation.BanStore.Active(now);
            if (bans.Count == 0) return KmhDiscordCommandResult.Text("Nobody is banned.", ephemeral: true);

            StringBuilder sb = new StringBuilder();
            int shown = 0;
            foreach (Features.Moderation.BanEntry b in bans)
            {
                if (shown++ >= 25) break;
                sb.Append("• ");
                sb.Append(string.IsNullOrEmpty(b.Username) ? "(no name)" : b.Username);
                if (!string.IsNullOrEmpty(b.Ip)) sb.Append(" + address");
                if (b.ExpiresUnix != 0) sb.Append(" _(expires)_");
                if (!string.IsNullOrEmpty(b.Reason)) sb.Append(" - ").Append(b.Reason);
                sb.AppendLine();
            }
            if (bans.Count > shown) sb.Append($"_…and {bans.Count - shown} more._");
            return KmhDiscordCommandResult.Text(sb.ToString(), ephemeral: true);
        }

        private static ServerClient FindOnline(string player)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, player, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        // The real IP reaches the admin and never the channel, so this reply is always private.
        private static KmhDiscordCommandResult DoWhois(DiscordConfig cfg, KmhDiscordCommandRequest req, string player)
        {
            KmhDiscordCommandResult denied = RequireModeration(req, cfg);
            if (denied != null) return denied;

            player = (player ?? "").Trim();
            if (player.Length == 0) return KmhDiscordCommandResult.Error("Usage: `/kmh whois <player>`");

            ServerClient match = FindOnline(player);
            if (match == null)
                return KmhDiscordCommandResult.Error(
                    $"`{player}` isn't online. This shows live connection IPs only - for offline players use the local server console / `banlist`.");

            string name = match.GetData<UserFile>()?.Username ?? player;
            string ip   = string.IsNullOrEmpty(match.IP) ? "(unknown)" : match.IP;
            ServerLog.Info($"Discord: whois {name} by {req.UserName} (private reply)");
            return KmhDiscordCommandResult.Text($"**{name}** (online)\nIP: `{ip}`", ephemeral: true);
        }

        private static KmhDiscordCommandResult DoLeaderboard(DiscordConfig cfg, KmhDiscordCommandRequest req)
        {
            string board = req.Option("board", "players").ToLowerInvariant();
            string sort  = req.Option("sort", "");

            string key;
            if (board == "guilds")
                key = string.IsNullOrEmpty(sort) ? DiscordLeaderboardBuilder.DefaultGuildSort : DiscordLeaderboardBuilder.NormalizeGuildSort(sort);
            else if (board == "rep")
                key = Leaderboards.LeaderboardCatalog.Reputation;
            else
                key = string.IsNullOrEmpty(sort) ? DiscordLeaderboardBuilder.DefaultSort : DiscordLeaderboardBuilder.NormalizeSort(sort);

            // The image where this process holds the gateway; the coordinator carries no files, so it gets the rows.
            Leaderboards.LeaderboardEngine.Inputs inputs = Leaderboards.LeaderboardEngine.Live();
            Leaderboards.LeaderboardBoardDto b = Leaderboards.LeaderboardEngine.Build(Leaderboards.LeaderboardCatalog.Find(key), inputs);
            return DiscordLeaderboardPoster.CommandResult(b, cfg, inputs, files: !DiscordCoordinatorLink.Active)
                ?? KmhDiscordCommandResult.Error("Nobody is on that board yet - a zero ranks nobody.");
        }

        private static KmhDiscordCommandResult DoRank(KmhDiscordCommandRequest req)
        {
            string query = req.Option("player", "").Trim();
            if (query.Length == 0)
            {
                query = LinkedAccountsStore.FindUsernameByDiscordId(req.UserId);   // id-only; no impersonable display fallback
                if (string.IsNullOrEmpty(query))
                    return KmhDiscordCommandResult.Error("Give a player name, or link your account (`!kmh-link`) to use it bare.");
            }

            KmhEmbedSpec card = DiscordLeaderboardBuilder.BuildPlayerCard(query, out List<string> matches);
            if (card == null)
                return KmhDiscordCommandResult.Error(matches != null && matches.Count > 0
                    ? $"Multiple players match \"{query}\": {string.Join(", ", matches)}"
                    : $"No player named \"{query}\" has been seen on this server.");
            return KmhDiscordCommandResult.Embed(card);
        }

        // Today's live boards update now (and are posted if missing), rather than a second copy appearing beside them.
        private static Task<KmhDiscordCommandResult> DoLeaderboardPost(DiscordConfig cfg)
        {
            ulong ch = cfg.LeaderboardChannelId;
            if (ch == 0) return Task.FromResult(KmhDiscordCommandResult.Error("No leaderboard channel set (Channels.Leaderboard)."));
            if (cfg.LiveLeaderboardBoards.Length == 0 || cfg.LeaderboardIntervalMinutes < 1)
                return Task.FromResult(KmhDiscordCommandResult.Error("The live leaderboard is off (Live.LeaderboardBoards / Live.LeaderboardEveryMinutes)."));
            Leaderboards.LeaderboardEngine.Inputs inputs = Leaderboards.LeaderboardEngine.Live();
            Leaderboards.LeaderboardBoardDto b = Leaderboards.LeaderboardEngine.Build(
                Leaderboards.LeaderboardCatalog.Find(DiscordLeaderboardBuilder.PostedSort(inputs)), inputs);
            if (b == null || b.Rows.Count == 0) return Task.FromResult(KmhDiscordCommandResult.Error("No leaderboard data yet."));
            DiscordLive.RefreshBoardsNow();
            return Task.FromResult(KmhDiscordCommandResult.Text("Updating today's leaderboard now.", ephemeral: true));
        }

        private static KmhDiscordCommandResult DoMarket(DiscordConfig cfg, KmhDiscordCommandRequest req, bool search)
        {
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot("");
            List<MarketplaceListing> rows = snap?.Listings ?? new List<MarketplaceListing>();

            string title = "Marketplace";
            if (search)
            {
                string q = req.Option("query", "").Trim();
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

            KmhEmbedSpecBuilder eb = Brand(cfg, title, null);
            if (rows.Count == 0)
            {
                eb.WithDescription(search ? "_No listings match._" : "_No open listings right now._");
                return KmhDiscordCommandResult.Embed(eb.Build());
            }

            const int pageSize = 8;
            int totalPages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
            int page = (int)Clamp(req.OptionInt("page", 1), 1, totalPages);
            int skip = (page - 1) * pageSize;
            int end  = Math.Min(skip + pageSize, rows.Count);
            for (int i = skip; i < end; i++)
            {
                MarketplaceListing l = rows[i];
                eb.AddField($"#{l.Id} · {DiscordText.Escape(ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex))}",
                    $"**{l.RemainingQty}**× @ `{SilverFmt.Format(l.UnitPriceSilver)}/ea` · by **{DiscordText.SafeName(l.SellerUsername)}**",
                    inline: false);
            }
            eb.WithFooter(totalPages > 1 ? $"Page {page}/{totalPages} · {rows.Count} listings" : $"{rows.Count} listing(s)");
            return KmhDiscordCommandResult.Embed(eb.Build());
        }

        private static KmhDiscordCommandResult DoSites(DiscordConfig cfg, int tile)
        {
            List<SiteEntry> sites = SiteStore.AllForApi();

            if (tile >= 0)
            {
                SiteEntry s = sites.FirstOrDefault(x => x.Tile == tile);
                if (s == null) return KmhDiscordCommandResult.Error($"No site at tile {tile}.");
                KmhEmbedSpecBuilder eb = Brand(cfg, $"Site · tile {s.Tile}", null)
                    .AddField("Owner",   DiscordText.SafeName(SiteOwnership.ControllerLabel(s)), true)
                    .AddField("Produces", $"{s.BaseAmountPerCycle}× {DiscordText.Escape(ItemLabelCache.LabelFor(s.ItemDefName))}", true)
                    .AddField("Access",  AccessLabel(s.AccessMode), true)
                    .AddField("Workers", $"{s.Workers?.Count ?? 0}/{s.MaxWorkers}", true);
                return KmhDiscordCommandResult.Embed(eb.Build());
            }

            KmhEmbedSpecBuilder list = Brand(cfg, $"Sites ({sites.Count})", null);
            if (sites.Count == 0)
            {
                list.WithDescription("_No custom sites built yet._");
                return KmhDiscordCommandResult.Embed(list.Build());
            }
            foreach (SiteEntry s in sites.Take(15))
            {
                string owner = DiscordText.SafeName(SiteOwnership.ControllerLabel(s));
                list.AddField($"Tile {s.Tile} · {DiscordText.Escape(ItemLabelCache.LabelFor(s.ItemDefName))}",
                    $"{s.BaseAmountPerCycle}×/cycle · {AccessLabel(s.AccessMode)} · by **{owner}** · workers {s.Workers?.Count ?? 0}/{s.MaxWorkers}",
                    inline: false);
            }
            if (sites.Count > 15) list.WithFooter($"Showing 15 of {sites.Count}");
            return KmhDiscordCommandResult.Embed(list.Build());
        }

        // Roads are world infrastructure, so this shows the network and never anyone's projects or reserved silver.
        private static KmhDiscordCommandResult DoRoads(DiscordConfig cfg)
        {
            if (!Features.Sites.SitesConfig.Current.AllowRoadworks)
                return KmhDiscordCommandResult.Error("Roadworks is turned off on this server.");

            List<Features.Roadworks.Dto.RoadSegment> segments = Features.Roadworks.RoadworksStore.AllSegments();
            KmhEmbedSpecBuilder e = Brand(cfg, $"Roadworks ({segments.Count} segment(s))", null);
            if (segments.Count == 0)
            {
                e.WithDescription("_No roads built yet._");
                return KmhDiscordCommandResult.Embed(e.Build());
            }

            var byTier  = new Dictionary<string, int>();
            var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Features.Roadworks.Dto.RoadSegment s in segments)
            {
                string tier = Features.Roadworks.RoadTiers.Normalize(s?.Tier);
                byTier[tier] = byTier.TryGetValue(tier, out int n) ? n + 1 : 1;
                string o = s?.OwnerUsername ?? "";   // RoadSegment builder, not a Site controller
                if (o.Length > 0) byOwner[o] = byOwner.TryGetValue(o, out int m) ? m + 1 : 1;
            }

            StringBuilder tiers = new StringBuilder();
            foreach (string tier in Features.Roadworks.RoadTiers.All)
                tiers.AppendLine($"**{Features.Roadworks.RoadTiers.DisplayName(tier)}** · {(byTier.TryGetValue(tier, out int c) ? c : 0)} segment(s)");
            e.AddField("By tier", tiers.ToString(), inline: false);

            StringBuilder builders = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in byOwner.OrderByDescending(k => k.Value).Take(10))
                builders.AppendLine($"{DiscordText.SafeName(kv.Key)} · {kv.Value}");
            if (builders.Length > 0) e.AddField("Top builders", builders.ToString(), inline: false);

            return KmhDiscordCommandResult.Embed(e.Build());
        }

        private static KmhDiscordCommandResult DoAnnounce(DiscordConfig cfg, KmhDiscordCommandRequest req, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return KmhDiscordCommandResult.Error("Provide a message.");
            ulong ch = cfg.AnnouncementsChannelId;
            if (ch == 0) return KmhDiscordCommandResult.Error("No announcements channel set (Channels.Announcements).");

            // A moderator's markdown is kept; no mention in it pings, as none did in the embed this replaced.
            DiscordBridge.PostToChannel(ch, $"📢 **Announcement** · {DiscordText.SafeName(KmhEmbedBuilder.ServerName(cfg))}\n{message.Trim()}");
            ServerLog.Info($"Discord: announcement by {req.UserName}: {message}");
            return KmhDiscordCommandResult.Text("Announcement posted.", ephemeral: true);
        }

        // Deferred, because in direct mode the reload tears down the very client that must still deliver this reply.
        private static KmhDiscordCommandResult DoDiscordReload()
        {
            if (!DiscordBridge.TryClaimReload(out TimeSpan wait))
                return KmhDiscordCommandResult.Text($"The Discord bridge was just reloaded - try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))}s.", ephemeral: true);
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                DiscordBridge.Reload();
            });
            return KmhDiscordCommandResult.Text("Reloading the Discord bridge...", ephemeral: true);
        }

        private static KmhDiscordCommandResult DoConfigReload()
        {
            Features.Economy.EconomyConfig.Reload();
            Features.Sites.SitesConfig.Reload();
            return KmhDiscordCommandResult.Text("Reloaded Economy + Sites config.", ephemeral: true);
        }

        private static KmhDiscordCommandResult DoConsoleRun(DiscordConfig cfg, KmhDiscordCommandRequest req, string command)
        {
            KmhDiscordCommandResult denied = RequireConsole(req, cfg);
            if (denied != null) return denied;

            string output;
            try { output = ConsoleExecutor.Run(command); }
            catch (Exception ex) { output = $"(command threw) {ex.Message}"; }
            output = DiscordBridge.Redact(output);
            ServerLog.Info($"Discord: console-run by {req.UserName}: {command}");

            KmhDiscordCommandResult result = KmhDiscordCommandResult.Text("", ephemeral: true);
            result.Ephemeral = true;
            List<string> chunks = SplitForBlocks(string.IsNullOrWhiteSpace(output) ? "(no output)" : output.TrimEnd(), 1900);
            for (int i = 0; i < chunks.Count && i < KmhDiscordProtocol.MaxFollowups; i++)
            {
                string c = chunks[i];
                if (i == KmhDiscordProtocol.MaxFollowups - 1 && chunks.Count > KmhDiscordProtocol.MaxFollowups)
                    c += "\n…(truncated)";
                if (i == 0) result.Content = $"```\n{c}\n```";
                else result.Followups.Add(new KmhDiscordFollowup { Content = $"```\n{c}\n```", Ephemeral = true });
            }
            return result;
        }

        private static KmhDiscordCommandResult DoHelp(DiscordConfig cfg)
        {
            KmhEmbedSpecBuilder eb = Brand(cfg, "Colony Nexus Discord commands", null)
                .AddField("Anyone",
                    "`/kmh status` · `/kmh players` · `/kmh servers` · `/kmh leaderboard show` · `/kmh market list|search` · `/kmh site list|info|roads` · `/kmh help`", false)
                .AddField("Moderators",
                    "`/kmh leaderboard post|refresh` · `/kmh announcement send` · `/kmh discord reload` · `/kmh config reload`", false)
                .AddField("Owners",
                    "`/kmh console run <command>` _(gated by the Console block in DiscordConfig.json)_", false)
                .WithFooter("Account linking still uses /kmh link in-game + !kmh-link here.");
            return KmhDiscordCommandResult.Embed(eb.Build(), ephemeral: true);
        }

        // Returns null when the caller passes; the refusal itself is the result otherwise.
        private static KmhDiscordCommandResult RequireMod(KmhDiscordCommandRequest req, DiscordConfig cfg)
            => IsMod(req, cfg) ? null : KmhDiscordCommandResult.Error("That command needs a moderator or admin role.");

        // Same rule as the text commands: with Bot.GuildId set, no other Discord server is served at all.
        internal static bool FromAllowedGuild(KmhDiscordCommandRequest req, DiscordConfig cfg)
        {
            if (req.IsDirectMessage) return true;
            ulong[] allowed = cfg.AllowedGuildIds;
            if (allowed == null || allowed.Length == 0) return true;
            foreach (ulong g in allowed) if (g == req.GuildId) return true;
            return false;
        }

        // Administrator and roles mean something only on this server's own Discord, which its channels prove.
        internal static bool InOwnServer(KmhDiscordCommandRequest req, DiscordConfig cfg)
            => !req.IsDirectMessage && FromAllowedGuild(req, cfg) && cfg.IsOwnChannel(req.ChannelId);

        internal static bool IsMod(KmhDiscordCommandRequest req, DiscordConfig cfg)
        {
            if (!InOwnServer(req, cfg)) return false;
            if (req.IsGuildAdministrator) return true;
            return HasAnyRole(cfg.Roles?.Moderators, req);
        }

        // Never Console.Allow's switch, either way; the Admin channel is required since a ban names the player and IP.
        private static KmhDiscordCommandResult RequireModeration(KmhDiscordCommandRequest req, DiscordConfig cfg)
        {
            if (req.IsDirectMessage)
                return KmhDiscordCommandResult.Error("Moderation commands only work in the server's Admin channel.");

            ulong wantChannel = cfg.AdminChannelId;
            if (wantChannel == 0)
                return KmhDiscordCommandResult.Error(
                    "Set Channels.Admin in DiscordConfig.json first - moderation replies name players and addresses.");
            if (req.ChannelId != wantChannel || !InOwnServer(req, cfg))
                return KmhDiscordCommandResult.Error("Moderation commands must be used in the Admin channel.");

            if (req.IsGuildAdministrator) return null;
            if (HasAnyRole(cfg.Roles?.Moderators, req)) return null;
            if (HasAnyRole(cfg.Roles?.ConsoleAccess, req)) return null;
            return KmhDiscordCommandResult.Error("That command needs a moderator role.");
        }

        internal static KmhDiscordCommandResult RequireConsole(KmhDiscordCommandRequest req, DiscordConfig cfg)
        {
            if (!cfg.AllowConsoleCommands)
                return KmhDiscordCommandResult.Error("Console commands are disabled (Console.Allow = false).");

            ulong wantChannel = cfg.ConsoleCommandsChannelId;
            if (wantChannel == 0)
                return KmhDiscordCommandResult.Error("Set Channels.Admin in DiscordConfig.json first - console commands run there only.");
            if (req.ChannelId != wantChannel || !InOwnServer(req, cfg))
                return KmhDiscordCommandResult.Error("Console commands must be used in the Admin channel.");

            if (cfg.RequireConsoleRole && !HasAnyRole(cfg.Roles?.ConsoleAccess, req))
                return KmhDiscordCommandResult.Error("You need a console-access role for that.");

            return null;
        }

        // Ids only: anyone with Manage Roles can create a role with any name.
        internal static bool HasAnyRole(string[] cfgRoles, KmhDiscordCommandRequest req)
        {
            if (cfgRoles == null || req?.RoleIds == null) return false;
            foreach (string s in cfgRoles)
            {
                ulong want = DiscordConfig.ParseId(s);
                // @everyone's id is the guild's, so that id pasted into a role list would admit every member.
                if (want == 0 || want == req.GuildId) continue;
                foreach (string id in req.RoleIds)
                    if (DiscordConfig.ParseId(id) == want) return true;
            }
            return false;
        }

        private static KmhEmbedSpecBuilder Brand(DiscordConfig cfg, string title, string description)
        {
            KmhEmbedSpecBuilder eb = new KmhEmbedSpecBuilder().WithTitle(title).WithColor(KmhEmbedBuilder.BrandColor(cfg));
            if (!string.IsNullOrEmpty(description)) eb.WithDescription(description);
            return eb;
        }

        private static string AccessLabel(string mode)
            => mode == SiteEntry.AccessPublic ? "Public"
             : mode == SiteEntry.AccessPrivate ? "Private"
             : "Guild + allies";

        // Clamped to Discord's 2000-character message ceiling.
        private static KmhDiscordCommandResult Block(string text, bool ephemeral)
        {
            string body = string.IsNullOrWhiteSpace(text) ? "(no output)" : text.TrimEnd();
            if (body.Length > KmhDiscordProtocol.MaxContentChars)
                body = body.Substring(0, KmhDiscordProtocol.MaxContentChars) + "\n…(truncated)";
            return KmhDiscordCommandResult.Text($"```\n{body}\n```", ephemeral);
        }

        // Breaks on newlines where it can, since a single over-long line still has to be hard-split.
        internal static List<string> SplitForBlocks(string text, int cap)
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

        private static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
