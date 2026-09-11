using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    // Stores live in memory, so hand-editing KMH-Data JSON does nothing until a restart or a reload command.
    internal static class KmhCleanupCommands
    {
        public static void AuditPlayer(string[] args, Action<string> reply)
        {
            string user = args != null && args.Length > 1 ? args[1] : "";
            if (string.IsNullOrEmpty(user)) { reply("Usage: kmh audit-player <user>"); return; }
            reply($"=== KMH audit for '{user}' (read-only) ===");
            Features.Economy.EconomyPolicy ecp = Features.Economy.EconomyAccess.Policy;
            string access = string.Equals(ecp.PersonalAccess, "Remote", StringComparison.OrdinalIgnoreCase)
                ? "" : $" (access={ecp.PersonalAccess}: accrued but withdrawable only when the player meets that rule"
                       + (string.Equals(ecp.PersonalAccess, "Disabled", StringComparison.OrdinalIgnoreCase) ? " - Disabled = locked until re-enabled)" : ")");
            reply($"  treasury silver: {Util.SilverFmt.Format(Features.Treasury.TreasuryStore.GetPersonalSilver(user))}{access}");
            // Treasury only, so this is a floor rather than the total a v1.3.0 client actually scales raids against.
            string wealthNote = Features.FeaturesConfig.Current.Wealth
                ? "raid/threat scaling on v1.3.0+ clients also counts the escrow listed below"
                : "not in raid/threat scaling - Features.Wealth off";
            reply($"  KMH off-map value: ~{Util.SilverFmt.Format(Features.Treasury.TreasuryStore.PersonalOffMapValue(user))} silver-equiv (treasury silver + item value only; {wealthNote})");
            (int mpListings, int mpItems) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: true);
            reply($"  marketplace listings: {mpListings} ({mpItems} item(s) escrowed)");
            int auc = 0; foreach (Features.Auctions.Dto.AuctionDto a in Features.Auctions.AuctionStore.AllForAdmin()) if (string.Equals(a?.SellerUsername, user, StringComparison.OrdinalIgnoreCase)) auc++;
            int want = 0; foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin()) if (string.Equals(w?.BuyerUsername, user, StringComparison.OrdinalIgnoreCase)) want++;
            reply($"  auctions (seller): {auc} · want posts: {want}");
            reply($"  recovery records: {Features.Recovery.RecoveryStore.ForUser(user).Count}");
            long saveGen = Features.Treasury.TreasuryStore.LastSaveGenerationOf(user);
            if (saveGen > 0) reply($"  client save generation: {saveGen} (highest trusted; a lower report is refused, not applied - check the log for a rollback warning)");
            int qPosted = 0, qClaimed = 0;
            foreach (Features.Quests.Dto.QuestEntry q in Features.Quests.QuestStore.BuildSnapshot(user)?.Quests
                     ?? new List<Features.Quests.Dto.QuestEntry>())
            {
                if (q == null) continue;
                if (string.Equals(q.PosterUsername, user, StringComparison.OrdinalIgnoreCase)) qPosted++;
                if (string.Equals(q.ClaimedByUsername, user, StringComparison.OrdinalIgnoreCase)) qClaimed++;
            }
            reply($"  quests: {qPosted} posted, {qClaimed} claimed"
                  + (Features.Economy.KmhEscrowPurge.HasQuestEscrow(user) ? " - bounty still ESCROWED on open quest(s)" : ""));
            // "notifications" is the offline notice queue; Player Mail is a separate system holding real escrow.
            reply($"  notifications queued: {Features.Notifications.NotificationStore.PeekForUser(user).Count}");
            Features.Mail.MailStore.InboxView mv = Features.Mail.MailStore.BuildView(user);
            long mailItems = 0; foreach (KeyValuePair<string, int> kv in mv.EscrowedOutItems) mailItems += kv.Value;
            reply($"  player mail: {mv.Inbox.Count} received ({mv.Unread} unread), {mv.Outgoing.Count} sent still recallable"
                  + (mv.EscrowedOutSilver > 0 || mailItems > 0 || mv.EscrowedOutPayloadValue > 0
                     ? $" - ESCROWED out: {Util.SilverFmt.Format(mv.EscrowedOutSilver)} silver, {mailItems} item(s), "
                       + $"{Util.SilverFmt.Format(mv.EscrowedOutPayloadValue)} gear value"
                     : ""));
            string guild = Features.Guilds.GuildStore.CurrentGuildOf(user);
            reply($"  guild: {(string.IsNullOrEmpty(guild) ? "(none)" : guild)}");
            int owned = 0, worked = 0;
            foreach (Features.Sites.Dto.SiteEntry s in Features.Sites.SiteStore.AllForApi())
            {
                if (s == null) continue;
                if (Features.Sites.SiteOwnership.IsOwnedBy(s, user)) owned++;
                if (s.Workers != null && s.Workers.Contains(user, StringComparer.OrdinalIgnoreCase)) worked++;
            }
            reply($"  sites: {owned} owned, {worked} worked");
            int colCount = 0, colFlags = 0;
            foreach (Features.PlayerStats.Dto.ColonistEntry c in Features.PlayerStats.PlayerStatsStore.BuildColonistRoster()?.Colonists
                     ?? new System.Collections.Generic.List<Features.PlayerStats.Dto.ColonistEntry>())
            {
                if (c == null || !string.Equals(c.Owner, user, StringComparison.OrdinalIgnoreCase)) continue;
                colCount++;
                int[] sk = { c.SkShooting, c.SkMelee, c.SkMedicine, c.SkCrafting, c.SkConstruction };
                int over20 = 0, at20 = 0; foreach (int s in sk) { if (s > 20) over20++; if (s >= 20) at20++; }
                if (over20 > 0 || (at20 >= 4 && c.Days < 60)) colFlags++;
            }
            reply($"  colonists: {colCount} in records" + (colFlags > 0 ? $" - {colFlags} flagged for skill review ('kmh validate colonists'; review-only)" : ""));
            Maintenance.KmhPlayerCleanup.Residual(user, reply);
            reply("  Deeper: kmh ledger " + user + " · kmh recover player " + user + " · kmh inspect <subsystem>");
        }

        public static void ResetPreview(string user, Action<string> reply)
        {
            if (string.IsNullOrEmpty(user)) { reply("Usage: kmh reset-preview <user>"); return; }
            reply($"=== Reset preview for '{user}' (read-only; nothing changed) ===");
            (long silver, int itemStacks, int pending) = Features.Treasury.TreasuryStore.PersonalSummary(user);
            reply($"  WOULD CLEAR: treasury {Util.SilverFmt.Format(silver)} silver, {itemStacks} item stack(s), {pending} pending deposit(s)");
            string solo = Features.Guilds.GuildStore.SoloGuildOf(user);
            if (solo != null) reply($"  WOULD CLEAR: solo guild '{solo}' vault {Util.SilverFmt.Format(Features.Treasury.TreasuryStore.GetGuildSilver(solo))}");
            (int mpListings, int mpItems) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: true);
            reply($"  WOULD CLEAR: {mpListings} marketplace listing(s) ({mpItems} escrowed item(s)), auctions/wants purged");
            var (ownedSites, _) = Features.Sites.SiteStore.PurgeOwner(user, dryRun: true);
            reply($"  WOULD CLEAR: {ownedSites} owned site(s) + all worker slots on other sites");
            reply($"  WOULD CLEAR: {Features.Recovery.RecoveryStore.ForUser(user).Count} recovery record(s)");
            // Escrow sits outside the treasury and is burned too, so omitting it would understate the loss.
            if (Features.Economy.KmhEscrowPurge.HasQuestEscrow(user)) reply("  WOULD CLEAR: open quest(s) with bounty still escrowed (bounty burned)");
            if (Features.Economy.KmhEscrowPurge.HasMailEscrow(user))  reply("  WOULD CLEAR: unclaimed mail attachment(s) you sent (escrow burned)");
            var (roadProjects, roadSilver) = Features.Roadworks.RoadworksStore.PurgeOwner(user, dryRun: true);
            if (roadProjects > 0)
                reply($"  WOULD CLEAR: {roadProjects} roadworks project(s) ({Util.SilverFmt.Format(roadSilver)} reserved silver burned; completed roads kept)");
            reply("  KEPT: username, Discord link, guild membership/rank, donations already in guild vaults, ledger history.");
        }

        public static void ReloadMarketplace(Action<string> reply)
        {
            Features.Marketplace.MarketplaceStore.LoadFromDisk();
            Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
            reply("Marketplace reloaded from Marketplace.json and re-pushed to clients. (Edit the file, then run this - a plain restart also works.)");
            ServerLog.Info("kmh reload-marketplace: re-read Marketplace.json and rebroadcast");
        }

        public static void Repush(string[] args, Action<string> reply)
        {
            string what = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "all";
            if (what == "marketplace") { Features.Marketplace.MarketplaceHandler.BroadcastSnapshot(); reply("Marketplace snapshot re-pushed."); }
            else if (what == "all") Maintenance.KmhPlayerCleanup.RepushAll(reply);
            else reply("Usage: kmh repush [marketplace|all]");
        }

        public static void PurgeMarketplaceSeller(string[] args, Action<string> reply)
        {
            string user = args != null && args.Length > 3 ? args[3] : "";
            string mode = args != null && args.Length > 4 ? args[4].ToLowerInvariant() : "dry";
            if (string.IsNullOrEmpty(user)) { reply("Usage: kmh purge marketplace seller <user> <dry|confirm>"); return; }

            (int listings, int items) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: true);
            if (listings == 0) { reply($"'{user}' has no marketplace listings."); return; }
            if (mode != "confirm")
            {
                reply($"[dry-run] Would cancel {listings} listing(s) from '{user}', refunding {items} escrowed item(s) to their treasury (recovery-safe). Run with 'confirm' to apply.");
                return;
            }
            if (Persistence.KmhDataBackup.TryCreate($"purge-marketplace-{user}", out string dir, out string err))
                reply($"Backup: {dir}");
            else reply($"[WARN] backup failed ({err}) - continuing anyway.");
            (int done, int refunded) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: false);
            Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
            reply($"Purged {done} listing(s) from '{user}'; {refunded} escrowed item(s) refunded (or held in recovery if undeliverable). Snapshot re-pushed.");
            ServerLog.Warn($"kmh purge marketplace seller {user}: removed {done} listing(s), refunded {refunded} item(s)");
        }
    }
}
