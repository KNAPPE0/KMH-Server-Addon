using System;

namespace KMH.Sdk.Server.Hooks
{
    /// <summary>
    /// Veto hooks that decide whether a pending action is allowed, where <c>IKmhEvents</c> only observes one.
    /// Reached through <c>IKmhServerHost.Hooks</c>.
    /// </summary>
    /// <remarks>
    /// With no hook registered an action behaves exactly as stock KMH. Several hooks on one action run in
    /// registration order and the first denial wins. A hook that throws is logged and treated as Allow, so a buggy
    /// extension cannot freeze the economy. Hooks run synchronously on the action's own thread, so keep them fast
    /// and side-effect-free and do bookkeeping from the matching event instead.
    /// </remarks>
    public interface IKmhHooks
    {
        /// <summary>Decide whether a player may create a marketplace listing.</summary>
        void OnMarketplaceListing(Func<KmhMarketplaceListingContext, KmhHookVerdict> hook);

        /// <summary>
        /// Decide whether a listing is shown to a given viewer; denying also blocks them buying it. See
        /// <see cref="KmhMarketplaceVisibilityContext"/> for what registering one costs.
        /// </summary>
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
