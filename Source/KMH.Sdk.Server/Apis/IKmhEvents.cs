using System;
using KMH.Sdk.Server.Events;

namespace KMH.Sdk.Server.Apis
{
    // Subscribes to KMH lifecycle/feature events; handlers run synchronously, throw-safe, and should queue long work.
    public interface IKmhEvents
    {
        // Player/account lifecycle.
        event Action<PlayerJoinedEvent> PlayerJoined;
        event Action<PlayerLeftEvent> PlayerLeft;
        event Action<PlayerLinkedEvent> PlayerLinked;
        event Action<PlayerUnlinkedEvent> PlayerUnlinked;

        // Marketplace.
        event Action<MarketplacePostEvent> MarketplacePost;
        event Action<MarketplaceBuyEvent> MarketplaceBuy;
        event Action<MarketplaceCancelEvent> MarketplaceCancel;

        // Player quests.
        event Action<QuestPostedEvent> QuestPosted;
        event Action<QuestClaimedEvent> QuestClaimed;
        event Action<QuestSubmittedEvent> QuestSubmitted;
        event Action<QuestApprovedEvent> QuestApproved;
        event Action<QuestCancelledEvent> QuestCancelled;

        // Economy/community state.
        event Action<TreasuryChangedEvent> TreasuryChanged;
        event Action<GuildChangedEvent> GuildChanged;
        event Action<ReputationChangedEvent> ReputationChanged;
        event Action<SiteChangedEvent> SiteChanged;

        // Auctions.
        event Action<AuctionPostedEvent> AuctionPosted;
        event Action<AuctionBidEvent> AuctionBid;
        event Action<AuctionSettledEvent> AuctionSettled;

        // World Engine.
        event Action<WorldEventFiredEvent> WorldEventFired;
        event Action<WorldEventEndedEvent> WorldEventEnded;
        event Action<GlobalQuestCreatedEvent> GlobalQuestCreated;
        event Action<GlobalQuestCompletedEvent> GlobalQuestCompleted;
        event Action<GlobalQuestExpiredEvent> GlobalQuestExpired;
    }
}
