using System;

namespace KMHServerAddon.Features.Sites
{
    // Defaults and identity; an archetype's only production effect is the perk the catalog shows (SiteArchetypeRegistry).
    internal static class SiteArchetypes
    {
        public const string Custom    = Dto.SiteEntry.ArchetypeCustom;
        public const string Farmland  = Dto.SiteEntry.ArchetypeFarmland;
        public const string Quarry    = Dto.SiteEntry.ArchetypeQuarry;
        public const string Woodland  = Dto.SiteEntry.ArchetypeWoodland;
        public const string Roadworks = Dto.SiteEntry.ArchetypeRoadworks;
        public const string Ranch     = Dto.SiteEntry.ArchetypeRanch;

        public static readonly string[] All = { Custom, Farmland, Quarry, Woodland, Ranch, Roadworks };

        // A value from a newer build degrades to Custom, not to a built-in whose rules this build would guess at.
        public static string Normalize(string archetype) => KmhVocabulary.Canonical(archetype, All, Custom);

        public static bool IsBuiltIn(string archetype) => Normalize(archetype) != Custom;

        // Null for Custom, because that archetype keeps whatever skill the site was already configured with.
        public static string DefaultSkillFor(string archetype)
        {
            switch (Normalize(archetype))
            {
                case Farmland:  return "Plants";
                case Quarry:    return "Mining";
                case Ranch:     return "Animals";
                case Woodland:  return "Plants";
                case Roadworks: return "Construction";
                default:        return null;   // Custom - caller keeps the site's configured skill
            }
        }

        public static string DisplayName(string archetype)
        {
            switch (Normalize(archetype))
            {
                case Farmland:  return "Farmland";
                case Quarry:    return "Quarry";
                case Ranch:     return "Ranch";
                case Woodland:  return "Woodland";
                case Roadworks: return "Roadworks";
                default:        return "Custom";
            }
        }
    }
}
