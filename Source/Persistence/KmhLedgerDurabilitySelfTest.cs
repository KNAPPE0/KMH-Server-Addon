using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Persistence
{
    // Each check makes a durable change, then reads the ledger FILE directly (never Flush or ReadRecent): the line must
    // already be on disk as a crash finds it, or a kill after a confirmed deposit keeps the balance but loses its line.
    internal static class KmhLedgerDurabilitySelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.GetType().Name}: {ex.Message}")); }
            }
            string Tag() => "ledger-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            bool OnDisk(string note)
            {
                string file = Path.Combine(KmhDataPaths.LedgerDir, $"ledger-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                return File.Exists(file) && File.ReadAllText(file).Contains(note);
            }

            Check("Ledger: a personal deposit's line is on disk the moment the deposit is", () =>
            {
                string user = "selftest_ld_" + Guid.NewGuid().ToString("N").Substring(0, 8), note = Tag();
                try
                {
                    bool saved = TreasuryStore.DepositSilver(user, 198, note);
                    return (saved && OnDisk(note), $"saved={saved} line on disk={OnDisk(note)}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(user); }
            });

            Check("Ledger: a guild vault credit's line is on disk the moment the credit is", () =>
            {
                string owner = "selftest_ld_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string guild = "selftest ld " + Guid.NewGuid().ToString("N").Substring(0, 6), note = Tag();
                try
                {
                    if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "guild: " + why);
                    bool saved = TreasuryStore.DepositGuildSilver(guild, 40, owner, note);
                    return (saved && OnDisk(note), $"saved={saved} line on disk={OnDisk(note)}");
                }
                finally { Features.Guilds.GuildStore.Leave(owner, out _); TreasuryStore.PurgeOwnerForTest(owner); TreasuryStore.PurgeOwnerForTest(guild); }
            });

            Check("Ledger: house-pool credits, spends and returns are on disk with the pool's own save", () =>
            {
                string credit = Tag(), once = Tag(), back = Tag(), spend = Tag();
                bool c = MarketplaceStore.TryCreditHousePool(7, credit);
                bool o = MarketplaceStore.TryCreditHousePoolOnce("ledger-selftest:" + once, 5, once);
                MarketplaceStore.ReturnToHousePool(3, back);
                bool backOnDisk = OnDisk(back);
                bool s = MarketplaceStore.TryDebitHousePool(15, spend);
                bool ok = c && o && s && OnDisk(credit) && OnDisk(once) && backOnDisk && OnDisk(spend);
                return (ok, $"credit={c}/{OnDisk(credit)} once={o}/{OnDisk(once)} return={backOnDisk} spend={s}/{OnDisk(spend)}");
            });

            Check("Ledger: a house-pool credit whose save fails leaves no line, then or later", () =>
            {
                string note = Tag();
                bool credited;
                long before = MarketplaceStore.HousePoolBalance();
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "selftest" : null))
                    credited = MarketplaceStore.TryCreditHousePool(9, note);
                TransactionLedger.Flush();
                bool ok = !credited && !OnDisk(note) && MarketplaceStore.HousePoolBalance() == before;
                return (ok, $"credited={credited} line on disk={OnDisk(note)} pool {before} -> {MarketplaceStore.HousePoolBalance()}");
            });

            // A refused deposit's line is queued before its save, so the flusher must not write it on its next beat.
            Check("Ledger: a treasury deposit whose save fails leaves no line, then or later; the next one that saves does", () =>
            {
                string user = "selftest_ld_" + Guid.NewGuid().ToString("N").Substring(0, 8), refused = Tag(), kept = Tag();
                try
                {
                    bool deposited;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "selftest" : null))
                        deposited = TreasuryStore.DepositSilver(user, 31, refused);
                    TransactionLedger.Flush();
                    bool refusedOnDisk = OnDisk(refused);
                    bool saved = TreasuryStore.DepositSilver(user, 12, kept);
                    bool ok = !deposited && !refusedOnDisk && !OnDisk(refused) && saved && OnDisk(kept)
                           && TreasuryStore.GetPersonalSilver(user) == 12;
                    return (ok, $"refused: deposited={deposited} line={refusedOnDisk}/{OnDisk(refused)}; next: saved={saved} line={OnDisk(kept)} balance={TreasuryStore.GetPersonalSilver(user)}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(user); }
            });

            // A disband hand-back whose save fails stays in memory; a commit refused after it must not drop its line too.
            Check("Ledger: a refused commit drops only its own lines; an unsaved change that stands is written with the next save", () =>
            {
                string guild = "selftest-ld-" + Guid.NewGuid().ToString("N").Substring(0, 10), refused = Tag();
                string heir = "selftest_ld_" + Guid.NewGuid().ToString("N").Substring(0, 8), other = "selftest_ld_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    TreasuryStore.PutVaultForTest(guild, 23);
                    long moved;
                    bool deposited;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "selftest" : null))
                    {
                        moved = TreasuryStore.MoveGuildVaultToPersonal(guild, heir);
                        deposited = TreasuryStore.DepositSilver(other, 8, refused);
                    }
                    TransactionLedger.Flush();
                    bool early = OnDisk(guild);
                    bool saved = TreasuryStore.SaveToDisk();
                    bool ok = moved == 23 && !deposited && !early && saved && OnDisk(guild) && !OnDisk(refused)
                           && TreasuryStore.GetPersonalSilver(heir) == 23;
                    return (ok, $"moved={moved} refused deposit={deposited} hand-back line before/after the save={early}/{OnDisk(guild)} refused line={OnDisk(refused)}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(heir); TreasuryStore.PurgeOwnerForTest(other); TreasuryStore.PurgeOwnerForTest(guild); }
            });

            return r;
        }
    }
}
