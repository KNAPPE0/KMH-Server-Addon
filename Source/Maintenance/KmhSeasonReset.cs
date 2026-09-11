using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Backs up first, so a season reset stays reversible through 'kmh restore'.
    internal static class KmhSeasonReset
    {
        public static bool Run(string actorName, out string summary)
        {
            summary = "";

            // Safety net first - never wipe without a backup.
            if (!Persistence.KmhDataBackup.TryCreate("pre-season-reset", out string dir, out string err))
            { summary = $"aborted - backup failed: {err}"; return false; }
            string backup = System.IO.Path.GetFileName(dir);
            // Named before the wipe, because a partial failure never produces the summary that would name it.
            ServerLog.Warn($"Season reset: restore point is backup {backup} (restore with: kmh restore {backup}).");

            // Archive the outgoing season's leaders BEFORE wiping the stats they're computed from.
            (int rolledSeason, int recordCount) = Features.Seasons.SeasonStore.RollSeason();

            // Treasury last, so a concurrent sweeper settlement cannot re-credit an already-wiped vault.
            foreach (Action clear in SeasonClearers()) clear();
            // In-memory only, but a fresh season must not inherit the old cooldown windows.
            Features.Economy.EconomyAccess.ClearRuntimeState();
            int vaults = Features.Treasury.TreasuryStore.ClearAllVaults();

            // Ids restart at 1, so anything still holding an old one must not reach whatever now carries that number.
            Features.Economy.KmhEconomyReset.BumpDataGeneration($"season {rolledSeason} reset");
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

        // Enumerated rather than inlined so the coverage test can reflect over it and prove none was forgotten.
        internal static IEnumerable<Action> SeasonClearers()
        {
            yield return Features.Marketplace.MarketplaceStore.ClearForNewSeason;
            yield return Features.Auctions.AuctionStore.ClearForNewSeason;
            yield return Features.WantBoard.WantStore.ClearForNewSeason;
            yield return Features.Mail.MailStore.ClearForNewSeason;
            yield return Features.Chat.ChatStore.ClearForNewSeason;
            yield return Features.Sites.SiteStore.ClearForNewSeason;
            yield return Features.Roadworks.RoadworksStore.ClearForNewSeason;
            yield return Features.Frontier.KmhWorldDirector.ClearForNewSeason;
            yield return Features.Quests.QuestStore.ClearForNewSeason;
            yield return Features.Reputation.ReputationStore.ClearForNewSeason;
            yield return Features.Notifications.NotificationStore.ClearForNewSeason;
            yield return Features.Recovery.RecoveryStore.ClearForNewSeason;
            yield return Features.World.WorldStore.ClearForNewSeason;
            yield return Features.PlayerStats.PlayerStatsStore.ClearForNewSeason;
        }

        // A stale client view is cosmetic, so one failing broadcast never aborts the reset.
        private static void Rebroadcast()
        {
            Safe(() => Features.Marketplace.MarketplaceHandler.BroadcastSnapshot());
            Safe(() => Features.Quests.QuestHandler.BroadcastSnapshot());
            Safe(() => Features.Auctions.AuctionHandler.BroadcastSnapshot());
            Safe(() => Features.WantBoard.WantHandler.BroadcastSnapshot());
            Safe(() => Features.Sites.SiteHandler.BroadcastSnapshot());
            Safe(() => Features.Roadworks.RoadworksHandler.BroadcastSnapshot());
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
