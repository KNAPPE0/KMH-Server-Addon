using System;

namespace KMH.Sdk.Server.Hooks
{
    /// <summary>Veto hooks that decide whether a pending action is allowed; <c>IKmhEvents</c> only observes.</summary>
    /// <remarks>Run synchronously in registration order, first denial wins; a throwing hook is logged and treated as Allow.
    /// Keep hooks fast and side-effect-free, and do bookkeeping from the matching event.</remarks>
    public interface IKmhHooks
    {
        /// <summary>Decide whether a player may create a marketplace listing.</summary>
        void OnMarketplaceListing(Func<KmhMarketplaceListingContext, KmhHookVerdict> hook);

        /// <summary>Decide whether a listing is shown to a viewer; denying also blocks them buying it.</summary>
        /// <remarks>See <see cref="KmhMarketplaceVisibilityContext"/> for what registering one costs.</remarks>
        void OnMarketplaceVisibility(Func<KmhMarketplaceVisibilityContext, KmhHookVerdict> hook);

        /// <summary>Decide whether a player may start an auction.</summary>
        void OnAuctionListing(Func<KmhAuctionListingContext, KmhHookVerdict> hook);

        /// <summary>Decide whether a player may post a want-board buy request.</summary>
        void OnWant(Func<KmhWantContext, KmhHookVerdict> hook);

        /// <summary>Decide whether a player may post a quest/bounty.</summary>
        void OnQuestPost(Func<KmhQuestPostContext, KmhHookVerdict> hook);

        /// <summary>Decide whether a player may withdraw from their treasury (banking rules). Player-initiated only.</summary>
        void OnTreasuryWithdraw(Func<KmhTreasuryWithdrawContext, KmhHookVerdict> hook);
    }
}
