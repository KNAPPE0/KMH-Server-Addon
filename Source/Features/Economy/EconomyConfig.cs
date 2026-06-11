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
        // Marketplace house tax (0..50). Skimmed from each sale into the house silver pool; reduced per seller by
        // their guild's MarketplaceTaxReduction perk
        public int MarketplaceTaxPercent { get; set; } = 5;

        // Hours an unsold listing lives before the sweeper refunds remaining stock to the seller's treasury. Used
        // when a post doesn't specify its own expiry
        public int MarketplaceListingLifetimeHours { get; set; } = 168; // 7 days

        // Hard cap on simultaneous open listings per seller - anti-spam.
        public int MarketplaceMaxOpenListingsPerUser { get; set; } = 25;

        // Silver-per-unit floor and ceiling for any listing - anti-flooding + overflow guard
        public int MarketplaceMinUnitPrice { get; set; } = 1;

        public int MarketplaceMaxUnitPrice { get; set; } = 100_000;

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
            MarketplaceListingLifetimeHours   = Clamp(MarketplaceListingLifetimeHours, 1, 24 * 365);
            MarketplaceMaxOpenListingsPerUser = Clamp(MarketplaceMaxOpenListingsPerUser, 1, 10_000);
            MarketplaceMinUnitPrice           = Clamp(MarketplaceMinUnitPrice, 1, 1_000_000);
            MarketplaceMaxUnitPrice           = Clamp(MarketplaceMaxUnitPrice, MarketplaceMinUnitPrice, 1_000_000_000);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
