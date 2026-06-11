using System;
using KMH.Sdk.Server.Events;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Subscribe to KMH lifecycle and feature events. All handlers fire
    /// synchronously on the thread the originating action was running
    /// on (usually RWT's chat-receive worker or KMH's expiry sweeper).
    /// Long-running work should be queued to your own background
    /// thread.
    /// </summary>
    /// <remarks>
    /// Subscriber-throw protection: if your handler throws, the
    /// exception is logged via the extension's IServerLog but dispatch
    /// continues to other subscribers. One bad handler can't break
    /// other extensions or KMH itself.
    /// </remarks>
    public interface IKmhEvents
    {
        /// <summary>Fires when a verified client appears (post-handshake login).</summary>
        event Action<PlayerJoinedEvent> PlayerJoined;

        /// <summary>Fires when a client disconnects.</summary>
        event Action<PlayerLeftEvent> PlayerLeft;

        /// <summary>Fires when a Discord identity is bound to an in-game account.</summary>
        event Action<PlayerLinkedEvent> PlayerLinked;

        /// <summary>Fires when a player's Discord link is removed.</summary>
        event Action<PlayerUnlinkedEvent> PlayerUnlinked;

        /// <summary>Fires when a marketplace listing is created.</summary>
        event Action<MarketplacePostEvent> MarketplacePost;

        /// <summary>Fires when a marketplace buy completes (silver + items moved).</summary>
        event Action<MarketplaceBuyEvent> MarketplaceBuy;

        /// <summary>Fires when a seller cancels a listing.</summary>
        event Action<MarketplaceCancelEvent> MarketplaceCancel;

        /// <summary>Fires when a quest is posted.</summary>
        event Action<QuestPostedEvent> QuestPosted;

        /// <summary>Fires when a quest is claimed.</summary>
        event Action<QuestClaimedEvent> QuestClaimed;

        /// <summary>Fires when a quest is submitted (DeliverItem auto-completes; Bounty waits for Approve).</summary>
        event Action<QuestSubmittedEvent> QuestSubmitted;

        /// <summary>Fires when a Bounty quest is approved (poster sign-off + payout).</summary>
        event Action<QuestApprovedEvent> QuestApproved;

        /// <summary>Fires when a quest is cancelled (by poster or by expiry sweeper).</summary>
        event Action<QuestCancelledEvent> QuestCancelled;

        /// <summary>Fires after any treasury mutation. Coarse - every deposit/withdraw triggers one event.</summary>
        event Action<TreasuryChangedEvent> TreasuryChanged;

        /// <summary>Fires after a guild changes (created, membership, perks, settings, MOTD, relationship, or vault).</summary>
        event Action<GuildChangedEvent> GuildChanged;

        /// <summary>Fires after a player's quest reputation score moves.</summary>
        event Action<ReputationChangedEvent> ReputationChanged;

        /// <summary>Fires after a custom site is built, gains/loses a worker, pays a reward, or is removed.</summary>
        event Action<SiteChangedEvent> SiteChanged;
    }
}
