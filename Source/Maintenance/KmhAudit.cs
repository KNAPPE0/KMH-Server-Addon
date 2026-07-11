using System;
using System.Collections.Generic;

namespace KMHServerAddon.Maintenance
{
    // Read-only, advisory anti-cheat scan for the ban-enforced season. Surfaces economy outliers + strong
    // modified-client signals for an admin to eyeball - it never bans or mutates. Deeper detail: kmh ledger / inspect.
    internal static class KmhAudit
    {
        public static void Run(Action<string> reply)
        {
            reply("=== KMH audit (advisory - review, don't auto-act) ===");

            // Active economy / treasury access policy.
            reply($"Economy policy: {Features.Economy.EconomyAccess.Describe()}");

            // Guild Hall policy + coverage (only interesting when a hall rule is enabled).
            Features.Economy.EconomyConfig ecfg = Features.Economy.EconomyConfig.Current;
            if (ecfg.AnyGuildHallRule)
            {
                reply($"Guild Hall rules ENABLED (createReq={ecfg.RequireGuildHallToCreateGuild}, treasuryReq={ecfg.RequireGuildHallForGuildTreasury}, " +
                      $"contribReq={ecfg.RequireGuildHallForGuildContributions}, joinNear={ecfg.RequireMemberNearGuildHallToJoin}, remoteInvites={ecfg.AllowRemoteGuildInvites}, radius={ecfg.GuildHallAccessRadiusTiles}).");
                var halls = Features.Guilds.GuildStore.AllGuildHallStatus();
                int missing = 0;
                foreach (var h in halls) if (!h.HasHall) { missing++; if (missing <= 15) reply($"  [REVIEW] guild '{h.Name}' has NO Guild Hall (rule is enabled - some actions will be blocked)."); }
                reply(missing == 0 ? $"  all {halls.Count} guild(s) have a Guild Hall." : $"  {missing}/{halls.Count} guild(s) missing a required Guild Hall.");
            }

            // Biggest treasuries.
            List<(string OwnerKey, long Silver, bool IsGuild)> vaults = Features.Treasury.TreasuryStore.TopVaults(10);
            reply($"Top treasury vault(s) by silver ({vaults.Count}):");
            if (vaults.Count == 0) reply("  (none yet)");
            foreach ((string OwnerKey, long Silver, bool IsGuild) v in vaults)
            {
                string who = v.IsGuild ? v.OwnerKey
                           : v.OwnerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) ? v.OwnerKey.Substring("_personal:".Length)
                           : v.OwnerKey;
                reply($"  {Util.SilverFmt.Format(v.Silver)}  -  {who}{(v.IsGuild ? " (guild)" : "")}");
            }

            // Highest reported colony wealth.
            List<Features.PlayerStats.Dto.PlayerLeaderboardEntry> players = Features.PlayerStats.PlayerStatsStore.BuildSnapshot().Entries;
            players.Sort((a, b) => b.Wealth.CompareTo(a.Wealth));
            int wn = Math.Min(10, players.Count);
            reply($"Top reported colony wealth ({wn}):");
            for (int i = 0; i < wn; i++) reply($"  {Util.SilverFmt.Format(players[i].Wealth)}  -  {players[i].Username}");

            // Sites declaring zero item value: a stock client reports the real market value, so 0 means the client was
            // modified - the site value-under-report vector (now throughput-bounded, but still worth an eyeball).
            List<Features.Sites.Dto.SiteEntry> sites = Features.Sites.SiteStore.AllForApi();
            List<string> flagged = new List<string>();
            foreach (Features.Sites.Dto.SiteEntry s in sites)
                if (s.MarketValuePerUnit <= 0f)
                    flagged.Add($"  tile {s.Tile}: {s.BaseAmountPerCycle}x {s.ItemDefName}/cycle by {s.OwnerUsername} - declared value 0 (client likely modified)");
            reply($"Sites declaring zero item value ({flagged.Count}):");
            if (flagged.Count == 0) reply("  (none)");
            else foreach (string f in flagged) reply(f);

            // Site output catalog classification (uses the client-reported ItemLabelCache as coverage data). Reports
            // tier distribution + risk so an owner can spot dangerous allowed outputs / incomplete catalogs.
            {
                var cat = Features.ItemLabels.ItemLabelCache.AllForCatalog();
                int total = cat.Count, allowed = 0, blocked = 0, unknown = 0;
                int[] tierAllowed = new int[5];
                List<string> danger = new List<string>();
                foreach ((string defName, string label, long value) in cat)
                {
                    Features.Sites.SiteOutputClass c = Features.Sites.SiteOutputRules.Classify(defName, label, value);
                    if (c.IsUnknownOrSuspicious) unknown++;
                    if (c.IsAllowed) { allowed++; tierAllowed[Math.Max(1, Math.Min(4, c.TierNumber))]++;
                        if (c.TierNumber >= 3 && danger.Count < 20) danger.Add($"    T{c.TierNumber} {label} ({defName})"); }
                    else blocked++;
                }
                reply($"Site output catalog: {total} known item(s)" + (total < 12 ? " - SPARSE/incomplete (clients haven't pushed labels; picker uses vanilla fallback)" : ""));
                reply($"  allowed: {allowed} (T1={tierAllowed[1]} T2={tierAllowed[2]} T3={tierAllowed[3]} T4={tierAllowed[4]}), blocked: {blocked}, unknown/suspicious: {unknown}");
                if (Features.Sites.SiteStore.LegacyBlockedSiteCount > 0)
                    reply($"  [REVIEW] {Features.Sites.SiteStore.LegacyBlockedSiteCount} existing site(s) PAUSED - output now blocked by current rules.");
                if (!Features.Sites.SitesConfig.Current.UseSiteOutputTiers)
                    reply("  [REVIEW] UseSiteOutputTiers=false - legacy path" + (Features.Sites.SitesConfig.Current.AllowUnsafeLegacySiteOutputs ? " with AllowUnsafeLegacySiteOutputs=ON (ANY def buildable - admin/dev only!)" : " (simple-resource gate still applies)"));
                if (danger.Count > 0) { reply($"  higher-tier allowed outputs to eyeball ({danger.Count}):"); foreach (string d in danger) reply(d); }
            }

            // Guild vaults: orphaned (no live guild -> resurrectable by name reuse) or solo (1 member -> reset shelter).
            List<(string OwnerKey, long Silver, bool IsGuild)> guildVaults =
                Features.Treasury.TreasuryStore.TopVaults(1000).FindAll(v => v.IsGuild);
            Dictionary<string, int> memberCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach ((string Name, int MemberCount) g in Features.Guilds.GuildStore.ListGuilds()) memberCounts[g.Name] = g.MemberCount;

            List<string> orphans = new List<string>(), solos = new List<string>();
            foreach ((string OwnerKey, long Silver, bool IsGuild) v in guildVaults)
            {
                if (!memberCounts.TryGetValue(v.OwnerKey, out int mc))
                    orphans.Add($"  '{v.OwnerKey}' holds {Util.SilverFmt.Format(v.Silver)} but no guild exists (resurrectable)");
                else if (mc <= 1 && v.Silver > 0)
                    solos.Add($"  '{v.OwnerKey}' (1 member) holds {Util.SilverFmt.Format(v.Silver)}");
            }
            reply($"Orphaned guild vaults ({orphans.Count}):");
            if (orphans.Count == 0) reply("  (none)"); else foreach (string o in orphans) reply(o);
            reply($"Solo-guild vaults ({solos.Count}):");
            if (solos.Count == 0) reply("  (none)"); else foreach (string sv in solos) reply(sv);

            // Recovery queue: value that couldn't reach an owner (invalid/deleted account, disbanded guild) is parked
            // here instead of destroyed - anything held is real value awaiting an admin decision.
            int recHeld = Features.Recovery.RecoveryStore.HeldCount;
            reply($"Recovery queue: {recHeld} held item/silver record(s)" + (recHeld > 0 ? " - review with 'kmh recover list'." : "."));

            reply("Deeper: kmh ledger [user] · kmh inspect treasury|sites · kmh recover list · kmh validate · kmh backups");
        }
    }
}
