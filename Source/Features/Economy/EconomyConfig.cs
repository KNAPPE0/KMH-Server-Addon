using System;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Economy
{
    // Server economy tuning from KMH-Data/Config/Economy.json (marketplace knobs). Generated with defaults on first
    // boot; values clamped on load so a hand-edit can't push the economy unsafe (negative caps, >100% tax)
    //
    // Cached in Current; call Reload() after editing the file at runtime (wired to /kmh server reload-economy)
    internal sealed class EconomyConfig
    {
        // Schema version for forward-compatible migrations (absent in old files = 1, the baseline). All changes so
        // far are additive, so nothing to migrate yet - this is the anchor a future field rename would key on.
        public int SchemaVersion { get; set; } = 1;

        // Marketplace house tax (0..50). Skimmed from each sale into the house silver pool; reduced per seller by
        // their guild's MarketplaceTaxReduction perk
        public int MarketplaceTaxPercent { get; set; } = 5;

        // Dynamic supply/demand pricing (opt-in, off by default so existing servers are unchanged). When on, the
        // house tax on a sale flexes with the item's live supply (open listings) vs demand (open want-board orders):
        // in-demand goods get a tax rebate (seller keeps more), gluts get a surcharge (more flows to the house pool,
        // which funds global-quest rewards). Closed loop - buyer cost never changes, only the seller/house split.
        public bool DynamicDemandPricingEnabled { get; set; } = false;
        // Max percentage-points the demand swing can move the tax in either direction (clamped to a 0..90 final tax).
        public int  DemandTaxSwingPercent       { get; set; } = 50;

        // Hours an unsold listing lives before the sweeper refunds remaining stock to the seller's treasury. Used
        // when a post doesn't specify its own expiry
        public int MarketplaceListingLifetimeHours { get; set; } = 168; // 7 days

        // Hard cap on simultaneous open listings per seller - anti-spam.
        public int MarketplaceMaxOpenListingsPerUser { get; set; } = 25;

        // Silver-per-unit floor and ceiling for any listing - anti-flooding + overflow guard
        public int MarketplaceMinUnitPrice { get; set; } = 1;

        public int MarketplaceMaxUnitPrice { get; set; } = 100_000;

        // --- auctions ---
        public int AuctionMaxOpenPerUser      { get; set; } = 5;
        public int AuctionDefaultDurationHours { get; set; } = 24;
        public int AuctionMaxDurationHours    { get; set; } = 72;
        public int AuctionAntiSnipeMinutes    { get; set; } = 5;   // a late bid extends the close by this much

        // --- want-to-buy board (buyers escrow silver up front; sellers fulfill from treasury) ---
        public int WantMaxOpenPerUser       { get; set; } = 10;
        public int WantDefaultDurationHours { get; set; } = 72;
        public int WantMaxDurationHours     { get; set; } = 168; // 7 days

        // --- cached accessor ---

        private static EconomyConfig _current;
        public static EconomyConfig Current => _current ?? (_current = LoadOrDefault());

        public static EconomyConfig LoadOrDefault()
        {
            EconomyConfig cfg =
                JsonFileStore.TryLoad(KmhDataPaths.EconomyConfigFile, out EconomyConfig loaded) && loaded != null
                    ? loaded
                    : new EconomyConfig();
            cfg.ClampInPlace();
            return cfg;
        }

        // Generate the file with defaults on first boot so admins have something to edit. No-op if it already
        // exists
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.EconomyConfigFile))
                JsonFileStore.Save(KmhDataPaths.EconomyConfigFile, new EconomyConfig());
        }

        public static void Reload()
        {
            _current = LoadOrDefault();
            Diagnostics.ServerLog.Info(
                $"Economy config reloaded (tax {_current.MarketplaceTaxPercent}%, " +
                $"price {_current.MarketplaceMinUnitPrice}-{_current.MarketplaceMaxUnitPrice}, " +
                $"max {_current.MarketplaceMaxOpenListingsPerUser} listings/user, " +
                $"lifetime {_current.MarketplaceListingLifetimeHours}h)");
        }

        private void ClampInPlace()
        {
            MarketplaceTaxPercent             = Clamp(MarketplaceTaxPercent, 0, 50);
            DemandTaxSwingPercent             = Clamp(DemandTaxSwingPercent, 0, 90);
            MarketplaceListingLifetimeHours   = Clamp(MarketplaceListingLifetimeHours, 1, 24 * 365);
            MarketplaceMaxOpenListingsPerUser = Clamp(MarketplaceMaxOpenListingsPerUser, 1, 10_000);
            MarketplaceMinUnitPrice           = Clamp(MarketplaceMinUnitPrice, 1, 1_000_000);
            MarketplaceMaxUnitPrice           = Clamp(MarketplaceMaxUnitPrice, MarketplaceMinUnitPrice, 1_000_000_000);
            AuctionMaxOpenPerUser             = Clamp(AuctionMaxOpenPerUser, 1, 1_000);
            AuctionDefaultDurationHours       = Clamp(AuctionDefaultDurationHours, 1, 24 * 30);
            AuctionMaxDurationHours           = Clamp(AuctionMaxDurationHours, AuctionDefaultDurationHours, 24 * 30);
            AuctionAntiSnipeMinutes           = Clamp(AuctionAntiSnipeMinutes, 0, 60);
            WantMaxOpenPerUser                = Clamp(WantMaxOpenPerUser, 1, 1_000);
            WantDefaultDurationHours          = Clamp(WantDefaultDurationHours, 1, 24 * 30);
            WantMaxDurationHours              = Clamp(WantMaxDurationHours, WantDefaultDurationHours, 24 * 30);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
