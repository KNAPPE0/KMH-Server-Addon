namespace KMH.Sdk.Server.Hooks
{
    /// <summary>
    /// A player is trying to create a marketplace listing. It has passed KMH's own validation and nothing is
    /// escrowed yet, so denying is side-effect-free.
    /// </summary>
    public sealed class KmhMarketplaceListingContext
    {
        /// <summary>Seller's KMH username.</summary>
        public string Seller { get; }

        /// <summary>defName of the item being listed.</summary>
        public string ItemDefName { get; }

        /// <summary>Number of units in the listing.</summary>
        public int Quantity { get; }

        /// <summary>Asking price per unit, in silver (may be fractional).</summary>
        public double UnitPriceSilver { get; }

        public KmhMarketplaceListingContext(string seller, string itemDefName, int quantity, double unitPriceSilver)
        {
            Seller          = seller ?? "";
            ItemDefName     = itemDefName ?? "";
            Quantity        = quantity;
            UnitPriceSilver = unitPriceSilver;
        }
    }

    /// <summary>A listing is about to be shown to <see cref="Viewer"/>. Deny to hide it from that player.</summary>
    /// <remarks>
    /// This runs once per listing per viewer, so it must be cheap - no I/O, no network, no locks. It is also
    /// consulted before a buy, so hiding a listing prevents purchasing it rather than only removing it from the board.
    /// <para>
    /// Registering any visibility hook turns off snapshot sharing between players: KMH cannot know a hook answers
    /// the same way for two viewers, so it fails closed and builds a snapshot per recipient.
    /// </para>
    /// </remarks>
    public sealed class KmhMarketplaceVisibilityContext
    {
        /// <summary>The player the listing would be shown to.</summary>
        public string Viewer { get; }
        /// <summary>Listing id.</summary>
        public long ListingId { get; }
        /// <summary>Seller's KMH username.</summary>
        public string Seller { get; }
        /// <summary>defName of the listed item.</summary>
        public string ItemDefName { get; }

        public KmhMarketplaceVisibilityContext(string viewer, long listingId, string seller, string itemDefName)
        {
            Viewer = viewer ?? ""; ListingId = listingId; Seller = seller ?? ""; ItemDefName = itemDefName ?? "";
        }
    }

    /// <summary>A player is trying to start an auction. Passed KMH validation; nothing is escrowed yet.</summary>
    public sealed class KmhAuctionListingContext
    {
        /// <summary>Seller's KMH username.</summary>
        public string Seller { get; }
        /// <summary>defName of the item being auctioned.</summary>
        public string ItemDefName { get; }
        /// <summary>Number of units in the auction.</summary>
        public int Quantity { get; }
        /// <summary>Opening bid, in silver.</summary>
        public long StartingBidSilver { get; }

        public KmhAuctionListingContext(string seller, string itemDefName, int quantity, long startingBidSilver)
        {
            Seller = seller ?? ""; ItemDefName = itemDefName ?? ""; Quantity = quantity; StartingBidSilver = startingBidSilver;
        }
    }

    /// <summary>A player is trying to post a want-board buy request. Passed KMH validation; nothing is escrowed yet.</summary>
    public sealed class KmhWantContext
    {
        /// <summary>Buyer's KMH username.</summary>
        public string Buyer { get; }
        /// <summary>defName of the item being requested.</summary>
        public string ItemDefName { get; }
        /// <summary>Number of units wanted.</summary>
        public int Quantity { get; }
        /// <summary>Offered price per unit, in silver.</summary>
        public int UnitPriceSilver { get; }

        public KmhWantContext(string buyer, string itemDefName, int quantity, int unitPriceSilver)
        {
            Buyer = buyer ?? ""; ItemDefName = itemDefName ?? ""; Quantity = quantity; UnitPriceSilver = unitPriceSilver;
        }
    }

    /// <summary>A player is trying to post a quest/bounty. Passed KMH validation; nothing is escrowed yet.</summary>
    public sealed class KmhQuestPostContext
    {
        /// <summary>Poster's KMH username.</summary>
        public string Poster { get; }
        /// <summary>Quest kind (e.g. deliver_item, bounty).</summary>
        public string Kind { get; }
        /// <summary>Quest title (already trimmed/capped by KMH).</summary>
        public string Title { get; }
        /// <summary>Bounty silver offered.</summary>
        public int BountySilver { get; }

        public KmhQuestPostContext(string poster, string kind, string title, int bountySilver)
        {
            Poster = poster ?? ""; Kind = kind ?? ""; Title = title ?? ""; BountySilver = bountySilver;
        }
    }

    /// <summary>
    /// A player is trying to withdraw from their treasury to their colony. Only player-initiated withdrawals reach
    /// a hook - KMH's internal escrow moves never do - and nothing has left the vault, so denying is side-effect-free.
    /// </summary>
    public sealed class KmhTreasuryWithdrawContext
    {
        /// <summary>Player's KMH username.</summary>
        public string Username { get; }
        /// <summary>True for an item withdrawal, false for silver.</summary>
        public bool IsItem { get; }
        /// <summary>defName of the item (empty for a silver withdrawal).</summary>
        public string ItemDefName { get; }
        /// <summary>Units for an item withdrawal (0 for silver).</summary>
        public int Quantity { get; }
        /// <summary>Silver for a silver withdrawal (0 for items).</summary>
        public int SilverAmount { get; }

        public KmhTreasuryWithdrawContext(string username, bool isItem, string itemDefName, int quantity, int silverAmount)
        {
            Username = username ?? ""; IsItem = isItem; ItemDefName = itemDefName ?? ""; Quantity = quantity; SilverAmount = silverAmount;
        }
    }
}
