using System.Collections.Generic;
using KMHServerAddon.Features.Roadworks.Dto;

namespace KMHServerAddon.Features.Roadworks
{
    // Must stay pure: the smoke test runs this against a live server.
    internal static class KmhRoadworksSelfTest
    {
        private static RoadTile T(int tile, int layer = 0) => new RoadTile { TileId = tile, LayerId = layer };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("Roadworks: a segment key is order-independent",
                   RoadKeys.For(T(10), T(99)) == RoadKeys.For(T(99), T(10)), RoadKeys.For(T(10), T(99))));

            r.Add(("Roadworks: layer is part of tile identity",
                   RoadKeys.For(T(10, 0), T(11, 0)) != RoadKeys.For(T(10, 1), T(11, 1)),
                   "a bare tile id would conflate layers"));
            r.Add(("Roadworks: same number on different layers is not the same tile",
                   T(10, 0).Key != T(10, 1).Key, ""));

            // Tile ids are identifiers, not coordinates, so a numerically distant pair must key like any other.
            r.Add(("Roadworks: numerically distant tiles key normally",
                   RoadKeys.For(T(69), T(99109)) != null
                   && RoadKeys.For(T(69), T(99109)) == RoadKeys.For(T(99109), T(69)), ""));

            r.Add(("Roadworks: a self-loop is not a segment",
                   !RoadKeys.IsWellFormed(T(10), T(10)) && RoadKeys.IsWellFormed(T(10), T(11)), ""));
            r.Add(("Roadworks: an invalid endpoint is refused",
                   !RoadKeys.IsWellFormed(T(-1), T(11)) && !RoadKeys.IsWellFormed(null, T(11)), ""));

            r.Add(("Roadworks: tiers normalize, unknown falls to Trail",
                   RoadTiers.Normalize("__kmh_not_a_tier__") == RoadTiers.Trail
                   && RoadTiers.Normalize("HIGHWAY") == RoadTiers.Highway
                   && RoadTiers.Normalize(null) == RoadTiers.Trail, "unknown must not become a premium tier"));
            r.Add(("Roadworks: cost and work rise with tier",
                   RoadTiers.SilverPerSegment(RoadTiers.Trail) < RoadTiers.SilverPerSegment(RoadTiers.Road)
                   && RoadTiers.SilverPerSegment(RoadTiers.Road) < RoadTiers.SilverPerSegment(RoadTiers.Highway)
                   && RoadTiers.WorkPerSegment(RoadTiers.Highway) > RoadTiers.WorkPerSegment(RoadTiers.Trail), ""));

            r.Add(("Roadworks: tier display names are the KMH ladder",
                   RoadTiers.DisplayName(RoadTiers.Trail) == "Trail"
                   && RoadTiers.DisplayName(RoadTiers.Road) == "Road"
                   && RoadTiers.DisplayName(RoadTiers.Highway) == "Highway", ""));

            var route = new List<RoadTile> { T(1), T(2), T(3), T(4) };
            var built = new HashSet<string>(System.StringComparer.Ordinal) { RoadKeys.For(T(2), T(3)) };
            var newSegs = RoadworksStore.BuildableSegments(route, built);
            r.Add(("Roadworks: an already-built portion is not charged again",
                   newSegs.Count == 2, $"{newSegs.Count} of 3 segments are new"));
            r.Add(("Roadworks: a route that is fully built costs nothing",
                   RoadworksStore.BuildableSegments(route,
                       new HashSet<string>(System.StringComparer.Ordinal)
                       { RoadKeys.For(T(1),T(2)), RoadKeys.For(T(2),T(3)), RoadKeys.For(T(3),T(4)) }).Count == 0, ""));
            r.Add(("Roadworks: a route doubling back counts a segment once",
                   RoadworksStore.BuildableSegments(new List<RoadTile> { T(1), T(2), T(1) }, null).Count == 1, ""));
            r.Add(("Roadworks: a self-loop in a route is skipped",
                   RoadworksStore.BuildableSegments(new List<RoadTile> { T(1), T(1), T(2) }, null).Count == 1, ""));

            r.Add(("Roadworks: active projects are capped by site tier",
                   RoadworksStore.MaxActiveProjectsForTier(1) == 1
                   && RoadworksStore.MaxActiveProjectsForTier(2) == 2
                   && RoadworksStore.MaxActiveProjectsForTier(3) == 3
                   && RoadworksStore.MaxActiveProjectsForTier(9) == 3, ""));

            r.Add(("Roadworks: a Tier 1 site builds Trail only",
                   RoadTiers.AllowedAtSiteTier(RoadTiers.Trail, 1)
                   && !RoadTiers.AllowedAtSiteTier(RoadTiers.Road, 1)
                   && !RoadTiers.AllowedAtSiteTier(RoadTiers.Highway, 1), ""));
            r.Add(("Roadworks: a Tier 2 site adds Road but not Highway",
                   RoadTiers.AllowedAtSiteTier(RoadTiers.Road, 2)
                   && !RoadTiers.AllowedAtSiteTier(RoadTiers.Highway, 2), ""));
            r.Add(("Roadworks: a Tier 3 site builds the whole ladder",
                   RoadTiers.AllowedAtSiteTier(RoadTiers.Highway, 3)
                   && RoadTiers.AllowedAtSiteTier(RoadTiers.Highway, 9), "a higher tier than exists still caps at Highway"));
            r.Add(("Roadworks: a garbage tier reads as Trail, not as unrestricted",
                   RoadTiers.AllowedAtSiteTier("__kmh_not_a_tier__", 1)
                   && RoadTiers.RankOf("__kmh_not_a_tier__") == 1, ""));
            r.Add(("Roadworks: a site tier of zero or below still builds Trail",
                   RoadTiers.AllowedAtSiteTier(RoadTiers.Trail, 0)
                   && !RoadTiers.AllowedAtSiteTier(RoadTiers.Road, 0), "a malformed tier must not lock a site out entirely"));

            const string road = "roadworks";
            r.Add(("Roadworks: the owner of a Tier 3 Roadworks site may build Highway",
                   RoadworksStore.SiteRefusalFor(true, true, road, 3, "ada", RoadTiers.Highway) == null, ""));
            r.Add(("Roadworks: another player may not build from your site",
                   RoadworksStore.SiteRefusalFor(true, false, road, 3, "Borys", RoadTiers.Trail) == "That isn't your site.", ""));
            r.Add(("Roadworks: a caller with no name may not build",
                   RoadworksStore.SiteRefusalFor(true, true, road, 3, "", RoadTiers.Trail) != null,
                   "a blank caller must fail even when the site says manageable"));
            r.Add(("Roadworks: a non-Roadworks site cannot build roads",
                   RoadworksStore.SiteRefusalFor(true, true, "quarry", 3, "Ada", RoadTiers.Trail)
                       == "Only a Roadworks site can build roads.", ""));
            r.Add(("Roadworks: a tile with no site is refused",
                   RoadworksStore.SiteRefusalFor(false, false, "", 0, "Ada", RoadTiers.Trail) == "No site on that tile.", ""));
            r.Add(("Roadworks: a Tier 1 site is refused a Highway",
                   (RoadworksStore.SiteRefusalFor(true, true, road, 1, "Ada", RoadTiers.Highway) ?? "").Contains("cannot build"),
                   "the tier comes from the site, not from the request"));

            var personal = new Sites.Dto.SiteEntry
            { OwnerKind = Sites.Dto.SiteEntry.OwnerPlayer, OwnerUsername = "Ada", Archetype = road };
            var guildSite = new Sites.Dto.SiteEntry
            { OwnerKind = Sites.Dto.SiteEntry.OwnerGuildKind, OwnerUsername = "", ControllingGuild = "Wardens", Archetype = road };
            var neutral = new Sites.Dto.SiteEntry
            { OwnerKind = Sites.Dto.SiteEntry.OwnerNeutral, OwnerUsername = "", Archetype = road };

            r.Add(("Roadworks: a personal site answers to its owner and nobody else",
                   Sites.SiteOwnership.CanManage(personal, "ada")
                   && !Sites.SiteOwnership.CanManage(personal, "Borys"), "owner match is case-insensitive"));
            r.Add(("Roadworks: a guild site is never manageable through an empty owner name",
                   !Sites.SiteOwnership.CanManage(guildSite, ""),
                   "a guild site leaves OwnerUsername blank, so a name comparison must not decide"));
            r.Add(("Roadworks: a neutral or system location answers to nobody",
                   !Sites.SiteOwnership.CanManage(neutral, "Ada")
                   && !Sites.SiteOwnership.CanManage(neutral, ""), ""));
            r.Add(("Roadworks: a guild site refuses a caller whose current guild does not control it",
                   !Sites.SiteOwnership.CanManage(guildSite, "__kmh_no_such_player__"),
                   "membership is read live, so leaving the guild removes access on the next call"));

            r.Add(("Roadworks: reserved escrow reads zero for a player with no project",
                   RoadworksStore.ReservedSilverFor("__kmh_no_such_player__") == 0
                   && RoadworksStore.ReservedSilverFor("") == 0, ""));

            // The settled fixtures have spent != reserved on purpose, or the subtraction alone returns 0 and the state rule is never exercised.
            RoadProject building  = new RoadProject { State = RoadProject.StateBuilding,  EscrowSilver = 300, EscrowSilverSpent = 100 };
            RoadProject done      = new RoadProject { State = RoadProject.StateComplete,  EscrowSilver = 300, EscrowSilverSpent = 100 };
            RoadProject cancelled = new RoadProject { State = RoadProject.StateCancelled, EscrowSilver = 300, EscrowSilverSpent = 100 };
            r.Add(("Roadworks: a building project owes back only what it has not spent",
                   RoadworksStore.UnspentEscrow(building) == 200, ""));
            r.Add(("Roadworks: a settled project owes nothing even with escrow left over",
                   RoadworksStore.UnspentEscrow(done) == 0 && RoadworksStore.UnspentEscrow(cancelled) == 0
                   && RoadworksStore.UnspentEscrow(null) == 0, "a settled project cannot be cancelled, so nothing comes back"));
            r.Add(("Roadworks: escrow spent past its reservation never goes negative",
                   RoadworksStore.UnspentEscrow(new RoadProject
                   { State = RoadProject.StateBuilding, EscrowSilver = 100, EscrowSilverSpent = 250 }) == 0, ""));

            var mixed = new List<RoadProject>
            {
                new RoadProject { Id = 1, OwnerUsername = "Ada",   State = RoadProject.StateBuilding,  EscrowSilver = 300, EscrowSilverSpent = 100 },
                new RoadProject { Id = 2, OwnerUsername = "ada",   State = RoadProject.StateBuilding,  EscrowSilver = 50,  EscrowSilverSpent = 0   },
                new RoadProject { Id = 3, OwnerUsername = "Ada",   State = RoadProject.StateComplete,  EscrowSilver = 300, EscrowSilverSpent = 100 },
                new RoadProject { Id = 4, OwnerUsername = "Borys", State = RoadProject.StateBuilding,  EscrowSilver = 900, EscrowSilverSpent = 0   },
            };
            var (purgeIds, purgeBurn) = RoadworksStore.PurgeSelection(mixed, "ADA");
            r.Add(("Roadworks: a purge takes the owner's projects whatever the case of their name",
                   purgeIds.Count == 3 && !purgeIds.Contains(4), $"{purgeIds.Count} project(s), ids {string.Join(",", purgeIds)}"));
            r.Add(("Roadworks: a purge burns only unspent escrow, and none of another player's",
                   purgeBurn == 250, $"burned {purgeBurn}, expected 250 (200 + 50, settled project owes nothing)"));
            r.Add(("Roadworks: purging an unknown owner takes nothing",
                   RoadworksStore.PurgeSelection(mixed, "__kmh_no_such_player__").ids.Count == 0
                   && RoadworksStore.PurgeSelection(null, "Ada").ids.Count == 0, ""));

            var owned = new List<RoadProject>
            {
                new RoadProject { Id = 1, OwnerUsername = "Ada",   State = RoadProject.StateBuilding, EscrowSilver = 300, EscrowSilverSpent = 100 },
                new RoadProject { Id = 2, OwnerUsername = "ada",   State = RoadProject.StateBuilding, EscrowSilver = 50 },
                new RoadProject { Id = 3, OwnerUsername = "Borys", State = RoadProject.StateBuilding, EscrowSilver = 900 },
            };
            List<Dto.RoadProjectDto> mine = RoadworksStore.ProjectsForSnapshot(owned, "ADA");
            bool leaked = false; long unspent = 0;
            foreach (Dto.RoadProjectDto d in mine) { if (d.Id == 3) leaked = true; unspent += d.EscrowUnspent; }
            r.Add(("Roadworks: a snapshot carries only the caller's own projects",
                   mine.Count == 2 && !leaked, $"{mine.Count} project(s) for Ada, another player's leaked: {leaked}"));
            r.Add(("Roadworks: escrow shown is the caller's unspent, not their whole reservation",
                   unspent == 250, $"{unspent}, expected 250"));
            r.Add(("Roadworks: an unknown or blank caller gets no projects",
                   RoadworksStore.ProjectsForSnapshot(owned, "__kmh_no_such_player__").Count == 0
                   && RoadworksStore.ProjectsForSnapshot(owned, "").Count == 0, ""));

            var network = new List<RoadSegment>
            {
                new RoadSegment { A = T(1), B = T(2), Tier = RoadTiers.Highway },
                new RoadSegment { A = T(2), B = T(3), Tier = RoadTiers.Trail },
            };
            r.Add(("Roadworks: every road segment is world-visible",
                   RoadworksStore.SegmentsForSnapshot(network, true).Count == 2, ""));
            r.Add(("Roadworks: turning Roadworks off withholds every segment",
                   RoadworksStore.SegmentsForSnapshot(network, false).Count == 0,
                   "clients withdraw the roads they applied; the store still holds them, so it is reversible"));

            Dto.RoadworksSnapshot snap = RoadworksStore.BuildSnapshotFor("__kmh_no_such_player__");
            bool pricedAll = true;
            foreach (string tier in RoadTiers.All)
                if (!snap.SilverPerSegment.ContainsKey(tier)) pricedAll = false;
            r.Add(("Roadworks: the snapshot quotes a price for every tier",
                   pricedAll && snap.SilverPerSegment.Count == RoadTiers.All.Length,
                   $"{snap.SilverPerSegment.Count} of {RoadTiers.All.Length} tier(s) priced"));
            r.Add(("Roadworks: a player with no projects gets none and no escrow",
                   snap.Projects.Count == 0 && snap.EscrowSilver == 0, ""));
            r.Add(("Roadworks: the snapshot carries the route limit the server will enforce",
                   snap.MaxRouteSegments == Economy.EconomyConfig.Current.RoadworksMaxSegmentsPerProject,
                   "a client that quotes a different limit builds routes the server rejects"));

            // The planet's roads are identical for everyone, so a broadcast builds them once and hands the same list out.
            Dto.RoadworksSnapshot a1 = RoadworksStore.BuildSnapshotFor("__kmh_no_such_player__");
            Dto.RoadworksSnapshot a2 = RoadworksStore.BuildSnapshotFor("__kmh_other_player__");
            r.Add(("Roadworks: two players in one broadcast share one segment list",
                   ReferenceEquals(a1.Segments, a2.Segments),
                   "rebuilding the whole network per recipient is what this avoids"));
            r.Add(("Roadworks: a shared segment list still reports the real network",
                   a1.Segments.Count == RoadworksStore.SegmentsForSnapshot(
                       RoadworksStore.SegmentsForTest(), a1.AllowRoadworks).Count,
                   $"{a1.Segments.Count} shared vs {RoadworksStore.SegmentsForTest().Count} held"));
            // Sharing is only safe while a change throws the copy away; a cache nothing invalidates would pass the check above.
            RoadworksStore.BumpRevisionForTest();
            Dto.RoadworksSnapshot a3 = RoadworksStore.BuildSnapshotFor("__kmh_no_such_player__");
            r.Add(("Roadworks: a change to the network throws the shared list away",
                   !ReferenceEquals(a1.Segments, a3.Segments) && a3.Segments.Count == a1.Segments.Count,
                   "a shared list that outlives a revision would hand clients a road map that no longer exists"));

            var s = new RoadNetworkState();
            r.Add(("Roadworks: a new network is empty and at revision 0",
                   s.Segments.Count == 0 && s.Projects.Count == 0 && s.Revision == 0 && s.NextProjectId == 1, ""));

            return r;
        }
    }
}
