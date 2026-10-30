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
                Marketplace = new Features.Economy.MarketplaceRules
                { TaxPercent = def.Marketplace.TaxPercent + 3, MinUnitPrice = 0.5 },
            };

            List<KmhConfigInspect.FieldDiff> diff = KmhConfigInspect.Diff(live, def);

            int changed = 0; bool taxFlagged = false, priceFlagged = false, milliListed = false;
            foreach (KmhConfigInspect.FieldDiff f in diff)
            {
                if (f.Changed) changed++;
                // Dotted, because a grouped setting must still be named individually rather than as "Marketplace".
                if (f.Name == "Marketplace.TaxPercent") taxFlagged = f.Changed;
                if (f.Name == "Marketplace.MinUnitPrice") priceFlagged = f.Changed;
                if (f.Name == "Marketplace.MinUnitPriceMilli") milliListed = true;   // derived - must be absent
            }

            r.Add(("Inspect: exactly the 2 tweaks flagged", changed == 2 && taxFlagged && priceFlagged, $"changed {changed}"));
            r.Add(("Inspect: derived [JsonIgnore] field excluded", !milliListed, milliListed ? "milli leaked" : ""));

            // A flat walk reports a whole block as one changed value, hiding WHICH setting differs.
            bool anyBlockAsAValue = false, anyLeafNamed = false;
            foreach (KmhConfigInspect.FieldDiff f in KmhConfigInspect.Diff(
                         new Features.World.WorldConfig(), new Features.World.WorldConfig()))
            {
                if (f.Name == "Events" || f.Name == "WorldQuests") anyBlockAsAValue = true;
                if (f.Name == "Events.Weather.AutoDiscover") anyLeafNamed = true;
            }
            r.Add(("Inspect: a grouped setting is named by its own path, not by its block",
                   anyLeafNamed && !anyBlockAsAValue,
                   anyBlockAsAValue ? "a whole block was listed as one value" : "nested leaves are reached"));

            List<KmhConfigInspect.FieldDiff> none = KmhConfigInspect.Diff(new Features.Economy.EconomyConfig(), new Features.Economy.EconomyConfig());
            int c2 = 0; foreach (KmhConfigInspect.FieldDiff f in none) if (f.Changed) c2++;
            r.Add(("Inspect: all-default shows 0 changed", c2 == 0 && none.Count > 0, $"changed {c2}/{none.Count}"));

            bool tokenSafe = false;
            foreach (KmhConfigInspect.FieldDiff f in KmhConfigInspect.Diff(new Secretish { ApiToken = "super-secret-abc123", Rate = 9 }, new Secretish()))
                if (f.Name == "ApiToken") tokenSafe = f.Changed && !f.Value.Contains("super-secret") && !f.Default.Contains("super-secret");
            r.Add(("Inspect: secret field redacted", tokenSafe, ""));

            // Recursing must not print a nested secret: the leaf is redacted by name like a top-level one, on every line.
            bool nestSafe = true, nestLeafNamed = false;
            foreach (KmhConfigInspect.FieldDiff f in KmhConfigInspect.Diff(new Nested { Inner = new Inner { Token = "hidden-xyz" } }, new Nested()))
            {
                if (f.Value.Contains("hidden-xyz") || f.Default.Contains("hidden-xyz")) nestSafe = false;
                if (f.Name == "Inner.Token") nestLeafNamed = true;
            }
            r.Add(("Inspect: a secret inside a grouped block is redacted, not dumped", nestSafe && nestLeafNamed,
                   nestLeafNamed ? "" : "the nested leaf was never reached"));

            r.Add(("Inspect: null-safe", KmhConfigInspect.Diff(null, def).Count == 0, ""));

            return r;
        }

        private sealed class Secretish { public string ApiToken { get; set; } = ""; public int Rate { get; set; } = 5; }
        private sealed class Inner  { public string Token { get; set; } = ""; }
        private sealed class Nested { public Inner Inner { get; set; } = new Inner(); }
    }
}
