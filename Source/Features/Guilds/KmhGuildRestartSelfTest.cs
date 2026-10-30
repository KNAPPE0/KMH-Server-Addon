using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Guilds
{
    // Across a cut write and a reload FROM DISK, silver ends in the vault or with the member: never nowhere, never twice.
    internal static class KmhGuildRestartSelfTest
    {
        private const string Perk = "worker_xp_bonus";

        private static string User(string tag) => $"selftest_gr{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private static string Name() => "selftest gr " + Guid.NewGuid().ToString("N").Substring(0, 6);

        private static void Restart() { GuildStore.LoadFromDisk(); TreasuryStore.LoadFromDisk(); }
        private static void Recover() { GuildStore.RecoverPendingPerks(); GuildStore.RecoverPendingPerks(); }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { GuildStore.AtPerkPhaseForTest = null; }
            }

            (string Owner, string Guild) Seed(int vault)
            {
                string o = User("o"), g = Name();
                if (!GuildStore.CreateGuildAndJoinAsAdmin(o, g, out string why)) throw new InvalidOperationException("guild: " + why);
                if (!TreasuryStore.DepositGuildSilver(g, vault, o, "selftest seed")) throw new InvalidOperationException("seed vault");
                return (o, g);
            }
            void Drop(string o, string g) { GuildStore.Leave(o, out _); TreasuryStore.PurgeOwnerForTest(o); TreasuryStore.PurgeOwnerForTest(g); }

            // A member whose rank has a daily cap, so the withdrawal writes its tally before the silver moves.
            string CappedMember(string o, string g)
            {
                string m = User("m");
                if (!GuildStore.SaveSettings(o, new GuildSettingsDto { MemberDailyWithdrawCap = 500 })) throw new InvalidOperationException("cap");
                if (!GuildStore.AddMember(m, g)) throw new InvalidOperationException("member");
                return m;
            }

            Check("Guild restart: a withdrawal moves vault to member in one write, and a reload agrees", () =>
            {
                (string o, string g) = Seed(1000);
                string m = CappedMember(o, g);
                try
                {
                    bool ok1 = GuildStore.WithdrawFromGuild(m, 300, out string why);
                    Restart();
                    bool ok = ok1 && TreasuryStore.GetGuildSilver(g) == 700 && TreasuryStore.GetPersonalSilver(m) == 300;
                    return (ok, $"withdrew={ok1} ({why}) vault={TreasuryStore.GetGuildSilver(g)} member={TreasuryStore.GetPersonalSilver(m)}");
                }
                finally { GuildStore.Leave(m, out _); TreasuryStore.PurgeOwnerForTest(m); Drop(o, g); }
            });

            foreach (string file in new[] { "guilds", "treasury" })
                Check($"Guild restart: a withdrawal whose {file} write fails moves nothing, before or after a reload", () =>
                {
                    (string o, string g) = Seed(1000);
                    string m = CappedMember(o, g);
                    try
                    {
                        string target = file == "guilds" ? KmhDataPaths.GuildsFile : KmhDataPaths.TreasuryFile;
                        bool ok1;
                        using (JsonFileStore.FailWritesForTest(p => p == target ? "selftest" : null))
                            ok1 = GuildStore.WithdrawFromGuild(m, 300, out _);
                        bool liveOk = TreasuryStore.GetGuildSilver(g) == 1000 && TreasuryStore.GetPersonalSilver(m) == 0;
                        Restart();
                        bool diskOk = TreasuryStore.GetGuildSilver(g) == 1000 && TreasuryStore.GetPersonalSilver(m) == 0;
                        // The refused attempt must not have used up the day's cap either.
                        bool capKept = GuildStore.WithdrawFromGuild(m, 500, out string why2);
                        bool ok = !ok1 && liveOk && diskOk && capKept;
                        return (ok, $"refused={!ok1} live={liveOk} afterReload={diskOk} fullCapAfter={capKept} ({why2})");
                    }
                    finally { GuildStore.Leave(m, out _); TreasuryStore.PurgeOwnerForTest(m); Drop(o, g); }
                });

            int cost = GuildPerksDto.CostFor(0);
            foreach (string phase in new[] { "recorded", "charged" })
                Check($"Guild restart: a perk purchase cut at '{phase}' and reloaded is charged once and applied, or neither", () =>
                {
                    (string o, string g) = Seed(50_000);
                    try
                    {
                        GuildStore.AtPerkPhaseForTest = ph => { if (ph == phase) throw new OperationCanceledException("selftest"); };
                        try { GuildStore.BuyPerk(o, Perk, out _, out _, out _); } catch (OperationCanceledException) { }
                        GuildStore.AtPerkPhaseForTest = null;
                        Restart();
                        Recover();
                        long vault = TreasuryStore.GetGuildSilver(g);
                        int level = GuildStore.PerkLevelForTest(g, Perk);
                        bool ok = phase == "recorded" ? vault == 50_000 && level == 0 : vault == 50_000 - cost && level == 1;
                        ok &= GuildStore.PendingPerkCountForTest == 0;
                        return (ok, $"vault {vault} (cost {cost}), level {level}, pending {GuildStore.PendingPerkCountForTest}");
                    }
                    finally { Drop(o, g); }
                });

            Check("Guild restart: a perk whose guild save fails after the charge is applied once from its record after a reload", () =>
            {
                (string o, string g) = Seed(50_000);
                IDisposable failing = null;
                try
                {
                    GuildStore.AtPerkPhaseForTest = ph => { if (ph == "charged") failing = JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.GuildsFile ? "selftest" : null); };
                    bool bought = GuildStore.BuyPerk(o, Perk, out string why, out _, out _);
                    failing?.Dispose(); failing = null;
                    GuildStore.AtPerkPhaseForTest = null;
                    Restart();
                    Recover();
                    long vault = TreasuryStore.GetGuildSilver(g);
                    int level = GuildStore.PerkLevelForTest(g, Perk);
                    bool ok = bought && vault == 50_000 - cost && level == 1 && GuildStore.PendingPerkCountForTest == 0;
                    return (ok, $"bought={bought} ({why}) vault {vault} level {level}");
                }
                finally { failing?.Dispose(); Drop(o, g); }
            });

            Check("Guild restart: a charged perk overtaken by another purchase is refunded once, not applied twice", () =>
            {
                (string o, string g) = Seed(50_000);
                try
                {
                    GuildStore.AtPerkPhaseForTest = ph => { if (ph == "charged") throw new OperationCanceledException("selftest"); };
                    try { GuildStore.BuyPerk(o, Perk, out _, out _, out _); } catch (OperationCanceledException) { }
                    GuildStore.AtPerkPhaseForTest = null;
                    // A second purchase lands at the same priced level first, so the interrupted one no longer fits.
                    bool second = GuildStore.BuyPerk(o, Perk, out string why, out _, out _);
                    Restart();
                    Recover();
                    long vault = TreasuryStore.GetGuildSilver(g);
                    int level = GuildStore.PerkLevelForTest(g, Perk);
                    bool ok = second && level == 1 && vault == 50_000 - cost && GuildStore.PendingPerkCountForTest == 0;
                    return (ok, $"second={second} ({why}) level {level} vault {vault} (want {50_000 - cost})");
                }
                finally { Drop(o, g); }
            });

            // ABA: a same-named guild can replace the buyer's between record and charge, so the charge follows the incarnation.
            Check("Guild ABA: a perk paused after its record, across a disband and a same-named new guild, charges neither and pays nobody", () =>
            {
                (string oA, string g) = Seed(50_000);
                string oB = User("b");
                try
                {
                    GuildStore.AtPerkPhaseForTest = ph =>
                    {
                        if (ph != "recorded") return;
                        GuildStore.Leave(oA, out _);
                        if (!GuildStore.CreateGuildAndJoinAsAdmin(oB, g, out string w)) throw new InvalidOperationException("recreate: " + w);
                        if (!TreasuryStore.DepositGuildSilver(g, 50_000, oB, "selftest seed B")) throw new InvalidOperationException("seed B");
                    };
                    bool bought = GuildStore.BuyPerk(oA, Perk, out string why, out _, out _);
                    GuildStore.AtPerkPhaseForTest = null;
                    long vaultB = TreasuryStore.GetGuildSilver(g), personalA = TreasuryStore.GetPersonalSilver(oA);
                    int levelB = GuildStore.PerkLevelForTest(g, Perk);
                    int holds = Recovery.RecoveryStore.ForUser(oA).Count;
                    Restart(); Recover();
                    bool stable = TreasuryStore.GetGuildSilver(g) == vaultB && TreasuryStore.GetPersonalSilver(oA) == personalA
                               && GuildStore.PerkLevelForTest(g, Perk) == levelB;
                    bool ok = !bought && vaultB == 50_000 && levelB == 0 && personalA == 50_000 && holds == 0 && stable
                           && GuildStore.PendingPerkCountForTest == 0;
                    return (ok, $"bought={bought} ({why}) newGuildVault={vaultB} newGuildLevel={levelB} oldOwnerPersonal={personalA} "
                              + $"holds={holds} stableAfterReload={stable} pending={GuildStore.PendingPerkCountForTest}");
                }
                finally { GuildStore.Leave(oB, out _); TreasuryStore.PurgeOwnerForTest(oB); Recovery.RecoveryStore.ClearUser(oA); Drop(oA, g); }
            });

            Check("Guild ABA: a perk charged and then orphaned by a disband is held once for an admin, not paid to whoever bought it", () =>
            {
                (string oA, string g) = Seed(50_000);
                try
                {
                    GuildStore.AtPerkPhaseForTest = ph => { if (ph == "charged") GuildStore.Leave(oA, out _); };
                    bool bought = GuildStore.BuyPerk(oA, Perk, out string why, out _, out _);
                    GuildStore.AtPerkPhaseForTest = null;
                    Restart(); Recover(); Recover();
                    long personalA = TreasuryStore.GetPersonalSilver(oA);
                    var holds = Recovery.RecoveryStore.ForUser(oA);
                    long held = 0; foreach (var h in holds) if (h.Kind == "silver") held += h.Silver;
                    bool ok = !bought && personalA == 50_000 - cost && holds.Count == 1 && held == cost
                           && GuildStore.PendingPerkCountForTest == 0;
                    return (ok, $"bought={bought} ({why}) handedBack={personalA} (want {50_000 - cost}) holds={holds.Count} held={held} (want {cost})");
                }
                finally { Recovery.RecoveryStore.ClearUser(oA); Drop(oA, g); }
            });

            Check("Guild ABA: a donation checked against one guild and booked after a same-named guild replaced it is refused", () =>
            {
                (string oA, string g) = Seed(1);
                string donor = User("d"), oB = User("b");
                string txn = "selftestdon" + Guid.NewGuid().ToString("N").Substring(0, 12);
                try
                {
                    if (!GuildStore.AddMember(donor, g)) return (false, "member");
                    if (!TreasuryStore.DepositSilver(donor, 1000)) return (false, "fund");
                    GuildStore.AfterDonationCheckForTest = _ =>
                    {
                        GuildStore.AfterDonationCheckForTest = null;
                        GuildStore.Leave(donor, out _); GuildStore.Leave(oA, out _);
                        if (!GuildStore.CreateGuildAndJoinAsAdmin(oB, g, out string w)) throw new InvalidOperationException("recreate: " + w);
                    };
                    bool booked = GuildStore.DepositToGuild(donor, 300, out string why, default, txn);
                    GuildStore.AfterDonationCheckForTest = null;
                    TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    long vaultB = TreasuryStore.GetGuildSilver(g), donorSilver = TreasuryStore.GetPersonalSilver(donor);
                    bool ok = !booked && vaultB == 0 && donorSilver == 1000;
                    return (ok, $"booked={booked} ({why}) newGuildVault={vaultB} (want 0) donor={donorSilver} (want 1000)");
                }
                finally
                {
                    GuildStore.AfterDonationCheckForTest = null;
                    GuildStore.Leave(oB, out _); TreasuryStore.PurgeOwnerForTest(oB); TreasuryStore.PurgeOwnerForTest(donor); Drop(oA, g);
                }
            });

            Check("Guild ABA: a pending donation left by a crash mid-disband is returned before a same-named guild can take it", () =>
            {
                (string oA, string g) = Seed(1);
                string donor = User("d"), oB = User("b");
                string txn = "selftestdon" + Guid.NewGuid().ToString("N").Substring(0, 12);
                try
                {
                    if (!GuildStore.AddMember(donor, g)) return (false, "member");
                    if (!TreasuryStore.DepositSilver(donor, 1000)) return (false, "fund");
                    if (!TreasuryStore.BeginPendingGuildDonation(donor, g, 300, txn, out string w)) return (false, "begin: " + w);
                    GuildStore.Leave(donor, out _);
                    // The disband commits and the process dies after the vault hand-back, before the name's clean-up.
                    GuildStore.AfterVaultHandBackForTest = _ => throw new OperationCanceledException("selftest crash");
                    try { GuildStore.Leave(oA, out _); } catch (OperationCanceledException) { }
                    GuildStore.AfterVaultHandBackForTest = null;
                    Restart();
                    // A new guild takes the name, and the old donation's save confirmation lands before its clean-up does.
                    GuildStore.AfterCreateCommitForTest = _ => TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(oB, g, out w)) return (false, "recreate: " + w);
                    GuildStore.AfterCreateCommitForTest = null;
                    long vaultB = TreasuryStore.GetGuildSilver(g), donorSilver = TreasuryStore.GetPersonalSilver(donor);
                    Restart();
                    bool stable = TreasuryStore.GetGuildSilver(g) == vaultB && TreasuryStore.GetPersonalSilver(donor) == donorSilver;
                    bool ok = vaultB == 0 && donorSilver == 1000 && stable;
                    return (ok, $"newGuildVault={vaultB} (want 0) donor={donorSilver} (want 1000) stableAfterReload={stable}");
                }
                finally
                {
                    GuildStore.AfterVaultHandBackForTest = null; GuildStore.AfterCreateCommitForTest = null;
                    GuildStore.Leave(oB, out _); TreasuryStore.PurgeOwnerForTest(oB); TreasuryStore.PurgeOwnerForTest(donor); Drop(oA, g);
                }
            });

            Check("Guild ABA: a pending donation recorded without its guild's incarnation goes back to the donor, never to a name", () =>
            {
                (string oA, string g) = Seed(1);
                string donor = User("d");
                string txn = "selftestdon" + Guid.NewGuid().ToString("N").Substring(0, 12);
                try
                {
                    if (!GuildStore.AddMember(donor, g)) return (false, "member");
                    if (!TreasuryStore.DepositSilver(donor, 1000)) return (false, "fund");
                    if (!TreasuryStore.BeginPendingGuildDonation(donor, g, 300, txn, out string w)) return (false, "begin: " + w);
                    // As a row written by an older build, which recorded only the name.
                    if (!TreasuryStore.ForgetPendingGuildIdentityForTest(donor, txn)) return (false, "no pending row");
                    int confirmed = TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    long vault = TreasuryStore.GetGuildSilver(g), donorSilver = TreasuryStore.GetPersonalSilver(donor);
                    bool ok = vault == 1 && donorSilver == 1000;
                    return (ok, $"confirmed={confirmed} guildVault={vault} (want 1) donor={donorSilver} (want 1000)");
                }
                finally { GuildStore.Leave(donor, out _); TreasuryStore.PurgeOwnerForTest(donor); Drop(oA, g); }
            });

            return r;
        }
    }
}
