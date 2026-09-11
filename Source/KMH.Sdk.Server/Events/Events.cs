namespace KMH.Sdk.Server.Events
{
    /// <summary>Event payloads KMH never mutates after raising, so a handler may keep a reference.</summary>

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
        public ulong  DiscordId              { get; init; }   // 0 for legacy links that never stored a snowflake
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
        public string Reason        { get; init; } = "";   // built / worker_joined / worker_left / removed / reward / road_started / road_cancelled / road_segment
        public string PreviousController { get; init; } = "";   // set on a capture; blank otherwise
        public long   OperationId        { get; init; } = 0;
    }

    public sealed class AuctionPostedEvent
    {
        public long   AuctionId      { get; init; }
        public string SellerUsername { get; init; } = "";
        public string ItemDefName    { get; init; } = "";
        public int    Qty            { get; init; }
        public long   StartingBid    { get; init; }
        public long   BuyoutSilver   { get; init; }
        public string Visibility     { get; init; } = "public";
    }

    public sealed class AuctionBidEvent
    {
        public long   AuctionId       { get; init; }
        public string BidderUsername  { get; init; } = "";
        public long   Amount          { get; init; }
        public string OutbidUsername  { get; init; } = "";   // previous high bidder we refunded, empty if first bid
    }

    public sealed class AuctionSettledEvent
    {
        public long   AuctionId      { get; init; }
        public bool   Sold           { get; init; }          // false = ended with no bids, item returned to seller
        public string WinnerUsername { get; init; } = "";
        public string SellerUsername { get; init; } = "";
        public string ItemDefName    { get; init; } = "";
        public int    Qty            { get; init; }
        public long   FinalBid       { get; init; }
        public long   SellerNet      { get; init; }          // bid minus house tax
    }

    public sealed class WorldEventFiredEvent
    {
        public string Type         { get; init; } = "";
        public string Title        { get; init; } = "";
        public double Magnitude    { get; init; }
        public string Target       { get; init; } = "";
        public long   EndsUtcTicks { get; init; }            // 0 = instantaneous
    }

    public sealed class WorldEventEndedEvent
    {
        public string Type  { get; init; } = "";
        public string Title { get; init; } = "";
    }

    public sealed class GlobalQuestCreatedEvent
    {
        public long   QuestId       { get; init; }
        public string Kind          { get; init; } = "";   // cooperative / competitive
        public string Objective     { get; init; } = "";   // hunt / build / deliver
        public string TargetDefName { get; init; } = "";
        public int    GoalQty       { get; init; }
        public long   RewardPool    { get; init; }
    }

    public sealed class GlobalQuestCompletedEvent
    {
        public long   QuestId          { get; init; }
        public string Kind             { get; init; } = "";
        public string Objective        { get; init; } = "";
        public string Winner           { get; init; } = "";   // competitive only, empty for cooperative
        public int    ContributorCount { get; init; }
        public long   RewardPool       { get; init; }
    }

    public sealed class GlobalQuestExpiredEvent
    {
        public long   QuestId { get; init; }
        public string Title   { get; init; } = "";
    }

    public sealed class BackupCreatedEvent
    {
        public string Name   { get; init; } = "";   // backup folder name (yyyyMMdd-HHmmss-<reason>)
        public string Utc    { get; init; } = "";   // ISO-8601, parsed from the name
        public string Reason { get; init; } = "";   // "boot" / "manual" / "pre-season-reset" / "pre-restore" / ...
    }

    public sealed class RestoreAppliedEvent
    {
        public string BackupName   { get; init; } = "";   // the backup KMH-Data was rolled back to
        public string SafetyBackup { get; init; } = "";   // snapshot of the pre-restore state (empty if it failed)
    }

    /// <summary>A versioned KMH snapshot was written under KMH-Data/Snapshots/.</summary>
    public sealed class SnapshotCreatedEvent
    {
        public string Kind           { get; init; } = "";   // "player" | "server"
        public string PlayerId       { get; init; } = "";   // empty for server snapshots
        public string Season         { get; init; } = "";   // "S6", ...
        public string MatchTimestamp { get; init; } = "";   // the YYYY-MM-DD_HH-MM folder name
        public string Dir            { get; init; } = "";   // absolute snapshot folder (snapshot + manifest inside)
    }

    public sealed class SeasonRolledEvent
    {
        public int    RolledSeason { get; init; }         // the season just archived
        public int    NewSeason    { get; init; }         // the season now current
        public int    RecordCount  { get; init; }         // leader records archived
        public string Actor        { get; init; } = "";
        public bool   EconomyWiped { get; init; }         // true when raised by a full 'season reset', false for 'roll'
    }

    /// <summary>A player-to-player mail was accepted and delivered. Attachment totals are what actually escrowed.</summary>
    public sealed class MailSentEvent
    {
        public long   MailId          { get; init; }
        public string FromUsername    { get; init; } = "";
        public string ToUsername      { get; init; } = "";
        public long   AttachedSilver  { get; init; }
        public int    AttachedItems   { get; init; }      // total units across compact stacks
        public int    AttachedGear    { get; init; }      // full-state payloads
    }

    /// <summary>
    /// A chat line was accepted. Channel is "server", "guild:&lt;name&gt;" or "dm:&lt;lo&gt;|&lt;hi&gt;", and Origin
    /// is "ingame" or "discord". Private bodies are included, as they already are in the owner's own chat log.
    /// </summary>
    public sealed class ChatMessagePostedEvent
    {
        public long   MessageId    { get; init; }
        public string Channel      { get; init; } = "";
        public string FromUsername { get; init; } = "";
        public string Body         { get; init; } = "";
        public string Origin       { get; init; } = "";
    }
}
