using System;
using System.Collections.Generic;
using System.Text;
using KMHServerAddon.Features.Roadworks;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // Walks the archetype x tier product off SiteArchetypes.All and RoadTiers.All, so a new one cannot be skipped.
    internal static class KmhSiteRoadMatrixSelfTest
    {
        private static SiteEntry Site(string archetype, int tier, int stability, params string[] buildings)
        {
            SiteEntry s = new SiteEntry { Archetype = archetype, OutputTier = tier, Stability = stability, Tile = 42 };
            foreach (string k in buildings)
                s.Buildings.Add(new SiteBuilding { Kind = k, State = SiteBuilding.StateOperational });
            return s;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Site matrix: every archetype at every site tier answers the road ladder the same way", () =>
            {
                var wrong = new List<string>();
                int cases = 0;
                foreach (string a in SiteArchetypes.All)
                    for (int siteTier = 0; siteTier <= 4; siteTier++)
                        foreach (string t in RoadTiers.All)
                        {
                            cases++;
                            string refusal = RoadworksStore.SiteRefusalFor(true, true, a, siteTier, "owner", t);
                            bool allowed = refusal == null;
                            // Only a Roadworks site builds anything, and only up to the rank its tier permits.
                            bool want = a == SiteArchetypes.Roadworks
                                     && RoadTiers.TierRankAllowed(RoadTiers.RankOf(t), siteTier);
                            if (allowed != want)
                                wrong.Add($"{a}/T{siteTier}/{t}: {(allowed ? "allowed" : "refused")}, expected {(want ? "allowed" : "refused")}");
                        }
                bool ok = wrong.Count == 0 && cases == SiteArchetypes.All.Length * 5 * RoadTiers.All.Length;
                return (ok, ok ? $"{cases} archetype/tier/road combinations, all as the ladder says"
                              : string.Join("; ", wrong.ToArray()));
            });

            Check("Site matrix: a road is refused for a stranger and a nameless caller at every combination", () =>
            {
                var wrong = new List<string>();
                foreach (string a in SiteArchetypes.All)
                    foreach (string t in RoadTiers.All)
                    {
                        if (RoadworksStore.SiteRefusalFor(true, false, a, 3, "someone", t) == null)
                            wrong.Add($"{a}/{t}: a caller who cannot manage the site was allowed");
                        if (RoadworksStore.SiteRefusalFor(true, true, a, 3, "", t) == null)
                            wrong.Add($"{a}/{t}: a nameless caller was allowed");
                        if (RoadworksStore.SiteRefusalFor(false, true, a, 3, "owner", t) == null)
                            wrong.Add($"{a}/{t}: a tile with no site was allowed");
                    }
                bool ok = wrong.Count == 0;
                return (ok, ok ? "ownership and existence are checked before the archetype, on every combination"
                              : string.Join("; ", wrong.ToArray()));
            });

            Check("Site matrix: every archetype round-trips and resolves a work skill", () =>
            {
                var wrong = new List<string>();
                foreach (string a in SiteArchetypes.All)
                {
                    if (SiteArchetypes.Normalize(a) != a) wrong.Add($"{a} does not normalize to itself");
                    if (SiteArchetypes.Normalize(a.ToUpperInvariant() + "  ") != a) wrong.Add($"{a} is case/space sensitive");
                    if (string.IsNullOrEmpty(SiteArchetypes.DisplayName(a))) wrong.Add($"{a} has no display name");

                    string skill = SiteStore.SkillFor(a, SiteOutputFamilies.Crafted, "Crafting");
                    if (string.IsNullOrEmpty(skill)) wrong.Add($"{a} resolves no skill");
                    // Custom is the one that keeps whatever the site was configured with; the rest override it.
                    bool builtIn = a != SiteArchetypes.Custom;
                    if (builtIn && skill != SiteArchetypes.DefaultSkillFor(a))
                        wrong.Add($"{a} does not use its own preset skill");
                    if (!builtIn && SiteArchetypes.DefaultSkillFor(a) != null)
                        wrong.Add("Custom presets a skill, so a configured one can never win");
                }
                bool ok = wrong.Count == 0;
                return (ok, ok ? $"{SiteArchetypes.All.Length} archetype(s): normalize, display and skill all resolve"
                              : string.Join("; ", wrong.ToArray()));
            });

            Check("Site matrix: construction, storage and repair behave the same for every archetype, apart from its storage perk", () =>
            {
                var wrong = new List<string>();
                foreach (string a in SiteArchetypes.All)
                {
                    int prev = 0;
                    for (int tier = 1; tier <= 3; tier++)
                    {
                        int slots = SiteBuildings.SlotsForTier(tier);
                        if (slots <= 0) wrong.Add($"{a}/T{tier}: no building slots");
                        if (slots < prev) wrong.Add($"{a}/T{tier}: fewer slots than T{tier - 1}");
                        prev = slots;

                        SiteEntry empty = Site(a, tier, 100);
                        if (SiteBuildings.StorageCapacity(empty) != 0) wrong.Add($"{a}/T{tier}: storage without a store");
                        if (SiteBuildings.FreeStorage(empty) != 0) wrong.Add($"{a}/T{tier}: free storage without a store");

                        SiteEntry stored = Site(a, tier, 100, SiteBuilding.KindStorage);
                        int oneStore = (int)(SiteBuildings.UnitsPerStorage * SiteArchetypeRegistry.StoragePerk(a));
                        if (SiteBuildings.StorageCapacity(stored) != oneStore)
                            wrong.Add($"{a}/T{tier}: one store is not one store's worth");
                        stored.StoredItems["Steel"] = oneStore + 50;
                        if (SiteBuildings.FreeStorage(stored) != 0)
                            wrong.Add($"{a}/T{tier}: an overfull store reports room");

                        // A second production building is the resource printer the slot rule exists to stop.
                        SiteEntry prod = Site(a, tier, 100, SiteBuilding.KindProduction);
                        if (SiteBuildings.CanAdd(prod, SiteBuilding.KindProduction, tier, out _))
                            wrong.Add($"{a}/T{tier}: a second production building was allowed");
                    }

                    // Condition is archetype-independent by design, so the matrix is what proves it.
                    if (SiteStability.RepairCost(100, 5) != 0) wrong.Add($"{a}: an undamaged site costs silver to repair");
                    if (SiteStability.RepairCost(50, 5) <= SiteStability.RepairCost(80, 5))
                        wrong.Add($"{a}: repair does not rise with damage");
                    if (SiteStability.RepairCost(0, 0) != 0) wrong.Add($"{a}: a zero price charges anyway");
                    if (!SiteStability.IsFallen(0) || SiteStability.IsFallen(1))
                        wrong.Add($"{a}: fallen is not exactly zero condition");
                    if (SiteStability.OutputFactor(0) != 0) wrong.Add($"{a}: a fallen site still produces");
                    foreach (int s in new[] { 1, 25, 50, 99, 100 })
                    {
                        double f = SiteStability.OutputFactor(s);
                        if (f <= 0 || f > 1.0) wrong.Add($"{a}: output factor {f} at condition {s} is out of bounds");
                    }
                    if (SiteStability.OutputFactor(50) >= SiteStability.OutputFactor(100))
                        wrong.Add($"{a}: damage does not reduce output");
                    if (SiteStability.Clamp(-5) != 0 || SiteStability.Clamp(500) != 100)
                        wrong.Add($"{a}: condition is not clamped to 0..100");
                }
                bool ok = wrong.Count == 0;
                return (ok, ok ? $"{SiteArchetypes.All.Length} archetype(s) x 3 tier(s): slots, storage, buildings and condition all agree"
                              : string.Join("; ", wrong.ToArray()));
            });

            return r;
        }
    }
}
