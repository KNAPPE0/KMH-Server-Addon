using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Economy;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Maintenance
{
    // A reset destroys authoritative state, so who starts it, its resume and stale-op refusal are all pinned.
    internal static class KmhResetLifecycleSelfTest
    {
        private static string User() => "selftest_reset_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Reset authority: a client cannot reset itself twice by claiming another new save", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);

                // First sighting is never a reset, or every returning player would be wiped on join.
                bool firstSighting = !EconomyResetStore.ShouldReset(who, "save-a", out string r1);
                bool sameSave      = !EconomyResetStore.ShouldReset(who, "save-a", out string r2);
                bool changed       =  EconomyResetStore.ShouldReset(who, "save-b", out string r3);

                EconomyResetStore.ConfirmReset(who, "save-b");
                bool rateLimited   = !EconomyResetStore.ShouldReset(who, "save-c", out string r4);

                EconomyResetStore.Forget(who);
                bool ok = firstSighting && sameSave && changed && rateLimited;
                return (ok, ok ? "first sighting and repeats refused; one change accepted, the next rate-limited"
                              : $"first={firstSighting}({r1}), same={sameSave}({r2}), changed={changed}({r3}), limited={rateLimited}({r4})");
            });

            Check("Reset durability: the save id advances only when the reset finishes", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                EconomyResetStore.ShouldReset(who, "save-a", out _);          // first sighting stores save-a
                if (!EconomyResetStore.ShouldReset(who, "save-b", out string why)) return (false, "should reset: " + why);

                if (!KmhEconomyReset.Begin(who, "save-b", out string beginWhy)) return (false, "begin: " + beginWhy);
                // Mid-reset the id must still read as the old save, or a restart would decide the work was done.
                bool stillOld = EconomyResetStore.ShouldReset(who, "save-b", out _) == false
                             && KmhEconomyReset.InProgress;
                // Discord, the SDK and scheduler settlement reach the same stores, so every ingress is barred mid-reset.
                bool barrier = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _)
                            && !KmhAdmission.AllowsValueMutation(KmhIngress.ClientChat, out _)
                            && !KmhAdmission.AllowsValueMutation(KmhIngress.Discord, out _)
                            && !KmhAdmission.AllowsValueMutation(KmhIngress.Sdk, out _)
                            && !KmhAdmission.AllowsValueMutation(KmhIngress.Scheduler, out _);
                bool adminOk =  KmhAdmission.AllowsValueMutation(KmhIngress.Admin, out _)
                            &&  KmhAdmission.AllowsValueMutation(KmhIngress.Recovery, out _);

                KmhEconomyReset.Run(who, "save-b");
                bool finished = !KmhEconomyReset.InProgress;
                bool released =  KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                bool advanced = !EconomyResetStore.ShouldReset(who, "save-b", out _);

                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = stillOld && barrier && adminOk && finished && released && advanced;
                return (ok, ok ? "barrier held for players, open for admin; id advanced only at the end"
                              : $"heldOldId={stillOld}, barrier={barrier}, adminOk={adminOk}, finished={finished}, " +
                                $"released={released}, advanced={advanced}");
            });

            Check("Reset resume: a reset interrupted mid-way finishes rather than half-applying", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(who, 900, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositItem(who, "Steel", 25, "selftest seed")) return (false, "seed items");

                if (!KmhEconomyReset.Begin(who, "save-b", out string why)) return (false, "begin: " + why);

                // The checkpoint write fails part-way, exactly as a crash mid-reset looks on the next boot.
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.EconomyResetFile ? "injected: disk full" : null))
                    KmhEconomyReset.Run(who, "save-b");
                bool stillOpen = KmhEconomyReset.InProgress;
                bool stillBarred = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                // Boot would call this; it must resume the same reset, not start a new one or give up.
                KmhEconomyReset.RecoverOnBoot();
                bool done      = !KmhEconomyReset.InProgress;
                long silver    = TreasuryStore.GetPersonalSilver(who);
                var  snap      = TreasuryStore.GetSnapshotFor(who);
                bool emptied   = silver == 0 && (snap?.Items == null || snap.Items.Count == 0);
                bool freeAgain =  KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = stillOpen && stillBarred && done && emptied && freeAgain;
                return (ok, ok ? "stayed open and barred, resumed on boot, vault fully cleared"
                              : $"stayedOpen={stillOpen}, stayedBarred={stillBarred}, resumed={done}, " +
                                $"emptied={emptied} (silver={silver}), released={freeAgain}");
            });

            Check("Reset generation: a pre-reset transaction is refused, not refunded", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositItem(who, "Steel", 8, "selftest seed")) return (false, "seed");

                KmhTransaction stale = KmhTransactionRepository.OpenTake(who, KmhTxType.Listing, "selftest stale", 0, 0,
                    new Dictionary<string, int> { { "Steel", 8 } }, null);
                if (stale == null) return (false, "could not open");
                if (!TreasuryStore.WithdrawItemForTxn(who, "Steel", 8, stale.TakeMarker, "selftest take")) return (false, "take");

                EconomyResetStore.ShouldReset(who, "save-a", out _);
                if (!KmhEconomyReset.Begin(who, "save-b", out string why)) return (false, "begin: " + why);
                KmhEconomyReset.Run(who, "save-b");

                // The row was authorised against a colony that no longer exists; compensating it would undo the reset.
                bool stale1 = KmhTransactionRepository.IsStale(stale);
                bool refusedUpdate = !KmhTransactionRepository.Update(stale);
                KmhTransactionRepository.Compensate(stale);
                int steel = 0;
                var snap = TreasuryStore.GetSnapshotFor(who);
                if (snap?.Items != null) foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, "Steel", "", 0)) steel += kv.Value;

                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = stale1 && refusedUpdate && steel == 0;
                return (ok, ok ? "stale row refused and closed; the reset's destruction stands"
                              : $"detectedStale={stale1}, refusedUpdate={refusedUpdate}, steelBack={steel} (expected 0)");
            });

            // The vault is not all a save owns: a payout written down but unpaid would otherwise land in the new save.
            Check("Reset authority: a new save with an empty vault still drops what the old save was owed", () =>
            {
                string who = User();
                EconomyConfig cfg = KmhDeepCopy.Of(EconomyConfig.Current);
                cfg.ResetEconomyOnNewSave = true;
                EconomyConfig hadCfg = EconomyConfig.ApplyForTest(cfg);
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                try
                {
                    EconomyResetStore.ShouldReset(who, "save-a", out _);
                    Features.Mail.MailStore.PlantPendingPayoutForTest(who, 300);
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = who });
                    Features.PlayerStats.PlayerStatsHandler.ColonyReportForTest(c, SubProtocol.KmhEnvelope.ParseForTest(new { save_id = "save-b" }));
                    Features.Mail.MailStore.RecoverPendingPayouts();
                    long silver = TreasuryStore.GetPersonalSilver(who);
                    bool ok = silver == 0 && !KmhEconomyReset.InProgress;
                    return (ok, ok ? "the owed payout went with the old save" : $"{silver} silver owed to the old save was paid into the new one");
                }
                finally
                {
                    EconomyConfig.ApplyForTest(hadCfg);
                    Features.Mail.MailStore.DropPendingPayoutsFor(who);
                    Features.PlayerStats.PlayerStatsStore.RemoveUser(who);
                    TreasuryStore.PurgeOwnerForTest(who);
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                }
            });

            // A restart reads the save id from disk: an unwritten one is the old save, and the new one would be wiped again.
            Check("Reset durability: a new save id the disk refused keeps the reset open until it is written", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                try
                {
                    EconomyResetStore.ShouldReset(who, "save-a", out _);
                    if (!KmhEconomyReset.Begin(who, "save-b", out string why)) return (false, "begin: " + why);
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SaveIdsFile ? "injected: disk full" : null))
                        KmhEconomyReset.Run(who, "save-b");
                    bool heldOpen = KmhEconomyReset.Resetting(who) && KmhEconomyReset.StalledForTest
                                 && !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                    KmhEconomyReset.ResumeStalled();
                    bool closed = !KmhEconomyReset.InProgress;
                    EconomyResetStore.LoadFromDisk();
                    bool onDisk = !EconomyResetStore.ShouldReset(who, "save-b", out string after);
                    bool ok = heldOpen && closed && onDisk;
                    return (ok, ok ? "held open while the id was unwritten; closed by the retry with the new id on disk"
                                  : $"heldOpen={heldOpen}, closedByRetry={closed}, newIdOnDisk={onDisk} ({after})");
                }
                finally
                {
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                }
            });

            return r;
        }
    }
}
