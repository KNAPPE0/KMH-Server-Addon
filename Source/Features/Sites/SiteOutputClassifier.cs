using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // Deliberately coarse production themes rather than RimWorld taxonomy.
    internal static class SiteOutputFamilies
    {
        public const string Plant        = "plant";
        public const string Forestry     = "forestry";
        public const string Mineral      = "mineral";
        public const string Animal       = "animal";
        public const string Crafted      = "crafted";
        public const string Construction = "construction";
        public const string Unknown      = "unknown";

        public static readonly string[] All =
            { Plant, Forestry, Mineral, Animal, Crafted, Construction, Unknown };

        // Unknown is a valid value but never a valid choice, so it is excluded here rather than by every caller.
        public static bool IsKnown(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;
            string f = family.Trim().ToLowerInvariant();
            foreach (string k in All) if (k == f && k != Unknown) return true;
            return false;
        }

        // The RimWorld skill a family's work draws on. Unknown has NO skill on purpose - see the classifier.
        public static string SkillFor(string family)
        {
            switch (family)
            {
                case Plant:        return "Plants";
                case Forestry:     return "Plants";
                case Mineral:      return "Mining";
                case Animal:       return "Animals";
                case Crafted:      return "Crafting";
                case Construction: return "Construction";
                default:           return "";      // Unknown - never a skill, never a fallback
            }
        }

        public static string DisplayName(string family)
        {
            switch (family)
            {
                case Plant:        return "Plant / agricultural";
                case Forestry:     return "Forestry";
                case Mineral:      return "Mineral";
                case Animal:       return "Animal product";
                case Crafted:      return "Manufactured";
                case Construction: return "Construction material";
                default:           return "Unclassified";
            }
        }
    }

    // Reads RimWorld's own taxonomy rather than def names, which is what makes modded content classify for free.
    internal static class SiteOutputClassifier
    {
        // First match wins, so Animal is tested before Plant: kibble carries both signals and its animal claim is stronger.
        public static string Classify(SiteOutputMetadata m)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.DefName)) return SiteOutputFamilies.Unknown;

            if (IsAnimal(m))       return SiteOutputFamilies.Animal;
            if (IsForestry(m))     return SiteOutputFamilies.Forestry;
            if (IsMineral(m))      return SiteOutputFamilies.Mineral;
            if (IsPlant(m))        return SiteOutputFamilies.Plant;
            if (IsConstruction(m)) return SiteOutputFamilies.Construction;
            if (IsCrafted(m))      return SiteOutputFamilies.Crafted;

            return SiteOutputFamilies.Unknown;
        }

        // Unknown yields an empty skill, which every caller must treat as unusable rather than as a default.
        public static void ClassifyInto(SiteOutputMetadata m)
        {
            if (m == null) return;
            m.Family = Classify(m);
            m.Skill  = SiteOutputFamilies.SkillFor(m.Family);
            m.Source = "classifier";
        }

        // A mechanoid butchers into plasteel and a boomalope into chemfuel, so the flag alone only decides raw things.
        private static bool IsAnimal(SiteOutputMetadata m)
        {
            if (HasStuff(m, "Metallic", "Stony")) return false;

            bool named =
                   HasCat(m, "MeatRaw", "Leathers", "Wools", "AnimalProductRaw", "EggsFertilized", "EggsUnfertilized")
                || HasFood(m, "Meat", "AnimalProduct")
                || HasTag(m, "AnimalProduct", "Leather", "Meat", "Wool", "AnimalPart");
            if (named) return true;

            // Wool and leather are Manufactured in RimWorld's tree, so this tests for no animal category, not for that.
            if (HasCat(m, "Manufactured", "ResourcesManufactured")) return false;
            return m.IsAnimalProduct;
        }

        // Before Plant, since wood is harvested too. The tree test catches tree crops; metal and stone still win.
        private static bool IsForestry(SiteOutputMetadata m)
            => HasStuff(m, "Woody")
            || HasCat(m, "WoodLog")
            || (m.IsTreeHarvest && !HasStuff(m, "Metallic", "Stony"));

        // Manufactured beats mineable, or compacted machinery makes components a mineral. No raw material is affected.
        private static bool IsMineral(SiteOutputMetadata m)
        {
            if (HasCat(m, "Manufactured", "ResourcesManufactured") && !HasStuff(m, "Metallic", "Stony")) return false;
            return m.IsMineable
                || HasStuff(m, "Metallic", "Stony")
                || HasCat(m, "StoneBlocks", "StoneChunks", "ResourcesRaw_Mineral", "Chunks")
                || HasTag(m, "Ore", "Mineral", "StoneBlocks");
        }

        private static bool IsPlant(SiteOutputMetadata m)
            => m.IsHarvestedFromPlant
            || HasCat(m, "PlantFoodRaw", "PlantMatter", "ResourcesRaw_Plant")
            || HasStuff(m, "Fabric")
            || HasFood(m, "VegetableOrFruit", "Plant")
            || HasTag(m, "PlantFood", "Crop", "Fiber");

        // Construction is narrow on purpose: it exists for Roadworks, not as a second Crafted bucket.
        private static bool IsConstruction(SiteOutputMetadata m)
            => HasCat(m, "BuildingsStructure", "Chunks")
            || HasTag(m, "Construction", "RoadMaterial");

        private static bool IsCrafted(SiteOutputMetadata m)
            => m.IsCraftedProduct
            || HasCat(m, "Manufactured", "ResourcesManufactured");

        // Comparisons are case-insensitive throughout, because modded category names vary in casing.

        private static bool HasCat(SiteOutputMetadata m, params string[] names)  => Any(m.Categories, names);
        private static bool HasStuff(SiteOutputMetadata m, params string[] names) => Any(m.StuffCategories, names);
        private static bool HasTag(SiteOutputMetadata m, params string[] names)   => Any(m.Tags, names);

        // FoodType arrives as RimWorld's flags string ("Meat, AnimalProduct"), so this is a token test, not a match.
        private static bool HasFood(SiteOutputMetadata m, params string[] flags)
        {
            if (string.IsNullOrEmpty(m.FoodType)) return false;
            foreach (string f in flags)
                if (m.FoodType.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool Any(List<string> have, string[] want)
        {
            if (have == null || have.Count == 0) return false;
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
