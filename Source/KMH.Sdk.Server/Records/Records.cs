using System.Collections.Generic;

namespace KMH.Sdk.Server.Records
{
    // Read-only record types returned by the API facades. All fields are init-only so handlers can't mutate the
    // snapshot they were handed. If you need to evolve state, call the corresponding mutation API - don't reach
    // into a record
    //
    // These are intentionally NOT the same types KMH stores internally. The internal store types have wire-DTO
    // concerns that change
    // shape across releases. The records here are the SDK contract

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

    /// <summary>One entry in a treasury's recent-transaction log.</summary>
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

    /// <summary>One guild's basic stats for leaderboards / browsing.</summary>
    public sealed class GuildSummaryRecord
    {
        public string Name           { get; init; } = "";
        public int    MemberCount    { get; init; }
        public long   TreasurySilver { get; init; }
    }

    /// <summary>One player's quest reputation: score + tier (Trusted / Neutral / Unreliable).</summary>
    public sealed class ReputationRecord
    {
        public string Username { get; init; } = "";
        public int    Score    { get; init; }
        public string Tier     { get; init; } = "Neutral";
    }

    /// <summary>One custom site: who owns it, what it makes, and its live production.</summary>
    public sealed class SiteRecord
    {
        public int    Tile                  { get; init; }
        public string OwnerUsername         { get; init; } = "";
        public string OwnerGuild            { get; init; } = "";
        public string ItemDefName           { get; init; } = "";
        public int    BaseAmountPerCycle     { get; init; }
        public string AccessMode            { get; init; } = "";   // guild_only / public / private
        public IReadOnlyList<string> Workers { get; init; } = System.Array.Empty<string>();
        public int    MaxWorkers            { get; init; }
        public double ProductionMultiplier  { get; init; }
        public double EffectiveCycleMinutes { get; init; }
        public double TotalSilverGenerated  { get; init; }
    }

    /// <summary>One player's lifetime stats.</summary>
    public sealed class PlayerStatRecord
    {
        public string Username           { get; init; } = "";
        public long   FirstSeenUtcTicks  { get; init; }
        public long   SilverDonated      { get; init; }
        public long   SalesEarned        { get; init; }
        public int    MarketplaceSales   { get; init; }
        public int    QuestsCompleted    { get; init; }
        public int    QuestsPosted       { get; init; }
        public int    SitesBuilt         { get; init; }
        public long   WorkerXp           { get; init; }
        public long   EconomyScore       { get; init; }
    }
}
