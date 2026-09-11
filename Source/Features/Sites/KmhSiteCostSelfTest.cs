using System.Collections.Generic;

namespace KMHServerAddon.Features.Sites
{
    // An overflowing double-to-int cast reads as int.MinValue, which every later Math.Max treats as the cheaper price.
    internal static class KmhSiteCostSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            int ordinary = SiteStore.BuildCost("_selftest", 12f, 10);
            int bigger   = SiteStore.BuildCost("_selftest", 12f, 100);
            r.Add(("Sites: cost rises with amount", bigger > ordinary, $"{ordinary} -> {bigger}"));

            int huge = SiteStore.BuildCost("_selftest", 100f, int.MaxValue);
            r.Add(("Sites: an overflowing amount cannot collapse the cost",
                   huge >= bigger, $"amount=int.MaxValue -> {huge} (vs {bigger} for amount=100)"));

            int negative = SiteStore.BuildCost("_selftest", 100f, -5);
            r.Add(("Sites: a negative amount never yields a negative cost", negative > 0, $"{negative}"));

            r.Add(("Sites: cost is never negative at any scale",
                   SiteStore.BuildCost("_selftest", float.MaxValue, int.MaxValue) > 0,
                   SiteStore.BuildCost("_selftest", float.MaxValue, int.MaxValue).ToString()));

            string unknown = "_kmh_selftest_unpriced_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            Dto.SiteBuildQuote unpriced = SiteStore.QuoteBuild("_selftest", unknown, 10, 1f, SiteArchetypes.Quarry, -1);
            r.Add(("Sites: an item with no trusted value is refused, not priced from the client's number",
                   !unpriced.Ok && unpriced.Cost == 0 && !string.IsNullOrEmpty(unpriced.Reason), unpriced.Reason));

            // Priced above the 500 floor on purpose: at floor level every amount costs the same and a misprice still matches.
            const float qValue = 500f;
            const int   qAmount = 10;
            // Pinned as the owner would, so the rest exercises the real pricing path rather than the refusal above.
            ItemLabels.ItemLabelCache.OwnerSetValue("Steel", (long)qValue);
            Dto.SiteBuildQuote q = SiteStore.QuoteBuild("_selftest", "Steel", qAmount, qValue, SiteArchetypes.Quarry, -1);
            r.Add(("Sites: a trusted value lets the quote price", q.Ok, q.Reason ?? ""));
            if (q.Ok)
            {
                int charged = SiteStore.BuildCost("_selftest", q.MarketValuePerUnit, q.Amount, q.CostMultiplier);
                r.Add(("Sites: a quote matches what the build would charge", q.Cost == charged, $"quote={q.Cost} build={charged}"));
                r.Add(("Sites: the quoted price is above the floor, so the check can actually fail",
                       q.Cost > 500, $"{q.Cost}"));
                int dearer = SiteStore.BuildCost("_selftest", q.MarketValuePerUnit, q.Amount + 1, q.CostMultiplier);
                r.Add(("Sites: one more unit costs more, so the quote is sensitive to amount",
                       dearer > q.Cost, $"{q.Cost} -> {dearer}"));
                r.Add(("Sites: a quote reports a cycle time and an amount cap",
                       q.CycleMinutes > 0 && q.MaxAmount >= 1, $"{q.CycleMinutes} min, max {q.MaxAmount}"));

                // The client's figure is ignored entirely, not merely clamped - the trusted value sets the price.
                Dto.SiteBuildQuote lowball = SiteStore.QuoteBuild("_selftest", "Steel", qAmount, 1f, SiteArchetypes.Quarry, -1);
                r.Add(("Sites: an under-reported client value does not cheapen the quote",
                       lowball.Ok && lowball.Cost == q.Cost, $"honest={q.Cost} lowball={lowball.Cost}"));
            }

            Dto.SiteBuildQuote bad = SiteStore.QuoteBuild("_selftest", "Steel", 0, 2f, SiteArchetypes.Quarry, -1);
            r.Add(("Sites: a zero-amount quote is refused, not priced",
                   !bad.Ok && bad.Cost == 0 && !string.IsNullOrEmpty(bad.Reason), bad.Reason));

            Dto.SiteBuildQuote noItem = SiteStore.QuoteBuild("_selftest", "", 5, 2f, SiteArchetypes.Quarry, -1);
            r.Add(("Sites: a quote with no item is refused, not priced",
                   !noItem.Ok && noItem.Cost == 0, noItem.Reason));

            Dto.SiteBuildQuote echo = SiteStore.QuoteBuild("_selftest", "Steel", 7, 2f, "", -1);
            r.Add(("Sites: a quote echoes the request verbatim so a stale reply can be told apart",
                   echo.ItemDefName == "Steel" && echo.Amount == 7 && echo.Archetype == "",
                   $"{echo.ItemDefName}/{echo.Amount}/'{echo.Archetype}'"));

            return r;
        }
    }
}
