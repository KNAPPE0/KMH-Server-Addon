using System.Collections.Generic;

namespace KMH.Sdk.Server.Records
{
    // Immutable SDK record contracts; use mutation APIs to change state, not the returned snapshots.

    /// <summary>Snapshot of one open marketplace listing.</summary>
    public sealed class MarketplaceListingRecord
    {
        public long   Id                { get; init; }
        public string SellerUsername    { get; init; } = "";
        public string SellerTreasuryKey { get; init; } = "";
        public string ItemDefName       { get; init; } = "";
        public int    RemainingQty      { get; init; }
        public int    OriginalQty       { get; init; }
        public int    UnitPriceSilver   { get; init; }
        public long   ListedUtcTicks    { get; init; }
        public long   ExpiresUtcTicks   { get; init; }
        public string Visibility        { get; init; } = "public";
    }

    /// <summary>One treasury recent-transaction entry.</summary>
    public sealed class TreasuryTransactionRecord
    {
        public long   UtcTicks    { get; init; }
        public string Username    { get; init; } = "";
        public string Kind        { get; init; } = "";  // deposit / withdraw / marketplace_sale / ...
        public int    Amount      { get; init; }
        public string ItemDefName { get; init; } = "";  // empty for silver-only tx
        public string Note        { get; init; } = "";
    }

    /// <summary>Snapshot of one quest on the board.</summary>
    public sealed class QuestRecord
    {
        public long   Id                { get; init; }
        public string Kind              { get; init; } = "";  // deliver_item / bounty
        public string State             { get; init; } = "";  // open / claimed / submitted / completed / cancelled / expired
        public string Visibility        { get; init; } = "public";
        public string PosterUsername    { get; init; } = "";
        public string PosterTreasuryKey { get; init; } = "";
        public string ClaimedByUsername { get; init; } = "";
        public string Title             { get; init; } = "";
        public string Description       { get; init; } = "";
        public int    BountySilver      { get; init; }
        public string TargetItemDefName { get; init; } = "";
        public int    TargetItemQty     { get; init; }
        public long   PostedUtcTicks    { get; init; }
        public long   ExpiresUtcTicks   { get; init; }
    }

    /// <summary>Basic guild stats for leaderboards and browsing.</summary>
    public sealed class GuildSummaryRecord
    {
        public string Name           { get; init; } = "";
        public int    MemberCount    { get; init; }
        public long   TreasurySilver { get; init; }
    }

    /// <summary>Player quest reputation score and tier.</summary>
    public sealed class ReputationRecord
    {
        public string Username { get; init; } = "";
        public int    Score    { get; init; }
        public string Tier     { get; init; } = "Neutral";
    }

    /// <summary>Custom site ownership, production type, and live output state.</summary>
    public sealed class SiteRecord
    {
        public int    Tile                  { get; init; }
        public string OwnerUsername         { get; init; } = "";
        public string OwnerGuild            { get; init; } = "";
        public string ItemDefName           { get; init; } = "";
        public int    BaseAmountPerCycle     { get; init; }
        public string AccessMode            { get; init; } = "";    // guild_only / public / private
        public IReadOnlyList<string> Workers { get; init; } = System.Array.Empty<string>();
        public int    MaxWorkers            { get; init; }
        public double ProductionMultiplier  { get; init; }
        public double EffectiveCycleMinutes { get; init; }
        public double TotalSilverGenerated  { get; init; }
    }

    /// <summary>Per-player totals, cumulative on this server until a destructive season reset clears them.</summary>
    public sealed class PlayerStatRecord
    {
        public string Username           { get; init; } = "";
        public long   FirstSeenUtcTicks  { get; init; }
        public long   SilverDonated      { get; init; }
        public long   SalesEarned        { get; init; }
        public int    MarketplaceSales   { get; init; }
        public int    QuestsCompleted    { get; init; }
        public int    QuestsPosted       { get; init; }
        // SitesBuilt and FrontierCaptures are cumulative history; SitesOwned and OutpostsHeld are current ownership.
        public int    SitesBuilt         { get; init; }
        public int    SitesOwned         { get; init; }
        public int    OutpostsHeld       { get; init; }
        public int    FrontierCaptures   { get; init; }
        public long   WorkerXp           { get; init; }
        public long   EconomyScore       { get; init; }
    }

    /// <summary>Open auction snapshot with escrowed item and live bidding.</summary>
    public sealed class AuctionRecord
    {
        public long   Id                { get; init; }
        public string SellerUsername    { get; init; } = "";
        public string SellerTreasuryKey { get; init; } = "";
        public string ItemDefName       { get; init; } = "";
        public string StuffDefName      { get; init; } = "";
        public int    QualityIndex      { get; init; }
        public int    Qty               { get; init; }
        public long   StartingBid       { get; init; }
        public long   MinIncrement      { get; init; }
        public long   BuyoutSilver      { get; init; }   // 0 = no buyout
        public long   CurrentBid        { get; init; }   // 0 = no bids yet
        public string HighBidder        { get; init; } = "";
        public int    BidCount          { get; init; }
        public long   ListedUtcTicks    { get; init; }
        public long   EndsUtcTicks      { get; init; }
        public string Visibility        { get; init; } = "public";
    }

    /// <summary>Live World Engine event; magnitude meaning depends on event type.</summary>
    public sealed class WorldEventRecord
    {
        public long   Id              { get; init; }
        public string Type            { get; init; } = "";   // tax_holiday / market_boom / market_crash / ...
        public string Title           { get; init; } = "";
        public string Description     { get; init; } = "";
        public double Magnitude       { get; init; }
        public string Target          { get; init; } = "";   // optional def the event scopes to
        public long   StartedUtcTicks { get; init; }
        public long   EndsUtcTicks    { get; init; }          // 0 = instantaneous
    }

    /// <summary>Server-owned global quest funded by the house pool, separate from player quests.</summary>
    public sealed class ServerQuestRecord
    {
        public long   Id            { get; init; }
        public string Kind          { get; init; } = "";   // cooperative / competitive
        public string Objective     { get; init; } = "";   // hunt / build / deliver
        public string Title         { get; init; } = "";
        public string Description   { get; init; } = "";
        public string TargetDefName { get; init; } = "";
        public int    GoalQty       { get; init; }
        public int    ProgressQty   { get; init; }
        public long   RewardPool    { get; init; }
        public string State         { get; init; } = "";   // active / completed / expired
        public string Winner        { get; init; } = "";   // competitive only
        public long   EndsUtcTicks  { get; init; }
        public IReadOnlyDictionary<string, int> Contributors { get; init; }
            = new Dictionary<string, int>();
    }
}
