using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // Absent fields must mean a legacy site behaves exactly as it did, which is what most of these defend.
    internal static class KmhFrontierWorksSelfTest
    {
        // A site with one real pawn worker, so the production path is not short-circuited by "paused, no workers".
        private static SiteEntry Worked(SiteEntry s)
        {
            s.Workers.Add("w");
            s.WorkerProgress["w"] = new WorkerProgressDto { PawnLoadId = 1 };
            return s;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var legacy = new SiteEntry { Tile = 42, OwnerUsername = "amy", ItemDefName = "Steel", OutputTier = 2 };
            r.Add(("Frontier: a legacy site reads as Custom",
                   SiteArchetypes.Normalize(legacy.Archetype) == SiteArchetypes.Custom
                   && !SiteArchetypes.IsBuiltIn(legacy.Archetype), $"'{legacy.Archetype}'"));
            r.Add(("Frontier: a legacy site reads as healthy, not damaged",
                   legacy.Stability == SiteStability.Healthy
                   && SiteStability.OutputFactor(legacy.Stability) == 1.0, "absent must never mean broken"));
            r.Add(("Frontier: a legacy site has no buildings and full slots",
                   SiteBuildings.UsedSlots(legacy) == 0
                   && SiteBuildings.FreeSlots(legacy, legacy.OutputTier) == SiteBuildings.SlotsForTier(2), ""));
            r.Add(("Frontier: Custom keeps its own configured skill",
                   SiteArchetypes.DefaultSkillFor(SiteArchetypes.Custom) == null, "null = caller keeps site's skill"));

            r.Add(("Frontier: built-in skills are the sensible RimWorld ones",
                   SiteArchetypes.DefaultSkillFor(SiteArchetypes.Farmland)  == "Plants"
                   && SiteArchetypes.DefaultSkillFor(SiteArchetypes.Quarry)    == "Mining"
                   && SiteArchetypes.DefaultSkillFor(SiteArchetypes.Woodland)  == "Plants"
                   && SiteArchetypes.DefaultSkillFor(SiteArchetypes.Roadworks) == "Construction", ""));
            // No archetype may take a capability away from another - Roadworks is defined by what it builds.
            r.Add(("Frontier: Roadworks is an archetype, not a nerf or a special case",
                   SiteArchetypes.IsBuiltIn(SiteArchetypes.Roadworks)
                   && SiteArchetypes.DisplayName(SiteArchetypes.Roadworks) == "Roadworks", ""));
            r.Add(("Frontier: an unknown archetype degrades to Custom",
                   SiteArchetypes.Normalize("orbital_elevator") == SiteArchetypes.Custom
                   && SiteArchetypes.Normalize(null) == SiteArchetypes.Custom
                   && SiteArchetypes.Normalize("  FARMLAND ") == SiteArchetypes.Farmland, "case/space tolerant"));

            r.Add(("Frontier: slots are 2/3/4 by tier",
                   SiteBuildings.SlotsForTier(1) == 2 && SiteBuildings.SlotsForTier(2) == 3
                   && SiteBuildings.SlotsForTier(3) == 4, ""));
            r.Add(("Frontier: an unknown/high tier does not widen the budget",
                   SiteBuildings.SlotsForTier(9) == 4 && SiteBuildings.SlotsForTier(0) == 2, ""));

            var site = new SiteEntry { OutputTier = 1 };
            site.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindProduction });
            r.Add(("Frontier: only ONE production building per site",
                   !SiteBuildings.CanAdd(site, SiteBuilding.KindProduction, 1, out string whyProd), whyProd));
            r.Add(("Frontier: other kinds still fit while a slot is free",
                   SiteBuildings.CanAdd(site, SiteBuilding.KindHousing, 1, out _), ""));
            site.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindHousing });
            r.Add(("Frontier: a full site refuses more buildings",
                   !SiteBuildings.CanAdd(site, SiteBuilding.KindHousing, 1, out string whyFull), whyFull));

            var stacked = new SiteEntry();
            for (int i = 0; i < 6; i++)
                stacked.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindProduction, Level = 9 });
            r.Add(("Frontier: building bonus is hard-capped",
                   SiteBuildings.ProductionBonus(stacked) == SiteBuildings.MaxBuildingBonus,
                   $"{SiteBuildings.ProductionBonus(stacked):0.00} (cap {SiteBuildings.MaxBuildingBonus:0.00})"));
            var damaged = new SiteEntry();
            damaged.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindProduction, State = SiteBuilding.StateRuined });
            r.Add(("Frontier: a ruined building contributes nothing",
                   SiteBuildings.ProductionBonus(damaged) == 0, ""));
            var housed = new SiteEntry();
            for (int i = 0; i < 5; i++) housed.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindHousing });
            r.Add(("Frontier: housing bonus is capped too",
                   SiteBuildings.HousingBonus(housed) == SiteBuildings.MaxHousingBonus, ""));

            r.Add(("Frontier: healthy is full output", SiteStability.OutputFactor(100) == 1.0, ""));
            r.Add(("Frontier: a badly damaged site still produces above the floor",
                   SiteStability.OutputFactor(1) >= SiteStability.Floor
                   && SiteStability.OutputFactor(20) > 0.55 && SiteStability.OutputFactor(20) < 0.75,
                   $"20% -> {SiteStability.OutputFactor(20):0.00} (NOT 0.20)"));
            r.Add(("Frontier: only a fallen site stops producing",
                   SiteStability.OutputFactor(0) == 0 && SiteStability.IsFallen(0) && !SiteStability.IsFallen(1), ""));
            r.Add(("Frontier: stability clamps to 0-100",
                   SiteStability.Clamp(-50) == 0 && SiteStability.Clamp(999) == 100, ""));

            // These call the site's real output path, so a rule deleted from it fails here rather than going dead quietly.
            SiteEntry live = Worked(new SiteEntry { OutputTier = 2, TierMaxOutputMultiplier = 2.0, Stability = 100 });
            r.Add(("Frontier: a healthy plain site produces at its skill factor",
                   Math.Abs(SiteStore.OutputFactor(live) - 1.0) < 0.001, $"{SiteStore.OutputFactor(live):0.000}"));

            SiteEntry shaky = Worked(new SiteEntry { OutputTier = 2, TierMaxOutputMultiplier = 2.0, Stability = 1 });
            r.Add(("Frontier: production actually reads stability",
                   Math.Abs(SiteStore.OutputFactor(shaky) - SiteStability.Floor) < 0.01,
                   $"{SiteStore.OutputFactor(shaky):0.000}, expected the {SiteStability.Floor} floor"));
            SiteEntry fallen = Worked(new SiteEntry { OutputTier = 2, TierMaxOutputMultiplier = 2.0, Stability = 0 });
            r.Add(("Frontier: a fallen site produces nothing",
                   SiteStore.OutputFactor(fallen) == 0, $"{SiteStore.OutputFactor(fallen):0.000}"));

            SiteEntry built = Worked(new SiteEntry { OutputTier = 2, TierMaxOutputMultiplier = 2.0, Stability = 100 });
            built.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindProduction });
            r.Add(("Frontier: production actually reads buildings",
                   SiteStore.OutputFactor(built) > SiteStore.OutputFactor(live),
                   $"{SiteStore.OutputFactor(built):0.000} vs {SiteStore.OutputFactor(live):0.000}"));

            SiteEntry barracks = new SiteEntry { OwnerUsername = "__kmh_fw_probe__diag" };
            int bare = SiteStore.MaxWorkersLive(barracks);
            barracks.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindHousing });
            r.Add(("Frontier: housing actually raises the worker cap",
                   SiteStore.MaxWorkersLive(barracks) > bare, $"{bare} -> {SiteStore.MaxWorkersLive(barracks)}"));

            // Offering a kind that changes nothing would be a button that spends silver for no effect.
            foreach (string kind in SiteBuildings.Buildable)
            {
                SiteEntry before = Worked(new SiteEntry { OutputTier = 3, TierMaxOutputMultiplier = 2.0, Stability = 100 });
                SiteEntry after  = Worked(new SiteEntry { OutputTier = 3, TierMaxOutputMultiplier = 2.0, Stability = 100 });
                after.Buildings.Add(new SiteBuilding { Kind = kind, Level = 1 });
                bool changesOutput  = SiteStore.OutputFactor(after) > SiteStore.OutputFactor(before);
                bool changesWorkers = SiteBuildings.HousingBonus(after) > SiteBuildings.HousingBonus(before);
                bool changesStorage = SiteBuildings.StorageCapacity(after) > SiteBuildings.StorageCapacity(before);
                r.Add(($"Frontier: the offered '{kind}' building has a real effect",
                       changesOutput || changesWorkers || changesStorage,
                       "an offered building that changes nothing is a button that spends silver for nothing"));
            }
            r.Add(("Frontier: a building with no effect yet is not offered",
                   !SiteBuildings.IsBuildable(SiteBuilding.KindLogistics)
                   && !SiteBuildings.IsBuildable(SiteBuilding.KindDefense), ""));
            r.Add(("Frontier: an unbuildable kind is refused by the admission rule",
                   !SiteBuildings.CanAdd(new SiteEntry { OutputTier = 3 }, SiteBuilding.KindLogistics, 3, out string whyLog)
                   && whyLog != null, whyLog));

            // A co-owner can reorder the list while the confirmation prompt is open, and demolishing refunds nothing.
            var prod = new SiteBuilding { Kind = SiteBuilding.KindProduction, State = SiteBuilding.StateOperational };
            r.Add(("Frontier: demolish accepts the building the player was looking at",
                   SiteStore.MatchesExpected(prod, SiteBuilding.KindProduction, SiteBuilding.StateOperational), ""));
            r.Add(("Frontier: demolish REFUSES a slot that now holds a different kind",
                   !SiteStore.MatchesExpected(prod, SiteBuilding.KindStorage, SiteBuilding.StateOperational), ""));
            r.Add(("Frontier: demolish REFUSES a slot whose condition changed",
                   !SiteStore.MatchesExpected(prod, SiteBuilding.KindProduction, SiteBuilding.StateRuined), ""));
            r.Add(("Frontier: an older client naming nothing keeps the previous behaviour",
                   SiteStore.MatchesExpected(prod, "", ""), ""));

            // Without an explicit scope the worker branch wins, and an owner-worker cannot reach site storage at all.
            r.Add(("Sites: an owner-worker can direct the site's own output",
                   SiteStore.ResolveDestTarget(SiteStore.ScopeSite, isWorker: true, owns: true, guildOwned: false,
                                               mayManage: true, SiteEntry.DestStorage, out _) == SiteStore.DestTarget.SiteOutput, ""));
            r.Add(("Sites: an owner-worker still directs their own share by default",
                   SiteStore.ResolveDestTarget("", isWorker: true, owns: true, guildOwned: false,
                                               mayManage: true, SiteEntry.DestTreasury, out _) == SiteStore.DestTarget.WorkerShare, ""));
            r.Add(("Sites: a non-worker cannot claim a worker share",
                   SiteStore.ResolveDestTarget(SiteStore.ScopeMine, isWorker: false, owns: true, guildOwned: false,
                                               mayManage: true, SiteEntry.DestTreasury, out _) == SiteStore.DestTarget.None, ""));
            r.Add(("Sites: an outsider cannot direct a site's output",
                   SiteStore.ResolveDestTarget(SiteStore.ScopeSite, isWorker: false, owns: false, guildOwned: false,
                                               mayManage: false, SiteEntry.DestTreasury, out _) == SiteStore.DestTarget.None, ""));
            r.Add(("Sites: a guild site's own output cannot be sent outside the guild",
                   SiteStore.ResolveDestTarget(SiteStore.ScopeSite, isWorker: false, owns: false, guildOwned: true,
                                               mayManage: true, SiteEntry.DestCaravan, out _) == SiteStore.DestTarget.None
                   && SiteStore.ResolveDestTarget(SiteStore.ScopeSite, isWorker: false, owns: false, guildOwned: true,
                                               mayManage: true, SiteEntry.DestStorage, out _) == SiteStore.DestTarget.SiteOutput, ""));

            // Storage is bounded, and a ruined store holds nothing - an unbounded warehouse is an unbounded shelter.
            var store = new SiteEntry();
            for (int i = 0; i < 6; i++) store.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindStorage });
            r.Add(("Frontier: storage capacity is hard-capped",
                   SiteBuildings.StorageCapacity(store) == SiteBuildings.MaxStorageUnits,
                   $"{SiteBuildings.StorageCapacity(store)} (cap {SiteBuildings.MaxStorageUnits})"));
            var ruinedStore = new SiteEntry();
            ruinedStore.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindStorage, State = SiteBuilding.StateRuined });
            r.Add(("Frontier: a ruined store holds nothing",
                   SiteBuildings.StorageCapacity(ruinedStore) == 0, ""));
            var oneStore = new SiteEntry();
            oneStore.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindStorage });
            oneStore.StoredItems["Steel"] = SiteBuildings.UnitsPerStorage;
            r.Add(("Frontier: a full store has no free room",
                   SiteBuildings.FreeStorage(oneStore) == 0 && SiteBuildings.StoredUnits(oneStore) == SiteBuildings.UnitsPerStorage, ""));
            r.Add(("Frontier: a site with no store can hold nothing",
                   SiteBuildings.FreeStorage(new SiteEntry()) == 0, "storage must be built, never implicit"));

            // A worker's share landing in the owner's warehouse would be the owner collecting someone else's pay.
            var owned = new SiteEntry { OwnerUsername = "Ada" };
            owned.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindStorage });
            r.Add(("Frontier: the owner's share can be stored at their own site",
                   SiteStore.StorableUnits(owned, "ada", 10) == 10, "owner match is case-insensitive"));
            r.Add(("Frontier: a worker's share is never stored in the owner's warehouse",
                   SiteStore.StorableUnits(owned, "Borys", 10) == 0, "it would be the owner collecting their pay"));
            r.Add(("Frontier: storage takes only what fits, never more",
                   SiteStore.StorableUnits(owned, "Ada", SiteBuildings.UnitsPerStorage + 50) == SiteBuildings.UnitsPerStorage,
                   "the rest falls through to the treasury rather than being lost"));
            r.Add(("Frontier: a site with no store takes nothing",
                   SiteStore.StorableUnits(new SiteEntry { OwnerUsername = "Ada" }, "Ada", 10) == 0, ""));

            // Repair is priced on damage actually undone, so the quote and the charge come from one rule.
            r.Add(("Frontier: a healthy site costs nothing to repair",
                   SiteStability.RepairCost(100, 25) == 0, ""));
            r.Add(("Frontier: repair is priced on the damage undone",
                   SiteStability.RepairCost(80, 25) == 500 && SiteStability.RepairCost(0, 25) == 2500,
                   "20 points at 25 = 500; a fallen site costs the full 100"));
            r.Add(("Frontier: a free repair price is honoured, not floored",
                   SiteStability.RepairCost(50, 0) == 0, "an owner may switch repair costs off"));
            r.Add(("Frontier: repair pricing saturates rather than overflowing",
                   SiteStability.RepairCost(0, int.MaxValue) == int.MaxValue, ""));

            // A system outpost has an empty OwnerUsername, so a bare comparison would hand it to any nameless caller.
            var mine   = new SiteEntry { OwnerKind = SiteEntry.OwnerPlayer,  OwnerUsername = "Ada" };
            var sys    = new SiteEntry { OwnerKind = SiteEntry.OwnerSystem,  ControllerFaction = "raiders" };
            var neut   = new SiteEntry { OwnerKind = SiteEntry.OwnerNeutral };
            var legacy2 = new SiteEntry { OwnerUsername = "Ada" };   // written before OwnerKind existed

            r.Add(("Ownership: a site written before OwnerKind reads as player-owned",
                   SiteOwnership.KindOf(legacy2) == SiteEntry.OwnerPlayer
                   && SiteOwnership.IsOwnedBy(legacy2, "ada"), "absent must mean player, or every old site breaks"));
            r.Add(("Ownership: the owner owns it, case-insensitively",
                   SiteOwnership.IsOwnedBy(mine, "ADA") && SiteOwnership.CanManage(mine, "ada"), ""));
            r.Add(("Ownership: another player does not own it",
                   !SiteOwnership.IsOwnedBy(mine, "Borys") && !SiteOwnership.CanManage(mine, "Borys"), ""));

            r.Add(("Ownership: an empty caller owns nothing, on any site",
                   !SiteOwnership.IsOwnedBy(sys, "") && !SiteOwnership.IsOwnedBy(neut, "")
                   && !SiteOwnership.IsOwnedBy(mine, "") && !SiteOwnership.IsOwnedBy(legacy2, ""),
                   "an empty OwnerUsername must never match an empty caller"));
            r.Add(("Ownership: nobody manages a system or neutral outpost",
                   !SiteOwnership.CanManage(sys, "Ada") && !SiteOwnership.CanManage(neut, "Ada")
                   && !SiteOwnership.IsOwnedBy(sys, "Ada"), ""));
            r.Add(("Ownership: system and neutral are not player-controlled",
                   SiteOwnership.IsSystemControlled(sys) && SiteOwnership.IsSystemControlled(neut)
                   && SiteOwnership.IsPlayerControlled(mine), ""));
            r.Add(("Ownership: an unknown kind degrades to player, never to system",
                   SiteOwnership.NormalizeKind("overlord") == SiteEntry.OwnerPlayer
                   && SiteOwnership.NormalizeKind(null) == SiteEntry.OwnerPlayer, ""));

            r.Add(("Ownership: a system outpost has no payout account and no guild",
                   SiteOwnership.PayoutAccount(sys) == "" && SiteOwnership.OwningGuildLive(sys) == ""
                   && SiteOwnership.PayoutAccount(mine) == "Ada", ""));

            var named = new SiteEntry { OwnerKind = SiteEntry.OwnerSystem, SiteName = "Blackridge Depot" };
            r.Add(("Ownership: a place name is not a controller",
                   SiteOwnership.ControllerLabel(named) == "Hostile"
                   && !SiteOwnership.IsOwnedBy(named, "Blackridge Depot")
                   && !SiteOwnership.CanManage(named, "Blackridge Depot"),
                   "the site's own name once stood in as its controller, so a ruin was held by itself"));

            var guildSite = new SiteEntry { OwnerKind = SiteEntry.OwnerGuildKind, ControllingGuild = "__kmh_no_such_guild__" };
            r.Add(("Ownership: a guild site is not owned by any individual",
                   !SiteOwnership.IsOwnedBy(guildSite, "Ada")
                   && !SiteOwnership.CanManage(guildSite, "Ada"), "no such guild, so no current member"));
            r.Add(("Ownership: a guild site with no guild named is manageable by nobody",
                   !SiteOwnership.CanManage(new SiteEntry { OwnerKind = SiteEntry.OwnerGuildKind }, "Ada"), ""));
            // That read is not a comparison, so the contract guard cannot see it and this is the only cover it has.
            var guildOwned = new SiteEntry { OwnerKind = SiteEntry.OwnerGuildKind, ControllingGuild = "Wardens" };
            r.Add(("Ownership: a guild site's live guild is the guild that controls it",
                   SiteStore.OwnerCurrentGuild(guildOwned) == "Wardens", SiteStore.OwnerCurrentGuild(guildOwned)));
            r.Add(("Ownership: a system outpost consumes nobody's guild quota",
                   SiteStore.OwnerCurrentGuild(sys) == "" && SiteStore.OwnerCurrentGuild(neut) == "", ""));


            r.Add(("Outpost: an ordinary site is not an outpost",
                   !SiteOutposts.IsOutpost(legacy2)
                   && SiteOutposts.NormalizeState(legacy2.OutpostState) == SiteEntry.OutpostNone, ""));
            r.Add(("Outpost: a template from a newer build does not become a live outpost",
                   SiteOutposts.NormalizeTemplate("orbital_platform") == SiteEntry.TemplateNone
                   && SiteOutposts.NormalizeState("besieged") == SiteEntry.OutpostNone, "unknown degrades to none"));

            r.Add(("Outpost: only Ruins is spawnable yet",
                   SiteOutposts.IsSpawnable(SiteEntry.TemplateRuins)
                   && !SiteOutposts.IsSpawnable(SiteEntry.TemplateFortified)
                   && !SiteOutposts.IsSpawnable(SiteEntry.TemplateResource), ""));
            bool everySpawnableStarts = true;
            foreach (string t in SiteOutposts.Spawnable)
                if (SiteOutposts.EntryStateFor(t) == SiteEntry.OutpostNone) everySpawnableStarts = false;
            r.Add(("Outpost: every spawnable template has an entry state",
                   everySpawnableStarts, "a spawnable template with no lifecycle would establish a dead location"));
            r.Add(("Outpost: Ruins starts derelict",
                   SiteOutposts.EntryStateFor(SiteEntry.TemplateRuins) == SiteEntry.OutpostDerelict, ""));

            r.Add(("Outpost: derelict becomes claimable only, never captured directly",
                   SiteOutposts.CanTransition(SiteEntry.OutpostDerelict, SiteEntry.OutpostClaimable)
                   && !SiteOutposts.CanTransition(SiteEntry.OutpostDerelict, SiteEntry.OutpostCaptured), ""));
            r.Add(("Outpost: a captured location cannot re-enter the lifecycle",
                   !SiteOutposts.CanTransition(SiteEntry.OutpostCaptured, SiteEntry.OutpostClaimable)
                   && !SiteOutposts.CanTransition(SiteEntry.OutpostCaptured, SiteEntry.OutpostDerelict),
                   "capture is terminal, or one outpost could be claimed twice"));
            r.Add(("Outpost: a dormant location comes back derelict or hostile, never claimable",
                   SiteOutposts.CanTransition(SiteEntry.OutpostDormant, SiteEntry.OutpostDerelict)
                   && !SiteOutposts.CanTransition(SiteEntry.OutpostDormant, SiteEntry.OutpostClaimable), ""));
            r.Add(("Outpost: an expired claim window falls to dormant",
                   SiteOutposts.CanTransition(SiteEntry.OutpostClaimable, SiteEntry.OutpostDormant), ""));
            r.Add(("Outpost: a state never transitions to itself",
                   !SiteOutposts.CanTransition(SiteEntry.OutpostClaimable, SiteEntry.OutpostClaimable), ""));
            r.Add(("Outpost: an ordinary site has no transitions at all",
                   !SiteOutposts.CanTransition(SiteEntry.OutpostNone, SiteEntry.OutpostDerelict), ""));

            var ruins = new SiteEntry
            {
                OwnerKind = SiteEntry.OwnerNeutral,
                OutpostTemplate = SiteEntry.TemplateRuins,
                OutpostState = SiteEntry.OutpostDerelict,
            };
            r.Add(("Outpost: a derelict neutral ruins is consistent",
                   SiteOutposts.IsConsistent(ruins, out string whyOk), whyOk ?? ""));

            var stolen = new SiteEntry
            {
                OwnerKind = SiteEntry.OwnerPlayer, OwnerUsername = "Ada",
                OutpostTemplate = SiteEntry.TemplateRuins, OutpostState = SiteEntry.OutpostClaimable,
            };
            r.Add(("Outpost: a contested outpost may not be player-owned",
                   !SiteOutposts.IsConsistent(stolen, out string whyStolen), whyStolen));

            var orphan = new SiteEntry { OutpostState = SiteEntry.OutpostDerelict };
            r.Add(("Outpost: a lifecycle state without a template is refused",
                   !SiteOutposts.IsConsistent(orphan, out string whyOrphan), whyOrphan));

            var captured = new SiteEntry
            {
                OwnerKind = SiteEntry.OwnerNeutral,
                OutpostTemplate = SiteEntry.TemplateRuins, OutpostState = SiteEntry.OutpostCaptured,
            };
            r.Add(("Outpost: a captured outpost must have a real controller",
                   !SiteOutposts.IsConsistent(captured, out string whyCap), whyCap));

            r.Add(("Outpost: a move that is already done reports success without moving anything",
                   SiteOutposts.VerdictFor(SiteEntry.OutpostClaimable, SiteEntry.OutpostDerelict, SiteEntry.OutpostClaimable)
                       == SiteOutposts.Move.AlreadyThere,
                   "a resumed resolution must not treat its own finished work as a failure"));
            r.Add(("Outpost: a move from the expected state is allowed",
                   SiteOutposts.VerdictFor(SiteEntry.OutpostDerelict, SiteEntry.OutpostDerelict, SiteEntry.OutpostClaimable)
                       == SiteOutposts.Move.Allowed, ""));
            r.Add(("Outpost: a move is refused when something else changed the state",
                   SiteOutposts.VerdictFor(SiteEntry.OutpostDormant, SiteEntry.OutpostDerelict, SiteEntry.OutpostClaimable)
                       == SiteOutposts.Move.WrongFrom,
                   "forcing it would overwrite whatever moved it"));
            r.Add(("Outpost: an illegal move is refused even from the expected state",
                   SiteOutposts.VerdictFor(SiteEntry.OutpostDerelict, SiteEntry.OutpostDerelict, SiteEntry.OutpostCaptured)
                       == SiteOutposts.Move.Illegal, ""));

            string n1 = SiteOutposts.GenerateName(276237, SiteEntry.TemplateRuins);
            string n2 = SiteOutposts.GenerateName(276237, SiteEntry.TemplateRuins);
            r.Add(("Outpost: a generated name is stable for a tile",
                   n1 == n2 && n1.Length > 0, n1));
            r.Add(("Outpost: different tiles usually get different names",
                   SiteOutposts.GenerateName(97380, SiteEntry.TemplateRuins) != n1,
                   SiteOutposts.GenerateName(97380, SiteEntry.TemplateRuins)));

            // Given a worker on purpose, so only the ownership rule can be what stops production.
            SiteEntry contested = Worked(new SiteEntry
            {
                OwnerKind = SiteEntry.OwnerNeutral, OutputTier = 2, TierMaxOutputMultiplier = 2.0, Stability = 100,
                OutpostTemplate = SiteEntry.TemplateRuins, OutpostState = SiteEntry.OutpostDerelict,
            });
            r.Add(("Outpost: a contested outpost produces nothing even with a worker",
                   SiteStore.OutputFactor(contested) == 0, $"{SiteStore.OutputFactor(contested):0.000}"));

            r.Add(("Frontier: a preset archetype sets the work skill",
                   SiteStore.SkillFor(SiteArchetypes.Roadworks, SiteOutputFamilies.Crafted, "Crafting") == "Construction"
                   && SiteStore.SkillFor(SiteArchetypes.Farmland, SiteOutputFamilies.Crafted, "Crafting") == "Plants", ""));
            r.Add(("Frontier: Custom derives the skill from the output family",
                   SiteStore.SkillFor(SiteArchetypes.Custom, SiteOutputFamilies.Mineral, "Crafting") == "Mining"
                   && SiteStore.SkillFor(SiteArchetypes.Custom, SiteOutputFamilies.Animal, "Crafting") == "Animals", ""));
            // The keyword guess survives for servers whose players are all on older clients with no classification.
            r.Add(("Frontier: the legacy guess applies only when the family is unknown",
                   SiteStore.SkillFor(SiteArchetypes.Custom, SiteOutputFamilies.Unknown, "Mining") == "Mining"
                   && SiteStore.SkillFor("", SiteOutputFamilies.Unknown, "Mining") == "Mining", ""));

            r.AddRange(SnapshotCopyChecks());
            return r;
        }

        // Checked by reflection, because a hand-written list of fields drifts exactly like the one it replaced.
        private static List<(string, bool, string)> SnapshotCopyChecks()
        {
            var r = new List<(string, bool, string)>();
            var src = new SiteEntry
            {
                Tile = 77,
                OwnerKind = SiteEntry.OwnerNeutral,
                SiteName = "Duststone Ruins",
                ControllerFaction = "Raiders",
                ControllingGuild = "Wardens",
                OutpostTemplate = SiteEntry.TemplateRuins,
                OutpostState = SiteEntry.OutpostClaimable,
                EstablishedUtcTicks = 123, ClaimWindowEndsUtcTicks = 456,
                OriginOperationId = 9, CapturedBy = "Ada",
                Archetype = SiteArchetypes.Quarry, Stability = 42,
                // Case-insensitive like the store's own, or the comparer check has nothing to compare against.
                StoredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { "Steel", 5 } },
            };
            src.Workers.Add("Ada");
            src.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindStorage, Level = 2 });
            src.WorkerProgress["Ada"] = new WorkerProgressDto { CyclesCompleted = 3, PawnLoadId = 11 };

            SiteEntry copy = SiteStore.Copy(src);
            r.AddRange(Maintenance.KmhCopyCoverage.Check("Sites", src, copy,
                "OwnerGuild", "MaxWorkers", "IsProducing", "PausedReason",
                "ProductionMultiplier", "EffectiveCycleMinutes"));

            // A lifecycle that cannot leave Claimable strands the outpost and holds a director slot forever.
            r.Add(("Outpost: an unclaimed location can leave the claimable state",
                   SiteOutposts.CanTransition(SiteEntry.OutpostClaimable, SiteEntry.OutpostDormant),
                   "without this there is no exit from claimable except being claimed"));
            r.Add(("Outpost: a dormant location can be offered again later",
                   SiteOutposts.CanTransition(SiteEntry.OutpostDormant, SiteEntry.OutpostDerelict)
                   && !SiteOutposts.CanTransition(SiteEntry.OutpostDormant, SiteEntry.OutpostClaimable),
                   "it re-enters through an operation, not by becoming claimable out of nowhere"));

            var crewed = Worked(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Stability = 100 });
            r.Add(("Roadworks: a crewed site produces construction work",
                   SiteStore.RoadworkPerCycle(crewed) > 0, $"{SiteStore.RoadworkPerCycle(crewed):0.00}"));
            r.Add(("Roadworks: an unworked site builds nothing",
                   SiteStore.RoadworkPerCycle(new SiteEntry { Archetype = SiteArchetypes.Roadworks }) == 0,
                   "no crew, no road - work is never banked for later"));

            var twoCrew = Worked(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Stability = 100 });
            twoCrew.Workers.Add("w2");
            twoCrew.WorkerProgress["w2"] = new WorkerProgressDto { PawnLoadId = 2 };
            r.Add(("Roadworks: more able workers build faster",
                   SiteStore.RoadworkPerCycle(twoCrew) > SiteStore.RoadworkPerCycle(crewed), ""));

            var legacyCrew = Worked(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Stability = 100 });
            legacyCrew.Workers.Add("ghost");
            legacyCrew.WorkerProgress["ghost"] = new WorkerProgressDto { Legacy = true };
            r.Add(("Roadworks: a legacy worker lays no road",
                   Math.Abs(SiteStore.RoadworkPerCycle(legacyCrew) - SiteStore.RoadworkPerCycle(crewed)) < 0.0001, ""));

            var damagedCrew = Worked(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Stability = 20 });
            r.Add(("Roadworks: a damaged site builds slower but not never",
                   SiteStore.RoadworkPerCycle(damagedCrew) > 0
                   && SiteStore.RoadworkPerCycle(damagedCrew) < SiteStore.RoadworkPerCycle(crewed), ""));

            // Yield multipliers raise what a site produces; they must not also speed up construction.
            var richTier = Worked(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Stability = 100,
                                                 OutputTier = 3, TierMaxOutputMultiplier = 2.0 });
            richTier.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindProduction, Level = 3 });
            r.Add(("Roadworks: output tier and production buildings do not speed up road work",
                   Math.Abs(SiteStore.RoadworkPerCycle(richTier) - SiteStore.RoadworkPerCycle(crewed)) < 0.0001,
                   "OutputFactor says how much a site yields, not how fast a crew lays road"));

            // Labour is only diverted when there IS a project, or a site between projects produces nothing at all.
            var roadSite = Worked(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Stability = 100, Tile = 77 });
            var building = new HashSet<int> { 77 };
            var idle     = new HashSet<int>();
            r.Add(("Roadworks: a site with an active project builds road instead of goods",
                   SiteStore.DivertsToRoadwork(roadSite, building), ""));
            r.Add(("Roadworks: a site with NO active project produces goods instead of losing the cycle",
                   !SiteStore.DivertsToRoadwork(roadSite, idle), ""));

            var farm = Worked(new SiteEntry { Archetype = SiteArchetypes.Farmland, Stability = 100, Tile = 77 });
            r.Add(("Roadworks: only a Roadworks site diverts its labour",
                   !SiteStore.DivertsToRoadwork(farm, building), ""));
            r.Add(("Roadworks: an unworked site never diverts",
                   !SiteStore.DivertsToRoadwork(new SiteEntry { Archetype = SiteArchetypes.Roadworks, Tile = 77 }, building), ""));

            var owner = new SiteEntry { Tile = 1, OwnerKind = SiteEntry.OwnerPlayer, OwnerUsername = "Ada" };
            owner.Workers.Add("Bo");
            owner.WorkerProgress["Bo"] = new WorkerProgressDto { PawnLoadId = 1, Xp = 500 };
            var outpost = new SiteEntry { Tile = 2, OwnerKind = SiteEntry.OwnerPlayer, OwnerUsername = "Ada",
                                          OutpostTemplate = SiteEntry.TemplateRuins };
            var guildSite = new SiteEntry { Tile = 3, OwnerKind = SiteEntry.OwnerGuildKind, ControllingGuild = "Wardens" };
            var all = new List<SiteEntry> { owner, outpost, guildSite };

            var ownedMap = PlayerStats.PlayerStatsStore.SitesOwnedFrom(all, out var outpostMap);
            r.Add(("Standings: sites owned counts every site a player controls",
                   ownedMap.TryGetValue("Ada", out int ac) && ac == 2, $"Ada={ac}"));
            r.Add(("Standings: an outpost counts as owned AND as an outpost held",
                   outpostMap.TryGetValue("Ada", out int oh) && oh == 1, $"held={oh}"));
            r.Add(("Standings: a guild-held site counts for no individual",
                   !ownedMap.ContainsKey("Wardens") && ownedMap.Count == 1,
                   "guild totals aggregate from members, they are not a player's own"));

            var xp = PlayerStats.PlayerStatsStore.WorkerXpFrom(all);
            r.Add(("Standings: worker XP goes to the worker, not the site owner",
                   xp.TryGetValue("Bo", out long bx) && bx == 500 && !xp.ContainsKey("Ada"),
                   $"Bo={bx}, owner credited={xp.ContainsKey("Ada")}"));

            var second = new SiteEntry { Tile = 4, OwnerKind = SiteEntry.OwnerPlayer, OwnerUsername = "Cy" };
            second.Workers.Add("Bo");
            second.WorkerProgress["Bo"] = new WorkerProgressDto { PawnLoadId = 2, Xp = 250 };
            var xp2 = PlayerStats.PlayerStatsStore.WorkerXpFrom(new List<SiteEntry> { owner, second });
            r.Add(("Standings: a worker's XP sums across every site they work",
                   xp2.TryGetValue("Bo", out long bx2) && bx2 == 750, $"Bo={bx2}"));

            // Derived from the joined row, never cached - a stored score cannot see the live WorkerXp join.
            long siteWorkScore = PlayerStats.PlayerStatsStore.EconomyScoreOf(
                new PlayerStats.Dto.PlayerLeaderboardEntry { WorkerXp = 750, SitesBuilt = 2 });
            r.Add(("Standings: the economy score counts site work and sites built",
                   siteWorkScore == 175, $"score={siteWorkScore} for 750 xp + 2 sites built"));
            r.Add(("Standings: selling a site does not lower the economy score",
                   PlayerStats.PlayerStatsStore.EconomyScoreOf(
                       new PlayerStats.Dto.PlayerLeaderboardEntry { SitesBuilt = 2, SitesOwned = 0 }) == 100,
                   "the score is cumulative history, not current holdings"));

            // Invisible but load-bearing: nulling a DTO initializer would make every reloaded site read as workerless.
            var rt = Newtonsoft.Json.JsonConvert.DeserializeObject<SiteEntry>(
                "{\"tile\":92,\"worker_progress\":{\"Ada\":{\"pawn_load_id\":7}},\"stored_items\":{\"Steel\":5}}");
            r.Add(("Sites: a deserialized site keeps case-insensitive worker and storage keys",
                   rt != null && rt.WorkerProgress.ContainsKey("ada") && rt.StoredItems.ContainsKey("STEEL"),
                   "a case-sensitive map reads as no workers and pauses the site"));

            // NormalizeKeys guarantees the same thing independently, and merges a case clash instead of dropping one.
            var reloaded = new SiteEntry { Tile = 91 };
            reloaded.WorkerProgress = new Dictionary<string, WorkerProgressDto> { { "Ada", new WorkerProgressDto { PawnLoadId = 7, Xp = 40 } } };
            reloaded.StoredItems    = new Dictionary<string, int> { { "Steel", 5 }, { "steel", 7 } };
            SiteStore.NormalizeKeys(reloaded);
            r.Add(("Sites: a reloaded site matches its workers regardless of casing",
                   reloaded.WorkerProgress.ContainsKey("ada"), "a case-sensitive map reads as no workers and pauses the site"));
            r.Add(("Sites: reloaded storage merges a case clash instead of dropping a side",
                   reloaded.StoredItems.TryGetValue("STEEL", out int steelQty) && steelQty == 12, $"steel={steelQty}"));

            // A JSON null passes the length check, so clamping must replace it or the classifier throws on first build.
            var holed = new SitesConfig { OutputTiers = new SiteOutputTier[] { null, null, null, null } };
            holed.Clamp();
            bool tiersFilled = true;
            foreach (SiteOutputTier t in holed.OutputTiers) if (t == null) tiersFilled = false;
            r.Add(("Sites: a hand-edited config with a null output tier is repaired, not carried",
                   tiersFilled && holed.OutputTiers.Length >= 4, "a null tier throws the first time it is classified"));

            r.Add(("Sites: an unnamed place is still called something",
                   SiteStore.NameOf(new SiteEntry { Tile = 4211 }) == "tile 4211"
                   && SiteStore.NameOf(new SiteEntry { Tile = 5, SiteName = "Duststone Ruins" }) == "Duststone Ruins",
                   "a blank name reads as ' is now yours.' in a claim message"));

            // An unclaimed outpost produces nothing legitimately, so the sweep must not read that as a blocked output.
            var derelict = new SiteEntry { Tile = 78, OutpostTemplate = SiteEntry.TemplateRuins, ItemDefName = "" };
            var brokenLegacy = new SiteEntry { Tile = 79, ItemDefName = "" };
            r.Add(("Sites: an unconfigured outpost is not a blocked output",
                   SiteStore.IsUnconfiguredOutpost(derelict) && !SiteStore.IsUnconfiguredOutpost(brokenLegacy),
                   "a legacy site with no output IS still a real problem worth pausing"));
            r.Add(("Sites: a configured outpost is judged on its output like any site",
                   !SiteStore.IsUnconfiguredOutpost(
                       new SiteEntry { OutpostTemplate = SiteEntry.TemplateRuins, ItemDefName = "Silver" }), ""));

            r.Add(("Sites: a copied outpost still reads as an outpost",
                   copy.OutpostTemplate == SiteEntry.TemplateRuins
                   && copy.OutpostState == SiteEntry.OutpostClaimable
                   && copy.OwnerKind == SiteEntry.OwnerNeutral,
                   "a blank template on the wire is why nothing could ever be claimed"));

            r.AddRange(GuildProduction());
            return r;
        }

        // A guild site has no owner username, so keying the owner share by one puts the whole share under "".
        private static List<(string, bool, string)> GuildProduction()
        {
            var r = new List<(string, bool, string)>();
            var solo = new List<string>();
            var two  = new List<string> { "bo", "cy" };

            int Sum(int owner, Dictionary<string, int> workers)
            {
                int t = owner;
                foreach (KeyValuePair<string, int> kv in workers) t += kv.Value;
                return t;
            }

            SiteStore.PlanShares("OwnerOnly", 100, true, two, false, 0, out int oOwner, out var oWorkers);
            r.Add(("Sites: OwnerOnly gives the entire cycle to the owner and nothing to a phantom recipient",
                   oOwner == 100 && oWorkers.Count == 0 && Sum(oOwner, oWorkers) == 100, $"owner {oOwner}"));

            SiteStore.PlanShares("SplitTotal", 100, true, two, false, 0, out int sOwner, out var sWorkers);
            r.Add(("Sites: SplitTotal conserves the whole cycle across owner and workers",
                   Sum(sOwner, sWorkers) == 100 && sWorkers.Count == 2 && sOwner >= 33,
                   $"owner {sOwner} + workers {Sum(0, sWorkers)}"));

            // 100 over 3 slots leaves a remainder of 1, which is the part a naive split drops.
            r.Add(("Sites: the split remainder stays with the owner rather than disappearing",
                   sOwner == 34 && sWorkers["bo"] == 33 && sWorkers["cy"] == 33, $"owner {sOwner}"));

            SiteStore.PlanShares("SplitTotal", 100, true, two, true, 25, out int tOwner, out var tWorkers);
            r.Add(("Sites: owner tax moves value from workers to the owner and conserves the total",
                   Sum(tOwner, tWorkers) == 100 && tOwner > 34, $"owner {tOwner}, total {Sum(tOwner, tWorkers)}"));

            SiteStore.PlanShares("PerWorkerCopy", 100, true, two, false, 0, out int pOwner, out var pWorkers);
            r.Add(("Sites: PerWorkerCopy still gives the owner a full copy alongside each worker",
                   pOwner == 100 && pWorkers.Count == 2 && pWorkers["bo"] == 100, $"owner {pOwner}"));

            SiteStore.PlanShares("SplitTotal", 100, false, two, false, 0, out int nOwner, out var nWorkers);
            r.Add(("Sites: with no owner the whole cycle stays with the workers",
                   nOwner == 0 && Sum(nOwner, nWorkers) == 100, $"owner {nOwner}, total {Sum(nOwner, nWorkers)}"));
            SiteStore.PlanShares("OwnerOnly", 100, false, two, false, 0, out int noOwner, out var noWorkers);
            r.Add(("Sites: OwnerOnly with no owner does not destroy the cycle",
                   noOwner == 0 && Sum(noOwner, noWorkers) == 100, $"total {Sum(noOwner, noWorkers)}"));
            SiteStore.PlanShares("SplitTotal", 100, true, solo, false, 0, out int lone, out var loneWorkers);
            r.Add(("Sites: an owner working alone keeps the whole cycle",
                   lone == 100 && loneWorkers.Count == 0, $"owner {lone}"));

            var guildSite = new SiteEntry
            {
                OwnerKind = SiteEntry.OwnerGuildKind, OwnerUsername = "", ControllingGuild = "Wardens",
                OutpostState = SiteEntry.OutpostCaptured,
            };
            guildSite.Buildings.Add(new SiteBuilding { Kind = SiteBuilding.KindStorage, Level = 1, State = SiteBuilding.StateOperational });
            r.Add(("Sites: a guild site accepts its own production into storage",
                   SiteStore.StorableUnits(guildSite, "Wardens", 10) == 10,
                   "guild-owned output had nowhere to go while storage keyed off a personal account"));
            r.Add(("Sites: a guild site refuses to store a member's personal share",
                   SiteStore.StorableUnits(guildSite, "bo", 10) == 0,
                   "a worker's pay must not land in a warehouse the guild can empty"));
            r.Add(("Sites: a site with no owner stores nothing",
                   SiteStore.StorableUnits(new SiteEntry { OwnerKind = SiteEntry.OwnerNeutral }, "", 10) == 0, ""));
            return r;
        }
    }
}
