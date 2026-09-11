using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read and mutate the marketplace.</summary>
    public interface IMarketplaceApi
    {
        /// <summary>
        /// Open listings visible to the caller, with guild-only ones filtered against their membership. An empty
        /// caller sees public listings only.
        /// </summary>
        IReadOnlyList<MarketplaceListingRecord> GetOpenListings(string callerUsername);

        /// <summary>
        /// Post a listing, escrowing the items from the seller's treasury. Returns the new listing id, or 0 if the
        /// post fails.
        /// </summary>
        long Post(string sellerUsername, string defName, int qty, int unitPrice,
                  string visibility = "public", int expiresHours = 0);

        /// <summary>
        /// Cancel a listing and refund its remaining items. Returns false if the caller does not own it.
        /// </summary>
        bool Cancel(string callerUsername, long listingId);

        /// <summary>
        /// Buy from a listing, moving silver and items between the two treasuries. On success
        /// <paramref name="sellerUsername"/> names the seller, so you can follow up with them.
        /// </summary>
        bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername);
    }
}
