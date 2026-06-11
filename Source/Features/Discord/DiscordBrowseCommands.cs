using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KMHServerAddon.Features.ItemLabels;
using KMHServerAddon.Util;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Marketplace.Dto;
using KMHServerAddon.Features.Quests;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Features.Treasury.Dto;

namespace KMHServerAddon.Features.Discord
{
    // Read-only browse commands for the Discord bridge - the surfaces that don't need cross-feature mutations:
    //
    // !kmh-market [page|mine|<query>] - paginated open listings. !kmh-quests [page] - paginated open quests.
    // !kmh-treasury - caller's treasury (needs link).
    //
    // Mutating commands (!buy, !sell, !cancel, !showcase, !wtb, button-driven Buy) live in DiscordTradeCommands.cs
    // - they need an ItemLabelCache for friendly-name resolution and a Discord-snowflake store on
    // LinkedAccountsStore.
    //
    // All commands are visibility-aware: unlinked callers see only public listings/quests; linked callers also see
    // guild-only items posted by their guildmates or allies.
    internal static class DiscordBrowseCommands
    {
        private const int PageSize = 8;

        public static async Task<bool> TryHandleAsync(SocketMessage raw, string cmd, string[] parts)
        {
            switch (cmd)
            {
                case "kmh-market":
                case "kmh-shop":
                case "kmh-listings":
                    await HandleMarket(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-quests":
                case "kmh-questboard":
                    await HandleQuests(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-treasury":
                case "kmh-vault":
                case "kmh-wallet":
                    await HandleTreasury(raw).ConfigureAwait(false);
                    return true;
                case "kmh-find":
                case "kmh-search":
                    await HandleFind(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-compare":
                case "kmh-price":
                case "kmh-prices":
                    await HandleCompare(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-history":
                case "kmh-txn":
                case "kmh-log":
                    await HandleHistory(raw, parts).ConfigureAwait(false);
                    return true;
                case "kmh-items":
                case "kmh-catalog":
                    await HandleCatalog(raw, parts).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }

        // -- !kmh-market --

        private static async Task HandleMarket(SocketMessage raw, string[] parts)
        {
            string callerUser = ResolveLinkedUsername(raw);
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(callerUser ?? "");
            List<MarketplaceListing> rows = snap?.Listings ?? new List<MarketplaceListing>();

            int  page    = 1;
            bool myOnly  = false;
            if (parts.Length >= 2)
            {
                if (string.Equals(parts[1], "mine", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(callerUser))
                    {
                        await raw.Channel.SendMessageAsync(
                            "Link your Discord first (`!kmh-link <code>` after running `/kmh link` in-game) " +
                            "to filter by your own listings.")
                            .ConfigureAwait(false);
                        return;
                    }
                    myOnly = true;
                    List<MarketplaceListing> mine = new List<MarketplaceListing>();
                    foreach (MarketplaceListing l in rows)
                    {
                        if (string.Equals(l.SellerUsername, callerUser, StringComparison.OrdinalIgnoreCase))
                            mine.Add(l);
                    }
                    rows = mine;
                }
                else if (int.TryParse(parts[1], out int p) && p > 0)
                {
                    page = p;
                }
            }

            rows.Sort((a, b) =>
            {
                int byItem = string.Compare(a.ItemDefName, b.ItemDefName, StringComparison.OrdinalIgnoreCase);
                return byItem != 0 ? byItem : a.UnitPriceSilver.CompareTo(b.UnitPriceSilver);
            });

            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle(myOnly ? "Your listings" : "Marketplace")
                .WithColor(new Color(0xFF, 0xC4, 0x61));

            if (rows.Count == 0)
            {
                eb.WithDescription(myOnly
                    ? "_You have no open listings._"
                    : "_No open listings on the server right now._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            int totalPages = Math.Max(1, (rows.Count + PageSize - 1) / PageSize);
            if (page > totalPages) page = totalPages;
            int skip = (page - 1) * PageSize;

            int  end   = Math.Min(skip + PageSize, rows.Count);
            int  shown = end - skip;
            for (int i = skip; i < end; i++)
            {
                MarketplaceListing l = rows[i];
                string expiry = FormatExpiry(l.ExpiresUtcTicks);
                string label  = ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex);
                string emoji  = DiscordItemIconMap.EmojiFor(l.ItemDefName);
                eb.AddField(
                    $"{emoji} #{l.Id} · {label}",
                    $"**{l.RemainingQty}**× @ `{SilverFmt.Format(l.UnitPriceSilver)}/ea` · total `{SilverFmt.Format((long)l.UnitPriceSilver * l.RemainingQty)}`\n" +
                    $"by **{l.SellerUsername}** · {expiry}",
                    inline: false);
            }

            string footer = totalPages > 1
                ? $"Showing {shown} of {rows.Count} listings · page {page}/{totalPages} (try `!kmh-market {Math.Min(totalPages, page + 1)}`)"
                : $"Showing {shown} listing{(shown == 1 ? "" : "s")}";
            eb.WithFooter(footer);
            eb.WithDescription("_`!kmh-market mine` to filter to your own listings._");

            await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        // -- !kmh-quests --

        private static async Task HandleQuests(SocketMessage raw, string[] parts)
        {
            string callerUser = ResolveLinkedUsername(raw);
            QuestSnapshot snap = QuestStore.BuildSnapshot(callerUser ?? "");
            List<QuestEntry> rows = snap?.Quests ?? new List<QuestEntry>();

            // Default view = currently open quests (claimable). Players wanting to see in-flight quests should ask
            // in-game
            List<QuestEntry> open = new List<QuestEntry>();
            foreach (QuestEntry q in rows)
            {
                if (q.State == QuestEntry.StateOpen) open.Add(q);
            }

            int page = 1;
            if (parts.Length >= 2 && int.TryParse(parts[1], out int p) && p > 0) page = p;

            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle("Quest board")
                .WithColor(new Color(0x8C, 0xDC, 0x8C));

            if (open.Count == 0)
            {
                eb.WithDescription("_No open quests on the board right now._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            // Newest first - keeps fresh posts above stale ones.
            open.Sort((a, b) => b.PostedUtcTicks.CompareTo(a.PostedUtcTicks));

            int totalPages = Math.Max(1, (open.Count + PageSize - 1) / PageSize);
            if (page > totalPages) page = totalPages;
            int skip = (page - 1) * PageSize;
            int end  = Math.Min(skip + PageSize, open.Count);

            for (int i = skip; i < end; i++)
            {
                QuestEntry q = open[i];
                string kindLabel = q.Kind == QuestEntry.KindBounty ? "Bounty" : "Deliver";
                string title     = string.IsNullOrEmpty(q.Title) ? "(no title)" : q.Title;
                string body      = $"**{kindLabel}** · bounty `{SilverFmt.Format(q.BountySilver)}` · by **{q.PosterUsername}**";
                if (q.Kind == QuestEntry.KindDeliverItem && !string.IsNullOrEmpty(q.TargetItemDefName))
                {
                    body += $"\nTarget: **{q.TargetItemQty}**× {ItemLabelCache.LabelFor(q.TargetItemDefName)}";
                }
                if (!string.IsNullOrEmpty(q.Description))
                {
                    string desc = q.Description.Length > 200
                        ? q.Description.Substring(0, 197) + "…"
                        : q.Description;
                    body += $"\n{desc}";
                }
                body += $"\n_{FormatExpiry(q.ExpiresUtcTicks)}_";
                eb.AddField($"#{q.Id} · {title}", body, inline: false);
            }

            int shown = end - skip;
            string footer = totalPages > 1
                ? $"Showing {shown} of {open.Count} open quests · page {page}/{totalPages} (try `!kmh-quests {Math.Min(totalPages, page + 1)}`)"
                : $"Showing {shown} open quest{(shown == 1 ? "" : "s")}";
            eb.WithFooter(footer);

            await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        // -- !kmh-treasury --

        private static async Task HandleTreasury(SocketMessage raw)
        {
            string callerUser = ResolveLinkedUsername(raw);
            if (string.IsNullOrEmpty(callerUser))
            {
                await raw.Channel.SendMessageAsync(
                    "Link your Discord first (`!kmh-link <code>` after running `/kmh link` in-game) " +
                    "to view your treasury.")
                    .ConfigureAwait(false);
                return;
            }

            TreasurySnapshot t = TreasuryStore.GetSnapshotFor(callerUser);
            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle($"Treasury · {callerUser}")
                .WithColor(new Color(0xBA, 0x84, 0xF6));

            if (t == null)
            {
                eb.WithDescription("_No treasury found for this account._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            string ownerLabel = t.IsGuildOwned ? $"guild ({t.OwnerKey})" : "personal vault";
            eb.WithDescription(
                $"Owner: **{ownerLabel}**\n" +
                $"Silver: **{t.SilverBalance:N0}s** _(lifetime in: {t.LifetimeSilverIn:N0}, out: {t.LifetimeSilverOut:N0})_");

            if (t.Items != null && t.Items.Count > 0)
            {
                // Compact items dump - at most 20 lines, sorted by qty desc.
                List<KeyValuePair<string, int>> items = new List<KeyValuePair<string, int>>(t.Items);
                items.Sort((a, b) => b.Value.CompareTo(a.Value));
                int max = Math.Min(20, items.Count);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("```\n");
                for (int i = 0; i < max; i++)
                {
                    sb.Append(items[i].Value.ToString().PadLeft(6))
                      .Append("× ")
                      .Append(ItemLabelCache.LabelFor(items[i].Key))
                      .Append('\n');
                }
                if (items.Count > max) sb.Append("… and ").Append(items.Count - max).Append(" more\n");
                sb.Append("```");
                eb.AddField("Items", sb.ToString(), inline: false);
            }
            else
            {
                eb.AddField("Items", "_None._", inline: false);
            }

            await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        // -- !kmh-find --

        // Filter listings by case-insensitive substring match on label OR defName. Reuses the same paged-embed
        // shape as !kmh-market so the output reads familiarly. Public - unlinked callers see only
        // public listings via BuildSnapshot("").
        private static async Task HandleFind(SocketMessage raw, string[] parts)
        {
            if (parts.Length < 2)
            {
                await raw.Channel.SendMessageAsync(
                    "Usage: `!kmh-find <query>` - search open listings by item name or defName.")
                    .ConfigureAwait(false);
                return;
            }
            string query = JoinFrom(parts, 1).Trim();
            if (string.IsNullOrEmpty(query)) return;

            string callerUser = ResolveLinkedUsername(raw);
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(callerUser ?? "");
            List<MarketplaceListing> hits = new List<MarketplaceListing>();
            if (snap?.Listings != null)
            {
                foreach (MarketplaceListing l in snap.Listings)
                {
                    string label = ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex);
                    bool matchLabel  = label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchDef    = l.ItemDefName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (matchLabel || matchDef) hits.Add(l);
                }
            }

            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle($"Search: {query}")
                .WithColor(new Color(0xFF, 0xC4, 0x61));

            if (hits.Count == 0)
            {
                eb.WithDescription($"_No open listings match `{query}`._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            hits.Sort((a, b) => a.UnitPriceSilver.CompareTo(b.UnitPriceSilver));
            int max = Math.Min(PageSize, hits.Count);
            for (int i = 0; i < max; i++)
            {
                MarketplaceListing l = hits[i];
                eb.AddField(
                    $"{DiscordItemIconMap.EmojiFor(l.ItemDefName)} #{l.Id} · {ItemLabelCache.LabelFor(l.ItemDefName, l.StuffDefName, l.QualityIndex)}",
                    $"**{l.RemainingQty}**× @ `{SilverFmt.Format(l.UnitPriceSilver)}/ea` · by **{l.SellerUsername}** · {FormatExpiry(l.ExpiresUtcTicks)}",
                    inline: false);
            }
            eb.WithFooter(hits.Count > max
                ? $"Showing {max} cheapest of {hits.Count} matches - narrow your query for more"
                : $"Showing {max} match{(max == 1 ? "" : "es")}");
            await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        // -- !kmh-compare --

        // Top-5 cheapest listings for a single item + min/max/avg stats. Resolves the friendly name through
        // ItemLabelCache the same way !kmh-sell does, including the "ambiguous → candidate list" reply shape so
        // usage is consistent
        private static async Task HandleCompare(SocketMessage raw, string[] parts)
        {
            if (parts.Length < 2)
            {
                await raw.Channel.SendMessageAsync(
                    "Usage: `!kmh-compare <item>` - price summary + 5 cheapest active listings.\n" +
                    "Example: `!kmh-compare plasteel`")
                    .ConfigureAwait(false);
                return;
            }
            string query = JoinFrom(parts, 1).Trim();
            if (string.IsNullOrEmpty(query)) return;

            string defName = ItemLabelCache.ResolveDefNameByQuery(query, out List<string> candidates);
            if (defName == null)
            {
                if (candidates != null && candidates.Count > 1)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.Append("`").Append(query).Append("` matches multiple items - be more specific:\n");
                    foreach (string c in candidates)
                    {
                        sb.Append("• **").Append(ItemLabelCache.LabelFor(c))
                          .Append("** _(").Append(c).Append(")_\n");
                    }
                    await raw.Channel.SendMessageAsync(sb.ToString()).ConfigureAwait(false);
                    return;
                }
                defName = query.Replace(' ', '_');
            }

            string callerUser = ResolveLinkedUsername(raw);
            MarketplaceSnapshot snap = MarketplaceStore.BuildSnapshot(callerUser ?? "");
            List<MarketplaceListing> matches = new List<MarketplaceListing>();
            if (snap?.Listings != null)
            {
                foreach (MarketplaceListing l in snap.Listings)
                {
                    if (string.Equals(l.ItemDefName, defName, StringComparison.OrdinalIgnoreCase))
                        matches.Add(l);
                }
            }

            string label = ItemLabelCache.LabelFor(defName);
            string emoji = DiscordItemIconMap.EmojiFor(defName);
            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle($"{emoji} Price compare · {label}")
                .WithColor(new Color(0xFF, 0xC4, 0x61));

            if (matches.Count == 0)
            {
                eb.WithDescription($"_No active listings for **{label}**._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            matches.Sort((a, b) => a.UnitPriceSilver.CompareTo(b.UnitPriceSilver));
            int   min = matches[0].UnitPriceSilver;
            int   max = matches[matches.Count - 1].UnitPriceSilver;
            long  sum = 0;
            foreach (MarketplaceListing l in matches) sum += l.UnitPriceSilver;
            int   avg = (int)(sum / matches.Count);
            eb.WithDescription(
                $"**{matches.Count}** active listing(s) · cheapest **{min}s** · highest **{max}s** · avg **{avg}s**");

            int show = Math.Min(5, matches.Count);
            for (int i = 0; i < show; i++)
            {
                MarketplaceListing l = matches[i];
                eb.AddField(
                    $"#{l.Id} · `{SilverFmt.Format(l.UnitPriceSilver)}/ea",
                    $"**{l.RemainingQty}**× · total `{(long)l.UnitPriceSilver * l.RemainingQty}s` · by **{l.SellerUsername}** · `!kmh-buy {l.Id}`",
                    inline: false);
            }

            // Attach Buy buttons to the cheapest listing only - Discord's 25-button cap + ambiguous attribution
            // rules out per-listing button rows. The text receipts still list `!kmh-buy <id>`
            // for the other 4 so they're never strictly worse.
            MessageComponent components = DiscordBuyButton.BuildBuyComponents(matches[0]);
            await raw.Channel.SendMessageAsync(embed: eb.Build(), components: components).ConfigureAwait(false);
        }

        // -- !kmh-history --

        // Last N treasury transactions for the linked user. Default 10, capped at 25 by Discord embed-field limits.
        // Most-recent first
        private static async Task HandleHistory(SocketMessage raw, string[] parts)
        {
            string callerUser = ResolveLinkedUsername(raw);
            if (string.IsNullOrEmpty(callerUser))
            {
                await raw.Channel.SendMessageAsync(
                    "Link your Discord first to view your transaction history.")
                    .ConfigureAwait(false);
                return;
            }

            int count = 10;
            if (parts.Length >= 2 && int.TryParse(parts[1], out int n) && n > 0) count = Math.Min(25, n);

            TreasurySnapshot t = TreasuryStore.GetSnapshotFor(callerUser);
            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle($"Recent transactions · {callerUser}")
                .WithColor(new Color(0xBA, 0x84, 0xF6));

            if (t == null || t.RecentTransactions == null || t.RecentTransactions.Count == 0)
            {
                eb.WithDescription("_No transactions yet._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            int total = t.RecentTransactions.Count;
            int take  = Math.Min(count, total);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = total - 1; i >= total - take; i--)
            {
                TreasuryTransaction tx = t.RecentTransactions[i];
                string when = TryFormatRelative(tx.UtcTicks);
                string what = string.IsNullOrEmpty(tx.ItemDefName)
                    ? $"`{tx.Amount}s`"
                    : $"`{tx.Amount}× {ItemLabelCache.LabelFor(tx.ItemDefName)}`";
                string by = string.IsNullOrEmpty(tx.Username) ? "-" : tx.Username;
                sb.Append(when).Append(" · **").Append(tx.Kind).Append("** · ").Append(what);
                if (!string.Equals(by, callerUser, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" · _by ").Append(by).Append('_');
                if (!string.IsNullOrEmpty(tx.Note))
                    sb.Append(" · `").Append(tx.Note).Append('`');
                sb.Append('\n');
            }
            eb.WithDescription(sb.ToString());
            if (total > take)
                eb.WithFooter($"{total - take} older entries truncated");

            await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        // -- !kmh-items / !kmh-catalog --

        // First-N entries from ItemLabelCache, alphabetical by label. Useful as a discovery tool - "what does the
        // server know about?" Cap at 25 entries (Discord embed limit on plain description length is large but
        // readability is the actual constraint)
        private static async Task HandleCatalog(SocketMessage raw, string[] parts)
        {
            int count = ItemLabelCache.Count;
            EmbedBuilder eb = new EmbedBuilder()
                .WithTitle($"Item catalog · {count} known defNames")
                .WithColor(new Color(0xBA, 0x84, 0xF6));

            if (count == 0)
            {
                eb.WithDescription(
                    "_No labels cached yet - at least one patch-mod client must connect first " +
                    "to push the local DefDatabase catalog._");
                await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
                return;
            }

            // Build a sample listing. The cache itself is the canonical source - we materialize via
            // ResolveDefNameByQuery's exposed shape: enumerate via Apply with an empty merge, no - actually we
            // don't have a public iterator. Add a quick one
            string query = parts.Length >= 2 ? JoinFrom(parts, 1).Trim() : "";
            List<KeyValuePair<string, string>> sample = ItemLabelCache.Sample(query, 25);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("```\n");
            foreach (KeyValuePair<string, string> kv in sample)
            {
                sb.Append(kv.Key.PadRight(28)).Append("  ").Append(kv.Value).Append('\n');
            }
            sb.Append("```");
            eb.WithDescription(sb.ToString());
            if (!string.IsNullOrEmpty(query))
                eb.WithFooter($"Showing {sample.Count} matches for `{query}`");
            else
                eb.WithFooter($"Showing first {sample.Count} alphabetically - `!kmh-items <query>` to filter");

            await raw.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }

        // -- helpers --

        private static string JoinFrom(string[] parts, int startIndex)
        {
            if (parts == null || startIndex >= parts.Length) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = startIndex; i < parts.Length; i++)
            {
                if (i > startIndex) sb.Append(' ');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        // Format a UTC-ticks timestamp as a Discord relative timestamp (<t:UNIX:R> → "5 minutes ago"). Falls back
        // to a literal "(?)" on parse failure - receipts shouldn't crash because of a bad ticks
        private static string TryFormatRelative(long utcTicks)
        {
            if (utcTicks <= 0) return "(?)";
            try
            {
                DateTime utc = new DateTime(utcTicks, DateTimeKind.Utc);
                long unix    = ((DateTimeOffset)utc).ToUnixTimeSeconds();
                return $"<t:{unix}:R>";
            }
            catch { return "(?)"; }
        }

        // Returns the in-game username linked to the Discord author, or null
        // if not linked. Prefers snowflake-id lookup so the link survives a
        // Discord display-name rename; falls back to display lookup for any legacy links from before Id storage
        // landed
        private static string ResolveLinkedUsername(SocketMessage raw)
        {
            if (raw?.Author == null) return null;
            ulong  id      = raw.Author.Id;
            string display = ResolveDiscordDisplay(raw.Author);
            string byId    = LinkedAccountsStore.FindUsernameByDiscordId(id);
            if (!string.IsNullOrEmpty(byId)) return byId;
            return string.IsNullOrEmpty(display) ? null : LinkedAccountsStore.FindUsernameByDiscord(display);
        }

        private static string ResolveDiscordDisplay(IUser user)
        {
            string g = user?.GlobalName;
            if (!string.IsNullOrEmpty(g)) return g;
            string u = user?.Username;
            string d = user?.Discriminator;
            if (!string.IsNullOrEmpty(d) && d != "0" && d != "0000") return $"{u}#{d}";
            return u ?? "";
        }

        // Renders an ExpiresUtcTicks value as a Discord relative timestamp (`<t:UNIX:R>` → "in 3 hours"). Treats 0
        // as "never expires"
        private static string FormatExpiry(long utcTicks)
        {
            if (utcTicks <= 0) return "never expires";
            try
            {
                DateTime utc = new DateTime(utcTicks, DateTimeKind.Utc);
                long unix    = ((DateTimeOffset)utc).ToUnixTimeSeconds();
                return $"expires <t:{unix}:R>";
            }
            catch { return "expires (unknown)"; }
        }
    }
}
