using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // Raw JSON in, raw JSON out, so the transform is unit-tested without disk; KmhConfigMigration does the file I/O.
    internal static class KmhConfigFieldMigrations
    {
        private static readonly string[] EconomyDeadKeys = { "MarketplaceListingFeePercent", "RequireSyncedLocalSaveForHighRiskEconomy" };

        // Named *Percent but stored as 0..1 fractions, so they need rescaling to a true 0..100.
        private static readonly string[] EconomyFractionToPercentKeys =
            { "MarketplaceMinPercentOfTrustedValue", "MarketplaceWarnBelowTrustedValuePercent" };

        public static string MigrateEconomyV3(string json, out int changed)
        {
            changed = 0;
            if (string.IsNullOrWhiteSpace(json)) return json;
            JObject o;
            try { o = JObject.Parse(json); } catch { return json; }   // leave a malformed file for the validator to catch

            changed += MergeMilli(o, "MarketplaceMinUnitPriceMilli", "MarketplaceMinUnitPrice");
            changed += MergeMilli(o, "MarketplaceMaxUnitPriceMilli", "MarketplaceMaxUnitPrice");
            foreach (string dead in EconomyDeadKeys) if (o.Remove(dead)) changed++;
            foreach (string key in EconomyFractionToPercentKeys) changed += FractionToPercent(o, key);

            return changed > 0 ? o.ToString(Formatting.Indented) : json;
        }

        // A value already above 1 is left alone, which is what makes a re-run safe.
        private static int FractionToPercent(JObject o, string key)
        {
            JToken t = o[key];
            if (t == null) return 0;
            double v;
            try { v = t.Value<double>(); } catch { return 0; }
            if (v <= 0 || v > 1.0) return 0;   // 0 = disabled; >1 already a percent
            o[key] = v * 100.0;
            return 1;
        }

        private static int MergeMilli(JObject o, string milliKey, string decimalKey)
        {
            JToken milli = o[milliKey];
            if (milli == null) return 0;
            // Read as a double, not a long: a live server had 0.1 here, and rounding first turns the owner's price into 0.
            double v;
            try { v = milli.Value<double>(); } catch { o.Remove(milliKey); return 1; }
            o[decimalKey] = v / 1000.0;   // authoritative; overwrite the legacy whole-silver value
            o.Remove(milliKey);
            return 1;
        }
    }
}
