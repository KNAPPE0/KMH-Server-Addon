using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read and mutate the marketplace.</summary>
    public interface IMarketplaceApi
    {
        /// <summary>Open listings visible to the caller (guild-only ones by membership); an empty caller sees public only.</summary>
        IReadOnlyList<MarketplaceListingRecord> GetOpenListings(string callerUsername);

        /// <summary>Posts a listing, escrowing the items from the seller's treasury; returns its id, or 0 on failure.</summary>
        long Post(string sellerUsername, string defName, int qty, int unitPrice,
                  string visibility = "public", int expiresHours = 0);

        /// <summary>Cancels a listing and refunds its remaining items; false if the caller does not own it.</summary>
        bool Cancel(string callerUsername, long listingId);

        /// <summary>Buys from a listing, moving silver and items between treasuries.</summary>
        /// <remarks>On success <paramref name="sellerUsername"/> names the seller.</remarks>
        bool Buy(string buyerUsername, long listingId, int qty, out string sellerUsername);
    }
}
