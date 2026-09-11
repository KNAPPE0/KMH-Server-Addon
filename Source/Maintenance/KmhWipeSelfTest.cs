using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Economy;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Maintenance
{
    // A destructive cleanup must destroy exactly what it claims, survive a restart mid-way, and leave nothing that hands the value back.
    internal static class KmhWipeSelfTest
    {
        private static string User() => "selftest_wipe_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Wipe: a pending transaction cannot refund value the wipe destroyed", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositItem(who, "Steel", 12, "selftest seed")) return (false, "seed");

                KmhTransaction open = KmhTransactionRepository.OpenTake(who, KmhTxType.Listing, "selftest open", 0, 0,
                    new Dictionary<string, int> { { "Steel", 12 } }, null);
                if (open == null) return (false, "could not open");
                if (!TreasuryStore.WithdrawItemForTxn(who, "Steel", 12, open.TakeMarker, "selftest take")) return (false, "take");

                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                KmhEconomyReset.Run(who, "");

                // Boot recovery after a wipe must not treat the old row as owed - the goods were destroyed on purpose.
                KmhTransactionRepository.Compensate(open);
                int steel = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = steel == 0;
                return (ok, ok ? "the wipe stands; the closed row returned nothing"
                              : $"steelBack={steel} (expected 0) - A WIPE WAS UNDONE BY COMPENSATION");
            });

            Check("Wipe: an unacked delivery does not hand the value back on the next join", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(who, 250, "selftest seed")) return (false, "seed");
                var owed = Features.Delivery.DeliveryStore.Owe(who, 250, null, null, "selftest delivery");
                if (owed == null) return (false, "could not owe");
                bool owedBefore = Features.Delivery.DeliveryStore.OwedFor(who).Count == 1;

                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                KmhEconomyReset.Run(who, "");

                bool owedAfter = Features.Delivery.DeliveryStore.OwedFor(who).Count == 0;
                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = owedBefore && owedAfter;
                return (ok, ok ? "the owed delivery went with the wipe"
                              : $"owedBefore={owedBefore}, clearedByWipe={owedAfter}");
            });

            Check("Wipe: the mailbox goes, and a third party's attachment goes home rather than burning", () =>
            {
                string who = User(), friend = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(friend, 400, "selftest seed")) return (false, "seed friend");

                long id = Features.Mail.MailStore.Send(friend, who, "hi", "body", 300, null, null,
                                                       out _, out _, out string sendWhy);
                if (id <= 0) return (false, "send: " + sendWhy);
                long friendAfterSend = TreasuryStore.GetPersonalSilver(friend);
                bool inboxSeeded = Features.Mail.MailStore.CountFor(who) == 1;

                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                KmhEconomyReset.Run(who, "");

                bool inboxGone = Features.Mail.MailStore.CountFor(who) == 0;
                // The silver was never the wiped player's, so destroying it would take a third party's value.
                bool returned = TreasuryStore.GetPersonalSilver(friend) == friendAfterSend + 300;
                TreasuryStore.PurgeOwnerForTest(who); TreasuryStore.PurgeOwnerForTest(friend);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = inboxSeeded && inboxGone && returned;
                return (ok, ok ? "mailbox removed, the sender's escrow returned to them"
                              : $"seeded={inboxSeeded}, inboxGone={inboxGone}, senderRefunded={returned}");
            });

            Check("Wipe: interrupted part-way, one resume produces exactly one complete wipe", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(who, 700, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositItem(who, "Steel", 9, "selftest seed")) return (false, "seed items");
                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);

                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.EconomyResetFile ? "injected: disk full" : null))
                    KmhEconomyReset.Run(who, "");
                bool stillOpen = KmhEconomyReset.InProgress;
                bool barred = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                KmhEconomyReset.RecoverOnBoot();
                var snap = TreasuryStore.GetSnapshotFor(who);
                bool empty = TreasuryStore.GetPersonalSilver(who) == 0
                          && (snap?.Items == null || snap.Items.Count == 0);
                bool finished = !KmhEconomyReset.InProgress;
                bool released = KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                // A second boot must not start the wipe again on a player who has since earned something back.
                if (!TreasuryStore.DepositSilver(who, 50, "post-wipe earnings")) return (false, "reseed");
                KmhEconomyReset.RecoverOnBoot();
                bool keptEarnings = TreasuryStore.GetPersonalSilver(who) == 50;

                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = stillOpen && barred && finished && empty && released && keptEarnings;
                return (ok, ok ? "stayed open and barred, resumed once, no second wipe on the next boot"
                              : $"stayedOpen={stillOpen}, barred={barred}, finished={finished}, emptied={empty}, " +
                                $"released={released}, keptPostWipeEarnings={keptEarnings}");
            });

            Check("Wipe scope: an economy wipe keeps identity, a player wipe removes it", () =>
            {
                string eco = User(), full = User();
                KmhEconomyReset.ResetForTest();
                Features.PlayerStats.PlayerStatsStore.EnsurePlayer(eco);
                Features.PlayerStats.PlayerStatsStore.EnsurePlayer(full);

                if (!KmhEconomyReset.Begin(eco, "", KmhResetScope.Economy, out string w1)) return (false, "begin eco: " + w1);
                KmhEconomyReset.Run(eco, "");
                bool ecoKeptStats = Features.PlayerStats.PlayerStatsStore.HasPlayer(eco);

                if (!KmhEconomyReset.Begin(full, "", KmhResetScope.Player, out string w2)) return (false, "begin full: " + w2);
                KmhEconomyReset.Run(full, "");
                bool fullLostStats = !Features.PlayerStats.PlayerStatsStore.HasPlayer(full);

                Features.PlayerStats.PlayerStatsStore.RemoveUser(eco);
                EconomyResetStore.Forget(eco); EconomyResetStore.Forget(full);
                KmhEconomyReset.ResetForTest();
                bool ok = ecoKeptStats && fullLostStats;
                return (ok, ok ? "a new-save reset keeps standings; an admin player wipe does not"
                              : $"economyKeptStats={ecoKeptStats}, playerWipeRemovedStats={fullLostStats}");
            });

            return r;
        }

        private static int ItemCount(string who, string def)
        {
            var s = TreasuryStore.GetSnapshotFor(who);
            if (s?.Items == null) return 0;
            int n = 0;
            foreach (KeyValuePair<string, int> kv in s.Items)
                if (Util.ItemKey.Matches(kv.Key, def, "", 0)) n += kv.Value;
            return n;
        }
    }
}
