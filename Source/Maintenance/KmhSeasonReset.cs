using System;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Season lifecycle reset: archive the outgoing season's leaders, then wipe the live economy for a fresh season
    // (guilds, Discord links, and the season archive are kept). Backs up first, so it's reversible via 'kmh restore'.
    internal static class KmhSeasonReset
    {
        public static bool Run(string actorName, out string summary)
        {
            summary = "";

            // Safety net first - never wipe without a backup.
            if (!Persistence.KmhDataBackup.TryCreate("pre-season-reset", out string dir, out string err))
            { summary = $"aborted - backup failed: {err}"; return false; }
            string backup = System.IO.Path.GetFileName(dir);

            // Archive the outgoing season's leaders BEFORE wiping the stats they're computed from.
            (int rolledSeason, int recordCount) = Features.Seasons.SeasonStore.RollSeason();

            // Wipe live economy (guilds, linked accounts, and the season archive are left intact). Clear every silver
            // source first, then treasury LAST, so a concurrent sweeper settlement can't re-credit a wiped vault.
            Features.Marketplace.MarketplaceStore.ClearForNewSeason();
            Features.Auctions.AuctionStore.ClearForNewSeason();
            Features.WantBoard.WantStore.ClearForNewSeason();
            Features.Sites.SiteStore.ClearForNewSeason();
            Features.Quests.QuestStore.ClearForNewSeason();
            Features.Reputation.ReputationStore.ClearForNewSeason();
            Features.Notifications.NotificationStore.ClearForNewSeason();
            Features.Recovery.RecoveryStore.ClearForNewSeason();
            Features.World.WorldStore.ClearForNewSeason();
            Features.PlayerStats.PlayerStatsStore.ClearForNewSeason();
            int vaults = Features.Treasury.TreasuryStore.ClearAllVaults();

            Rebroadcast();

            int newSeason = Features.Seasons.SeasonStore.CurrentSeason;
            summary = $"Season {rolledSeason} archived ({recordCount} record(s)); economy wiped ({vaults} vault(s) + " +
                      $"listings/auctions/wants/sites/quests/reputation/mail/world/standings cleared). " +
                      $"Now in season {newSeason}. Backup {backup} (restore with: kmh restore {backup}).";
            ServerLog.Warn($"SEASON RESET by {actorName}: {summary}");
            Extensibility.KmhEventBus.Instance.RaiseSeasonRolled(new KMH.Sdk.Server.Events.SeasonRolledEvent
            { RolledSeason = rolledSeason, NewSeason = newSeason, RecordCount = recordCount, Actor = actorName, EconomyWiped = true });
            try { KmhStatusExport.WriteToDisk(); } catch { }
            return true;
        }

        // Push fresh state to connected clients. Defensive: a stale client view is cosmetic (server state is correct),
        // so one failing broadcast never aborts the reset.
        private static void Rebroadcast()
        {
            Safe(() => Features.Marketplace.MarketplaceHandler.BroadcastSnapshot());
            Safe(() => Features.Quests.QuestHandler.BroadcastSnapshot());
            Safe(() => Features.Auctions.AuctionHandler.BroadcastSnapshot());
            Safe(() => Features.WantBoard.WantHandler.BroadcastSnapshot());
            Safe(() => Features.Sites.SiteHandler.BroadcastSnapshot());
            Safe(() => Features.Reputation.ReputationHandler.BroadcastSnapshot());
            Safe(() => Features.World.WorldHandler.BroadcastSnapshot());
            Safe(() => Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot());
            Safe(() => Features.Seasons.SeasonHandler.Broadcast());
            // Treasury has no broadcast helper - push each verified client its (now empty) vault.
            Safe(() =>
            {
                foreach (ServerClient c in Network.ServerClients.Keys)
                {
                    if (c?.IsVerified != true) continue;
                    string u = c.GetData<UserFile>()?.Username;
                    if (string.IsNullOrEmpty(u)) continue;
                    KmhRouter.SendTo(c, KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(u));
                }
            });
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception ex) { ServerLog.Warn($"Season reset rebroadcast step failed: {ex.Message}"); }
        }
    }
}
