using System;
using KMH.Sdk.Server.Events;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Subscribes to KMH lifecycle/feature events; handlers run synchronously, throw-safe, and should queue long work.</summary>
    public interface IKmhEvents
    {
        event Action<PlayerJoinedEvent> PlayerJoined;
        event Action<PlayerLeftEvent> PlayerLeft;
        event Action<PlayerLinkedEvent> PlayerLinked;
        event Action<PlayerUnlinkedEvent> PlayerUnlinked;

        event Action<MarketplacePostEvent> MarketplacePost;
        event Action<MarketplaceBuyEvent> MarketplaceBuy;
        event Action<MarketplaceCancelEvent> MarketplaceCancel;

        event Action<QuestPostedEvent> QuestPosted;
        event Action<QuestClaimedEvent> QuestClaimed;
        event Action<QuestSubmittedEvent> QuestSubmitted;
        event Action<QuestApprovedEvent> QuestApproved;
        event Action<QuestCancelledEvent> QuestCancelled;

        event Action<TreasuryChangedEvent> TreasuryChanged;
        event Action<GuildChangedEvent> GuildChanged;
        event Action<ReputationChangedEvent> ReputationChanged;
        event Action<SiteChangedEvent> SiteChanged;

        event Action<AuctionPostedEvent> AuctionPosted;
        event Action<AuctionBidEvent> AuctionBid;
        event Action<AuctionSettledEvent> AuctionSettled;

        event Action<WorldEventFiredEvent> WorldEventFired;
        event Action<WorldEventEndedEvent> WorldEventEnded;
        event Action<GlobalQuestCreatedEvent> GlobalQuestCreated;
        event Action<GlobalQuestCompletedEvent> GlobalQuestCompleted;
        event Action<GlobalQuestExpiredEvent> GlobalQuestExpired;

        event Action<BackupCreatedEvent> BackupCreated;
        event Action<RestoreAppliedEvent> RestoreApplied;
        event Action<SnapshotCreatedEvent> SnapshotCreated;
        event Action<SeasonRolledEvent> SeasonRolled;

        /// <summary>Carries private-channel bodies to the owner's own machine; nothing here reaches a player's client.</summary>
        event Action<ChatMessagePostedEvent> ChatMessagePosted;
        event Action<MailSentEvent> MailSent;
    }
}
