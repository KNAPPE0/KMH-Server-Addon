using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Sweeps expired marketplace listings and open quests once per minute; fire-and-forget, lightweight, and cancellable for safety.
    internal static class ExpirySweeper
    {
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
        private static Task              _task;
        private static CancellationTokenSource _cts;

        public static void Start()
        {
            if (_task != null) return; // idempotent - already running

            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => RunLoop(_cts.Token));
            ServerLog.Verbose($"ExpirySweeper started (interval {SweepInterval.TotalSeconds:F0}s)");
        }

        // Defensive - not currently called (RWT exits without cleanup), but useful if a future host wants graceful shutdown
        public static void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            _task = null;
        }

        private static async Task RunLoop(CancellationToken ct)
        {
            // Initial delay so the sweeper doesn't fight bootstrap I/O.
            try { await Task.Delay(SweepInterval, ct); }
            catch (TaskCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex)
                {
                    ServerLog.Error("ExpirySweeper tick threw", ex);
                }

                try { await Task.Delay(SweepInterval, ct); }
                catch (TaskCanceledException) { return; }
            }
        }

        // Sweeps expired IDs outside store locks, expires each item, and pushes affected online treasury snapshots.
        private static void Tick()
        {
            long now = DateTime.UtcNow.Ticks;

            // --- marketplace listings ---
            List<long> expiredListings = Features.Marketplace.MarketplaceStore.CollectExpiredIds(now);
            bool marketplaceChanged = false;
            foreach (long id in expiredListings)
            {
                if (Features.Marketplace.MarketplaceStore.ExpireListing(id, out string sellerUsername))
                {
                    marketplaceChanged = true;
                    ServerLog.Info($"ExpirySweeper: marketplace listing #{id} expired (refunded to {sellerUsername})");
                    PushTreasuryTo(sellerUsername);
                }
            }
            if (marketplaceChanged) BroadcastMarketplaceSnapshot();

            // --- open quests ---
            List<long> expiredQuests = Features.Quests.QuestStore.CollectExpiredOpenIds(now);
            bool questsChanged = false;
            foreach (long id in expiredQuests)
            {
                if (Features.Quests.QuestStore.ExpireQuest(id, out string posterAffected))
                {
                    questsChanged = true;
                    ServerLog.Info($"ExpirySweeper: quest #{id} expired (bounty refunded to {posterAffected})");
                    PushTreasuryTo(posterAffected);
                }
            }
            // Drop day-old completed quests so the board doesn't grow forever.
            if (Features.Quests.QuestStore.PruneFinalized(now)) questsChanged = true;
            if (questsChanged) BroadcastQuestSnapshot();

            // --- custom-site production ---
            System.Collections.Generic.HashSet<string> sitePaid = Features.Sites.SiteStore.RunRewardCycle();
            if (sitePaid.Count > 0)
            {
                foreach (string u in sitePaid) PushTreasuryTo(u);
                Features.Sites.SiteHandler.BroadcastSnapshot();
            }

            // --- auctions ---
            bool auctionsChanged = false;
            foreach (long id in Features.Auctions.AuctionStore.CollectEndedIds(now))
            {
                Features.Auctions.AuctionStore.SettleOutcome o = Features.Auctions.AuctionStore.SettleNow(id);
                if (!o.Done) continue;
                auctionsChanged = true;
                foreach (string u in o.Affected) PushTreasuryTo(u);
                Features.Auctions.AuctionHandler.NotifySettled(o);   // won / sold / no-bid notices
            }
            if (auctionsChanged) Features.Auctions.AuctionHandler.BroadcastSnapshot();

            // --- want-to-buy board ---
            bool wantsChanged = false;
            foreach (long id in Features.WantBoard.WantStore.CollectEndedIds(now))
            {
                Features.WantBoard.WantStore.ExpireOutcome o = Features.WantBoard.WantStore.ExpireRefund(id);
                if (!o.Done) continue;
                wantsChanged = true;
                PushTreasuryTo(o.Buyer);
                if (o.Refunded > 0)
                    Features.Notifications.KmhMail.ToUser(o.Buyer, "neutral", "Want expired",
                        $"Your want expired - {Util.SilverFmt.Format(o.Refunded)} of unspent escrow was refunded to your treasury.");
            }
            if (wantsChanged) Features.WantBoard.WantHandler.BroadcastSnapshot();
        }

        private static void PushTreasuryTo(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            Features.Treasury.Dto.TreasurySnapshot snapshot =
                Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendToUsername(username, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }

        private static void BroadcastMarketplaceSnapshot()
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u)) continue;
                Features.Marketplace.Dto.MarketplaceSnapshot s
                    = Features.Marketplace.MarketplaceStore.BuildSnapshot(u);
                KmhRouter.SendTo(c, KmhProtocol.Kind.MarketplaceSnapshot, s);
            }
        }

        private static void BroadcastQuestSnapshot()
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u)) continue;
                Features.Quests.Dto.QuestSnapshot s
                    = Features.Quests.QuestStore.BuildSnapshot(u);
                KmhRouter.SendTo(c, KmhProtocol.Kind.QuestSnapshot, s);
            }
        }
    }
}
