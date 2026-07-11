using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    // Live-store admin cleanup so owners never hand-edit KMH-Data JSON (stores are in memory; disk edits are ignored
    // until a restart or a reload command). Destructive ops back up first, support dry-run, and refund/recover value.
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
            reply($"  KMH off-map value: ~{Util.SilverFmt.Format(Features.Treasury.TreasuryStore.PersonalOffMapValue(user))} silver-equiv (treasury silver + item value; NOT counted in RimWorld raid/threat scaling yet)");
            (int mpListings, int mpItems) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: true);
            reply($"  marketplace listings: {mpListings} ({mpItems} item(s) escrowed)");
            int auc = 0; foreach (Features.Auctions.Dto.AuctionDto a in Features.Auctions.AuctionStore.AllForAdmin()) if (string.Equals(a?.SellerUsername, user, StringComparison.OrdinalIgnoreCase)) auc++;
            int want = 0; foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin()) if (string.Equals(w?.BuyerUsername, user, StringComparison.OrdinalIgnoreCase)) want++;
            reply($"  auctions (seller): {auc} · want posts: {want}");
            reply($"  recovery records: {Features.Recovery.RecoveryStore.ForUser(user).Count}");
            reply($"  mail/notifications: {Features.Notifications.NotificationStore.PeekForUser(user).Count}");
            string guild = Features.Guilds.GuildStore.CurrentGuildOf(user);
            reply($"  guild: {(string.IsNullOrEmpty(guild) ? "(none)" : guild)}");
            int owned = 0, worked = 0;
            foreach (Features.Sites.Dto.SiteEntry s in Features.Sites.SiteStore.AllForApi())
            {
                if (s == null) continue;
                if (string.Equals(s.OwnerUsername, user, StringComparison.OrdinalIgnoreCase)) owned++;
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

        // Read-only preview of what a save-reset (auto or 'kmh reset-player-economy') would clear for this user.
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
            // args: purge marketplace seller <user> <dry|confirm>
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
