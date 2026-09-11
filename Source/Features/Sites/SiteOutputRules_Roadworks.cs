using System;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // Deliberately narrower than the Mineral family - gold worked by Construction is the mismatch archetypes prevent.
    internal static class SiteOutputRules_Roadworks
    {
        private static readonly string[] ExcludedTags =
        {
            "Uranium", "Gold", "Silver", "Jade", "Plasteel", "Precious", "Exotic",
        };

        public static bool IsRoadworksMaterial(SiteOutputMetadata m)
        {
            if (m == null) return false;

            // The path a mod's own bricks or paving take without KMH knowing them.
            if (m.Family == SiteOutputFamilies.Construction) return true;
            if (HasTag(m, "RoadMaterial", "Construction")) return true;

            if (m.Family == SiteOutputFamilies.Mineral)
            {
                if (IsExcluded(m)) return false;
                if (HasStuff(m, "Stony")) return true;
                if (HasCat(m, "StoneBlocks")) return true;
                if (HasStuff(m, "Metallic")) return true;   // structural metal, precious already excluded above
                return false;
            }

            return false;
        }

        // The one place a name check is justified: RimWorld makes Gold and Steel both Metallic ResourcesRaw.
        private static bool IsExcluded(SiteOutputMetadata m)
        {
            if (HasTag(m, ExcludedTags)) return true;
            foreach (string bad in ExcludedTags)
                if (string.Equals(m.DefName, bad, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool HasTag(SiteOutputMetadata m, params string[] names) => Any(m.Tags, names);
        private static bool HasStuff(SiteOutputMetadata m, params string[] names) => Any(m.StuffCategories, names);
        private static bool HasCat(SiteOutputMetadata m, params string[] names) => Any(m.Categories, names);

        private static bool Any(System.Collections.Generic.List<string> have, string[] want)
        {
            if (have == null) return false;
            foreach (string h in have)
            {
                if (string.IsNullOrEmpty(h)) continue;
                foreach (string w in want)
                    if (string.Equals(h, w, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
