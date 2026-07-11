using System;
using KMH.Sdk.Server.Apis;
using KMH.Sdk.Server.Events;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Extensibility
{
    // Singleton event hub extensions subscribe to. Each handler runs in a try/catch so one misbehaving extension can't
    // break dispatch for the others.
    internal sealed class KmhEventBus : IKmhEvents
    {
        public static KmhEventBus Instance { get; } = new KmhEventBus();
        private KmhEventBus() { }

        public event Action<PlayerJoinedEvent>     PlayerJoined;
        public event Action<PlayerLeftEvent>       PlayerLeft;
        public event Action<PlayerLinkedEvent>     PlayerLinked;
        public event Action<PlayerUnlinkedEvent>   PlayerUnlinked;
        public event Action<MarketplacePostEvent>  MarketplacePost;
        public event Action<MarketplaceBuyEvent>   MarketplaceBuy;
        public event Action<MarketplaceCancelEvent> MarketplaceCancel;
        public event Action<QuestPostedEvent>      QuestPosted;
        public event Action<QuestClaimedEvent>     QuestClaimed;
        public event Action<QuestSubmittedEvent>   QuestSubmitted;
        public event Action<QuestApprovedEvent>    QuestApproved;
        public event Action<QuestCancelledEvent>   QuestCancelled;
        public event Action<TreasuryChangedEvent>  TreasuryChanged;
        public event Action<GuildChangedEvent>     GuildChanged;
        public event Action<ReputationChangedEvent> ReputationChanged;
        public event Action<SiteChangedEvent>      SiteChanged;
        public event Action<AuctionPostedEvent>    AuctionPosted;
        public event Action<AuctionBidEvent>       AuctionBid;
        public event Action<AuctionSettledEvent>   AuctionSettled;
        public event Action<WorldEventFiredEvent>  WorldEventFired;
        public event Action<WorldEventEndedEvent>  WorldEventEnded;
        public event Action<GlobalQuestCreatedEvent>   GlobalQuestCreated;
        public event Action<GlobalQuestCompletedEvent> GlobalQuestCompleted;
        public event Action<GlobalQuestExpiredEvent>   GlobalQuestExpired;
        public event Action<BackupCreatedEvent>        BackupCreated;
        public event Action<RestoreAppliedEvent>       RestoreApplied;
        public event Action<SnapshotCreatedEvent>      SnapshotCreated;
        public event Action<SeasonRolledEvent>         SeasonRolled;

        // Raise helpers called by KMH internals. Each one walks the delegate chain manually so one throwing
        // subscriber doesn't stop the others - Action<T>.Invoke would short-circuit on first exception
        internal void RaisePlayerJoined    (PlayerJoinedEvent     e) => SafeRaise(PlayerJoined,     e, nameof(PlayerJoined));
        internal void RaisePlayerLeft      (PlayerLeftEvent       e) => SafeRaise(PlayerLeft,       e, nameof(PlayerLeft));
        internal void RaisePlayerLinked    (PlayerLinkedEvent     e) => SafeRaise(PlayerLinked,     e, nameof(PlayerLinked));
        internal void RaisePlayerUnlinked  (PlayerUnlinkedEvent   e) => SafeRaise(PlayerUnlinked,   e, nameof(PlayerUnlinked));
        internal void RaiseMarketplacePost (MarketplacePostEvent  e) => SafeRaise(MarketplacePost,  e, nameof(MarketplacePost));
        internal void RaiseMarketplaceBuy  (MarketplaceBuyEvent   e) => SafeRaise(MarketplaceBuy,   e, nameof(MarketplaceBuy));
        internal void RaiseMarketplaceCancel(MarketplaceCancelEvent e) => SafeRaise(MarketplaceCancel, e, nameof(MarketplaceCancel));
        internal void RaiseQuestPosted     (QuestPostedEvent      e) => SafeRaise(QuestPosted,     e, nameof(QuestPosted));
        internal void RaiseQuestClaimed    (QuestClaimedEvent     e) => SafeRaise(QuestClaimed,    e, nameof(QuestClaimed));
        internal void RaiseQuestSubmitted  (QuestSubmittedEvent   e) => SafeRaise(QuestSubmitted,  e, nameof(QuestSubmitted));
        internal void RaiseQuestApproved   (QuestApprovedEvent    e) => SafeRaise(QuestApproved,   e, nameof(QuestApproved));
        internal void RaiseQuestCancelled  (QuestCancelledEvent   e) => SafeRaise(QuestCancelled,  e, nameof(QuestCancelled));
        internal void RaiseTreasuryChanged (TreasuryChangedEvent  e) => SafeRaise(TreasuryChanged, e, nameof(TreasuryChanged));
        internal void RaiseGuildChanged    (GuildChangedEvent     e) => SafeRaise(GuildChanged,    e, nameof(GuildChanged));
        internal void RaiseReputationChanged(ReputationChangedEvent e) => SafeRaise(ReputationChanged, e, nameof(ReputationChanged));
        internal void RaiseSiteChanged     (SiteChangedEvent      e) => SafeRaise(SiteChanged,     e, nameof(SiteChanged));
        internal void RaiseAuctionPosted   (AuctionPostedEvent    e) => SafeRaise(AuctionPosted,   e, nameof(AuctionPosted));
        internal void RaiseAuctionBid      (AuctionBidEvent       e) => SafeRaise(AuctionBid,      e, nameof(AuctionBid));
        internal void RaiseAuctionSettled  (AuctionSettledEvent   e) => SafeRaise(AuctionSettled,  e, nameof(AuctionSettled));
        internal void RaiseWorldEventFired (WorldEventFiredEvent  e) => SafeRaise(WorldEventFired, e, nameof(WorldEventFired));
        internal void RaiseWorldEventEnded (WorldEventEndedEvent  e) => SafeRaise(WorldEventEnded, e, nameof(WorldEventEnded));
        internal void RaiseGlobalQuestCreated  (GlobalQuestCreatedEvent   e) => SafeRaise(GlobalQuestCreated,   e, nameof(GlobalQuestCreated));
        internal void RaiseGlobalQuestCompleted(GlobalQuestCompletedEvent e) => SafeRaise(GlobalQuestCompleted, e, nameof(GlobalQuestCompleted));
        internal void RaiseGlobalQuestExpired  (GlobalQuestExpiredEvent   e) => SafeRaise(GlobalQuestExpired,   e, nameof(GlobalQuestExpired));
        internal void RaiseBackupCreated       (BackupCreatedEvent        e) => SafeRaise(BackupCreated,        e, nameof(BackupCreated));
        internal void RaiseRestoreApplied      (RestoreAppliedEvent       e) => SafeRaise(RestoreApplied,       e, nameof(RestoreApplied));
        internal void RaiseSnapshotCreated     (SnapshotCreatedEvent      e) => SafeRaise(SnapshotCreated,      e, nameof(SnapshotCreated));
        internal void RaiseSeasonRolled        (SeasonRolledEvent         e) => SafeRaise(SeasonRolled,         e, nameof(SeasonRolled));

        private static void SafeRaise<T>(Action<T> evt, T payload, string name)
        {
            if (evt == null) return;
            foreach (Delegate d in evt.GetInvocationList())
            {
                try { ((Action<T>)d)(payload); }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Extension event subscriber on '{name}' threw: {ex.Message}");
                }
            }
        }
    }
}
