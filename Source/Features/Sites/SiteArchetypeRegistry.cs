using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Sites
{
    // A site type's whole definition, so adding one is a registry entry rather than an edit in twenty classes.
    internal sealed class SiteArchetypeDef
    {
        public string Id;
        public string DisplayName;
        public string Description;

        // Null for Custom, which derives it per output instead.
        public string WorkerSkill;

        // Empty means the approved Custom pool, which only Custom uses.
        public string[] AllowedFamilies = new string[0];

        // An extra gate after the family test, for archetypes whose eligibility a family cannot express.
        public Func<Dto.SiteOutputMetadata, bool> AllowedOutputRule;

        public double CostMultiplier = 1.0;

        public double ProductionMultiplier = 1.0;

        // Separate from CostMultiplier, which is the only one that touches what a site costs to build.
        public double RoadworksProgressMultiplier = 1.0;
        public double StorageMultiplier           = 1.0;
        public string PerkText = "";

        public bool IsCustom;
        public bool UnlocksRoadworks;

        public bool Allows(string family)
        {
            if (string.IsNullOrEmpty(family) || family == SiteOutputFamilies.Unknown) return false;   // fail closed
            if (IsCustom) return true;    // the approved Custom pool is applied by the caller, not per-family here
            foreach (string f in AllowedFamilies) if (f == family) return true;
            return false;
        }

        public bool AllowsOutput(Dto.SiteOutputMetadata m)
        {
            if (m == null) return false;
            if (!Allows(m.Family)) return false;
            return AllowedOutputRule == null || AllowedOutputRule(m);
        }
    }

    internal static class SiteArchetypeRegistry
    {
        private static readonly Dictionary<string, SiteArchetypeDef> _byId =
            new Dictionary<string, SiteArchetypeDef>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<SiteArchetypeDef> _order = new List<SiteArchetypeDef>();

        static SiteArchetypeRegistry()
        {
            Register(new SiteArchetypeDef
            {
                Id = SiteArchetypes.Farmland, DisplayName = "Farmland",
                Description  = "Produces crops, plant food and fibres.",
                WorkerSkill  = "Plants",
                AllowedFamilies = new[] { SiteOutputFamilies.Plant },
                ProductionMultiplier = 1.08, PerkText = "+8% production",
            });

            Register(new SiteArchetypeDef
            {
                Id = SiteArchetypes.Quarry, DisplayName = "Quarry",
                Description  = "Produces stone, ores, metals and other raw minerals.",
                WorkerSkill  = "Mining",
                AllowedFamilies = new[] { SiteOutputFamilies.Mineral },
                ProductionMultiplier = 1.08, StorageMultiplier = 1.08, PerkText = "+8% extraction and storage",
            });

            Register(new SiteArchetypeDef
            {
                Id = SiteArchetypes.Woodland, DisplayName = "Woodland",
                Description  = "Produces timber, tree crops and wild-foraged plants.",
                WorkerSkill  = "Plants",
                // Forestry alone is nearly empty on vanilla, so gathered plants come here and sown ones stay Farmland's.
                AllowedFamilies = new[] { SiteOutputFamilies.Forestry, SiteOutputFamilies.Plant },
                AllowedOutputRule = m => m.Family == SiteOutputFamilies.Forestry || m.IsWildHarvest,
                ProductionMultiplier = 1.08, PerkText = "+8% production",
            });

            Register(new SiteArchetypeDef
            {
                Id = SiteArchetypes.Ranch, DisplayName = "Ranch",
                Description  = "Produces meat, leather, wool, milk, eggs and other animal products.",
                WorkerSkill  = "Animals",
                AllowedFamilies = new[] { SiteOutputFamilies.Animal },
                ProductionMultiplier = 1.08, PerkText = "+8% animal production",
            });

            Register(new SiteArchetypeDef
            {
                Id = SiteArchetypes.Roadworks, DisplayName = "Roadworks",
                Description  = "Produces approved construction materials, and unlocks road building.",
                WorkerSkill  = "Construction",
                AllowedFamilies = new[] { SiteOutputFamilies.Construction, SiteOutputFamilies.Mineral },
                // Family alone would make uranium, gold and every modded ore a legal Roadworks output.
                AllowedOutputRule = SiteOutputRules_Roadworks.IsRoadworksMaterial,
                RoadworksProgressMultiplier = 1.08,
                ProductionMultiplier = 1.0, PerkText = "+8% road construction progress",
                UnlocksRoadworks = true,
            });

            Register(new SiteArchetypeDef
            {
                Id = SiteArchetypes.Custom, DisplayName = "Custom",
                Description  = "Produces from the broader server-approved pool. The worker skill is assigned "
                             + "automatically from whatever you select.",
                WorkerSkill  = null,                    // derived per output - never player-chosen
                CostMultiplier = 1.35,                  // not SitesConfig.CustomSitePriceMultiplier, which scales every archetype
                ProductionMultiplier = 1.0, PerkText = "No specialization bonus",
                IsCustom = true,
            });
        }

        private static void Register(SiteArchetypeDef d)
        {
            _byId[d.Id] = d;
            _order.Add(d);
        }

        public static IReadOnlyList<SiteArchetypeDef> All => _order;

        // Unknown ids resolve to Custom, matching SiteArchetypes.Normalize.
        public static SiteArchetypeDef Get(string id)
        {
            if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out SiteArchetypeDef d)) return d;
            return _byId[SiteArchetypes.Custom];
        }

        // Would this archetype accept an output of this family? Unknown is refused everywhere, including Custom.
        public static bool Accepts(string archetypeId, string family) => Get(archetypeId).Allows(family);

        // Never the client's choice, and never a fallback for Unknown.
        public static string ResolveSkill(string archetypeId, string family)
        {
            SiteArchetypeDef d = Get(archetypeId);
            if (!d.IsCustom) return d.WorkerSkill;
            return SiteOutputFamilies.SkillFor(family);   // "" when Unknown
        }

        // Computed here because eligibility is not always a family test, so a client re-deriving it would disagree.
        public static string ArchetypesFor(Dto.SiteOutputMetadata m)
        {
            if (m == null) return "";
            var ids = new List<string>();
            foreach (SiteArchetypeDef d in _order)
                if (d.AllowsOutput(m)) ids.Add(d.Id);
            return string.Join(",", ids);
        }

        // The one call Build uses, so preview and enforcement can only ever give the same answer.
        public static bool AcceptsOutput(string archetypeId, Dto.SiteOutputMetadata m)
            => m != null && Get(archetypeId).AllowsOutput(m);
    }
}
