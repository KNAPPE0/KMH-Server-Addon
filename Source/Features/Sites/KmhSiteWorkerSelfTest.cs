using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // A slot is real production: a pre-migration username-only row must not hold a second one beside a named colonist.
    internal static class KmhSiteWorkerSelfTest
    {
        // Far outside any tile a fixture uses, so a leftover site cannot make a check pass by accident.
        private static int _nextTile = 880000;
        private static int Tile() => System.Threading.Interlocked.Increment(ref _nextTile);
        private static string User(string tag) => $"selftest_sw{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            int Workers(int tile) => SiteStore.WorkerKeysForTest(tile).Count;

            Check("Sites: one colonist cannot be worked by two accounts at once", () =>
            {
                int tile = Tile();
                string a = User("a"), b = User("b");
                SiteStore.InsertSiteForTest(tile, a, 5);
                try
                {
                    var first = SiteStore.JoinWorker(a, tile, 5, "Rusty", 4242);
                    // The same pawn id from a different account is either a collision or a claim; both lose.
                    var second = SiteStore.JoinWorker(b, tile, 5, "Rusty", 4242);
                    var third = SiteStore.JoinWorker(b, tile, 5, "Mara", 4243);
                    bool ok = first.ok && !second.ok && third.ok && Workers(tile) == 2;
                    return (ok, ok ? "the second account is refused; a different colonist joins"
                                  : $"first={first.ok}, sameePawnOther={second.ok} ({second.reason}), "
                                    + $"otherPawn={third.ok}, workers={Workers(tile)} (expected 2)");
                }
                finally { SiteStore.RemoveSiteForTest(tile); }
            });

            Check("Sites: a site stops taking workers at its capacity", () =>
            {
                int tile = Tile();
                string owner = User("o");
                SiteStore.InsertSiteForTest(tile, owner, 2);
                try
                {
                    // Capacity is derived live from perks and housing, so the site's own number is only a floor.
                    int cap = SiteStore.MaxWorkersLive(SiteStore.SiteForTest(tile));
                    int joined = 0;
                    string lastReason = "";
                    for (int i = 0; i < cap + 3; i++)
                    {
                        var res = SiteStore.JoinWorker(User("w" + i), tile, 5, "Pawn" + i, 5000 + i);
                        if (res.ok) joined++; else lastReason = res.reason;
                    }
                    bool ok = joined == cap && Workers(tile) == cap && lastReason.Contains("full");
                    return (ok, ok ? $"{cap} joined, the next was refused: {lastReason}"
                                  : $"cap={cap}, joined={joined}, workers={Workers(tile)}, lastReason={lastReason}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); }
            });

            Check("Sites: one player cannot fill a site with their own colonists", () =>
            {
                int perPlayer = SitesConfig.Current.Limits.WorkersPerPlayerPerSite;
                if (perPlayer <= 0)
                    return (true, "no per-player limit is configured on this server");

                int tile = Tile();
                string owner = User("o"), who = User("p");
                SiteStore.InsertSiteForTest(tile, owner, 20);
                try
                {
                    int joined = 0;
                    string lastReason = "";
                    for (int i = 0; i < perPlayer + 2; i++)
                    {
                        var res = SiteStore.JoinWorker(who, tile, 5, "Pawn" + i, 6000 + i);
                        if (res.ok) joined++; else lastReason = res.reason;
                    }
                    bool ok = joined == perPlayer && Workers(tile) == perPlayer;
                    return (ok, ok ? $"{perPlayer} of that player's colonists accepted, the rest refused: {lastReason}"
                                  : $"limit={perPlayer}, joined={joined}, workers={Workers(tile)}, last={lastReason}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); }
            });

            Check("Sites: a migrated username-only worker does not keep a slot beside its own colonist", () =>
            {
                // Old rows were keyed per account; one surviving a named colonist takes two slots and is paid twice.
                int tile = Tile();
                string owner = User("o"), who = User("m");
                SiteStore.InsertSiteForTest(tile, owner, 5);
                try
                {
                    SiteStore.InsertLegacyWorkerForTest(tile, who);
                    int before = Workers(tile);

                    var res = SiteStore.JoinWorker(who, tile, 5, "Rusty", 7777);
                    List<string> keys = SiteStore.WorkerKeysForTest(tile);
                    bool legacyGone = !keys.Contains(who);
                    bool perPawnThere = keys.Contains(SiteWorkerKey.For(who, 7777));

                    bool ok = before == 1 && res.ok && keys.Count == 1 && legacyGone && perPawnThere;
                    return (ok, ok ? "the legacy row was replaced, not added to"
                                  : $"before={before}, joined={res.ok} ({res.reason}), after={keys.Count} "
                                    + $"[{string.Join(", ", keys)}], legacyRemoved={legacyGone}, perPawn={perPawnThere} "
                                    + "- ONE PLAYER HOLDS TWO SLOTS ON ONE SITE");
                }
                finally { SiteStore.RemoveSiteForTest(tile); }
            });

            Check("Sites: rejoining the same colonist refreshes it rather than taking another slot", () =>
            {
                int tile = Tile();
                string owner = User("o"), who = User("j");
                SiteStore.InsertSiteForTest(tile, owner, 5);
                try
                {
                    var first = SiteStore.JoinWorker(who, tile, 5, "Rusty", 8100);
                    var again = SiteStore.JoinWorker(who, tile, 9, "Rusty Renamed", 8100);
                    List<string> keys = SiteStore.WorkerKeysForTest(tile);
                    bool ok = first.ok && again.ok && keys.Count == 1;
                    return (ok, ok ? "a reconnect updates the existing row"
                                  : $"first={first.ok}, again={again.ok} ({again.reason}), workers={keys.Count}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); }
            });

            Check("Sites: leaving takes only the colonist named, and leaving twice is refused", () =>
            {
                int tile = Tile();
                string owner = User("o"), who = User("l");
                SiteStore.InsertSiteForTest(tile, owner, 5);
                try
                {
                    SiteStore.JoinWorker(who, tile, 5, "Rusty", 8200);
                    SiteStore.JoinWorker(who, tile, 5, "Mara", 8201);
                    int both = Workers(tile);

                    var left = SiteStore.LeaveWorker(who, tile, 8200);
                    int afterOne = Workers(tile);
                    var twice = SiteStore.LeaveWorker(who, tile, 8200);
                    // No pawn named: every colonist that account has on the site comes off at once.
                    var all = SiteStore.LeaveWorker(who, tile);
                    int afterAll = Workers(tile);

                    bool ok = both == 2 && left.ok && afterOne == 1 && !twice.ok && all.ok && afterAll == 0;
                    return (ok, ok ? "one colonist left, the repeat refused, then the account's rest came off"
                                  : $"both={both}, leftOne={left.ok}, afterOne={afterOne}, repeat={twice.ok}, "
                                    + $"leftAll={all.ok}, afterAll={afterAll}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); }
            });

            Check("Sites: a worker on a site that no longer exists is refused, not created", () =>
            {
                int tile = Tile();
                string who = User("g");
                var res = SiteStore.JoinWorker(who, tile, 5, "Ghost", 8300);
                var leave = SiteStore.LeaveWorker(who, tile, 8300);
                bool ok = !res.ok && !leave.ok;
                return (ok, ok ? "no site, no worker" : $"joined={res.ok} ({res.reason}), left={leave.ok}");
            });

            Check("Sites: a worker survives a save and reload with its colonist still named", () =>
            {
                int tile = Tile();
                string owner = User("o"), who = User("s");
                SiteStore.InsertSiteForTest(tile, owner, 5);
                try
                {
                    if (!SiteStore.JoinWorker(who, tile, 7, "Rusty", 8400).ok) return (false, "join");
                    if (!SiteStore.SaveToDisk()) return (false, "save");
                    SiteStore.LoadFromDisk();

                    List<string> keys = SiteStore.WorkerKeysForTest(tile);
                    SiteEntry s = SiteStore.SiteForTest(tile);
                    bool named = s != null && s.WorkerProgress.TryGetValue(SiteWorkerKey.For(who, 8400),
                                                                          out WorkerProgressDto wp)
                              && wp != null && wp.PawnLoadId == 8400 && wp.PawnName == "Rusty" && !wp.Legacy;
                    bool ok = keys.Count == 1 && named;
                    return (ok, ok ? "the pawn id and name came back from disk"
                                  : $"workers={keys.Count}, named={named}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); }
            });

            return r;
        }
    }
}
