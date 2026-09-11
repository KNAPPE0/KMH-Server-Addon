using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Maintenance
{
    // A defName-substring classifier mis-sorts modded content and hands players an exploitable rule.
    internal static class KmhSiteClassifierSelfTest
    {
        private static SiteOutputMetadata M(string def, string[] cats = null, string[] stuff = null,
                                            string[] tags = null, string food = "", bool animal = false,
                                            bool harvested = false, bool mineable = false, bool crafted = false,
                                            bool tree = false, bool wild = false)
            => new SiteOutputMetadata
            {
                DefName = def,
                Categories      = new List<string>(cats  ?? new string[0]),
                StuffCategories = new List<string>(stuff ?? new string[0]),
                Tags            = new List<string>(tags  ?? new string[0]),
                FoodType = food, IsAnimalProduct = animal, IsHarvestedFromPlant = harvested,
                IsMineable = mineable, IsCraftedProduct = crafted,
                IsTreeHarvest = tree, IsWildHarvest = wild,
            };

        // Family gate plus the archetype's own predicate - what the picker actually applies.
        private static bool Offers(string archetype, SiteOutputMetadata m)
        {
            SiteOutputClassifier.ClassifyInto(m);
            return SiteArchetypeRegistry.Get(archetype).AllowsOutput(m);
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Families come from RimWorld metadata, never from the def name.
            string meat = SiteOutputClassifier.Classify(M("Meat_Muffalo", cats: new[] { "MeatRaw" }, food: "Meat", animal: true));
            r.Add(("Sites: raw meat classifies as Animal", meat == SiteOutputFamilies.Animal, meat));

            string leather = SiteOutputClassifier.Classify(M("Leather_Plain", cats: new[] { "Leathers" }, stuff: new[] { "Leathery" }, animal: true));
            r.Add(("Sites: leather classifies as Animal", leather == SiteOutputFamilies.Animal, leather));

            string milk = SiteOutputClassifier.Classify(M("Milk", cats: new[] { "AnimalProductRaw" }, food: "AnimalProduct", animal: true));
            r.Add(("Sites: milk classifies as Animal", milk == SiteOutputFamilies.Animal, milk));

            string steel = SiteOutputClassifier.Classify(M("Steel", stuff: new[] { "Metallic" }, mineable: true));
            r.Add(("Sites: steel classifies as Mineral", steel == SiteOutputFamilies.Mineral, steel));

            string wood = SiteOutputClassifier.Classify(M("WoodLog", cats: new[] { "WoodLog" }, stuff: new[] { "Woody" }, harvested: true));
            r.Add(("Sites: wood classifies as Forestry, not Plant", wood == SiteOutputFamilies.Forestry, wood));

            string rice = SiteOutputClassifier.Classify(M("RawRice", cats: new[] { "PlantFoodRaw" }, food: "VegetableOrFruit", harvested: true));
            r.Add(("Sites: a crop classifies as Plant", rice == SiteOutputFamilies.Plant, rice));

            string comps = SiteOutputClassifier.Classify(M("ComponentIndustrial", cats: new[] { "Manufactured" }, crafted: true));
            r.Add(("Sites: components classify as Crafted", comps == SiteOutputFamilies.Crafted, comps));

            // Shapes taken from a live catalog, because a plausible-looking fixture missed the real bug.

            // Wool carries a component's category while being a sheared animal product.
            string wool = SiteOutputClassifier.Classify(M("WoolSheep", cats: new[] { "Wools", "Textiles", "Manufactured" },
                                                          stuff: new[] { "Fabric" }, animal: true));
            r.Add(("Sites: wool classifies as Animal, not Plant", wool == SiteOutputFamilies.Animal, wool));

            // A boomalope butchers into chemfuel, so the animal flag is true for a manufactured good.
            string fuel = SiteOutputClassifier.Classify(M("Chemfuel", cats: new[] { "Manufactured" },
                                                          animal: true, harvested: true, crafted: true));
            r.Add(("Sites: chemfuel is not an Animal product despite the butcher flag",
                   fuel != SiteOutputFamilies.Animal, fuel));

            // Compacted machinery is mined for components, so mineable is genuinely true here.
            string minedComps = SiteOutputClassifier.Classify(M("ComponentIndustrial", cats: new[] { "Manufactured" },
                                                                mineable: true, crafted: true));
            r.Add(("Sites: a mineable manufactured item stays Crafted, not Mineral",
                   minedComps == SiteOutputFamilies.Crafted, minedComps));

            // Tusks and horns carry no useful category - only a trade tag says what they are.
            string tusk = SiteOutputClassifier.Classify(M("ElephantTusk", cats: new[] { "ItemsMisc" },
                                                          tags: new[] { "ExoticMisc", "AnimalPart" }));
            r.Add(("Sites: an animal part with only a trade tag classifies as Animal",
                   tusk == SiteOutputFamilies.Animal, tusk));

            string cocoa = SiteOutputClassifier.Classify(M("Chocolate", cats: new[] { "Foods" },
                                                           harvested: true, tree: true, wild: true));
            r.Add(("Sites: a tree crop classifies as Forestry", cocoa == SiteOutputFamilies.Forestry, cocoa));

            // Anomaly grows bioferrite on a tree, and a Woodland must not become an ore mine.
            string ferrite = SiteOutputClassifier.Classify(M("Bioferrite", stuff: new[] { "Metallic" },
                                                             harvested: true, tree: true, wild: true));
            r.Add(("Sites: a metal grown on a tree stays Mineral", ferrite == SiteOutputFamilies.Mineral, ferrite));

            // Woodland forages as well as logs, which is what keeps it distinct from Farmland.
            r.Add(("Sites: Woodland accepts a wild-foraged plant",
                   Offers(SiteArchetypes.Woodland, M("RawBerries", cats: new[] { "PlantFoodRaw" },
                                                     food: "VegetableOrFruit", harvested: true, wild: true)), ""));
            r.Add(("Sites: Woodland REFUSES a sown crop",
                   !Offers(SiteArchetypes.Woodland, M("RawRice", cats: new[] { "PlantFoodRaw" },
                                                      food: "VegetableOrFruit", harvested: true)), ""));
            r.Add(("Sites: Farmland still accepts a sown crop",
                   Offers(SiteArchetypes.Farmland, M("RawRice", cats: new[] { "PlantFoodRaw" },
                                                     food: "VegetableOrFruit", harvested: true)), ""));

            // A modded item with real metadata classifies without KMH ever knowing its name.
            string moddedOre = SiteOutputClassifier.Classify(M("XYZ_Mythril", stuff: new[] { "Metallic" }, mineable: true));
            r.Add(("Sites: an unknown modded ore still classifies as Mineral", moddedOre == SiteOutputFamilies.Mineral, moddedOre));

            string moddedHide = SiteOutputClassifier.Classify(M("QQ_DragonHide", cats: new[] { "Leathers" }, animal: true));
            r.Add(("Sites: an unknown modded leather still classifies as Animal", moddedHide == SiteOutputFamilies.Animal, moddedHide));

            string bare = SiteOutputClassifier.Classify(M("SomeMod_MysteryThing"));
            r.Add(("Sites: metadata-free item is Unknown, NOT Crafting", bare == SiteOutputFamilies.Unknown, bare));
            r.Add(("Sites: Unknown has no worker skill",
                   SiteOutputFamilies.SkillFor(SiteOutputFamilies.Unknown) == "", ""));
            r.Add(("Sites: null metadata is Unknown", SiteOutputClassifier.Classify(null) == SiteOutputFamilies.Unknown, ""));

            r.Add(("Sites: Quarry REFUSES meat",
                   !SiteArchetypeRegistry.Accepts(SiteArchetypes.Quarry, SiteOutputFamilies.Animal), ""));
            r.Add(("Sites: Farmland REFUSES steel",
                   !SiteArchetypeRegistry.Accepts(SiteArchetypes.Farmland, SiteOutputFamilies.Mineral), ""));
            r.Add(("Sites: Ranch REFUSES uranium",
                   !SiteArchetypeRegistry.Accepts(SiteArchetypes.Ranch, SiteOutputFamilies.Mineral), ""));
            r.Add(("Sites: Woodland REFUSES components",
                   !SiteArchetypeRegistry.Accepts(SiteArchetypes.Woodland, SiteOutputFamilies.Crafted), ""));

            r.Add(("Sites: Ranch accepts animal products",
                   SiteArchetypeRegistry.Accepts(SiteArchetypes.Ranch, SiteOutputFamilies.Animal), ""));
            r.Add(("Sites: Quarry accepts minerals",
                   SiteArchetypeRegistry.Accepts(SiteArchetypes.Quarry, SiteOutputFamilies.Mineral), ""));
            r.Add(("Sites: Woodland accepts forestry",
                   SiteArchetypeRegistry.Accepts(SiteArchetypes.Woodland, SiteOutputFamilies.Forestry), ""));

            r.Add(("Sites: no archetype accepts Unknown, Custom included",
                   !SiteArchetypeRegistry.Accepts(SiteArchetypes.Custom, SiteOutputFamilies.Unknown)
                   && !SiteArchetypeRegistry.Accepts(SiteArchetypes.Farmland, SiteOutputFamilies.Unknown), ""));

            // Skills are the server's, never the player's.
            r.Add(("Sites: a preset's skill is fixed regardless of output",
                   SiteArchetypeRegistry.ResolveSkill(SiteArchetypes.Quarry, SiteOutputFamilies.Animal) == "Mining", ""));
            r.Add(("Sites: Custom derives its skill from the output family",
                   SiteArchetypeRegistry.ResolveSkill(SiteArchetypes.Custom, SiteOutputFamilies.Animal) == "Animals"
                   && SiteArchetypeRegistry.ResolveSkill(SiteArchetypes.Custom, SiteOutputFamilies.Mineral) == "Mining", ""));
            r.Add(("Sites: Custom gets NO skill for an Unknown output",
                   SiteArchetypeRegistry.ResolveSkill(SiteArchetypes.Custom, SiteOutputFamilies.Unknown) == "", ""));

            // Presets are the baseline; Custom pays for its flexibility.
            double preset = SiteArchetypeRegistry.Get(SiteArchetypes.Farmland).CostMultiplier;
            double custom = SiteArchetypeRegistry.Get(SiteArchetypes.Custom).CostMultiplier;
            r.Add(("Sites: presets keep the balanced baseline cost", System.Math.Abs(preset - 1.0) < 0.001, preset.ToString("0.00")));
            r.Add(("Sites: Custom costs more than a preset", custom > preset, custom.ToString("0.00")));

            bool perksSane = true;
            foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
                if (d.ProductionMultiplier < 1.0 || d.ProductionMultiplier > 1.10) perksSane = false;
            r.Add(("Sites: no archetype perk exceeds +10%", perksSane, ""));
            r.Add(("Sites: Custom has no specialization bonus",
                   System.Math.Abs(SiteArchetypeRegistry.Get(SiteArchetypes.Custom).ProductionMultiplier - 1.0) < 0.001, ""));

            r.Add(("Sites: Ranch is registered", SiteArchetypeRegistry.Get(SiteArchetypes.Ranch).Id == SiteArchetypes.Ranch, ""));
            r.Add(("Sites: an unknown archetype id degrades to Custom",
                   SiteArchetypeRegistry.Get("orbital_elevator").IsCustom, ""));

            // Roadworks eligibility is a narrower rule than its family.
            SiteOutputMetadata blocks = M("BlocksGranite", cats: new[] { "StoneBlocks" }, stuff: new[] { "Stony" }, mineable: true);
            SiteOutputMetadata steelM = M("Steel", stuff: new[] { "Metallic" }, mineable: true);
            SiteOutputMetadata uran   = M("Uranium", stuff: new[] { "Metallic" }, mineable: true);
            SiteOutputMetadata goldM  = M("Gold", stuff: new[] { "Metallic" }, mineable: true);
            foreach (SiteOutputMetadata mm in new[] { blocks, steelM, uran, goldM })
                SiteOutputClassifier.ClassifyInto(mm);

            SiteArchetypeDef road = SiteArchetypeRegistry.Get(SiteArchetypes.Roadworks);
            r.Add(("Sites: Roadworks accepts stone blocks", road.AllowsOutput(blocks), ""));
            r.Add(("Sites: Roadworks accepts steel", road.AllowsOutput(steelM), ""));
            r.Add(("Sites: Roadworks REJECTS uranium despite it being Mineral",
                   !road.AllowsOutput(uran) && uran.Family == SiteOutputFamilies.Mineral, uran.Family));
            r.Add(("Sites: Roadworks REJECTS gold despite it being Mineral",
                   !road.AllowsOutput(goldM) && goldM.Family == SiteOutputFamilies.Mineral, goldM.Family));
            r.Add(("Sites: Roadworks always works at Construction",
                   SiteArchetypeRegistry.ResolveSkill(SiteArchetypes.Roadworks, SiteOutputFamilies.Mineral) == "Construction", ""));

            // Quarry keeps the whole Mineral family - the narrowing is Roadworks-specific, not global.
            SiteArchetypeDef quarry = SiteArchetypeRegistry.Get(SiteArchetypes.Quarry);
            r.Add(("Sites: Quarry still accepts uranium (Mineral is not narrowed globally)", quarry.AllowsOutput(uran), ""));

            SiteOutputMetadata meatM = M("Meat_Muffalo", cats: new[] { "MeatRaw" }, food: "Meat", animal: true);
            SiteOutputClassifier.ClassifyInto(meatM);
            r.Add(("Sites: Quarry rejects an Animal output", !quarry.AllowsOutput(meatM), ""));
            r.Add(("Sites: Ranch accepts that same Animal output",
                   SiteArchetypeRegistry.Get(SiteArchetypes.Ranch).AllowsOutput(meatM), ""));
            r.Add(("Sites: Ranch rejects a Mineral output",
                   !SiteArchetypeRegistry.Get(SiteArchetypes.Ranch).AllowsOutput(steelM), ""));

            SiteOutputMetadata mystery = M("SomeMod_MysteryThing");
            SiteOutputClassifier.ClassifyInto(mystery);
            r.Add(("Sites: an Unknown output is refused by Custom and has no skill",
                   !SiteArchetypeRegistry.Get(SiteArchetypes.Custom).AllowsOutput(mystery)
                   && string.IsNullOrEmpty(mystery.Skill), mystery.Family));

            // A perk multiplier must never be confused with construction cost.
            bool costClean = true;
            foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
            {
                bool isCustom = d.IsCustom;
                if (!isCustom && System.Math.Abs(d.CostMultiplier - 1.0) > 0.001) costClean = false;   // presets 1.00
                // A perk must never be the thing that sets the price.
                if (System.Math.Abs(d.CostMultiplier - d.ProductionMultiplier) < 0.001 && d.ProductionMultiplier != 1.0)
                    costClean = false;
            }
            r.Add(("Sites: every preset construction cost is exactly 1.00x baseline", costClean, ""));
            r.Add(("Sites: Custom construction cost is 1.35x",
                   System.Math.Abs(SiteArchetypeRegistry.Get(SiteArchetypes.Custom).CostMultiplier - 1.35) < 0.001,
                   SiteArchetypeRegistry.Get(SiteArchetypes.Custom).CostMultiplier.ToString("0.00")));
            r.Add(("Sites: Ranch 1.08 is its PRODUCTION perk, not its price",
                   System.Math.Abs(SiteArchetypeRegistry.Get(SiteArchetypes.Ranch).ProductionMultiplier - 1.08) < 0.001
                   && System.Math.Abs(SiteArchetypeRegistry.Get(SiteArchetypes.Ranch).CostMultiplier - 1.0) < 0.001, ""));
            r.Add(("Sites: the Roadworks perk is a road-progress field, not production",
                   System.Math.Abs(road.RoadworksProgressMultiplier - 1.08) < 0.001
                   && System.Math.Abs(road.ProductionMultiplier - 1.0) < 0.001, ""));

            // Marker art never crosses the wire, so contract section 43 checks it against the client tree instead.

            // A mechanoid has a race, which once put its butchered plasteel in the Ranch picker.
            string plasteel = SiteOutputClassifier.Classify(
                M("Plasteel", cats: new[] { "ResourcesRaw" }, stuff: new[] { "Metallic" }, animal: true));
            r.Add(("Sites: plasteel is a mineral even when a client calls it an animal product",
                   plasteel == SiteOutputFamilies.Mineral, plasteel));

            string mechSteel = SiteOutputClassifier.Classify(
                M("Steel", cats: new[] { "ResourcesRaw" }, stuff: new[] { "Metallic" }, animal: true, crafted: true));
            string mechGold = SiteOutputClassifier.Classify(
                M("Gold", cats: new[] { "ResourcesRaw" }, stuff: new[] { "Metallic" }, animal: true));
            r.Add(("Sites: the rest of a mechanoid's drops are minerals too",
                   mechSteel == SiteOutputFamilies.Mineral && mechGold == SiteOutputFamilies.Mineral,
                   $"{mechSteel}/{mechGold}"));

            string comp = SiteOutputClassifier.Classify(
                M("ComponentIndustrial", cats: new[] { "Manufactured" }, animal: true, crafted: true));
            r.Add(("Sites: a component is crafted, not an animal product and not a mineral",
                   comp == SiteOutputFamilies.Crafted, comp));

            // The guard must not cost real animal products their family - none of them is a metal or a stone.
            string alpaca = SiteOutputClassifier.Classify(
                M("WoolAlpaca", cats: new[] { "Wools", "Textiles", "Manufactured" }, stuff: new[] { "Fabric" }, animal: true));
            string humanLeather = SiteOutputClassifier.Classify(
                M("Leather_Human", cats: new[] { "Leathers", "Textiles", "Manufactured" }, stuff: new[] { "Leathery" }));
            string egg = SiteOutputClassifier.Classify(
                M("EggChickenUnfertilized", cats: new[] { "EggsUnfertilized" }, food: "AnimalProduct"));
            r.Add(("Sites: wool, leather and eggs are still animal products",
                   alpaca == SiteOutputFamilies.Animal && humanLeather == SiteOutputFamilies.Animal
                   && egg == SiteOutputFamilies.Animal, $"{alpaca}/{humanLeather}/{egg}"));

            // Nothing metallic or stony may reach a Ranch, and nothing animal may reach a Quarry.
            bool ranchRefusesMetal = !SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Ranch,
                M("Plasteel", stuff: new[] { "Metallic" }, animal: true));
            bool quarryRefusesWool = !SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Quarry,
                M("WoolAlpaca", cats: new[] { "AnimalProductRaw" }, animal: true));
            r.Add(("Sites: a Ranch refuses metal and a Quarry refuses wool",
                   ranchRefusesMetal && quarryRefusesWool, ""));

            r.AddRange(CatalogChecks());

            return r;
        }

        // A client can claim anything, so the server classifies and a disagreeing catalog is refused, never merged.
        private static List<(string, bool, string)> CatalogChecks()
        {
            var r = new List<(string, bool, string)>();

            SiteCatalogStore.ResetForTest();

            // A catalog arrives in chunks, and a partial one must never become canonical.
            var chunk1 = new List<Features.Sites.Dto.SiteOutputMetadata> { Meta("Steel", mineable: true, stuff: "Metallic") };
            var chunk2 = new List<Features.Sites.Dto.SiteOutputMetadata> { Meta("Meat_Cow", animal: true) };

            var res = SiteCatalogStore.Accept("alice", "fp-A", 1, 2, chunk1, out string d1);
            r.Add(("catalog: a partial push does not become canonical",
                   res == SiteCatalogStore.PushResult.Accumulating && !SiteCatalogStore.HasCatalog, d1));

            res = SiteCatalogStore.Accept("alice", "fp-A", 2, 2, chunk2, out string d2);
            r.Add(("catalog: the final chunk commits the whole push",
                   res == SiteCatalogStore.PushResult.Accepted && SiteCatalogStore.Count == 2, d2));

            // Classification happens server-side, from facts. Neither of these families was sent by the client.
            r.Add(("catalog: the server classifies from raw facts, not from anything the client asserted",
                   SiteCatalogStore.FamilyOf("Steel") == SiteOutputFamilies.Mineral
                   && SiteCatalogStore.FamilyOf("Meat_Cow") == SiteOutputFamilies.Animal, ""));
            r.Add(("catalog: the skill follows the family",
                   SiteCatalogStore.SkillOf("Steel") == "Mining"
                   && SiteCatalogStore.SkillOf("Meat_Cow") == "Animals", ""));

            // The guard. A different client with a different modpack must not overwrite the established catalog.
            var evil = new List<Features.Sites.Dto.SiteOutputMetadata> { Meta("Meat_Cow", mineable: true) };
            res = SiteCatalogStore.Accept("mallory", "fp-EVIL", 1, 1, evil, out string d3);
            r.Add(("catalog: a mismatched fingerprint is REFUSED", res == SiteCatalogStore.PushResult.Rejected, d3));
            r.Add(("catalog: a refused push leaves the established classification untouched",
                   SiteCatalogStore.FamilyOf("Meat_Cow") == SiteOutputFamilies.Animal
                   && SiteCatalogStore.RejectedPushes == 1, ""));

            // The same modpack, said again, is a legitimate refresh.
            var again = new List<Features.Sites.Dto.SiteOutputMetadata> { Meta("Steel", mineable: true, stuff: "Metallic"), Meta("Meat_Cow", animal: true) };
            res = SiteCatalogStore.Accept("bob", "fp-A", 1, 1, again, out string d4);
            r.Add(("catalog: the same fingerprint from another client refreshes rather than fights",
                   res == SiteCatalogStore.PushResult.Refreshed, d4));

            // Unknown is never quietly turned into Crafting.
            r.Add(("catalog: an unknown def stays Unknown and has NO skill",
                   SiteCatalogStore.FamilyOf("__kmh_no_such_def__") == SiteOutputFamilies.Unknown
                   && SiteCatalogStore.SkillOf("__kmh_no_such_def__") == "", ""));

            // Eligibility, computed server-side and shipped per item. A preset must refuse a family it does not own.
            Features.Sites.Dto.SiteOutputMetadata steel = SiteCatalogStore.Lookup("Steel");
            Features.Sites.Dto.SiteOutputMetadata meat  = SiteCatalogStore.Lookup("Meat_Cow");
            r.Add(("catalog: a Quarry accepts a mineral and refuses an animal product",
                   SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Quarry, steel)
                   && !SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Quarry, meat), ""));
            r.Add(("catalog: a Ranch accepts an animal product and refuses a mineral",
                   SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Ranch, meat)
                   && !SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Ranch, steel), ""));
            r.Add(("catalog: Custom accepts both",
                   SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Custom, steel)
                   && SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Custom, meat), ""));

            // A predicate, not a family test: gold and steel share every fact RimWorld records, so only the exclusion list separates them.
            var gold = Meta("Gold", mineable: true, stuff: "Metallic");
            SiteCatalogStore.Classify(new List<Features.Sites.Dto.SiteOutputMetadata> { gold });
            r.Add(("catalog: Roadworks takes plain metal but not gold, though both are Mineral",
                   gold.Family == SiteOutputFamilies.Mineral
                   && SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Roadworks, steel)
                   && !SiteArchetypeRegistry.AcceptsOutput(SiteArchetypes.Roadworks, gold), gold.Family));

            // Nothing at all may be built on an unclassified output.
            var unknown = Meta("__kmh_mystery__");
            SiteCatalogStore.Classify(new List<Features.Sites.Dto.SiteOutputMetadata> { unknown });
            bool anyAccepts = false;
            foreach (SiteArchetypeDef a in SiteArchetypeRegistry.All)
                if (a.AllowsOutput(unknown)) anyAccepts = true;
            r.Add(("catalog: an Unclassified output is refused by EVERY archetype, Custom included",
                   unknown.Family == SiteOutputFamilies.Unknown && !anyAccepts, ""));

            // Empty leaves Unclassified alone, so an owner opts in rather than inheriting a guess.
            r.Add(("catalog: with no owner fallback, an unclassified def stays Unclassified",
                   SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, "") == SiteOutputFamilies.Unknown
                   && SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, null) == SiteOutputFamilies.Unknown,
                   SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, "")));
            r.Add(("catalog: the owner's UnknownOutputFamily is applied, and only to Unclassified",
                   SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, "Mineral") == SiteOutputFamilies.Mineral
                   && SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Animal, "mineral") == SiteOutputFamilies.Animal,
                   SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, "Mineral")));
            r.Add(("catalog: a fallback that is not a real family is ignored rather than applied",
                   SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, "not-a-family") == SiteOutputFamilies.Unknown,
                   SiteCatalogStore.ResolveFamily(SiteOutputFamilies.Unknown, "not-a-family")));

            // A config field nothing consults is a setting that lies to the owner.
            string catalogSrc = ReadSource(System.IO.Path.Combine("Features", "Sites", "SiteCatalogStore.cs"));
            if (catalogSrc != null)
                r.Add(("catalog: the live classify path applies UnknownOutputFamily",
                       catalogSrc.Contains("ResolveFamily(classified, cfg?.UnknownOutputFamily)"), ""));

            // The wire form the picker filters on.
            string archList = SiteArchetypeRegistry.ArchetypesFor(steel);
            r.Add(("catalog: the eligibility list names the archetypes that accept the item",
                   archList.Contains(SiteArchetypes.Quarry) && archList.Contains(SiteArchetypes.Custom)
                   && !archList.Contains(SiteArchetypes.Ranch), archList));
            r.Add(("catalog: an unclassified item is eligible for nothing",
                   SiteArchetypeRegistry.ArchetypesFor(unknown) == "", ""));

            // The owner's escape hatch for a real modpack change.
            SiteCatalogStore.Reset();
            r.Add(("catalog: a reset clears the catalog and its fingerprint",
                   !SiteCatalogStore.HasCatalog && SiteCatalogStore.Fingerprint == "", ""));

            // A source check, because no value test can observe a mutation the migration deliberately does not make.
            string store = ReadSource(System.IO.Path.Combine("Features", "Sites", "SiteStore.cs"));
            if (store == null)
            {
                r.Add(("migration: store source not available (shipped build) - checks skipped", true, ""));
            }
            else
            {
                int open = store.IndexOf("public static int ReclassifySkills()", StringComparison.Ordinal);
                if (open < 0)
                {
                    r.Add(("migration: the re-skill pass exists", false, "ReclassifySkills not found"));
                }
                else
                {
                    // The method's closing brace: the first one at method indentation after the signature.
                    int close = store.IndexOf("\n        }", open, StringComparison.Ordinal);
                    string body = close > open ? store.Substring(open, close - open) : store.Substring(open);

                    r.Add(("migration: the re-skill pass exists and is callable when a catalog lands", true, ""));
                    r.Add(("migration: it assigns the work skill",
                           Regex.IsMatch(body, @"s\.RelevantSkillDef\s*="), ""));
                    // Every other field a panicked "enforce the new design" implementation would reach for.
                    bool destructive = Regex.IsMatch(body, @"s\.(ItemDefName|BaseAmountPerCycle|BlockedOutput|Archetype|Workers|WorkerProgress|MarketValuePerUnit)\s*=")
                                    || body.Contains("_byTile.Remove");
                    r.Add(("migration: it changes NOTHING else - no output, worker, archetype or removal",
                           !destructive, destructive ? "a destructive assignment appeared in the migration" : ""));
                    r.Add(("migration: an unclassified output is left completely alone",
                           body.Contains("if (string.IsNullOrEmpty(classified)) continue;"), ""));
                }

                // The build path must never retroactively pause an existing site for an archetype mismatch.
                r.Add(("migration: archetype eligibility is checked when BUILDING, never when loading",
                       Regex.IsMatch(store, @"AcceptsOutput\(archId")
                       && !Regex.IsMatch(store, @"LoadFromDisk[\s\S]{0,4000}?AcceptsOutput"), ""));
            }

            SiteCatalogStore.ResetForTest();
            return r;
        }

        // KMH-Patch/Textures/KMHPatch, when the client repo sits beside the server one. Null on a shipped server.
        private static string ReadSource(string relative)
        {
            try
            {
                string dir = System.AppContext.BaseDirectory.TrimEnd('\\', '/');
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = System.IO.Path.Combine(dir, "Source", relative);
                    if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                    candidate = System.IO.Path.Combine(dir, relative);
                    if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        // Raw facts only, never Family or Skill, or a fixture could hand the classifier the answer it is being asked for.
        private static Features.Sites.Dto.SiteOutputMetadata Meta(string defName, bool mineable = false, bool animal = false,
                                                   bool harvested = false, bool crafted = false,
                                                   string stuff = null)
        {
            var m = new Features.Sites.Dto.SiteOutputMetadata
            {
                DefName = defName, Label = defName,
                IsMineable = mineable, IsAnimalProduct = animal,
                IsHarvestedFromPlant = harvested, IsCraftedProduct = crafted,
            };
            if (!string.IsNullOrEmpty(stuff)) m.StuffCategories.Add(stuff);
            return m;
        }
    }
}
