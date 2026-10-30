using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    internal static class KmhSiteArchetypeSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Sites: every declared archetype is registered and survives a round trip", () =>
            {
                var missing = new List<string>();
                foreach (string id in SiteArchetypes.All)
                {
                    SiteArchetypeDef d = SiteArchetypeRegistry.Get(id);
                    // Get falls back to Custom for an unknown id, so a missing registration reads as Custom.
                    if (d == null || (d.Id != id && id != SiteArchetypes.Custom)) missing.Add(id);
                    if (SiteArchetypes.Normalize(id) != id) missing.Add(id + " (normalize)");
                    if (SiteArchetypes.Normalize(id.ToUpperInvariant()) != id) missing.Add(id + " (case)");
                    if (SiteArchetypes.Normalize("  " + id + " ") != id) missing.Add(id + " (space)");
                }
                return (missing.Count == 0,
                        missing.Count == 0 ? $"{SiteArchetypes.All.Length} archetype(s), each registered and stable"
                                           : "not registered or not stable: " + string.Join(", ", missing));
            });

            Check("Sites: the registry and the id list are the same set, in both directions", () =>
            {
                // A registered archetype missing from All is still offered, then normalized to Custom on the way in:
                // the player pays Custom's 1.35x for a perk they never get, and nothing logs.
                var declared = new List<string>(SiteArchetypes.All);
                var registered = new List<string>();
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All) registered.Add(d.Id);

                var offeredButDowngraded = new List<string>();
                foreach (string id in registered)
                    if (!declared.Contains(id)) offeredButDowngraded.Add(id);

                var declaredButUnregistered = new List<string>();
                foreach (string id in declared)
                    if (!registered.Contains(id)) declaredButUnregistered.Add(id);

                // Wire DTO ids: an unwired constant is a value a newer client may send with no rules behind it.
                var constants = new List<string>
                {
                    Dto.SiteEntry.ArchetypeCustom, Dto.SiteEntry.ArchetypeFarmland, Dto.SiteEntry.ArchetypeQuarry,
                    Dto.SiteEntry.ArchetypeWoodland, Dto.SiteEntry.ArchetypeRoadworks, Dto.SiteEntry.ArchetypeRanch,
                };
                var undeclaredConstants = new List<string>();
                foreach (string id in constants) if (!declared.Contains(id)) undeclaredConstants.Add(id);

                bool ok = offeredButDowngraded.Count == 0 && declaredButUnregistered.Count == 0
                       && undeclaredConstants.Count == 0 && declared.Count == registered.Count;
                return (ok, ok ? $"{declared.Count} archetype(s), one set across the id list, the registry and the wire"
                              : $"offeredThenDowngradedToCustom=[{string.Join(", ", offeredButDowngraded)}], "
                                + $"declaredButUnregistered=[{string.Join(", ", declaredButUnregistered)}], "
                                + $"constantNotDeclared=[{string.Join(", ", undeclaredConstants)}]");
            });

            Check("Sites: no archetype's multiplier can zero out or invert what a site produces", () =>
            {
                var bad = new List<string>();
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
                {
                    if (d.ProductionMultiplier <= 0) bad.Add($"{d.Id} production {d.ProductionMultiplier}");
                    if (d.StorageMultiplier <= 0) bad.Add($"{d.Id} storage {d.StorageMultiplier}");
                    if (d.CostMultiplier <= 0) bad.Add($"{d.Id} cost {d.CostMultiplier}");
                    if (d.RoadworksProgressMultiplier <= 0) bad.Add($"{d.Id} roadworks {d.RoadworksProgressMultiplier}");
                    // A specialization is a bonus, never a tax: below 1 makes the matching type worse than Custom.
                    if (d.ProductionMultiplier < 1.0) bad.Add($"{d.Id} produces less than Custom");
                    if (d.StorageMultiplier < 1.0) bad.Add($"{d.Id} stores less than Custom");
                }
                return (bad.Count == 0,
                        bad.Count == 0 ? $"{SiteArchetypeRegistry.All.Count} archetype(s) with usable multipliers"
                                       : string.Join("; ", bad));
            });

            Check("Sites: a declared perk corresponds to a multiplier that is actually different", () =>
            {
                // Perk text is what a player reads before paying; claiming a bonus it lacks is a toggle that does nothing.
                var lying = new List<string>();
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
                {
                    bool hasBonus = d.ProductionMultiplier > 1.0 || d.StorageMultiplier > 1.0
                                 || d.RoadworksProgressMultiplier > 1.0;
                    bool claimsBonus = (d.PerkText ?? "").IndexOf('%') >= 0;
                    if (claimsBonus && !hasBonus) lying.Add($"{d.Id} claims '{d.PerkText}' with no multiplier");
                    if (hasBonus && !claimsBonus) lying.Add($"{d.Id} has a bonus its perk text never mentions");
                }
                return (lying.Count == 0,
                        lying.Count == 0 ? "every advertised perk is backed by a real multiplier"
                                         : string.Join("; ", lying));
            });

            Check("Sites: exactly one archetype unlocks road building", () =>
            {
                var unlockers = new List<string>();
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All) if (d.UnlocksRoadworks) unlockers.Add(d.Id);
                bool ok = unlockers.Count == 1 && unlockers[0] == SiteArchetypes.Roadworks;
                return (ok, ok ? "Roadworks, and nothing else"
                              : "unlocks roadworks: " + string.Join(", ", unlockers));
            });

            Check("Sites: Custom pays for its freedom and claims no specialization", () =>
            {
                SiteArchetypeDef custom = SiteArchetypeRegistry.Get(SiteArchetypes.Custom);
                bool dearer = true;
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
                    if (!d.IsCustom && d.CostMultiplier >= custom.CostMultiplier) dearer = false;

                bool noBonus = custom.ProductionMultiplier == 1.0 && custom.StorageMultiplier == 1.0
                            && custom.RoadworksProgressMultiplier == 1.0;
                // Derived per output, never chosen: a fixed skill would let a player pick the easiest one for any output.
                bool derivedSkill = custom.WorkerSkill == null && SiteArchetypes.DefaultSkillFor(SiteArchetypes.Custom) == null;

                bool ok = dearer && noBonus && derivedSkill && custom.IsCustom;
                return (ok, ok ? $"Custom costs x{custom.CostMultiplier}, has no bonus, derives its skill"
                              : $"dearerThanEverySpecialist={dearer}, noBonus={noBonus}, derivedSkill={derivedSkill}");
            });

            Check("Sites: every specialist archetype names a worker skill, and Custom does not", () =>
            {
                var bad = new List<string>();
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
                {
                    string declared = SiteArchetypes.DefaultSkillFor(d.Id);
                    if (d.IsCustom)
                    {
                        if (d.WorkerSkill != null || declared != null) bad.Add($"{d.Id} fixes a skill");
                        continue;
                    }
                    if (string.IsNullOrEmpty(d.WorkerSkill)) bad.Add($"{d.Id} names no skill");
                    // The registry and the archetype helper are two places saying the same thing.
                    else if (declared != d.WorkerSkill) bad.Add($"{d.Id}: registry {d.WorkerSkill} vs helper {declared}");
                }
                return (bad.Count == 0,
                        bad.Count == 0 ? "every specialist names one skill, and both places agree"
                                       : string.Join("; ", bad));
            });

            Check("Sites: every specialist restricts what it accepts, and none accepts nothing", () =>
            {
                var bad = new List<string>();
                foreach (SiteArchetypeDef d in SiteArchetypeRegistry.All)
                {
                    if (d.IsCustom) continue;
                    if (d.AllowedFamilies == null || d.AllowedFamilies.Length == 0)
                    { bad.Add($"{d.Id} declares no family, so it accepts nothing"); continue; }
                    foreach (string f in d.AllowedFamilies)
                    {
                        // A family the classifier can never produce is a site type nobody can build on.
                        if (string.IsNullOrEmpty(f) || f == SiteOutputFamilies.Unknown)
                            bad.Add($"{d.Id} allows '{f}'");
                        else if (!d.Allows(f)) bad.Add($"{d.Id} declares {f} but refuses it");
                    }
                    // Unknown is the classifier's "I could not tell", and must fail closed everywhere.
                    if (d.Allows(SiteOutputFamilies.Unknown)) bad.Add($"{d.Id} accepts Unknown");
                    if (d.Allows(null) || d.Allows("")) bad.Add($"{d.Id} accepts a blank family");
                }
                return (bad.Count == 0,
                        bad.Count == 0 ? "every specialist restricts to real families and fails closed on Unknown"
                                       : string.Join("; ", bad));
            });

            Check("Sites: an archetype from a newer build degrades to Custom rather than to a specialist", () =>
            {
                // Falling back to a specialist would hand a bonus and a family restriction that build never chose.
                string[] unknown = { "__kmh_not_a_type__", "Farmlands", "quarry2", "HIGHWAY", "  ", "\t" };
                var bad = new List<string>();
                foreach (string u in unknown)
                {
                    if (SiteArchetypes.Normalize(u) != SiteArchetypes.Custom) bad.Add($"'{u}' -> {SiteArchetypes.Normalize(u)}");
                    if (SiteArchetypeRegistry.Get(u).Id != SiteArchetypes.Custom) bad.Add($"registry '{u}'");
                    if (SiteArchetypes.IsBuiltIn(u)) bad.Add($"'{u}' reads as built-in");
                }
                if (SiteArchetypes.Normalize(null) != SiteArchetypes.Custom) bad.Add("null");
                return (bad.Count == 0,
                        bad.Count == 0 ? $"{unknown.Length + 1} unknown value(s) all degrade to Custom"
                                       : string.Join("; ", bad));
            });

            return r;
        }
    }
}
