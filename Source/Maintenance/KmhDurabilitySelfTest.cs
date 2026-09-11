using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Runs the real stores against a write that fails on demand: a full or read-only KMH-Data never shows up in a green suite.
    internal static class KmhDurabilitySelfTest
    {
        private static string ScratchFile => System.IO.Path.Combine(KmhDataPaths.Folder, ".kmh-durability-probe.json");

        private static int ItemCount(string who, string def)
        {
            var s = TreasuryStore.GetSnapshotFor(who);
            return s != null && s.Items.TryGetValue(def, out int n) ? n : 0;
        }

        private static int TxnCount(string who) => TreasuryStore.GetSnapshotFor(who)?.RecentTransactions?.Count ?? 0;

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { JsonFileStore.ClearFailureStateForTest(); }
            }

            Check("Persistence: a failed write is reported to its caller", () =>
            {
                using (JsonFileStore.FailWritesForTest(_ => "injected: disk full"))
                {
                    bool saved = JsonFileStore.Save(KmhDataPaths.TreasuryFile, new List<string> { "x" });
                    return (!saved, saved ? "Save returned true for a write that never happened" : "Save returned false");
                }
            });

            Check("Persistence: writes resume once the disk does", () =>
            {
                bool during, after;
                using (JsonFileStore.FailWritesForTest(_ => "injected: disk full"))
                    during = JsonFileStore.Save(ScratchFile, new List<string> { "x" });
                after = JsonFileStore.Save(ScratchFile, new List<string> { "x" });
                try { System.IO.File.Delete(ScratchFile); } catch { }
                return (!during && after, $"during={during} after={after} (expected false then true)");
            });

            Check("Treasury: a withdrawal that cannot be saved is not granted", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositSilver(who, 1000, "selftest seed")) return (false, "could not seed the vault");
                long seeded = TreasuryStore.GetPersonalSilver(who);

                bool granted;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    granted = TreasuryStore.WithdrawSilver(who, 100, "selftest withdraw");

                long afterFail = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                // The disk still says 1000. Anything but a refusal here mints 100 silver on the next restart.
                return (!granted && afterFail == seeded,
                    !granted && afterFail == seeded
                        ? "refused, and the balance is back to what the disk holds"
                        : $"granted={granted}, memory={afterFail}, disk={seeded} - THIS DUPLICATES SILVER ON RESTART");
            });

            Check("Treasury: a deposit that cannot be saved is not acknowledged", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositSilver(who, 500, "selftest seed")) return (false, "could not seed the vault");
                long seeded = TreasuryStore.GetPersonalSilver(who);

                bool ok;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    ok = TreasuryStore.DepositSilver(who, 250, "selftest deposit");

                long after = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                // A deposit reported as banked but not written is the loss half of the same bug.
                return (!ok && after == seeded,
                    !ok && after == seeded
                        ? "refused, so the client keeps the goods instead of trading them for nothing"
                        : $"ok={ok}, memory={after}, disk={seeded} - THIS LOSES A DEPOSIT ON RESTART");
            });

            Check("Treasury: an item withdrawal that cannot be saved is not granted", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositItem(who, "Steel", 50, "selftest seed")) return (false, "could not seed the vault");
                int seeded = ItemCount(who, "Steel");

                bool granted;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    granted = TreasuryStore.WithdrawItem(who, "Steel", 20, "selftest withdraw");

                int after = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                return (!granted && after == seeded,
                    !granted && after == seeded ? "refused, stack intact"
                                                : $"granted={granted}, memory={after}, disk={seeded} - THIS DUPLICATES ITEMS ON RESTART");
            });

            Check("Treasury: a guild withdrawal that cannot be saved is not granted", () =>
            {
                string guild = "selftest_guild_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositGuildSilver(guild, 800, "selftest", "selftest seed")) return (false, "could not seed the vault");
                long seeded = TreasuryStore.GetGuildSilver(guild);

                bool granted;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    granted = TreasuryStore.WithdrawGuildSilver(guild, 300, "selftest", "selftest withdraw");

                long after = TreasuryStore.GetGuildSilver(guild);
                TreasuryStore.PurgeOwnerForTest(guild);
                return (!granted && after == seeded,
                    !granted && after == seeded ? "refused, vault intact"
                                                : $"granted={granted}, memory={after}, disk={seeded}");
            });

            Check("Treasury: a refused move leaves no transaction behind", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositSilver(who, 1000, "selftest seed")) return (false, "could not seed the vault");
                int before = TxnCount(who);

                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    TreasuryStore.WithdrawSilver(who, 100, "selftest withdraw");

                int after = TxnCount(who);
                TreasuryStore.PurgeOwnerForTest(who);
                // A ledger line for a move that was rolled back would read as a withdrawal the player never got.
                return (after == before, $"transactions {before} -> {after} (expected unchanged)");
            });

            Check("Marketplace: a post that cannot be saved returns the goods", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositItem(who, "Steel", 100, "selftest seed")) return (false, "could not seed the vault");

                long id;
                string reason;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    id = Features.Marketplace.MarketplaceStore.Post(who, "Steel", 40, 5, "public", 0, out reason);

                int stillHeld = ItemCount(who, "Steel");
                bool listed = Features.Marketplace.MarketplaceStore.HasSellerListings(who);
                TreasuryStore.PurgeOwnerForTest(who);
                // Escrow leaves the vault before the listing is written; an unwritten listing would burn it.
                return (id == 0 && !listed && stillHeld == 100,
                    id == 0 && !listed && stillHeld == 100
                        ? "no listing, and all 100 steel are back in the vault"
                        : $"id={id}, listed={listed}, held={stillHeld}/100 - THIS DESTROYS THE SELLER'S GOODS");
            });

            Check("Marketplace: a cancel that cannot be saved does not refund", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositItem(who, "Steel", 100, "selftest seed")) return (false, "could not seed the vault");
                long id = Features.Marketplace.MarketplaceStore.Post(who, "Steel", 40, 5, "public", 0, out _);
                if (id == 0) return (false, "could not seed a listing");
                int afterPost = ItemCount(who, "Steel");

                bool cancelled;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    cancelled = Features.Marketplace.MarketplaceStore.Cancel(who, id);

                int afterCancel = ItemCount(who, "Steel");
                bool stillListed = Features.Marketplace.MarketplaceStore.HasSellerListings(who);
                Features.Marketplace.MarketplaceStore.Cancel(who, id);
                TreasuryStore.PurgeOwnerForTest(who);
                // Refunding first would hand back 40 steel AND leave the listing on disk to sell again.
                return (!cancelled && stillListed && afterCancel == afterPost,
                    !cancelled && stillListed && afterCancel == afterPost
                        ? "refused, listing intact, nothing refunded twice"
                        : $"cancelled={cancelled}, stillListed={stillListed}, held {afterPost} -> {afterCancel}");
            });

            Check("Marketplace: a buy that cannot be saved charges nobody", () =>
            {
                string seller = "selftest_durability_s" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string buyer  = "selftest_durability_b" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!TreasuryStore.DepositItem(seller, "Steel", 100, "selftest seed")) return (false, "could not seed the seller");
                if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "could not seed the buyer");
                long id = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 40, 5, "public", 0, out _);
                if (id == 0) return (false, "could not seed a listing");
                long buyerBefore = TreasuryStore.GetPersonalSilver(buyer);

                bool bought;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    bought = Features.Marketplace.MarketplaceStore.Buy(buyer, id, 10, out _, out _, out _);

                long buyerAfter = TreasuryStore.GetPersonalSilver(buyer);
                int  buyerGoods = ItemCount(buyer, "Steel");
                Features.Marketplace.MarketplaceStore.Cancel(seller, id);
                TreasuryStore.PurgeOwnerForTest(seller);
                TreasuryStore.PurgeOwnerForTest(buyer);
                // Unwritten, the reservation is undone by the restart and the same units sell a second time.
                return (!bought && buyerAfter == buyerBefore && buyerGoods == 0,
                    !bought && buyerAfter == buyerBefore && buyerGoods == 0
                        ? "refused before any silver or goods moved"
                        : $"bought={bought}, silver {buyerBefore} -> {buyerAfter}, goods={buyerGoods} - THIS DUPLICATES THE LISTING");
            });

            Check("Marketplace: a house-pool debit that cannot be saved is refused", () =>
            {
                long before = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                Features.Marketplace.MarketplaceStore.TryCreditHousePool(500, "selftest seed");
                long seeded = Features.Marketplace.MarketplaceStore.HousePoolBalance();

                bool debited;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    debited = Features.Marketplace.MarketplaceStore.TryDebitHousePool(200, "selftest debit");

                long after = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                Features.Marketplace.MarketplaceStore.TryDebitHousePool(seeded - before, "selftest cleanup");
                return (!debited && after == seeded,
                    !debited && after == seeded ? "refused, pool intact"
                                                : $"debited={debited}, pool {seeded} -> {after}");
            });

            Check("Auctions: a post that cannot be saved returns the goods", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositItem(who, "Steel", 60, "selftest seed")) return (false, "could not seed the vault");

                long id;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.AuctionsFile ? "injected: disk full" : null))
                    (id, _) = Features.Auctions.AuctionStore.Post(who, "Steel", "", 0, 25, 100, 10, 0, 24, "public");

                int held = ItemCount(who, "Steel");
                bool active = Features.Auctions.AuctionStore.HasUserActivity(who);
                TreasuryStore.PurgeOwnerForTest(who);
                return (id == 0 && !active && held == 60,
                    id == 0 && !active && held == 60 ? "no auction, and all 60 steel are back"
                                                     : $"id={id}, active={active}, held={held}/60 - THIS DESTROYS THE SELLER'S GOODS");
            });

            Check("WantBoard: a post that cannot be saved refunds the escrow", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositSilver(who, 4000, "selftest seed")) return (false, "could not seed the vault");
                long seeded = TreasuryStore.GetPersonalSilver(who);

                long id;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.WantsFile ? "injected: disk full" : null))
                    (id, _) = Features.WantBoard.WantStore.Post(who, "Steel", 20, 10, 24, "public", 0, "", false, false, false);

                long after = TreasuryStore.GetPersonalSilver(who);
                bool open = Features.WantBoard.WantStore.HasBuyerWants(who);
                TreasuryStore.PurgeOwnerForTest(who);
                return (id == 0 && !open && after == seeded,
                    id == 0 && !open && after == seeded ? "no want, escrow returned in full"
                                                       : $"id={id}, open={open}, silver {seeded} -> {after} - THIS STRANDS THE BUYER'S ESCROW");
            });

            Check("Quests: a post that cannot be saved returns the bounty", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositSilver(who, 3000, "selftest seed")) return (false, "could not seed the vault");
                long seeded = TreasuryStore.GetPersonalSilver(who);

                long id;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.QuestsFile ? "injected: disk full" : null))
                    id = Features.Quests.QuestStore.Post(who, Features.Quests.Dto.QuestEntry.KindDeliverItem,
                                                         Features.Quests.Dto.QuestEntry.VisibilityPublic,
                                                         "selftest quest", "probe", 500, "Steel", 5, 24);

                long after = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                return (id == 0 && after == seeded,
                    id == 0 && after == seeded ? "no quest, bounty returned in full"
                                               : $"id={id}, silver {seeded} -> {after} - THIS STRANDS THE POSTER'S BOUNTY");
            });

            Check("Guilds: authority the disk refused is not acknowledged", () =>
            {
                string owner = "selftest_g_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string mate  = "selftest_g_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string gname = "selftest_guild_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, gname, out string why)) return (false, "seed failed: " + why);
                if (!Features.Guilds.GuildStore.AddMember(mate, gname)) return (false, "could not seed a member");

                bool kicked;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.GuildsFile ? "injected: disk full" : null))
                    kicked = Features.Guilds.GuildStore.Kick(owner, mate);

                // Told they were kicked while the disk still says otherwise is an authority rollback on restart.
                bool stillMember = string.Equals(Features.Guilds.GuildStore.CurrentGuildOf(mate), gname, StringComparison.OrdinalIgnoreCase);
                Features.Guilds.GuildStore.Kick(owner, mate);
                Features.Guilds.GuildStore.Leave(owner, out _);
                bool ok = !kicked && stillMember;
                return (ok, ok ? "refused, membership intact in memory and on disk"
                              : $"kicked={kicked}, stillMember={stillMember} - MEMORY AND DISK DISAGREE ON WHO IS IN THE GUILD");
            });

            Check("Guilds: settings and invites the disk refused are not acknowledged either", () =>
            {
                string owner = "selftest_g_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string target = "selftest_g_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string gname = "selftest_guild_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, gname, out string why)) return (false, "seed failed: " + why);
                if (!Features.Guilds.GuildStore.SetMotd(owner, "before")) return (false, "could not seed a motd");

                bool motdOk, joinOk, invited;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.GuildsFile ? "injected: disk full" : null))
                {
                    motdOk  = Features.Guilds.GuildStore.SetMotd(owner, "after");
                    joinOk  = Features.Guilds.GuildStore.SetOpenJoin(owner, true, out _);
                    invited = Features.Guilds.GuildStore.Invite(owner, target, out _, out _);
                }
                // Rolled back too, or memory would keep serving a setting the disk will lose on restart.
                var g = Features.Guilds.GuildStore.BuildEnvelopeFor(owner).Guild;
                bool motdKept   = g != null && g.Motd == "before";
                bool joinKept   = g != null && !g.OpenJoin;
                bool noInvite   = g != null && (g.PendingInvites == null || !g.PendingInvites.Contains(target));
                Features.Guilds.GuildStore.Leave(owner, out _);
                bool ok = !motdOk && !joinOk && !invited && motdKept && joinKept && noInvite;
                return (ok, ok ? "all three refused and rolled back"
                              : $"motd={motdOk}/{motdKept}, openJoin={joinOk}/{joinKept}, invite={invited}/{noInvite}");
            });

            Check("Guilds: a perk bought while the buyer's authority moved is refunded, not applied", () =>
            {
                string owner = "selftest_g_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string gname = "selftest_guild_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, gname, out string why)) return (false, "seed failed: " + why);
                if (!TreasuryStore.DepositGuildSilver(gname, 50000, owner, "selftest seed")) return (false, "seed vault");
                long vaultBefore = TreasuryStore.GetGuildSilver(gname);

                // The vault charge inside BuyPerk raises this, landing in the window where the buyer's rank is no longer held.
                bool fired = false;
                Action<KMH.Sdk.Server.Events.TreasuryChangedEvent> moveAuthority = _ =>
                {
                    if (fired) return;
                    fired = true;
                    Features.Guilds.GuildStore.SetMotd(owner, "authority moved mid-purchase");
                };
                Extensibility.KmhEventBus.Instance.TreasuryChanged += moveAuthority;
                bool bought;
                int lvl, cost;
                string reason;
                try { bought = Features.Guilds.GuildStore.BuyPerk(owner, "marketplace_tax_cut", out reason, out lvl, out cost); }
                finally { Extensibility.KmhEventBus.Instance.TreasuryChanged -= moveAuthority; }

                bool notApplied = Features.Guilds.GuildStore.GetMarketplaceTaxReductionPoints(gname) == 0;
                bool refunded   = TreasuryStore.GetGuildSilver(gname) == vaultBefore;
                Features.Guilds.GuildStore.Leave(owner, out _);
                TreasuryStore.PurgeOwnerForTest(gname);
                bool ok = fired && !bought && notApplied && refunded;
                return (ok, ok ? "authority moved mid-charge: perk refused and the vault refunded in full"
                              : $"interleaved={fired}, bought={bought} ({reason}), notApplied={notApplied}, refunded={refunded}");
            });

            Check("Invalidation: a commit nobody announced is still pushed on the next tick", () =>
            {
                string seller = "selftest_inv_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositItem(seller, "Steel", 2, "selftest seed")) return (false, "seed");
                Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
                bool clean = !KmhInvalidation.MarketplacePending;

                // A store path that commits without announcing leaves the mark standing.
                if (!Features.Marketplace.MarketplaceStore.SaveToDisk()) return (false, "could not commit");
                bool marked = KmhInvalidation.MarketplacePending;

                KmhInvalidation.Drain();
                bool drained = !KmhInvalidation.MarketplacePending;
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = clean && marked && drained;
                return (ok, ok ? "commit marked the area, the drain pushed and cleared it"
                              : $"cleanAfterBroadcast={clean}, markedByCommit={marked}, clearedByDrain={drained}");
            });

            Check("Recovery: a hold that cannot be written is not reported as safe", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                long id;
                bool queuedForRetry, knownMemoryOnly;
                // Read inside the scope: disposing the seam clears the failure state it recorded.
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.RecoveryFile ? "injected: disk full" : null))
                {
                    id = Features.Recovery.RecoveryStore.HoldSilver(who, 700, "selftest", "probe");
                    queuedForRetry  = ExpirySweeper.ShouldRetrySaves;
                    knownMemoryOnly = Features.Recovery.RecoveryStore.MemoryOnlyHoldCount == 1;
                }

                // The record still exists to be retried; what must not happen is calling it safe on disk.
                bool inMemory   = Features.Recovery.RecoveryStore.ForUser(who).Count == 1;
                bool durableNow = Features.Recovery.RecoveryStore.SaveToDisk();
                bool clearedAfterWrite = Features.Recovery.RecoveryStore.MemoryOnlyHoldCount == 0;
                Features.Recovery.RecoveryStore.ClearUser(who);
                bool ok = id != 0 && inMemory && queuedForRetry && knownMemoryOnly && durableNow && clearedAfterWrite;
                return (ok, ok
                    ? "held, known memory-only, retry armed, durable once writable"
                    : $"id={id}, inMemory={inMemory}, retryArmed={queuedForRetry}, memoryOnly={knownMemoryOnly}, durable={durableNow}, cleared={clearedAfterWrite}");
            });

            Check("Persistence: one failed save is retried, not only three", () =>
            {
                JsonFileStore.ClearFailureStateForTest();
                bool quietWhenClean = !ExpirySweeper.ShouldRetrySaves;
                bool retryArmed, notFrozenYet;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.RecoveryFile ? "injected: disk full" : null))
                {
                    Features.Recovery.RecoveryStore.SaveToDisk();
                    // The freeze deliberately still waits for a repeated failure; the retry sweeper must not.
                    retryArmed   = ExpirySweeper.ShouldRetrySaves;
                    notFrozenYet = !JsonFileStore.AnyStoreUnwritable;
                }
                bool ok = quietWhenClean && retryArmed && notFrozenYet;
                return (ok, ok
                    ? "first failure arms the retry without freezing the economy"
                    : $"clean={quietWhenClean}, retryArmed={retryArmed}, notFrozenYet={notFrozenYet}");
            });

            Check("Recovery: a triage that cannot be recorded delivers nothing", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                long id = Features.Recovery.RecoveryStore.HoldSilver(who, 250, "selftest", "probe");
                if (id == 0) return (false, "could not seed a held record");
                long before = TreasuryStore.GetPersonalSilver(who);

                bool retried;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.RecoveryFile ? "injected: disk full" : null))
                    retried = Features.Recovery.RecoveryStore.Retry(id, out _);

                long after = TreasuryStore.GetPersonalSilver(who);
                bool stillHeld = Features.Recovery.RecoveryStore.ForUser(who).Count == 1;
                Features.Recovery.RecoveryStore.ClearUser(who);
                TreasuryStore.PurgeOwnerForTest(who);
                // Delivering first and recording after is what lets one held record pay out twice across a restart.
                return (!retried && after == before && stillHeld,
                    !retried && after == before && stillHeld
                        ? "refused, record still held, nothing paid"
                        : $"retried={retried}, silver {before} -> {after}, stillHeld={stillHeld} - THIS CAN PAY THE SAME RECORD TWICE");
            });

            Check("Treasury: repeated failures raise the degraded gate", () =>
            {
                string who = "selftest_durability_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!TreasuryStore.DepositSilver(who, 1000, "selftest seed")) return (false, "could not seed the vault");
                bool degradedDuring;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                {
                    for (int i = 0; i < 3; i++) TreasuryStore.WithdrawSilver(who, 10, "selftest");
                    degradedDuring = KmhMaintenanceGate.PersistenceDegraded;
                }
                bool clearedAfter = !KmhMaintenanceGate.PersistenceDegraded;
                TreasuryStore.PurgeOwnerForTest(who);
                // Per-transaction refusal is the fix; the global freeze stays as the owner-facing safety net on top.
                return (degradedDuring && clearedAfter,
                    $"degraded during={degradedDuring}, cleared after={clearedAfter}");
            });

            return r;
        }
    }
}
