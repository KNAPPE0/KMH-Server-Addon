using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhConfigFieldMigrationSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Carries both a whole-silver int and its *Milli pair, so the fixture can prove which one wins.
            string withMilli = "{\"MarketplaceMinUnitPrice\":1,\"MarketplaceMaxUnitPrice\":100000," +
                               "\"MarketplaceMinUnitPriceMilli\":550,\"MarketplaceMaxUnitPriceMilli\":100000000," +
                               "\"MarketplaceListingFeePercent\":0.0,\"RequireSyncedLocalSaveForHighRiskEconomy\":false," +
                               "\"MarketplaceMinPercentOfTrustedValue\":0.02,\"MarketplaceWarnBelowTrustedValuePercent\":0.25}";
            string outA = KmhConfigFieldMigrations.MigrateEconomyV3(withMilli, out int cA);
            JObject a = JObject.Parse(outA);
            bool aOk = cA == 6
                    && (double)a["MarketplaceMinUnitPrice"] == 0.55       // 550 milli -> 0.55
                    && (double)a["MarketplaceMaxUnitPrice"] == 100000.0   // 100000000 milli -> 100000
                    && a["MarketplaceMinUnitPriceMilli"] == null
                    && a["MarketplaceMaxUnitPriceMilli"] == null
                    && a["MarketplaceListingFeePercent"] == null
                    && a["RequireSyncedLocalSaveForHighRiskEconomy"] == null
                    && (double)a["MarketplaceMinPercentOfTrustedValue"] == 2.0      // 0.02 fraction -> 2%
                    && (double)a["MarketplaceWarnBelowTrustedValuePercent"] == 25.0; // 0.25 -> 25%
            r.Add(("ConfigMig: milli + dead keys + percent", aOk, $"changed {cA}, min% {a["MarketplaceMinPercentOfTrustedValue"]}"));

            string alreadyPct = "{\"MarketplaceMinPercentOfTrustedValue\":2,\"MarketplaceWarnBelowTrustedValuePercent\":0}";
            KmhConfigFieldMigrations.MigrateEconomyV3(alreadyPct, out int cPct);
            r.Add(("ConfigMig: percent already scaled -> no-op", cPct == 0, $"changed {cPct}"));

            KmhConfigFieldMigrations.MigrateEconomyV3(outA, out int cB);
            r.Add(("ConfigMig: idempotent re-run", cB == 0, $"changed {cB}"));

            string preDecimal = "{\"MarketplaceMinUnitPrice\":2,\"MarketplaceMaxUnitPrice\":50000}";
            string outC = KmhConfigFieldMigrations.MigrateEconomyV3(preDecimal, out int cC);
            JObject c = JObject.Parse(outC);
            bool cOk = cC == 0 && (int)c["MarketplaceMinUnitPrice"] == 2 && (int)c["MarketplaceMaxUnitPrice"] == 50000;
            r.Add(("ConfigMig: pre-decimal preserved", cOk, $"changed {cC}"));

            string oneSide = "{\"MarketplaceMinUnitPriceMilli\":10}";
            KmhConfigFieldMigrations.MigrateEconomyV3(oneSide, out int cD);
            r.Add(("ConfigMig: single milli field", cD == 1, $"changed {cD}"));

            KmhConfigFieldMigrations.MigrateEconomyV3("{ not json", out int cE);
            KmhConfigFieldMigrations.MigrateEconomyV3("", out int cF);
            r.Add(("ConfigMig: malformed/empty safe", cE == 0 && cF == 0, ""));

            var cfg = new Features.Economy.EconomyConfig { MarketplaceMinUnitPrice = 0.55, MarketplaceMaxUnitPrice = 100000 };
            bool milliOk = cfg.MarketplaceMinUnitPriceMilli == 550 && cfg.MarketplaceMaxUnitPriceMilli == 100000000;
            r.Add(("ConfigMig: decimal -> milli accessor", milliOk,
                   $"{cfg.MarketplaceMinUnitPriceMilli}/{cfg.MarketplaceMaxUnitPriceMilli}"));

            // The obsolete-key prune runs first on a real boot, so without alias protection it strips *Milli before the merge reads it.
            string bothFields = "{\"MarketplaceMinUnitPrice\":1,\"MarketplaceMinUnitPriceMilli\":10,\"MarketplaceMaxUnitPrice\":100000}";
            var econDefaults = new Features.Economy.EconomyConfig();
            double keptMin = (double)JObject.Parse(KmhConfigFieldMigrations.MigrateEconomyV3(
                Persistence.JsonFileStore.PruneUnknownJson(bothFields, econDefaults, Features.Economy.EconomyConfig.LegacyAliasKeys, out _), out _))
                ["MarketplaceMinUnitPrice"];
            double lostMin = (double)JObject.Parse(KmhConfigFieldMigrations.MigrateEconomyV3(
                Persistence.JsonFileStore.PruneUnknownJson(bothFields, econDefaults, null, out _), out _))
                ["MarketplaceMinUnitPrice"];
            r.Add(("ConfigMig: aliased prune keeps milli so sub-1 price survives upgrade",
                   keptMin == 0.01 && lostMin == 1.0, $"aliased={keptMin} unaliased={lostMin}"));

            // The shape that quarantined a live Economy.json: a decimal in a whole-milli field, now parsed because raw migration runs first.
            string historical = "{\"MarketplaceMinUnitPriceMilli\":0.1,\"MarketplaceMaxUnitPriceMilli\":100000000," +
                                "\"MarketplaceListingLifetimeHours\":72,\"MarketplaceMaxOpenListingsPerUser\":9}";
            var defaults = new Features.Economy.EconomyConfig();
            string pruned = Persistence.JsonFileStore.PruneUnknownJson(
                historical, defaults, Features.Economy.EconomyConfig.LegacyAliasKeys, out _);
            string migrated = KmhConfigFieldMigrations.MigrateEconomyV3(pruned, out int cHist);
            bool parses = false, kept = false, noMilli = false;
            try
            {
                Features.Economy.EconomyConfig loaded = Persistence.JsonFileStore.FromJson<Features.Economy.EconomyConfig>(migrated);
                parses  = loaded != null;
                // Owner settings alongside the bad field must survive: losing them is what the quarantine did.
                kept    = loaded != null && loaded.MarketplaceListingLifetimeHours == 72 && loaded.MarketplaceMaxOpenListingsPerUser == 9;
                noMilli = JObject.Parse(migrated)["MarketplaceMinUnitPriceMilli"] == null;
            }
            catch { }
            r.Add(("ConfigMig: the historical decimal-in-milli file parses instead of being quarantined",
                   parses && kept && noMilli, $"changed {cHist}, parses={parses}, ownerSettingsKept={kept}, milliRemoved={noMilli}"));

            // 0.1 milli is 0.0001 silver; reading the field as a whole number first would zero the owner's intent.
            double migratedMin = (double)JObject.Parse(migrated)["MarketplaceMinUnitPrice"];
            r.Add(("ConfigMig: a fractional milli value keeps its meaning through the conversion",
                   Math.Abs(migratedMin - 0.0001) < 1e-9, $"{migratedMin:0.#####} silver"));

            // A second boot must be a no-op, or every restart rewrites the file and logs a migration that did nothing.
            KmhConfigFieldMigrations.MigrateEconomyV3(migrated, out int cHist2);
            r.Add(("ConfigMig: the historical file is idempotent on the second boot", cHist2 == 0, $"changed {cHist2}"));

            // Every value the clamp accepts has to be a price a listing can actually be posted at.
            var histCfg = Persistence.JsonFileStore.FromJson<Features.Economy.EconomyConfig>(migrated);
            Features.Economy.EconomyConfig.ClampForTest(histCfg);
            r.Add(("ConfigMig: a degenerate migrated price clamps to a usable one",
                   histCfg != null && histCfg.MarketplaceMinUnitPriceMilli >= 1
                   && histCfg.MarketplaceMinUnitPrice <= histCfg.MarketplaceMaxUnitPrice,
                   $"min {histCfg?.MarketplaceMinUnitPrice:0.###} ({histCfg?.MarketplaceMinUnitPriceMilli} milli), max {histCfg?.MarketplaceMaxUnitPrice:0.###}"));

            return r;
        }
    }
}
