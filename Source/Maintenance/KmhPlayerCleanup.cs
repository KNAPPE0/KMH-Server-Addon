using System;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Live admin player-cleanup for cheat/reset/duper cases. Mutates in-memory stores (JSON edits are ignored until a
    // restart), backs up before any confirm, refunds/holds escrow recovery-safe, writes ledger entries, and repushes.
    internal static class KmhPlayerCleanup
    {
        public static void WipeEconomy(string user, bool commit, Action<string> reply)
        {
            if (Bad(user, "wipe-economy", reply)) return;
            Head("wipe-economy", user, commit, reply);
            if (commit && !Backup($"wipe-economy-{user}", reply)) return;
            EconomyWork(user, commit, reply);
            Finish(user, commit, reply, RepushEconomy);
        }

        public static void WipePlayer(string user, bool commit, Action<string> reply)
        {
            if (Bad(user, "wipe-player", reply)) return;
            Head("wipe-player", user, commit, reply);
            if (commit && !Backup($"wipe-player-{user}", reply)) return;
            EconomyWork(user, commit, reply);
            NonEconomyWork(user, commit, reply);
            Finish(user, commit, reply, RepushAll);
        }

        // Safe one-command rollback/reset-abuse cleanup: backup -> audit -> wipe economy -> rebuild -> verify -> repush.
        public static void ResetCleanup(string user, bool commit, Action<string> reply)
        {
            if (Bad(user, "reset-cleanup", reply)) return;
            Head("reset-cleanup", user, commit, reply);
            if (commit && !Backup($"reset-cleanup-{user}", reply)) return;
            AdminCommands.KmhCleanupCommands.AuditPlayer(new[] { "audit-player", user }, reply);
            EconomyWork(user, commit, reply);
            if (commit) { Features.PlayerStats.PlayerStatsStore.LoadFromDisk(); Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot(); reply("  standings rebuilt."); }
            Finish(user, commit, reply, RepushAll);
        }

        public static void PurgeSites(string user, bool commit, Action<string> reply)
        {
            if (Bad(user, "purge sites owner", reply)) return;
            Head("purge sites owner", user, commit, reply);
            if (commit && !Backup($"purge-sites-{user}", reply)) return;
            (int sites, int pawns) = Features.Sites.SiteStore.PurgeOwner(user, dryRun: !commit);
            reply($"  sites owned: {sites}{(commit ? " removed" : " WOULD be removed")}; {pawns} active pawn-worker assignment(s) dropped (client pawns stay in caravan, NOT deleted).");
            if (commit) { Features.Sites.SiteHandler.BroadcastSnapshot(); ServerLog.Warn($"kmh purge sites {user}: {sites} site(s)"); }
            Tail(commit, reply);
        }

        public static void RemoveGuilds(string user, bool commit, Action<string> reply)
        {
            if (Bad(user, "remove-player-guilds", reply)) return;
            Head("remove-player-guilds", user, commit, reply);
            string g = Features.Guilds.GuildStore.CurrentGuildOf(user);
            reply($"  guild: {(string.IsNullOrEmpty(g) ? "(none)" : g)}");
            if (commit && !string.IsNullOrEmpty(g))
            {
                if (!Backup($"remove-guilds-{user}", reply)) return;
                if (Features.Guilds.GuildStore.Leave(user, out string err)) reply("  removed from guild (guild kept unless they were its last member; members' Hall refreshes on next open).");
                else reply($"  [WARN] leave failed: {err} - left intact for admin review.");
            }
            Tail(commit, reply);
        }

        public static void Unlink(string user, bool commit, Action<string> reply)
        {
            if (Bad(user, "unlink-player", reply)) return;
            Head("unlink-player", user, commit, reply);
            if (commit) { Features.LinkedAccounts.LinkedAccountsStore.Unlink(user); Features.LinkedAccounts.LinkedAccountsHandler.BroadcastSnapshot(); reply("  Discord link removed (economy data untouched)."); }
            else reply("  WOULD remove the player's Discord link only.");
            Tail(commit, reply);
        }

        public static void RebuildPlayer(string user, Action<string> reply)
        {
            Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
            Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
            Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot();
            reply($"Rebuilt standings from disk and re-pushed (covers '{user}').");
        }

        public static void RepushAll(Action<string> reply)
        {
            RepushAll();
            reply("Re-pushed Marketplace/Auction/Want/Sites/Standings/Reputation/World/LinkedAccounts snapshots. (Treasury is per-user - refreshes on the player's next action.)");
        }

        // ---- shared work (no backup/repush; the public entry points own those) ----

        private static void EconomyWork(string user, bool commit, Action<string> reply)
        {
            (int mpL, int mpI) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: !commit);
            reply($"  marketplace listings: {mpL} ({mpI} escrowed item(s){(commit ? " refunded->treasury" : "")})");

            int auc = 0;
            foreach (Features.Auctions.Dto.AuctionDto a in Features.Auctions.AuctionStore.AllForAdmin())
                if (a != null && Eq(a.SellerUsername, user)) { auc++; if (commit) Features.Auctions.AuctionStore.AdminVoid(a.Id); }
            reply($"  auctions (seller): {auc}{(commit ? " voided (bids returned to bidders)" : "")}");

            int want = 0;
            foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin())
                if (w != null && Eq(w.BuyerUsername, user)) { want++; if (commit) Features.WantBoard.WantStore.AdminCancel(w.Id); }
            reply($"  want posts: {want}{(commit ? " cancelled (escrow refunded)" : "")}");

            // Recovery records are single-owner (the intended recipient), so clearing the target's is not third-party value.
            System.Collections.Generic.List<Features.Recovery.Dto.RecoveryRecord> recs = Features.Recovery.RecoveryStore.ForUser(user);
            reply($"  recovery records: {recs.Count}{(commit ? " cleared:" : "")}");
            if (commit)
            {
                int shown = 0;
                foreach (Features.Recovery.Dto.RecoveryRecord r in recs) { if (shown++ >= 10) { reply($"    ... +{recs.Count - 10} more"); break; } reply("    - " + Features.Recovery.RecoveryStore.Describe(r)); }
                Features.Recovery.RecoveryStore.ClearUser(user);
            }

            // Treasury LAST: escrow above refunds into it first, so this single removal captures + logs everything.
            (long silver, int stacks, int pend) = Features.Treasury.TreasuryStore.PersonalSummary(user);
            if (commit)
            {
                long had = Features.Treasury.TreasuryStore.ResetPersonal(user);
                if (had > 0) Persistence.TransactionLedger.Record($"_personal:{user}", "admin", "wipe", -had, "", "player cleanup");
                reply($"  treasury cleared: {Util.SilverFmt.Format(Math.Max(0, had))} silver + {stacks} item stack(s) + {pend} pending removed");
            }
            else reply($"  treasury: {Util.SilverFmt.Format(silver)} silver + {stacks} item stack(s) + {pend} pending WOULD be removed");
        }

        private static void NonEconomyWork(string user, bool commit, Action<string> reply)
        {
            string g = Features.Guilds.GuildStore.CurrentGuildOf(user);
            reply($"  guild: {(string.IsNullOrEmpty(g) ? "(none)" : g)}{(commit && !string.IsNullOrEmpty(g) && Features.Guilds.GuildStore.Leave(user, out _) ? " - removed" : "")}");
            (int sites, int pawns) = Features.Sites.SiteStore.PurgeOwner(user, dryRun: !commit);
            reply($"  sites owned: {sites}{(commit ? " removed" : "")} ({pawns} pawn-worker assignment(s); client pawns NOT deleted)");
            // Also drop them as a worker at OTHER owners' sites - their pawns no longer exist, so they must not keep producing.
            int foreignWorker = Features.Sites.SiteStore.RemoveWorkerEverywhere(user, dryRun: !commit);
            reply($"  worker slots at other sites: {foreignWorker}{(commit ? " dropped" : " WOULD drop")} (those sites stay; client recalls the pawn)");
            bool stats = commit && Features.PlayerStats.PlayerStatsStore.RemoveUser(user);
            bool rep = commit && Features.Reputation.ReputationStore.RemoveUser(user);
            int mail = commit ? Features.Notifications.NotificationStore.ClearUser(user) : Features.Notifications.NotificationStore.PeekForUser(user).Count;
            if (commit) Features.LinkedAccounts.LinkedAccountsStore.Unlink(user);
            reply($"  standings {(stats ? "removed" : commit ? "none" : "WOULD remove")} · reputation {(rep ? "removed" : commit ? "none" : "WOULD remove")} · mail {mail}{(commit ? " cleared" : "")} · Discord link {(commit ? "removed" : "WOULD remove")}");
        }

        // ---- helpers ----

        private static bool Bad(string user, string cmd, Action<string> reply)
        {
            if (string.IsNullOrEmpty(user)) { reply($"Usage: kmh {cmd} <user> <dry|confirm>"); return true; }
            return false;
        }

        private static void Head(string cmd, string user, bool commit, Action<string> reply)
            => reply($"=== {cmd} '{user}' [{(commit ? "CONFIRM" : "dry-run")}] ===");

        private static bool Backup(string name, Action<string> reply)
        {
            if (Persistence.KmhDataBackup.TryCreate(name, out string dir, out string err)) { reply($"  backup: {dir}"); return true; }
            reply($"  [ABORT] backup failed ({err}) - no changes made.");
            return false;
        }

        private static void Finish(string user, bool commit, Action<string> reply, Action repush)
        {
            if (!commit) { Tail(commit, reply); return; }
            repush();
            Verify(user, reply);
            ServerLog.Warn($"kmh player cleanup applied for '{user}'.");
        }

        private static void Tail(bool commit, Action<string> reply)
        {
            if (!commit) reply("  (dry-run - nothing changed; run with 'confirm')");
        }

        private static void Verify(string user, Action<string> reply)
        {
            (int mp, _) = Features.Marketplace.MarketplaceStore.AdminPurgeSeller(user, dryRun: true);
            (long silver, int stacks, int pend) = Features.Treasury.TreasuryStore.PersonalSummary(user);
            int rec = Features.Recovery.RecoveryStore.ForUser(user).Count;
            int aucSeller = 0; foreach (Features.Auctions.Dto.AuctionDto a in Features.Auctions.AuctionStore.AllForAdmin()) if (Eq(a?.SellerUsername, user)) aucSeller++;
            int wantBuyer = 0; foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin()) if (Eq(w?.BuyerUsername, user)) wantBuyer++;
            (int sites, _) = Features.Sites.SiteStore.PurgeOwner(user, dryRun: true);
            int fworker = Features.Sites.SiteStore.RemoveWorkerEverywhere(user, dryRun: true);
            bool clean = mp == 0 && silver == 0 && stacks == 0 && pend == 0 && rec == 0 && aucSeller == 0 && wantBuyer == 0 && sites == 0 && fworker == 0;
            reply(clean
                ? "  verify: OK - no residual owned economy/site data for this player."
                : $"  verify: RESIDUAL - mp={mp} silver={silver} items={stacks} pending={pend} auc(seller)={aucSeller} want(buyer)={wantBuyer} sites={sites} worker={fworker} recovery={rec} (re-run/investigate).");
            Residual(user, reply);
        }

        // Multi-party refs a wipe must NOT auto-delete (would punish innocent third parties) - report for targeted handling.
        public static void Residual(string user, Action<string> reply)
        {
            (int posted, int claimed) = Features.Quests.QuestStore.CountForUser(user);
            int gq = Features.World.WorldStore.GlobalQuestContributionsOf(user);
            int bids = 0; foreach (Features.Auctions.Dto.AuctionDto a in Features.Auctions.AuctionStore.AllForAdmin()) if (Eq(a?.HighBidder, user)) bids++;
            if (posted + claimed + gq + bids == 0) return;
            reply($"  still referenced (multi-party, NOT auto-wiped): quests posted {posted}/claimed {claimed}, global-quest contributions {gq}, auction high-bids {bids}.");
            reply("    handle specific ones with 'kmh cancel quest|auction <id>' (refunds all parties correctly).");
        }

        private static void RepushEconomy()
        {
            Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
            Features.Auctions.AuctionHandler.BroadcastSnapshot();
            Features.WantBoard.WantHandler.BroadcastSnapshot();
        }

        private static void RepushAll()
        {
            RepushEconomy();
            Features.Sites.SiteHandler.BroadcastSnapshot();
            Features.Quests.QuestHandler.BroadcastSnapshot();
            Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot();
            Features.Reputation.ReputationHandler.BroadcastSnapshot();
            Features.World.WorldHandler.BroadcastSnapshot();
            Features.LinkedAccounts.LinkedAccountsHandler.BroadcastSnapshot();
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
