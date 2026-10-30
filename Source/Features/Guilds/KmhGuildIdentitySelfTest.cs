using System;
using System.Collections.Generic;
using System.Threading;
using KMHServerAddon.Features.Sites;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Guilds
{
    // Economic ownership follows the guild's incarnation, never its name, or a dead guild's sites pay whoever takes the name.
    internal static class KmhGuildIdentitySelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }
            string Id() => Guid.NewGuid().ToString("N").Substring(0, 8);
            string NewUser(string tag) => "__kmh_gid_" + tag + "_" + Id();
            string NewGuild() => "selftest gid " + Id();
            int NewTile() => 8_300_000 + new Random(Guid.NewGuid().GetHashCode()).Next(0, 600_000);
            long Personal(string user) => TreasuryStore.GetSnapshotFor(user)?.SilverBalance ?? 0;

            void GuildSite(int tile, string guild, bool bind = true)
            {
                SiteStore.InsertSiteForTest(tile, "", 4);
                SiteEntry s = SiteStore.SiteForTest(tile);
                s.OwnerKind = SiteEntry.OwnerGuildKind;
                s.ControllingGuild = guild;
                s.Buildings = new List<SiteBuilding> { new SiteBuilding { Kind = SiteBuilding.KindStorage, Level = 1, State = SiteBuilding.StateOperational } };
                SiteStore.BindGuildForTest(tile, bind ? GuildIdentity.Of(guild) : null);
            }
            void Drop(int tile, params string[] owners)
            {
                SiteStore.BindGuildForTest(tile, null);
                SiteStore.RemoveSiteForTest(tile);
                foreach (string o in owners) TreasuryStore.PurgeOwnerForTest(o);
            }

            Check("Identity: a disbanded guild's site pays nobody, and a new guild of the same name neither manages nor is paid by it", () =>
            {
                string a = NewUser("a"), b = NewUser("b"), g = NewGuild();
                int tile = NewTile();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    GuildSite(tile, g);
                    int paidLive = SiteStore.DeliverGuildOwnedForTest(tile, g, "Silver", 30, SiteEntry.DestTreasury);
                    long liveVault = TreasuryStore.GetGuildSilver(g);
                    if (!GuildStore.Leave(a, out why)) return (false, "disband: " + why);
                    long handedBack = Personal(a);

                    SiteStore.DeliverGuildOwnedForTest(tile, g, "Silver", 30, SiteEntry.DestTreasury);
                    bool noOrphanVault = !TreasuryStore.HasVaultForTest(g);
                    int heldAtSite = SiteStore.SiteForTest(tile)?.StoredItems != null && SiteStore.SiteForTest(tile).StoredItems.TryGetValue("Silver", out int h) ? h : 0;

                    if (!GuildStore.CreateGuildAndJoinAsAdmin(b, g, out why)) return (false, "recreate: " + why);
                    SiteStore.DeliverGuildOwnedForTest(tile, g, "Silver", 30, SiteEntry.DestTreasury);
                    long successorVault = TreasuryStore.GetGuildSilver(g);
                    bool successorManages = SiteStore.CanManageTile(tile, b);
                    var collect = SiteStore.CollectStorage(b, tile);
                    bool listed = SiteStore.OrphanedGuildSites().Exists(o => o.Tile == tile);

                    bool ok = paidLive == 30 && liveVault == 30 && handedBack == 30 && noOrphanVault && heldAtSite == 30
                           && successorVault == 0 && !successorManages && !collect.ok && listed;
                    return (ok, $"livePaid={paidLive}/{liveVault} handedBack={handedBack} orphanVault={!noOrphanVault} held={heldAtSite} "
                              + $"successorVault={successorVault} successorManages={successorManages} collect={collect.ok} listed={listed}");
                }
                finally { Drop(tile, a, b, g); GuildStore.Leave(b, out _); GuildStore.Leave(a, out _); }
            });

            Check("Identity: only an explicit admin rebind hands an orphaned site to a guild, and only to one that exists", () =>
            {
                string a = NewUser("a"), b = NewUser("b"), g = NewGuild();
                int tile = NewTile();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    GuildSite(tile, g);
                    GuildStore.Leave(a, out _);
                    bool refusedDead = !SiteStore.RebindOrphanedSite(tile, g, "selftest", out string deadMsg);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(b, g, out why)) return (false, "recreate: " + why);
                    bool before = SiteStore.CanManageTile(tile, b);
                    bool rebound = SiteStore.RebindOrphanedSite(tile, g, "selftest", out string msg);
                    bool after = SiteStore.CanManageTile(tile, b);
                    SiteStore.DeliverGuildOwnedForTest(tile, g, "Silver", 12, SiteEntry.DestTreasury);
                    long paid = TreasuryStore.GetGuildSilver(g);
                    bool refusedLive = !SiteStore.RebindOrphanedSite(tile, g, "selftest", out _);
                    bool ok = refusedDead && !before && rebound && after && paid == 12 && refusedLive;
                    return (ok, $"refusedWithNoGuild={refusedDead} ({deadMsg}) before={before} rebound={rebound} ({msg}) after={after} paid={paid} refusedWhenLive={refusedLive}");
                }
                finally { Drop(tile, a, b, g); GuildStore.Leave(b, out _); GuildStore.Leave(a, out _); }
            });

            Check("Identity: a credit landing the instant after a disband hands its vault back is refused and builds no vault", () =>
            {
                string a = NewUser("a"), g = NewGuild();
                bool byIdentity = true, byName = true, vaultAfter = true;
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    string id = GuildIdentity.Of(g);
                    TreasuryStore.DepositGuildSilver(g, 40, a, "seed");
                    GuildStore.AfterVaultHandBackForTest = name =>
                    {
                        if (name != g) return;
                        byIdentity = TreasuryStore.DepositGuildSilver(g, 7, a, "racing production", identity: id);
                        byName = TreasuryStore.DepositGuildItem(g, "Steel", 7, "racing production");
                        vaultAfter = TreasuryStore.HasVaultForTest(g);
                    };
                    GuildStore.Leave(a, out _);
                    bool ok = !byIdentity && !byName && !vaultAfter && Personal(a) == 40 && GuildIdentity.Of(g) == null;
                    return (ok, $"creditByIdentity={byIdentity} creditByName={byName} vaultRebuilt={vaultAfter} handedBack={Personal(a)}");
                }
                finally { GuildStore.AfterVaultHandBackForTest = null; TreasuryStore.PurgeOwnerForTest(a); TreasuryStore.PurgeOwnerForTest(g); GuildStore.Leave(a, out _); }
            });

            Check("Identity: production racing a disband on four threads loses nothing and leaves no orphan vault", () =>
            {
                string a = NewUser("a"), g = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    string id = GuildIdentity.Of(g);
                    int accepted = 0, refused = 0;
                    var stop = new ManualResetEventSlim(false);
                    var threads = new List<Thread>();
                    for (int i = 0; i < 4; i++)
                    {
                        var t = new Thread(() =>
                        {
                            while (!stop.IsSet)
                            {
                                if (TreasuryStore.DepositGuildSilver(g, 1, g, "stress production", identity: id)) Interlocked.Increment(ref accepted);
                                else Interlocked.Increment(ref refused);
                            }
                        }) { IsBackground = true };
                        threads.Add(t); t.Start();
                    }
                    Thread.Sleep(150);
                    bool left = GuildStore.Leave(a, out why);
                    Thread.Sleep(150);
                    stop.Set();
                    foreach (Thread t in threads) t.Join(10000);
                    bool ok = left && accepted > 0 && refused > 0 && !TreasuryStore.HasVaultForTest(g) && Personal(a) == accepted;
                    return (ok, $"accepted={accepted} refusedAfterDisband={refused} handedBack={Personal(a)} orphanVault={TreasuryStore.HasVaultForTest(g)}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(a); TreasuryStore.PurgeOwnerForTest(g); GuildStore.Leave(a, out _); }
            });

            Check("Identity: an orphaned guild site is skipped by the reward cycle - timer untouched, nobody paid - while a bound one pays its guild", () =>
            {
                string a = NewUser("a"), g = NewGuild(), dead = NewGuild(), worker = NewUser("w");
                int live = NewTile(), orphan = live + 1;
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    GuildSite(live, g);
                    GuildSite(orphan, dead, bind: false);
                    SiteStore.BindGuildForTest(orphan, SiteStore.OrphanBindingPrefix + "selftest");
                    long due = DateTime.UtcNow.Ticks - TimeSpan.FromHours(3).Ticks;
                    foreach (int t in new[] { live, orphan })
                    {
                        SiteEntry s = SiteStore.SiteForTest(t);
                        s.BaseAmountPerCycle = 10;
                        s.LastRewardUtcTicks = due;
                        string key = SiteWorkerKey.For(worker, 900 + t % 50);
                        s.Workers.Add(key);
                        s.WorkerProgress[key] = new WorkerProgressDto { PawnLoadId = 900 + t % 50, Username = worker, Destination = SiteEntry.DestTreasury };
                    }
                    SiteStore.RunRewardCycle();
                    int liveVault = TreasuryStore.GuildItemForTest(g, "Steel");
                    bool orphanTimer = SiteStore.SiteForTest(orphan).LastRewardUtcTicks == due;
                    bool orphanVault = TreasuryStore.HasVaultForTest(dead);
                    bool ok = liveVault > 0 && orphanTimer && !orphanVault;
                    return (ok, $"boundGuildPaid={liveVault} orphanTimerUntouched={orphanTimer} orphanVaultCreated={orphanVault}");
                }
                finally { Drop(live, a, g, worker); Drop(orphan, dead); GuildStore.Leave(a, out _); }
            });

            Check("Migration: no pre-binding guild site is bound automatically; a dead guild's stays orphaned across a re-created name, a reload and a bulk rebind", () =>
            {
                string a = NewUser("a"), b = NewUser("b"), g = NewGuild(), dead = NewGuild();
                int held = NewTile(), gone = held + 1;
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    GuildSite(held, g, bind: false);
                    GuildSite(gone, dead, bind: false);
                    long established = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(1).Ticks;
                    SiteStore.SiteForTest(held).EstablishedUtcTicks = established;
                    SiteStore.SiteForTest(gone).EstablishedUtcTicks = established;
                    if (!SiteStore.SaveToDisk()) return (false, "save");
                    SiteStore.LoadFromDisk();
                    // Even a guild that has plainly existed throughout gets nothing on its name alone.
                    bool heldPaused = (SiteStore.GuildBindingOf(held) ?? "").StartsWith(SiteStore.UnprovenBindingPrefix, StringComparison.Ordinal)
                                   && !SiteStore.CanManageTile(held, a);
                    string goneBinding = SiteStore.GuildBindingOf(gone) ?? "";
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(b, dead, out why)) return (false, "recreate: " + why);
                    bool successorBefore = SiteStore.CanManageTile(gone, b);
                    SiteStore.LoadFromDisk();
                    bool successorAfterReload = SiteStore.CanManageTile(gone, b);
                    SiteStore.RebindUnprovenLegacy(commit: true, "selftest", out _);
                    bool notAdoptedInBulk = !SiteStore.CanManageTile(gone, b);
                    bool heldAdopted = SiteStore.CanManageTile(held, a);
                    bool ok = heldPaused && heldAdopted && goneBinding.StartsWith(SiteStore.OrphanBindingPrefix, StringComparison.Ordinal)
                           && !goneBinding.StartsWith(SiteStore.UnprovenBindingPrefix, StringComparison.Ordinal)
                           && !successorBefore && !successorAfterReload && SiteStore.GuildBindingOf(gone) == goneBinding && notAdoptedInBulk;
                    return (ok, $"heldPausedUntilConfirmed={heldPaused}/{heldAdopted} goneBinding='{goneBinding}' successorManages={successorBefore}/{successorAfterReload} bulkAdoptedDead={!notAdoptedInBulk}");
                }
                finally
                {
                    Drop(held, a, g); Drop(gone, b, dead);
                    SiteStore.SaveToDisk();
                    GuildStore.Leave(a, out _); GuildStore.Leave(b, out _);
                }
            });

            Check("Migration: a matching name with no proof of age is paused, never assigned, until an admin confirms the bulk rebind", () =>
            {
                string a = NewUser("a"), g = NewGuild();
                int tile = NewTile();
                try
                {
                    GuildSite(tile, g, bind: false);
                    SiteStore.SiteForTest(tile).EstablishedUtcTicks = DateTime.UtcNow.Ticks - TimeSpan.FromDays(30).Ticks;
                    // The guild of that name is created after the outpost existed: it may be a successor, so nothing is proven.
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    if (!SiteStore.SaveToDisk()) return (false, "save");
                    SiteStore.LoadFromDisk();
                    string binding = SiteStore.GuildBindingOf(tile) ?? "";
                    bool paused = binding.StartsWith(SiteStore.UnprovenBindingPrefix, StringComparison.Ordinal) && !SiteStore.CanManageTile(tile, a);
                    var dry = SiteStore.RebindUnprovenLegacy(commit: false, "selftest", out _);
                    bool stillPaused = !SiteStore.CanManageTile(tile, a);
                    SiteStore.RebindUnprovenLegacy(commit: true, "selftest", out string msg);
                    bool adopted = SiteStore.CanManageTile(tile, a) && SiteStore.GuildBindingOf(tile) == GuildIdentity.Of(g);
                    SiteStore.LoadFromDisk();
                    bool persisted = SiteStore.CanManageTile(tile, a);
                    bool ok = paused && dry.Exists(d => d.Tile == tile) && stillPaused && adopted && persisted;
                    return (ok, $"pausedUnproven={paused} dryRunListed={dry.Exists(d => d.Tile == tile)} dryRunChanged={!stillPaused} adoptedOnConfirm={adopted} persisted={persisted} ({msg})");
                }
                finally { Drop(tile, a, g); SiteStore.SaveToDisk(); GuildStore.Leave(a, out _); }
            });

            Check("Identity: a vault an earlier guild left under a name is set aside when the name is reused, and released only by an admin", () =>
            {
                string a = NewUser("a"), heir = NewUser("h"), g = NewGuild();
                string key = null;
                try
                {
                    TreasuryStore.PutVaultForTest(g, 500);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    long inherited = TreasuryStore.GetGuildSilver(g);
                    foreach (var v in TreasuryStore.OrphanVaults())
                        if (v.Key.StartsWith(TreasuryStore.OrphanVaultPrefix + g + ":", StringComparison.Ordinal)) key = v.Key;
                    bool refusedLive = !TreasuryStore.ReleaseOrphanVault(g, heir, "selftest", out _);
                    bool released = key != null && TreasuryStore.ReleaseOrphanVault(key, heir, "selftest", out string msg);
                    bool ok = inherited == 0 && key != null && refusedLive && released && Personal(heir) == 500 && !TreasuryStore.HasVaultForTest(key);
                    return (ok, $"inherited={inherited} setAside={key ?? "none"} refusedForLiveGuild={refusedLive} released={released} heir={Personal(heir)}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(heir); TreasuryStore.PurgeOwnerForTest(g); if (key != null) TreasuryStore.PurgeOwnerForTest(key); GuildStore.Leave(a, out _); }
            });

            Check("Identity: a sale's guild tax reaches the live guild, but goes back to the payer once if the guild is gone or the name was reused", () =>
            {
                string a = NewUser("a"), b = NewUser("b"), seller = NewUser("s"), g = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    string oldId = GuildIdentity.Of(g);
                    bool live = TreasuryStore.SettleGuildTaxOnce(g, oldId, "gidtax1:guild", 20, seller, "selftest tax");
                    long guildGot = TreasuryStore.GetGuildSilver(g);
                    GuildStore.Leave(a, out _);
                    bool dead = TreasuryStore.SettleGuildTaxOnce(g, oldId, "gidtax2:guild", 15, seller, "selftest tax");
                    bool deadReplay = TreasuryStore.SettleGuildTaxOnce(g, oldId, "gidtax2:guild", 15, seller, "selftest tax");
                    long sellerAfterDead = Personal(seller);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(b, g, out why)) return (false, "recreate: " + why);
                    bool reused = TreasuryStore.SettleGuildTaxOnce(g, oldId, "gidtax3:guild", 5, seller, "selftest tax");
                    long successorVault = TreasuryStore.GetGuildSilver(g);
                    bool ok = live && guildGot == 20 && dead && deadReplay && sellerAfterDead == 15 && reused && Personal(seller) == 20 && successorVault == 0;
                    return (ok, $"liveGuild={guildGot} deadReturned={sellerAfterDead} (replay ok={deadReplay}) afterReuse seller={Personal(seller)} successorVault={successorVault}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(a); TreasuryStore.PurgeOwnerForTest(b); TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(g); GuildStore.Leave(b, out _); GuildStore.Leave(a, out _); }
            });

            Check("Identity: a donation confirmed after its guild disbanded refunds the donor rather than building a vault", () =>
            {
                string a = NewUser("a"), donor = NewUser("d"), g = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, g, out string why)) return (false, "create: " + why);
                    if (!TreasuryStore.DepositSilver(donor, 100, "seed")) return (false, "seed");
                    string txn = "gid-" + Guid.NewGuid().ToString("N");
                    if (!TreasuryStore.BeginPendingGuildDonation(donor, g, 60, txn, out why)) return (false, "begin: " + why);
                    long debited = Personal(donor);
                    // Bypassing Leave's own refund pass, which would otherwise mask the commit path under test.
                    GuildStore.AfterVaultHandBackForTest = name => { if (name == g) TreasuryStore.ConfirmDeposits(donor, new[] { txn }); };
                    GuildStore.Leave(a, out _);
                    bool ok = debited == 40 && Personal(donor) == 100 && !TreasuryStore.HasVaultForTest(g);
                    return (ok, $"afterBegin={debited} afterConfirm={Personal(donor)} orphanVault={TreasuryStore.HasVaultForTest(g)}");
                }
                finally { GuildStore.AfterVaultHandBackForTest = null; TreasuryStore.PurgeOwnerForTest(a); TreasuryStore.PurgeOwnerForTest(donor); TreasuryStore.PurgeOwnerForTest(g); GuildStore.Leave(a, out _); }
            });

            Check("Identity: silver returned to a guild that disbanded meanwhile is held for an admin, not used to rebuild the vault", () =>
            {
                string actor = NewUser("x"), g = NewGuild();
                try
                {
                    int before = Recovery.RecoveryStore.HeldCountFor(actor);
                    bool returned = TreasuryStore.ReturnGuildSilver(g, 25, actor, "selftest refund");
                    bool ok = returned && Recovery.RecoveryStore.HeldCountFor(actor) == before + 1 && !TreasuryStore.HasVaultForTest(g);
                    return (ok, $"returned={returned} holds={Recovery.RecoveryStore.HeldCountFor(actor) - before} vault={TreasuryStore.HasVaultForTest(g)}");
                }
                finally { Recovery.RecoveryStore.ClearUser(actor); TreasuryStore.PurgeOwnerForTest(g); }
            });

            Check("Identity: an unsaved create publishes no identity, and an unsaved disband keeps the guild's own", () =>
            {
                string a = NewUser("a"), b = NewUser("b"), g = NewGuild(), h = NewGuild();
                try
                {
                    bool created;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.GuildsFile ? "selftest" : null))
                        created = GuildStore.CreateGuildAndJoinAsAdmin(a, g, out _);
                    bool noGhost = GuildIdentity.Of(g) == null;
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(b, h, out string why)) return (false, "create: " + why);
                    string id = GuildIdentity.Of(h);
                    bool left;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.GuildsFile ? "selftest" : null))
                        left = GuildStore.Leave(b, out _);
                    bool kept = GuildIdentity.Of(h) == id && GuildStore.IncarnationOf(h) == id;
                    bool ok = !created && noGhost && !left && kept;
                    return (ok, $"createdUnsaved={created} ghostIdentity={!noGhost} disbandedUnsaved={left} identityKept={kept}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(b); GuildStore.Leave(b, out _); GuildStore.Leave(a, out _); }
            });

            return r;
        }
    }
}
