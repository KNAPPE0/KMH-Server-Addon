using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Roadworks.Dto;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Features.Roadworks
{
    // Drives projects end to end: the ladder and key checks in KmhRoadworksSelfTest catch no AdvanceProject guard.
    internal static class KmhRoadworksProgressSelfTest
    {
        private static int _nextTile = 770000;
        private static int Tile() => System.Threading.Interlocked.Add(ref _nextTile, 10);
        private static string User() => "selftest_rw_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static List<RoadTile> Route(int from, int count)
        {
            var route = new List<RoadTile>();
            for (int i = 0; i < count; i++) route.Add(new RoadTile { LayerId = 0, TileId = from + i });
            return route;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            if (!SitesConfig.Current.AllowRoadworks)
            {
                r.Add((Diagnostics.KmhSelfTestResult.Skipped("Roadworks: project progression",
                       "roadworks is turned off in this server's config")));
                return r;
            }

            // A silent seed failure would leave every cost and refund below measured against an empty vault.
            (int tile, string owner) Site(string tier)
            {
                int t = Tile();
                string owner = User();
                SiteStore.InsertSiteForTest(t, owner, 5, SiteArchetypes.Roadworks, 3);
                if (!TreasuryStore.DepositSilver(owner, 2_000_000, "selftest seed"))
                    throw new InvalidOperationException("could not seed the roadworks owner's treasury");
                return (t, owner);
            }

            void Cleanup(int tile, string owner)
            {
                SiteStore.RemoveSiteForTest(tile);
                TreasuryStore.PurgeOwnerForTest(owner);
                RoadworksStore.ClearForNewSeason();
            }

            foreach (string tier in new[] { RoadTiers.Trail, RoadTiers.Road, RoadTiers.Highway })
            {
                string display = RoadTiers.DisplayName(tier);

                Check($"Roadworks ({display}): a route is laid once, and laying it again costs nothing", () =>
                {
                    RoadworksStore.ClearForNewSeason();
                    (int tile, string owner) = Site(tier);
                    try
                    {
                        List<RoadTile> route = Route(tile + 1000, 4);          // 3 segments
                        long before = TreasuryStore.GetPersonalSilver(owner);
                        if (!RoadworksStore.TryStartProject(owner, tile, tier, route,
                                out RoadProject p, out string why)) return (false, "start: " + why);
                        long charged = before - TreasuryStore.GetPersonalSilver(owner);

                        int laid = RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(tier) * 10);
                        int segments = RoadworksStore.SegmentCount;

                        int again = RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(tier) * 10);
                        int segmentsAfter = RoadworksStore.SegmentCount;

                        bool secondRefused = !RoadworksStore.TryStartProject(owner, tile, tier, route,
                                                 out RoadProject _, out string why2);

                        bool ok = charged == RoadTiers.SilverPerSegment(tier) * 3
                               && laid == 3 && segments == 3 && again == 0 && segmentsAfter == 3 && secondRefused;
                        return (ok, ok ? $"3 segments charged {charged} and laid once; the repeat laid 0 ({why2})"
                                      : $"charged={charged}, laid={laid}, segments={segments}, "
                                        + $"repeatLaid={again}, segmentsAfter={segmentsAfter}, "
                                        + $"secondProjectRefused={secondRefused}");
                    }
                    finally { Cleanup(tile, owner); }
                });

                Check($"Roadworks ({display}): a finished project stops building and owes no escrow", () =>
                {
                    RoadworksStore.ClearForNewSeason();
                    (int tile, string owner) = Site(tier);
                    try
                    {
                        List<RoadTile> route = Route(tile + 2000, 3);          // 2 segments
                        if (!RoadworksStore.TryStartProject(owner, tile, tier, route,
                                out RoadProject p, out string why)) return (false, "start: " + why);

                        bool reservedWhileBuilding = RoadworksStore.ReservedSilverFor(owner) > 0;
                        RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(tier) * 5);

                        RoadProject after = null;
                        foreach (RoadProject q in RoadworksStore.ProjectsFor(owner)) if (q.Id == p.Id) after = q;
                        bool complete = after != null && after.State == RoadProject.StateComplete;
                        // A complete project reserves nothing: its escrow was spent on the segments it laid.
                        bool reservesNothing = RoadworksStore.ReservedSilverFor(owner) == 0;
                        bool escrowSane = after != null && after.EscrowSilverSpent <= after.EscrowSilver;

                        bool ok = reservedWhileBuilding && complete && reservesNothing && escrowSane;
                        return (ok, ok ? "building reserves, complete reserves nothing, spend never exceeds escrow"
                                      : $"reservedWhileBuilding={reservedWhileBuilding}, complete={complete} "
                                        + $"(state={after?.State}), reservesNothing={reservesNothing}, "
                                        + $"spent={after?.EscrowSilverSpent}/{after?.EscrowSilver}");
                    }
                    finally { Cleanup(tile, owner); }
                });

                Check($"Roadworks ({display}): a restart mid-route resumes without relaying what is built", () =>
                {
                    RoadworksStore.ClearForNewSeason();
                    (int tile, string owner) = Site(tier);
                    try
                    {
                        List<RoadTile> route = Route(tile + 3000, 5);          // 4 segments
                        if (!RoadworksStore.TryStartProject(owner, tile, tier, route,
                                out RoadProject p, out string why)) return (false, "start: " + why);

                        int firstPass = RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(tier) * 2);
                        if (!RoadworksStore.SaveToDisk()) return (false, "save");
                        int builtBefore = RoadworksStore.SegmentCount;
                        RoadworksStore.LoadFromDisk();
                        int builtAfterLoad = RoadworksStore.SegmentCount;

                        int secondPass = RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(tier) * 10);
                        int total = RoadworksStore.SegmentCount;

                        bool ok = firstPass == 2 && builtBefore == 2 && builtAfterLoad == 2
                               && secondPass == 2 && total == 4;
                        return (ok, ok ? "2 laid, reloaded, 2 more laid, 4 in the world - none twice"
                                      : $"firstPass={firstPass}, beforeReload={builtBefore}, "
                                        + $"afterReload={builtAfterLoad}, secondPass={secondPass}, total={total} "
                                        + "- A RESTART RELAID OR LOST ROAD");
                    }
                    finally { Cleanup(tile, owner); }
                });
            }

            Check("Roadworks: an upgrade cannot resurrect a lower tier over a segment already built higher", () =>
            {
                RoadworksStore.ClearForNewSeason();
                (int tile, string owner) = Site(RoadTiers.Highway);
                try
                {
                    List<RoadTile> route = Route(tile + 4000, 3);
                    if (!RoadworksStore.TryStartProject(owner, tile, RoadTiers.Highway, route,
                            out RoadProject p, out string why)) return (false, "start: " + why);
                    RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(RoadTiers.Highway) * 10);

                    // Every segment already exists, so Trail has nothing to build that could overwrite the Highway.
                    bool refused = !RoadworksStore.TryStartProject(owner, tile, RoadTiers.Trail, route,
                                       out RoadProject _, out string why2);
                    string tierStill = null;
                    foreach (RoadSegment sgm in RoadworksStore.AllSegments()) tierStill = sgm.Tier;

                    bool ok = refused && tierStill == RoadTiers.Highway;
                    return (ok, ok ? $"the lower tier is refused ({why2}); the world still holds Highway"
                                  : $"trailRefused={refused}, tierInWorld={tierStill}");
                }
                finally { Cleanup(tile, owner); }
            });

            Check("Roadworks: cancelling returns only the escrow that was never spent", () =>
            {
                RoadworksStore.ClearForNewSeason();
                (int tile, string owner) = Site(RoadTiers.Road);
                try
                {
                    List<RoadTile> route = Route(tile + 5000, 5);              // 4 segments
                    long before = TreasuryStore.GetPersonalSilver(owner);
                    if (!RoadworksStore.TryStartProject(owner, tile, RoadTiers.Road, route,
                            out RoadProject p, out string why)) return (false, "start: " + why);
                    long charged = before - TreasuryStore.GetPersonalSilver(owner);

                    RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(RoadTiers.Road) * 2);
                    long beforeCancel = TreasuryStore.GetPersonalSilver(owner);
                    bool cancelled = RoadworksStore.CancelProject(owner, p.Id, out int refunded, out string cwhy);
                    long back = TreasuryStore.GetPersonalSilver(owner) - beforeCancel;
                    bool twice = RoadworksStore.CancelProject(owner, p.Id, out int _, out string _);
                    long backAgain = TreasuryStore.GetPersonalSilver(owner) - beforeCancel - back;

                    int perSegment = RoadTiers.SilverPerSegment(RoadTiers.Road);
                    bool ok = cancelled && charged == perSegment * 4 && refunded == perSegment * 2
                           && back == perSegment * 2 && !twice && backAgain == 0
                           && RoadworksStore.SegmentCount == 2;
                    return (ok, ok ? $"charged {charged} for 4, laid 2, refunded {refunded}; the repeat returns nothing"
                                  : $"cancelled={cancelled} ({cwhy}), charged={charged}, refunded={refunded}, "
                                    + $"credited={back}, repeat={twice}, creditedAgain={backAgain}, "
                                    + $"segments={RoadworksStore.SegmentCount}");
                }
                finally { Cleanup(tile, owner); }
            });

            Check("Roadworks: a cancelled project cannot be advanced back into building", () =>
            {
                RoadworksStore.ClearForNewSeason();
                (int tile, string owner) = Site(RoadTiers.Trail);
                try
                {
                    List<RoadTile> route = Route(tile + 6000, 4);
                    if (!RoadworksStore.TryStartProject(owner, tile, RoadTiers.Trail, route,
                            out RoadProject p, out string why)) return (false, "start: " + why);
                    if (!RoadworksStore.CancelProject(owner, p.Id, out int _, out string cwhy))
                        return (false, "cancel: " + cwhy);

                    int segmentsAfterCancel = RoadworksStore.SegmentCount;
                    int laid = RoadworksStore.AdvanceProject(p.Id, RoadTiers.WorkPerSegment(RoadTiers.Trail) * 10);
                    bool reservesNothing = RoadworksStore.ReservedSilverFor(owner) == 0;

                    bool ok = laid == 0 && RoadworksStore.SegmentCount == segmentsAfterCancel && reservesNothing;
                    return (ok, ok ? "a cancelled project lays nothing and reserves nothing"
                                  : $"laidAfterCancel={laid}, segments={RoadworksStore.SegmentCount} "
                                    + $"(was {segmentsAfterCancel}), reservesNothing={reservesNothing}");
                }
                finally { Cleanup(tile, owner); }
            });

            Check("Roadworks: two projects over the same stretch lay it once and each pay for their own", () =>
            {
                // Without the already-built guard the shared stretch is laid twice and overspends the second escrow.
                RoadworksStore.ClearForNewSeason();
                (int tile, string owner) = Site(RoadTiers.Road);
                try
                {
                    int baseTile = tile + 7000;
                    List<RoadTile> a = Route(baseTile, 3);          // baseTile .. baseTile+2   (2 segments)
                    List<RoadTile> b = Route(baseTile + 1, 3);      // baseTile+1 .. baseTile+3 (2 segments, 1 shared)

                    long before = TreasuryStore.GetPersonalSilver(owner);
                    if (!RoadworksStore.TryStartProject(owner, tile, RoadTiers.Road, a,
                            out RoadProject pa, out string wa)) return (false, "start a: " + wa);
                    if (!RoadworksStore.TryStartProject(owner, tile, RoadTiers.Road, b,
                            out RoadProject pb, out string wb)) return (false, "start b: " + wb);
                    long charged = before - TreasuryStore.GetPersonalSilver(owner);

                    int laidA = RoadworksStore.AdvanceProject(pa.Id, RoadTiers.WorkPerSegment(RoadTiers.Road) * 10);
                    int laidB = RoadworksStore.AdvanceProject(pb.Id, RoadTiers.WorkPerSegment(RoadTiers.Road) * 10);
                    int world = RoadworksStore.SegmentCount;

                    bool escrowSane = true;
                    string spend = "";
                    foreach (RoadProject q in RoadworksStore.ProjectsFor(owner))
                    {
                        if (q.EscrowSilverSpent > q.EscrowSilver) escrowSane = false;
                        spend += $"{q.Id}:{q.EscrowSilverSpent}/{q.EscrowSilver} ";
                    }

                    // Three distinct stretches exist; the shared one was laid by whichever project reached it.
                    int perSegment = RoadTiers.SilverPerSegment(RoadTiers.Road);
                    bool ok = charged == perSegment * 4 && laidA == 2 && laidB == 1 && world == 3 && escrowSane;
                    return (ok, ok ? $"4 segments paid for, 3 distinct laid ({laidA} + {laidB}), no project overspent"
                                  : $"charged={charged}, laidA={laidA}, laidB={laidB}, world={world} "
                                    + $"(expected 3), escrowSane={escrowSane} [{spend.Trim()}] - "
                                    + "A SHARED STRETCH WAS LAID OR CHARGED TWICE");
                }
                finally { Cleanup(tile, owner); }
            });

            Check("Roadworks: an unknown project id advances nothing", () =>
            {
                RoadworksStore.ClearForNewSeason();
                int laid = RoadworksStore.AdvanceProject(987654321, 1_000_000);
                bool zeroWork = RoadworksStore.AdvanceProject(987654321, 0) == 0;
                bool negative = RoadworksStore.AdvanceProject(987654321, -5) == 0;
                bool ok = laid == 0 && zeroWork && negative;
                return (ok, ok ? "no project, no road" : $"laid={laid}, zero={zeroWork}, negative={negative}");
            });

            return r;
        }
    }
}
