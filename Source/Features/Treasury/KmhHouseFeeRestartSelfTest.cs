using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Treasury.Dto;

namespace KMHServerAddon.Features.Treasury
{
    // The fee is owed in the vault's own commit and credited on that marker, or a kill between the vault's and the
    // pool's saves keeps the deposit and loses the fee; each case reloads FROM DISK and settles twice.
    internal static class KmhHouseFeeRestartSelfTest
    {
        private static string User(string tag) => $"selftest_hf{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static void Restart() { TreasuryStore.LoadFromDisk(); MarketplaceStore.LoadFromDisk(); }

        private static void Boot() { TreasuryStore.SettleOwedFees(); TreasuryStore.SettleOwedFees(); }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            // Memory and disk agree before anything is measured, or the first reload counts an earlier run's pool as a fee.
            Restart();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.GetType().Name}: {ex.Message}")); }
                finally { TreasuryStore.AtFeePhaseForTest = null; Mail.MailStore.AtSendPhaseForTest = null; }
            }
            void Die(string at) => TreasuryStore.AtFeePhaseForTest = p => { if (p == at) throw new OperationCanceledException("selftest: died at " + p); };

            (bool, string) Confirm(string phase, bool reconcile)
            {
                string who = User("d");
                string txn = Guid.NewGuid().ToString("N");
                try
                {
                    if (!TreasuryStore.BeginPendingDeposit(who, new PendingDeposit { TxnId = txn, Kind = PendingDeposit.KindSilver, Silver = 198, Fee = 2 }))
                        return (false, "begin");
                    long pool0 = MarketplaceStore.HousePoolBalance();
                    if (phase != null) Die(phase);
                    try
                    {
                        if (reconcile) TreasuryStore.ReconcileDeposits(who, new[] { txn }, null, 0);
                        else TreasuryStore.ConfirmDeposits(who, new[] { txn });
                    }
                    catch (OperationCanceledException) { }
                    TreasuryStore.AtFeePhaseForTest = null;
                    long poolAtCrash = MarketplaceStore.HousePoolBalance() - pool0;
                    Restart();
                    Boot();
                    long vault = TreasuryStore.GetPersonalSilver(who), fee = MarketplaceStore.HousePoolBalance() - pool0;
                    int owed = TreasuryStore.OwedFeeCountForTest(TreasuryStore.FeeMarkerFor(TreasuryStore.PersonalKeyFor(who), ""));
                    bool ok = vault == 198 && fee == 2 && owed == 0;
                    return (ok, $"pool at the cut +{poolAtCrash}; after restart and two settles: vault={vault} pool +{fee} still owed={owed}");
                }
                finally
                {
                    TreasuryStore.ForgetOwedFeesForTest(TreasuryStore.FeeMarkerFor(TreasuryStore.PersonalKeyFor(who), ""));
                    TreasuryStore.PurgeOwnerForTest(who);
                }
            }

            Check("House fee restart: a deposit confirm cut off after the vault's save credits its fee once, at boot",
                  () => Confirm("vault-saved", reconcile: false));
            Check("House fee restart: a deposit confirm cut off after the pool's save but before the clear credits nothing twice",
                  () => Confirm("pool-saved", reconcile: false));
            Check("House fee restart: a reconcile cut off after the vault's save credits its fee once, at boot",
                  () => Confirm("vault-saved", reconcile: true));
            Check("House fee: an uninterrupted confirm credits the fee once and leaves nothing owed",
                  () => Confirm(null, reconcile: false));

            // A rolled-back deposit must take its fee back from the pool too, or the pool keeps a fee nobody paid.
            foreach (string phase in new[] { null, "return-vault-saved", "return-pool-saved" })
            {
                Check("House fee: a deposit reversed by an older save takes its fee back from the pool once" +
                      (phase == null ? "" : $", across a cut at '{phase}'"), () =>
                {
                    string who = User("r");
                    string txn = Guid.NewGuid().ToString("N");
                    string ownerKey = TreasuryStore.PersonalKeyFor(who);
                    try
                    {
                        long pool0 = MarketplaceStore.HousePoolBalance();
                        if (!TreasuryStore.BeginPendingDeposit(who, new PendingDeposit { TxnId = txn, Kind = PendingDeposit.KindSilver, Silver = 198, Fee = 2 }))
                            return (false, "begin");
                        TreasuryStore.ConfirmDeposits(who, new[] { txn });
                        long credited = MarketplaceStore.HousePoolBalance() - pool0;
                        if (phase != null) Die(phase);
                        try { TreasuryStore.ReverseRolledBackCommits(who, new string[0], null, 0); } catch (OperationCanceledException) { }
                        TreasuryStore.AtFeePhaseForTest = null;
                        Restart();
                        Boot();
                        TreasuryStore.ReverseRolledBackCommits(who, new string[0], null, 0);
                        Boot();
                        long vault = TreasuryStore.GetPersonalSilver(who), pool = MarketplaceStore.HousePoolBalance() - pool0;
                        int owed = TreasuryStore.OwedFeeCountForTest(TreasuryStore.FeeReturnMarkerFor(ownerKey, ""));
                        bool ok = credited == 2 && vault == 0 && pool == 0 && owed == 0;
                        return (ok, $"fee credited +{credited}; after the reversal, a restart and a second join: vault={vault} pool {pool:+0;-0;0} (want 0) still owed={owed}");
                    }
                    finally
                    {
                        TreasuryStore.ForgetOwedFeesForTest(TreasuryStore.FeeMarkerFor(ownerKey, ""));
                        TreasuryStore.ForgetOwedFeesForTest(TreasuryStore.FeeReturnMarkerFor(ownerKey, ""));
                        TreasuryStore.PurgeOwnerForTest(who);
                    }
                });
            }

            Check("House fee restart: an immediate deposit and an access fee cut off after the vault's save are each credited once", () =>
            {
                string who = User("i");
                string prefix = TreasuryStore.FeeMarkerFor(TreasuryStore.PersonalKeyFor(who), "");
                try
                {
                    long pool0 = MarketplaceStore.HousePoolBalance();
                    Die("vault-saved");
                    try { TreasuryStore.DepositSilverOwingFee(who, 495, 5, "selftest deposit"); } catch (OperationCanceledException) { }
                    Die("vault-saved");
                    try { TreasuryStore.ChargeHouseFee(who, 7, "treasury access fee"); } catch (OperationCanceledException) { }
                    TreasuryStore.AtFeePhaseForTest = null;
                    long atCrash = MarketplaceStore.HousePoolBalance() - pool0;
                    Restart();
                    Boot();
                    long vault = TreasuryStore.GetPersonalSilver(who), fees = MarketplaceStore.HousePoolBalance() - pool0;
                    bool ok = atCrash == 0 && vault == 488 && fees == 12 && TreasuryStore.OwedFeeCountForTest(prefix) == 0;
                    return (ok, $"pool at the cut +{atCrash}; after restart: vault={vault} (want 488) pool +{fees} (want 12)");
                }
                finally { TreasuryStore.ForgetOwedFeesForTest(prefix); TreasuryStore.PurgeOwnerForTest(who); }
            });

            foreach (string phase in new[] { "saved", "fee-paid" })
            {
                Check($"House fee restart: a mail attachment's fee survives a cut at '{phase}' and is paid once", () =>
                {
                    string from = User("mf"), to = User("mt");
                    Mail.MailConfig cfg = Mail.MailConfig.Current;
                    int hadFlat = cfg.AttachmentFeeFlat; double hadPct = cfg.AttachmentFeePercent;
                    try
                    {
                        cfg.AttachmentFeeFlat = 6; cfg.AttachmentFeePercent = 0;
                        if (!TreasuryStore.DepositSilver(from, 1000, "selftest seed")) return (false, "seed");
                        long pool0 = MarketplaceStore.HousePoolBalance();
                        Mail.MailStore.AtSendPhaseForTest = p => { if (p == phase) throw new OperationCanceledException("selftest: died at " + p); };
                        try { Mail.MailStore.Send(from, to, "s", "b", 100, null, null, out _, out _, out _); }
                        catch (OperationCanceledException) { }
                        Mail.MailStore.AtSendPhaseForTest = null;
                        long atCrash = MarketplaceStore.HousePoolBalance() - pool0;
                        Transactions.KmhTransactionRepository.RecoverPendingForTest(from);
                        Transactions.KmhTransactionRepository.RecoverPendingForTest(from);
                        long fee = MarketplaceStore.HousePoolBalance() - pool0;
                        bool ok = atCrash == (phase == "saved" ? 0 : 6) && fee == 6 && TreasuryStore.GetPersonalSilver(from) == 894;
                        return (ok, $"pool at the cut +{atCrash}; after recovery twice +{fee} (want 6); sender={TreasuryStore.GetPersonalSilver(from)}");
                    }
                    finally
                    {
                        cfg.AttachmentFeeFlat = hadFlat; cfg.AttachmentFeePercent = hadPct;
                        Mail.MailStore.PurgeUser(from); Mail.MailStore.PurgeUser(to);
                        Transactions.KmhTransactionRepository.AbortAllFor(from);
                        TreasuryStore.PurgeOwnerForTest(from); TreasuryStore.PurgeOwnerForTest(to);
                    }
                });
            }

            Check("House fee: a withdrawal fee the pool will not take is owed, then paid once", () =>
            {
                string who = User("w");
                string hadIdentity = KmhServerIdentity.Id;
                if (string.IsNullOrEmpty(hadIdentity)) Persistence.KmhDataMeta.SetInstanceIdForTest("selftestsrv");
                Economy.EconomyConfig hadCfg = Economy.EconomyConfig.ApplyForTest(new Economy.EconomyConfig { EconomyMode = "Balanced" });
                try
                {
                    if (!TreasuryStore.DepositSilver(who, 5000, "selftest seed")) return (false, "seed");
                    long pool0 = MarketplaceStore.HousePoolBalance();
                    TreasuryHandler.WithdrawOutcome res;
                    using (Persistence.JsonFileStore.FailWritesForTest(p => p == Persistence.KmhDataPaths.MarketplaceFile ? "selftest" : null))
                        res = TreasuryHandler.WithdrawSilverToColony(who, 1000, out _);
                    long refused = MarketplaceStore.HousePoolBalance() - pool0;
                    TreasuryStore.SettleOwedFees();
                    TreasuryStore.SettleOwedFees();
                    long fee = MarketplaceStore.HousePoolBalance() - pool0;
                    bool ok = res == TreasuryHandler.WithdrawOutcome.Delivered && refused == 0 && fee == 10;
                    return (ok, $"outcome={res} pool while refused +{refused}; after two settles +{fee} (want 10)");
                }
                finally
                {
                    Economy.EconomyConfig.ApplyForTest(hadCfg);
                    if (string.IsNullOrEmpty(hadIdentity)) Persistence.KmhDataMeta.SetInstanceIdForTest(hadIdentity);
                    Transactions.KmhTransactionRepository.AbortAllFor(who);
                    Delivery.DeliveryStore.PurgeUser(who);
                    TreasuryStore.PurgeOwnerForTest(who);
                }
            });

            return r;
        }
    }
}
