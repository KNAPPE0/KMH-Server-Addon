using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    internal static class ExpirySweeper
    {
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

        public static void Start()
        {
            try { KmhStatusExport.WriteToDisk(); } catch { }

            // Its own job: an unwritable store must be retried whether or not the expiry sweep has anything to do.
            KmhScheduler.Register("persistence-retry", SweepInterval, RetryUnwritableStores,
                                  KmhJobKind.Infrastructure, SweepInterval);
            // Infrastructure so snapshot, pruning and status export run through a freeze; value moves ask admission below.
            KmhScheduler.Register("expiry-sweep",      SweepInterval, Tick,
                                  KmhJobKind.Infrastructure, SweepInterval);

            // FlushAll only covers `kmh save`, backups and exit, so without this a crash costs every message since boot.
            KmhScheduler.Register("chat-save", SweepInterval, Features.Chat.ChatStore.SaveIfDirty,
                                  KmhJobKind.Infrastructure, SweepInterval);
            KmhScheduler.Register("playerstats-save", Features.PlayerStats.PlayerStatsStore.SaveInterval,
                                  Features.PlayerStats.PlayerStatsStore.SaveIfDirty, KmhJobKind.Infrastructure,
                                  Features.PlayerStats.PlayerStatsStore.SaveInterval);

            // Infrastructure: a backup only reads, and a freeze is exactly when an owner wants a recent one.
            int every = MaintenanceConfig.Current.BackupEveryHours;
            if (every > 0)
                KmhScheduler.Register("scheduled-backup", TimeSpan.FromHours(every), Persistence.KmhDataBackup.ScheduledTick,
                                      KmhJobKind.Infrastructure);
        }

        public static void Stop() => KmhScheduler.Stop();

        // One failed save is enough to retry: the freeze threshold would leave a transient failure unscheduled in memory.
        internal static bool ShouldRetrySaves => Persistence.JsonFileStore.AnyStoreUnsaved;

        // Value moves are what would have saved the store, so without this retry the freeze outlives the disk problem.
        private static void RetryUnwritableStores()
        {
            if (!ShouldRetrySaves) return;
            ServerLog.Warn("Persistence: retrying the stores that could not be written...");
            KmhDataFlush.FlushAll();
        }

        internal static void TickForTest(long nowTicks) => Tick(nowTicks);

        // The scheduler only isolates whole jobs, so one throw here would starve every duty after it.
        private static void Tick() => Tick(DateTime.UtcNow.Ticks);

        private static void Tick(long now)
        {
            // A timer coming due is not permission to move value: these stay due for a later tick rather than pay through
            // the freeze. Counted for the whole set, so a reset or backup that closes the gate waits for the tick to end.
            using (KmhValueOps.Scope op = KmhValueOps.Begin(KmhIngress.Scheduler, out _))
            {
                if (op != null)
                {
                    Step("marketplace expiry", () => SweepMarketplace(now));
                    Step("quest expiry",       () => SweepQuests(now));
                    Step("site production",    SweepSiteProduction);
                    Step("auction settlement", () => SweepAuctions(now));
                    Step("want expiry",        () => SweepWants(now));
                    Step("guild invites",      () => Features.Guilds.GuildStore.PruneExpiredInvites());
                    Step("stale deposits",     RevertStalePendingDeposits);
                    Step("owed house fees",    () => Features.Treasury.TreasuryStore.SettleOwedFees());
                    Step("mail",               () => SweepMail(now));
                }
            }

            // Infrastructure: it only deletes temp files, and an abandoned publish otherwise holds up to 48MB of staging.
            Step("stale publish staging", Features.Enforcement.EnforcementPublish.PurgeExpired);
            Step("fragment transfers",   () => SubProtocol.KmhRouter.SweepFragments());
            // Outside the admission check on purpose: the reset's own barrier is what closes it.
            Step("economy reset retry",  () => Features.Economy.KmhEconomyReset.ResumeStalled());
            Step("season reset retry",   () => KmhSeasonReset.ResumePending());
            Step("stale snapshots",    KmhInvalidation.Drain);
            Step("snapshot requests",  Persistence.KmhSnapshot.ConsumePendingRequests);
            Step("snapshot pruning",   Persistence.KmhSnapshot.PruneOldIfDue);
            Step("status export",      () => KmhStatusExport.WriteToDisk());
        }

        private static void Step(string what, Action work)
        {
            try { work(); }
            catch (Exception ex) { ServerLog.Error($"ExpirySweeper: {what} failed", ex); }
        }

        private static void SweepMarketplace(long now)
        {
            List<long> expiredListings = Features.Marketplace.MarketplaceStore.CollectExpiredIds(now);
            bool marketplaceChanged = false;
            foreach (long id in expiredListings)
            {
                if (Features.Marketplace.MarketplaceStore.ExpireListing(id, out string sellerUsername, out string itemKey, out int returned))
                {
                    marketplaceChanged = true;
                    ServerLog.Info($"ExpirySweeper: marketplace listing #{id} expired (refunded to {sellerUsername})");
                    PushTreasuryTo(sellerUsername);
                    string def = (itemKey ?? "").Split('|')[0];
                    string what = returned > 0 ? $"{returned}x {Features.ItemLabels.ItemLabelCache.LabelFor(def) ?? def}" : "nothing left to return";
                    Features.Notifications.KmhNotify.ToUser(sellerUsername, "neutral", "Listing expired",
                        $"Your marketplace listing #{id} expired - {what} went back to your treasury.");
                }
            }
            if (marketplaceChanged) Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
        }

        private static void SweepQuests(long now)
        {
            // Roll forward first: a delivery whose goods already moved must complete, not expire.
            bool questsChanged = Features.Quests.QuestStore.CompleteHandedOverDeliveries() > 0;
            Features.Quests.QuestStore.RetryOwed();
            // Before expiry, so a released submission whose quest is also overdue is refunded in the same pass.
            foreach (var (id, claimer, poster) in Features.Quests.QuestStore.ReleaseStaleReviews(now))
            {
                questsChanged = true;
                ServerLog.Info($"ExpirySweeper: quest #{id} was not reviewed in time - reopened (claim by {claimer} released).");
                Features.Notifications.KmhNotify.ToUser(claimer, "neutral", "Quest reopened",
                    $"Quest #{id} waited too long for its poster's review and is open again. Nothing was paid; an admin can settle it.");
                Features.Notifications.KmhNotify.ToUser(poster, "neutral", "Quest reopened",
                    $"You did not review quest #{id} in time, so it is open again and your bounty is still escrowed.");
            }
            List<long> expiredQuests = Features.Quests.QuestStore.CollectExpiredOpenIds(now);
            foreach (long id in expiredQuests)
            {
                // Read first: an expired quest leaves the board, and the notices still have to name it and its claimer.
                var q = Features.Quests.QuestStore.PartiesOf(id);
                if (Features.Quests.QuestStore.ExpireQuest(id, out string posterAffected))
                {
                    questsChanged = true;
                    ServerLog.Info($"ExpirySweeper: quest #{id} expired (bounty refunded to {posterAffected})");
                    PushTreasuryTo(posterAffected);
                    Features.Quests.QuestHandler.NoticeExpired(id, q.Title, posterAffected, q.Claimer);
                }
            }
            if (Features.Quests.QuestStore.PruneFinalized(now)) questsChanged = true;
            if (questsChanged) Features.Quests.QuestHandler.BroadcastSnapshot();
        }

        private static void SweepSiteProduction()
        {
            Features.Sites.SiteStore.RetryOwed();
            Features.Roadworks.RoadworksStore.RetryOwed();
            HashSet<string> sitePaid = Features.Sites.SiteStore.RunRewardCycle();
            if (sitePaid.Count > 0)
            {
                foreach (string u in sitePaid) PushTreasuryTo(u);
                Features.Sites.SiteHandler.BroadcastSnapshot();
            }
        }

        private static void SweepAuctions(long now)
        {
            bool auctionsChanged = false;
            foreach (long id in Features.Auctions.AuctionStore.CollectEndedIds(now))
            {
                Features.Auctions.AuctionStore.SettleOutcome o = Features.Auctions.AuctionStore.SettleNow(id);
                if (!o.Done) continue;
                auctionsChanged = true;
                foreach (string u in o.Affected) PushTreasuryTo(u);
                Features.Auctions.AuctionHandler.NotifySettled(o);
            }
            if (auctionsChanged) Features.Auctions.AuctionHandler.BroadcastSnapshot();
        }

        private static void SweepWants(long now)
        {
            bool wantsChanged = false;
            foreach (long id in Features.WantBoard.WantStore.CollectEndedIds(now))
            {
                Features.WantBoard.WantStore.ExpireOutcome o = Features.WantBoard.WantStore.ExpireRefund(id);
                if (!o.Done) continue;
                wantsChanged = true;
                PushTreasuryTo(o.Buyer);
                if (o.Refunded > 0)
                    Features.Notifications.KmhNotify.ToUser(o.Buyer, "neutral", "Want expired",
                        $"Your want expired - {Util.SilverFmt.Format(o.Refunded)} of unspent escrow was refunded to your treasury.");
            }
            if (wantsChanged) Features.WantBoard.WantHandler.BroadcastSnapshot();
        }

        // Online owners will still save and confirm, so only an abandoned deposit is allowed to time out.
        private static void RevertStalePendingDeposits()
        {
            HashSet<string> online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (!string.IsNullOrEmpty(u)) online.Add(u);
            }
            int revertedPending = Features.Treasury.TreasuryStore.SweepStalePendingDeposits(
                Features.Economy.EconomyConfig.Current.Treasury.PendingTimeoutMinutes,
                u => online.Contains(u));
            if (revertedPending > 0)
                ServerLog.Info($"ExpirySweeper: reverted {revertedPending} stale pending deposit(s) (unconfirmed past timeout).");
        }

        private static void SweepMail(long now)
        {
            Features.Mail.MailStore.PruneOld(now);
            Features.Mail.MailHandler.SweepUnclaimedAttachments(now);
        }

        private static void PushTreasuryTo(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            Features.Treasury.Dto.TreasurySnapshot snapshot =
                Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendToUsername(username, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
