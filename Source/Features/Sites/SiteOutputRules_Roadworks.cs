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
            if (SiteOutputClassifier.HasTag(m, "RoadMaterial", "Construction")) return true;

            if (m.Family == SiteOutputFamilies.Mineral)
            {
                if (IsExcluded(m)) return false;
                if (SiteOutputClassifier.HasStuff(m, "Stony")) return true;
                if (SiteOutputClassifier.HasCat(m, "StoneBlocks")) return true;
                // Structural metal; precious ones were excluded above.
                if (SiteOutputClassifier.HasStuff(m, "Metallic")) return true;
                return false;
            }

            return false;
        }

        // The one place a name check is justified: RimWorld makes Gold and Steel both Metallic ResourcesRaw.
        private static bool IsExcluded(SiteOutputMetadata m)
        {
            if (SiteOutputClassifier.HasTag(m, ExcludedTags)) return true;
            foreach (string bad in ExcludedTags)
                if (string.Equals(m.DefName, bad, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
