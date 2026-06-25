using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    // Auction house API for timed bidding on treasury items.
    public interface IAuctionApi
    {
        // Auction API.
        IReadOnlyList<AuctionRecord> GetOpenAuctions(string callerUsername);

        // Posts an auction and escrows the seller's item; returns auction id or 0 on failure.
        long Post(string sellerUsername, string itemDefName, string stuffDefName, int quality, int qty,
                  long startingBid, long minIncrement, long buyoutSilver, int durationHours,
                  string visibility = "public");

        // Places a bid, escrows silver, refunds the previous high bidder, and settles on buyout.
        bool Bid(string bidderUsername, long auctionId, long amount, out string reason);

        // Cancels an unbid auction and returns the item to the seller.
        bool Cancel(string sellerUsername, long auctionId, out string reason);
    }
}
