using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read + mutate the KMH marketplace.</summary>
    public interface IMarketplaceApi
    {
        /// <summary>
        /// All open listings visible to the given caller. Guild-only
        /// listings get filtered against the caller's guild
        /// membership. Pass an empty string to see only public
        /// listings (no guild scope).
        /// </summary>
        IReadOnlyList<MarketplaceListingRecord> GetOpenListings(string callerUsername);

        /// <summary>
        /// Create a listing on behalf of <paramref name="sellerUsername"/>.
        /// Returns the new listing id, or 0 if the post fails (treasury
        /// short on items, invalid qty / price, missing defName, etc).
        /// Items are escrowed from the seller's treasury at post time.
        /// </summary>
        long Post(string sellerUsername, string defName, int qty, int unitPrice,
                  string visibility = "public", int expiresHours = 0);

        /// <summary>
        /// Cancel a listing on behalf of <paramref name="callerUsername"/>.
        /// Returns false if the caller doesn't own the listing or it
        /// doesn't exist. Refunds remaining items to caller's treasury.
        /// </summary>
        bool Cancel(string callerUsername, long listingId);

        /// <summary>
        /// Execute a buy on behalf of <paramref name="buyerUsername"/>.
        /// Pulls silver from buyer's treasury, credits seller, moves
        /// items to buyer's treasury. <paramref name="sellerUsername"/>
        /// is set to the seller's username on success (useful for
        /// pushing them a treasury snapshot or firing your own follow-
        /// up event).
        /// </summary>
        bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername);
    }
}
