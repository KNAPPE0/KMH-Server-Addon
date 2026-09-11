using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Auction house API for timed bidding on treasury items.</summary>
    public interface IAuctionApi
    {
        /// <summary>Open auctions this caller may see, honouring guild-only visibility.</summary>
        IReadOnlyList<AuctionRecord> GetOpenAuctions(string callerUsername);

        /// <summary>Posts an auction and escrows the seller's item; returns auction id or 0 on failure.</summary>
        long Post(string sellerUsername, string itemDefName, string stuffDefName, int quality, int qty,
                  long startingBid, long minIncrement, long buyoutSilver, int durationHours,
                  string visibility = "public");

        /// <summary>Places a bid, escrows silver, refunds the previous high bidder, and settles on buyout.</summary>
        bool Bid(string bidderUsername, long auctionId, long amount, out string reason);

        /// <summary>Cancels an unbid auction and returns the item to the seller.</summary>
        bool Cancel(string sellerUsername, long auctionId, out string reason);
    }
}
