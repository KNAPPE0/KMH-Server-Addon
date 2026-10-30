using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Delivery;
using KMHServerAddon.Features.Roadworks;
using KMHServerAddon.Features.Roadworks.Dto;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Features.Sites
{
    // Value crosses two files with a crash window; each case cuts at one boundary, reloads, and must match a clean run.
    internal static class KmhSiteRestartSelfTest
    {
        private const string Steel = "Steel";
        private static int _nextTile = 8_910_000;
        private static int Tile() => System.Threading.Interlocked.Add(ref _nextTile, 10);
        private static string User(string tag) => $"selftest_sr{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static int SteelOf(string user)
        {
            int n = 0;
            var snap = TreasuryStore.GetSnapshotFor(user);
            if (snap?.Items != null)
                foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
            return n;
        }

        private static int Stored(int tile, string def)
        {
            SiteEntry s = SiteStore.SiteForTest(tile);
            return s?.StoredItems != null && s.StoredItems.TryGetValue(def, out int n) ? n : 0;
        }

        private static void Restart()
        {
            SiteStore.LoadFromDisk();
            RoadworksStore.LoadFromDisk();
            TreasuryStore.LoadFromDisk();
            DeliveryStore.LoadFromDisk();
            KmhTransactionRepository.LoadFromDisk();
        }

        private static void Recover(params string[] players)
        {
            SiteStore.RecoverOnBoot();
            RoadworksStore.RecoverOnBoot();
            foreach (string p in players) KmhTransactionRepository.RecoverPendingForTest(p);
        }

        private static Action<string> Die(string phase) => ph => { if (ph == phase) throw new OperationCanceledException("selftest: process died at " + ph); };

        private static void ClearHooks()
        {
            SiteStore.AtCyclePhaseForTest = null; SiteStore.AtPayoutPhaseForTest = null;
            SiteStore.AtCollectPhaseForTest = null; SiteStore.AtPurchasePhaseForTest = null;
            RoadworksStore.AtStartPhaseForTest = null; RoadworksStore.AtPayoutPhaseForTest = null;
        }

        // A due site with one working colonist from another account, so the cycle pays an owner and a worker.
        private static void ProducingSite(int tile, string owner, string worker, string def, int amount)
        {
            SiteStore.InsertSiteForTest(tile, owner, 4);
            SiteEntry s = SiteStore.SiteForTest(tile);
            s.ItemDefName = def;
            s.BaseAmountPerCycle = amount;
            s.LastRewardUtcTicks = DateTime.UtcNow.Ticks - TimeSpan.FromHours(3).Ticks;
            s.OwnerRewardDestination = SiteEntry.DestTreasury;
            string key = SiteWorkerKey.For(worker, 4242);
            s.Workers.Add(key);
            s.WorkerProgress[key] = new WorkerProgressDto { PawnLoadId = 4242, Username = worker, Destination = SiteEntry.DestTreasury };
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { ClearHooks(); }
            }

            // The offline runner never boots KmhDataMeta; without an identity no colony delivery can be minted at all.
            if (string.IsNullOrEmpty(KmhServerIdentity.Id)) KmhDataMeta.SetInstanceIdForTest("selftestsrv");

            (long Owner, long Worker, long Guild, bool TimerKept, int Owed) Cycle(string phase, bool restart, bool disbandFirst)
            {
                string o = User("o"), w = User("w"), g = "selftest sr " + Guid.NewGuid().ToString("N").Substring(0, 6);
                int tile = Tile();
                try
                {
                    if (!Guilds.GuildStore.CreateGuildAndJoinAsAdmin(o, g, out string why)) throw new InvalidOperationException("guild: " + why);
                    if (!Guilds.GuildStore.SaveSettings(o, new Guilds.Dto.GuildSettingsDto { SiteRewardSilverTaxPercent = 20 }))
                        throw new InvalidOperationException("tax setting");
                    ProducingSite(tile, o, w, "Silver", 200);
                    if (!SiteStore.SaveToDisk()) throw new InvalidOperationException("save");
                    SiteStore.AtCyclePhaseForTest = Die(phase);
                    SiteStore.AtPayoutPhaseForTest = Die(phase);
                    try { SiteStore.RunRewardCycle(); } catch (OperationCanceledException) { }
                    ClearHooks();
                    long cycleAt = SiteStore.SiteForTest(tile)?.LastRewardUtcTicks ?? 0;
                    // The guild going away before recovery: its tax leg falls back to the player it was taken from.
                    if (disbandFirst) Guilds.GuildStore.Leave(o, out _);
                    if (restart) Restart();
                    Recover();
                    Recover();
                    bool timerKept = (SiteStore.SiteForTest(tile)?.LastRewardUtcTicks ?? -1) == cycleAt && cycleAt > DateTime.UtcNow.Ticks - TimeSpan.FromHours(1).Ticks;
                    // A second cycle right away finds nothing due, so recovery never stands in for a fresh cycle.
                    SiteStore.RunRewardCycle();
                    return (TreasuryStore.GetPersonalSilver(o), TreasuryStore.GetPersonalSilver(w),
                            TreasuryStore.GetGuildSilver(g), timerKept, SiteStore.OwedCountForTest);
                }
                finally
                {
                    ClearHooks();
                    SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk();
                    Guilds.GuildStore.Leave(o, out _);
                    TreasuryStore.PurgeOwnerForTest(o); TreasuryStore.PurgeOwnerForTest(w); TreasuryStore.PurgeOwnerForTest(g);
                }
            }

            (long Owner, long Worker, long Guild, bool TimerKept, int Owed) baseline = default;
            Check("Site restart: an uninterrupted cycle pays owner, worker and the owner's guild tax", () =>
            {
                baseline = Cycle("none", false, false);
                bool ok = baseline.Owner > 0 && baseline.Worker > 0 && baseline.Guild > 0 && baseline.TimerKept && baseline.Owed == 0;
                return (ok, $"owner {baseline.Owner}, worker {baseline.Worker}, guild tax {baseline.Guild}, owed left {baseline.Owed}");
            });
            foreach (bool restart in new[] { false, true })
                foreach (string phase in new[] { "recorded", "credited", "cleared" })
                    Check($"Site restart: a production cycle cut at '{phase}'{(restart ? ", reloaded from disk" : "")} pays every share exactly once", () =>
                    {
                        var got = Cycle(phase, restart, false);
                        bool ok = got.Owner == baseline.Owner && got.Worker == baseline.Worker && got.Guild == baseline.Guild && got.TimerKept && got.Owed == 0;
                        return (ok, $"owner {got.Owner}/{baseline.Owner}, worker {got.Worker}/{baseline.Worker}, guild {got.Guild}/{baseline.Guild}, timerKept={got.TimerKept}, owed={got.Owed}");
                    });
            Check("Site restart: a guild tax recorded for a guild that disbands before it is paid goes back to the player, once", () =>
            {
                var got = Cycle("recorded", true, true);
                bool ok = got.Owner == baseline.Owner + baseline.Guild && got.Guild == 0 && got.Worker == baseline.Worker && got.Owed == 0;
                return (ok, $"owner {got.Owner} (want {baseline.Owner + baseline.Guild}), guild {got.Guild}, worker {got.Worker}, owed {got.Owed}");
            });

            foreach (bool restart in new[] { false, true })
                foreach (string phase in new[] { "none", "collected", "recorded", "credited", "cleared" })
                    Check($"Site restart: a storage collection cut at '{phase}'{(restart ? ", reloaded from disk" : "")} hands the goods over exactly once", () =>
                    {
                        string o = User("c");
                        int tile = Tile();
                        try
                        {
                            SiteStore.InsertSiteForTest(tile, o, 4);
                            SiteStore.SiteForTest(tile).StoredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Steel] = 50, ["Silver"] = 30 };
                            if (!SiteStore.SaveToDisk()) return (false, "save");
                            SiteStore.AtCollectPhaseForTest = ph => { if (phase == "collected" && ph == "recorded") throw new OperationCanceledException("selftest"); };
                            SiteStore.AtPayoutPhaseForTest = Die(phase);
                            try { SiteStore.CollectStorage(o, tile); } catch (OperationCanceledException) { }
                            ClearHooks();
                            if (restart) Restart();
                            Recover(); Recover();
                            bool ok = SteelOf(o) == 50 && TreasuryStore.GetPersonalSilver(o) == 30
                                   && Stored(tile, Steel) == 0 && Stored(tile, "Silver") == 0 && SiteStore.UnpaidOwedForTest(o) == (0, 0);
                            return (ok, $"vault {SteelOf(o)} steel / {TreasuryStore.GetPersonalSilver(o)}s, storage {Stored(tile, Steel)}/{Stored(tile, "Silver")}");
                        }
                        finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); }
                    });

            Check("Site restart: a collection the sites file cannot write leaves storage intact and owes nothing", () =>
            {
                string o = User("c");
                int tile = Tile();
                try
                {
                    SiteStore.InsertSiteForTest(tile, o, 4);
                    SiteStore.SiteForTest(tile).StoredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Steel] = 50 };
                    if (!SiteStore.SaveToDisk()) return (false, "save");
                    (bool ok1, string why) res;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SitesFile ? "selftest" : null))
                        res = SiteStore.CollectStorage(o, tile);
                    // Live, not just on disk: the running server keeps this state, and its next save writes it.
                    int live = Stored(tile, Steel);
                    if (!SiteStore.SaveToDisk()) return (false, "later save");
                    Restart();
                    Recover();
                    bool ok = !res.ok1 && live == 50 && Stored(tile, Steel) == 50 && SteelOf(o) == 0 && SiteStore.UnpaidOwedForTest(o) == (0, 0);
                    return (ok, $"refused={!res.ok1} storage live={live} afterSaveAndRestart={Stored(tile, Steel)} vault={SteelOf(o)}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); }
            });

            Check("Site restart: a guild collection whose guild disbands before it is paid goes back into storage, once", () =>
            {
                string a = User("g"), g = "selftest sr " + Guid.NewGuid().ToString("N").Substring(0, 6);
                int tile = Tile();
                try
                {
                    if (!Guilds.GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "guild: " + why);
                    SiteStore.InsertSiteForTest(tile, "", 4);
                    SiteEntry s = SiteStore.SiteForTest(tile);
                    s.OwnerKind = SiteEntry.OwnerGuildKind; s.ControllingGuild = g;
                    s.StoredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Steel] = 40 };
                    SiteStore.BindGuildForTest(tile, Guilds.GuildIdentity.Of(g));
                    if (!SiteStore.SaveToDisk()) return (false, "save");
                    SiteStore.AtPayoutPhaseForTest = Die("recorded");
                    try { SiteStore.CollectStorage(a, tile); } catch (OperationCanceledException) { }
                    ClearHooks();
                    Guilds.GuildStore.Leave(a, out _);
                    Restart();
                    Recover(); Recover();
                    bool ok = Stored(tile, Steel) == 40 && !TreasuryStore.HasVaultForTest(g) && SteelOf(a) == 0 && SiteStore.OwedCountForTest == 0;
                    return (ok, $"storage={Stored(tile, Steel)} orphanVault={TreasuryStore.HasVaultForTest(g)} founder={SteelOf(a)}");
                }
                finally { SiteStore.BindGuildForTest(tile, null); SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); Guilds.GuildStore.Leave(a, out _); TreasuryStore.PurgeOwnerForTest(a); TreasuryStore.PurgeOwnerForTest(g); }
            });

            foreach (string phase in new[] { "recorded", "credited" })
                Check($"Site restart: colony-bound output cut at '{phase}' and reloaded is delivered once - minted or banked, never both", () =>
                {
                    string who = User("d");
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = who });
                    var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                    try
                    {
                        SiteStore.OnlineClientForTest = u => string.Equals(u, who, StringComparison.OrdinalIgnoreCase) ? c : null;
                        SubProtocol.KmhRouter.OutboundForTest = (cl, kind, data) => { };
                        SiteStore.AtPayoutPhaseForTest = Die(phase);
                        try { SiteStore.DeliverForTest(who, Tile(), Steel, 25); } catch (OperationCanceledException) { }
                        ClearHooks();
                        SiteStore.OnlineClientForTest = null;
                        Restart();
                        Recover(); Recover();
                        int minted = 0;
                        foreach (OutboundDelivery d in DeliveryStore.OwedFor(who)) if (d.Items != null && d.Items.TryGetValue(Steel, out int q)) minted += q;
                        int banked = SteelOf(who);
                        // Cut before the mint, the vault holds it; cut after, the colony delivery does - and never both.
                        bool where = phase == "recorded" ? banked == 25 && minted == 0 : minted == 25 && banked == 0;
                        bool ok = where && SiteStore.UnpaidOwedForTest(who) == (0, 0);
                        return (ok, $"minted={minted} banked={banked}");
                    }
                    finally
                    {
                        SiteStore.OnlineClientForTest = null;
                        SubProtocol.KmhRouter.OutboundForTest = hadOut;
                        DeliveryStore.PurgeUser(who);
                        TreasuryStore.PurgeOwnerForTest(who);
                    }
                });

            foreach (string phase in new[] { "charged", "saved" })
                Check($"Site restart: a repair cut at '{phase}' and reloaded is either refunded or kept - charged once at most", () =>
                {
                    string o = User("p");
                    int tile = Tile();
                    try
                    {
                        SiteStore.InsertSiteForTest(tile, o, 4);
                        SiteStore.SiteForTest(tile).Stability = 50;
                        if (!SiteStore.SaveToDisk() || !TreasuryStore.DepositSilver(o, 5000, "selftest seed")) return (false, "seed");
                        int cost = SiteStability.RepairCost(50, SitesConfig.Current.Costs.RepairPerPoint);
                        if (cost <= 0) return (false, "repairs are free in this config - the charge path cannot be measured");
                        SiteStore.AtPurchasePhaseForTest = Die(phase);
                        try { SiteStore.RepairSite(o, tile); } catch (OperationCanceledException) { }
                        ClearHooks();
                        Restart();
                        Recover(o); Recover(o);
                        long silver = TreasuryStore.GetPersonalSilver(o);
                        int stability = SiteStore.SiteForTest(tile)?.Stability ?? -1;
                        bool ok = phase == "charged" ? silver == 5000 && stability == 50 : silver == 5000 - cost && stability == SiteStability.Healthy;
                        return (ok, $"silver {silver} (cost {cost}), stability {stability}");
                    }
                    finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); }
                });

            Check("Site restart: a building the sites file cannot write is refunded at once and never built", () =>
            {
                string o = User("p");
                int tile = Tile();
                try
                {
                    SiteStore.InsertSiteForTest(tile, o, 4);
                    if (!SiteStore.SaveToDisk() || !TreasuryStore.DepositSilver(o, 5000, "selftest seed")) return (false, "seed");
                    if (SitesConfig.Current.Costs.BuildingSilver <= 0) return (false, "buildings are free in this config");
                    (bool ok1, string why) res;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SitesFile ? "selftest" : null))
                        res = SiteStore.AddBuilding(o, tile, SiteBuilding.KindStorage);
                    Restart();
                    Recover(o);
                    bool ok = !res.ok1 && TreasuryStore.GetPersonalSilver(o) == 5000 && (SiteStore.SiteForTest(tile)?.Buildings.Count ?? -1) == 0;
                    return (ok, $"refused={!res.ok1} silver={TreasuryStore.GetPersonalSilver(o)} buildings={SiteStore.SiteForTest(tile)?.Buildings.Count}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); }
            });

            if (!SitesConfig.Current.AllowRoadworks)
            {
                r.Add(Diagnostics.KmhSelfTestResult.Skipped("Site restart: roadworks", "roadworks is turned off in this server's config"));
                return r;
            }
            List<RoadTile> Route(int from, int count)
            {
                var route = new List<RoadTile>();
                for (int i = 0; i < count; i++) route.Add(new RoadTile { LayerId = 0, TileId = from + i });
                return route;
            }

            foreach (string phase in new[] { "charged", "saved" })
                Check($"Roadworks restart: a project start cut at '{phase}' and reloaded is refunded or funded - never both", () =>
                {
                    RoadworksStore.ClearForNewSeason();
                    string o = User("r");
                    int tile = Tile();
                    try
                    {
                        SiteStore.InsertSiteForTest(tile, o, 5, SiteArchetypes.Roadworks, 3);
                        if (!SiteStore.SaveToDisk() || !TreasuryStore.DepositSilver(o, 2_000_000, "selftest seed")) return (false, "seed");
                        RoadworksStore.AtStartPhaseForTest = Die(phase);
                        try { RoadworksStore.TryStartProject(o, tile, RoadTiers.Trail, Route(tile + 1000, 4), out _, out _); } catch (OperationCanceledException) { }
                        ClearHooks();
                        Restart();
                        Recover(o); Recover(o);
                        long silver = TreasuryStore.GetPersonalSilver(o);
                        int projects = RoadworksStore.ProjectsFor(o).Count;
                        long cost = RoadTiers.SilverPerSegment(RoadTiers.Trail) * 3;
                        bool ok = phase == "charged" ? silver == 2_000_000 && projects == 0 : silver == 2_000_000 - cost && projects == 1;
                        return (ok, $"silver {silver} (cost {cost}), projects {projects}");
                    }
                    finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); RoadworksStore.ClearForNewSeason(); }
                });

            foreach (string phase in new[] { "recorded", "credited", "cleared" })
                Check($"Roadworks restart: a cancel cut at '{phase}' and reloaded refunds the unbuilt escrow exactly once", () =>
                {
                    RoadworksStore.ClearForNewSeason();
                    string o = User("r");
                    int tile = Tile();
                    try
                    {
                        SiteStore.InsertSiteForTest(tile, o, 5, SiteArchetypes.Roadworks, 3);
                        if (!SiteStore.SaveToDisk() || !TreasuryStore.DepositSilver(o, 2_000_000, "selftest seed")) return (false, "seed");
                        if (!RoadworksStore.TryStartProject(o, tile, RoadTiers.Trail, Route(tile + 2000, 4), out RoadProject p, out string why)) return (false, "start: " + why);
                        RoadworksStore.AtPayoutPhaseForTest = Die(phase);
                        try { RoadworksStore.CancelProject(o, p.Id, out _, out _); } catch (OperationCanceledException) { }
                        ClearHooks();
                        Restart();
                        Recover(o); Recover(o);
                        bool ok = TreasuryStore.GetPersonalSilver(o) == 2_000_000 && RoadworksStore.UnpaidOwedForTest(o) == (0, 0);
                        return (ok, $"silver {TreasuryStore.GetPersonalSilver(o)} (want 2000000 - nothing was built)");
                    }
                    finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); RoadworksStore.ClearForNewSeason(); }
                });

            // Road progress after recovery must match an uninterrupted cycle exactly - not skipped, not doubled.
            (int Segment, double Progress) RoadCycle(string phase)
            {
                RoadworksStore.ClearForNewSeason();
                string o = User("r"), w = User("rw");
                int tile = Tile();
                try
                {
                    SiteStore.InsertSiteForTest(tile, o, 5, SiteArchetypes.Roadworks, 3);
                    SiteEntry s = SiteStore.SiteForTest(tile);
                    s.LastRewardUtcTicks = DateTime.UtcNow.Ticks - TimeSpan.FromHours(3).Ticks;
                    string key = SiteWorkerKey.For(w, 777);
                    s.Workers.Add(key);
                    s.WorkerProgress[key] = new WorkerProgressDto { PawnLoadId = 777, Username = w, Destination = SiteEntry.DestTreasury };
                    if (!SiteStore.SaveToDisk() || !TreasuryStore.DepositSilver(o, 2_000_000, "selftest seed")) throw new InvalidOperationException("seed");
                    if (!RoadworksStore.TryStartProject(o, tile, RoadTiers.Highway, Route(tile + 3000, 6), out RoadProject p, out string why))
                        throw new InvalidOperationException("start: " + why);
                    SiteStore.AtCyclePhaseForTest = Die(phase);
                    SiteStore.AtPayoutPhaseForTest = Die(phase);
                    try { SiteStore.RunRewardCycle(); } catch (OperationCanceledException) { }
                    ClearHooks();
                    Restart();
                    Recover(o); Recover(o);
                    RoadProject after = RoadworksStore.ProjectForTest(p.Id);
                    return (after?.CurrentSegment ?? -1, Math.Round(after?.CurrentProgress ?? -1, 6));
                }
                finally { ClearHooks(); SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); TreasuryStore.PurgeOwnerForTest(w); RoadworksStore.ClearForNewSeason(); }
            }
            (int Segment, double Progress) roadBase = default;
            Check("Roadworks restart: an uninterrupted cycle lays the site's road work", () =>
            {
                roadBase = RoadCycle("none");
                bool ok = roadBase.Segment > 0 || roadBase.Progress > 0;
                return (ok, $"segment {roadBase.Segment}, progress {roadBase.Progress}");
            });
            foreach (string phase in new[] { "recorded", "credited", "cleared" })
                Check($"Roadworks restart: a cycle's road work cut at '{phase}' and reloaded is laid exactly once", () =>
                {
                    var got = RoadCycle(phase);
                    bool ok = got.Segment == roadBase.Segment && got.Progress == roadBase.Progress;
                    return (ok, $"segment {got.Segment}/{roadBase.Segment}, progress {got.Progress}/{roadBase.Progress}");
                });

            Check("Site restart: a payout owed to a player whose economy is reset is dropped with it, not paid after a restart", () =>
            {
                string o = User("x"), w = User("xw");
                int tile = Tile();
                try
                {
                    ProducingSite(tile, o, w, Steel, 100);
                    if (!SiteStore.SaveToDisk()) return (false, "save");
                    SiteStore.AtCyclePhaseForTest = Die("recorded");
                    try { SiteStore.RunRewardCycle(); } catch (OperationCanceledException) { }
                    ClearHooks();
                    int dropped = SiteStore.DropOwedFor(o);
                    SiteStore.SaveToDisk();
                    Restart();
                    Recover();
                    bool ok = dropped >= 1 && SteelOf(o) == 0 && SteelOf(w) > 0;
                    return (ok, $"dropped={dropped} owner={SteelOf(o)} worker={SteelOf(w)}");
                }
                finally { SiteStore.RemoveSiteForTest(tile); SiteStore.SaveToDisk(); TreasuryStore.PurgeOwnerForTest(o); TreasuryStore.PurgeOwnerForTest(w); }
            });

            return r;
        }
    }
}
