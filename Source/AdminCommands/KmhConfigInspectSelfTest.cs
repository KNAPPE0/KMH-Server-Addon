using System.Collections.Generic;

namespace KMHServerAddon.AdminCommands
{
    internal static class KmhConfigInspectSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();

            var def  = new Features.Economy.EconomyConfig();
            var live = new Features.Economy.EconomyConfig
            {
                MarketplaceTaxPercent  = def.MarketplaceTaxPercent + 3,
                MarketplaceMinUnitPrice = 0.5,
            };

            List<KmhConfigInspect.FieldDiff> diff = KmhConfigInspect.Diff(live, def);

            int changed = 0; bool taxFlagged = false, priceFlagged = false, milliListed = false;
            foreach (KmhConfigInspect.FieldDiff f in diff)
            {
                if (f.Changed) changed++;
                if (f.Name == "MarketplaceTaxPercent") taxFlagged = f.Changed;
                if (f.Name == "MarketplaceMinUnitPrice") priceFlagged = f.Changed;
                if (f.Name == "MarketplaceMinUnitPriceMilli") milliListed = true;   // derived - must be absent
            }

            r.Add(("Inspect: exactly the 2 tweaks flagged", changed == 2 && taxFlagged && priceFlagged, $"changed {changed}"));
            r.Add(("Inspect: derived [JsonIgnore] field excluded", !milliListed, milliListed ? "milli leaked" : ""));

            List<KmhConfigInspect.FieldDiff> none = KmhConfigInspect.Diff(new Features.Economy.EconomyConfig(), new Features.Economy.EconomyConfig());
            int c2 = 0; foreach (KmhConfigInspect.FieldDiff f in none) if (f.Changed) c2++;
            r.Add(("Inspect: all-default shows 0 changed", c2 == 0 && none.Count > 0, $"changed {c2}/{none.Count}"));

            bool tokenSafe = false;
            foreach (KmhConfigInspect.FieldDiff f in KmhConfigInspect.Diff(new Secretish { ApiToken = "super-secret-abc123", Rate = 9 }, new Secretish()))
                if (f.Name == "ApiToken") tokenSafe = f.Changed && !f.Value.Contains("super-secret") && !f.Default.Contains("super-secret");
            r.Add(("Inspect: secret field redacted", tokenSafe, ""));

            bool nestSafe = false;
            foreach (KmhConfigInspect.FieldDiff f in KmhConfigInspect.Diff(new Nested { Inner = new Inner { Token = "hidden-xyz" } }, new Nested()))
                if (f.Name == "Inner") nestSafe = !f.Value.Contains("hidden-xyz");
            r.Add(("Inspect: nested object not dumped", nestSafe, ""));

            r.Add(("Inspect: null-safe", KmhConfigInspect.Diff(null, def).Count == 0, ""));

            return r;
        }

        private sealed class Secretish { public string ApiToken { get; set; } = ""; public int Rate { get; set; } = 5; }
        private sealed class Inner  { public string Token { get; set; } = ""; }
        private sealed class Nested { public Inner Inner { get; set; } = new Inner(); }
    }
}
