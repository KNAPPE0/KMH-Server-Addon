namespace KMH.Sdk.Server.Events
{
    // Immutable event payloads. Always passed by reference but never mutated - handler code is welcome to
    // capture-and-stash these without worrying about KMH writing into them later

    public sealed class PlayerJoinedEvent
    {
        public string Username { get; init; } = "";
        public string Ip       { get; init; } = "";
    }

    public sealed class PlayerLeftEvent
    {
        public string Username { get; init; } = "";
    }

    public sealed class PlayerLinkedEvent
    {
        public string Username       { get; init; } = "";
        public string DiscordDisplay { get; init; } = "";
        public ulong  DiscordId      { get; init; }
    }

    public sealed class PlayerUnlinkedEvent
    {
        public string Username               { get; init; } = "";
        public string PreviousDiscordDisplay { get; init; } = "";
    }

    public sealed class MarketplacePostEvent
    {
        public long   ListingId       { get; init; }
        public string SellerUsername  { get; init; } = "";
        public string ItemDefName     { get; init; } = "";
        public int    Qty             { get; init; }
        public int    UnitPriceSilver { get; init; }
        public string Visibility      { get; init; } = "public";
    }

    public sealed class MarketplaceBuyEvent
    {
        public long   ListingId       { get; init; }
        public string BuyerUsername   { get; init; } = "";
        public string SellerUsername  { get; init; } = "";
        public string ItemDefName     { get; init; } = "";
        public int    QtyBought       { get; init; }
        public int    TotalSilverPaid { get; init; }
    }

    public sealed class MarketplaceCancelEvent
    {
        public long   ListingId      { get; init; }
        public string SellerUsername { get; init; } = "";
        public int    RemainingQty   { get; init; }
    }

    public sealed class QuestPostedEvent
    {
        public long   QuestId         { get; init; }
        public string Kind            { get; init; } = "";
        public string PosterUsername  { get; init; } = "";
        public int    BountySilver    { get; init; }
    }

    public sealed class QuestClaimedEvent
    {
        public long   QuestId           { get; init; }
        public string ClaimerUsername   { get; init; } = "";
        public string PosterUsername    { get; init; } = "";
    }

    public sealed class QuestSubmittedEvent
    {
        public long   QuestId          { get; init; }
        public string ClaimerUsername  { get; init; } = "";
        public bool   AutoCompleted    { get; init; }   // true for DeliverItem on success; false for Bounty (awaiting Approve)
    }

    public sealed class QuestApprovedEvent
    {
        public long   QuestId           { get; init; }
        public string PosterUsername    { get; init; } = "";
        public string ClaimerUsername   { get; init; } = "";
        public int    BountyPaidSilver  { get; init; }
    }

    public sealed class QuestCancelledEvent
    {
        public long   QuestId           { get; init; }
        public string PosterUsername    { get; init; } = "";
        public bool   ExpiredAutomatically { get; init; }
    }

    public sealed class TreasuryChangedEvent
    {
        public string OwnerKey     { get; init; } = "";   // "_personal:<user>" or "<guild_name>"
        public bool   IsGuildOwned { get; init; }
        public string Reason       { get; init; } = "";   // free-form note ("marketplace_sale", "deposit", etc.)
    }

    public sealed class GuildChangedEvent
    {
        public string GuildName { get; init; } = "";
        public string Reason    { get; init; } = "";   // "created" / "member_joined" / "member_left" / "perk" / "settings" / "treasury" / ...
        public string Actor     { get; init; } = "";   // username that triggered the change, when known
    }

    public sealed class ReputationChangedEvent
    {
        public string Username { get; init; } = "";
        public int    Score    { get; init; }
        public string Tier     { get; init; } = "Neutral";
    }

    public sealed class SiteChangedEvent
    {
        public int    Tile          { get; init; }
        public string OwnerUsername { get; init; } = "";
        public string Reason        { get; init; } = "";   // "built" / "worker_joined" / "worker_left" / "removed" / "reward"
    }
}
