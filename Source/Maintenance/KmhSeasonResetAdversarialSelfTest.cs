using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Economy;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Maintenance
{
    // Per-player reset is covered by KmhResetLifecycleSelfTest; this is the adversarial, server-wide half.
    internal static class KmhSeasonResetAdversarialSelfTest
    {
        private static string User() => "selftest_season_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Season reset: a row opened before a server-wide wipe is stale afterwards", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                KmhTransaction pre = KmhTransaction.Create(who, "personal_treasury", KmhTxType.Listing, "pre-reset");
                long stampedAt = pre.TargetGeneration;

                // Exactly what KmhSeasonReset calls after destroying everyone's data.
                KmhEconomyReset.BumpDataGeneration("selftest season wipe");

                bool stale = KmhTransactionRepository.IsStale(pre);
                // A row opened after the wipe carries the new floor, so the guard does not refuse live work.
                KmhTransaction post = KmhTransaction.Create(who, "personal_treasury", KmhTxType.Listing, "post-reset");
                bool freshOk = !KmhTransactionRepository.IsStale(post);

                KmhEconomyReset.ResetForTest();
                bool ok = stale && freshOk;
                return (ok, ok ? "pre-wipe row refused, post-wipe row accepted"
                              : $"staleAfterWipe={stale} (stamped {stampedAt}, floor now {KmhEconomyReset.GenerationOf(who)}), "
                                + $"freshAccepted={freshOk} - A WORKER HOLDING PRE-RESET STATE CAN STILL MOVE VALUE");
            });

            Check("Season reset: a stale worker cannot hand back value the wipe destroyed", () =>
            {
                string buyer = User(), seller = User();
                KmhEconomyReset.ResetForTest();

                // Only the counter leg lacks a marker gate (the primary refund asks the vault), so only it can resurrect value.
                KmhTransaction tx = KmhTransaction.Create(buyer, "personal_treasury", KmhTxType.Purchase, "selftest");
                tx.CounterPlayer = seller;
                tx.CounterSilver = 500;
                tx.CounterItems["Steel"] = 12;
                tx.Advance(KmhTxState.Validating);
                tx.Advance(KmhTxState.Reserved);
                if (!KmhTransactionRepository.Add(tx)) return (false, "could not add");

                TreasuryStore.ClearAllVaults();
                KmhEconomyReset.BumpDataGeneration("selftest season wipe");
                KmhTransactionRepository.Compensate(tx);

                long silver = TreasuryStore.GetPersonalSilver(seller);
                int steel = 0;
                var snap = TreasuryStore.GetSnapshotFor(seller);
                if (snap?.Items != null) foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, "Steel", "", 0)) steel += kv.Value;

                TreasuryStore.PurgeOwnerForTest(seller);
                TreasuryStore.PurgeOwnerForTest(buyer);
                KmhEconomyReset.ResetForTest();
                bool ok = silver == 0 && steel == 0;
                return (ok, ok ? "the wipe stands; the stale row was closed, not settled"
                              : $"{silver} silver and {steel} Steel came back after the wipe - "
                                + "DESTROYED VALUE RESURRECTED INTO A CLEARED VAULT");
            });

            Check("Season reset: the destructive window is closed to ordinary value moves", () =>
            {
                List<string> calls = KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Maintenance.KmhSeasonReset", "Run");
                bool holds = calls.Contains("Enter") || calls.Contains("Run");
                // KeepOrRelease releases it, or keeps it held while a failed wipe waits for its retry.
                bool releases = calls.Contains("Release") || calls.Contains("KeepOrRelease");
                bool ok = holds && releases;
                return (ok, ok ? "the wipe runs inside the maintenance gate"
                              : "KmhSeasonReset.Run does not hold the maintenance gate - a marketplace purchase, "
                                + "auction settlement, want refund or mail claim can run DURING the wipe");
            });

            Check("Season reset: a second reset cannot start while one is running", () =>
            {
                // Begin is the only door that writes an operation down, so two of them is the whole re-entrancy question.
                string a = User(), b = User();
                KmhEconomyReset.ResetForTest();
                if (!KmhEconomyReset.Begin(a, "save-a", out string whyA)) return (false, "first begin: " + whyA);
                bool refused = !KmhEconomyReset.Begin(b, "save-b", out string whyB);
                bool sameRefused = !KmhEconomyReset.Begin(a, "save-a", out _);
                KmhEconomyReset.Run(a, "save-a");
                Features.Economy.EconomyResetStore.Forget(a);
                Features.Economy.EconomyResetStore.Forget(b);
                KmhEconomyReset.ResetForTest();
                bool ok = refused && sameRefused;
                return (ok, ok ? "a concurrent reset is refused for both a different and the same player"
                              : $"other={refused} ({whyB}), same={sameRefused}");
            });

            // The server-wide latch on KmhSeasonReset.Run: two confirmations would take two backups and roll twice.
            Check("Season reset: two operators confirming at once produce exactly one reset", () =>
            {
                using var inside = new ManualResetEventSlim(false);
                using var release = new ManualResetEventSlim(false);
                int entered = 0;
                KmhSeasonReset.BeforeWipeForTest = () =>
                {
                    Interlocked.Increment(ref entered);
                    inside.Set();
                    release.Wait(5000);
                    return false;          // stop before the backup and the wipe
                };
                try
                {
                    Task first = Task.Run(() => KmhSeasonReset.Run("operator-a", out _));
                    if (!inside.Wait(5000)) return (false, "the first reset never reached the wipe");

                    bool secondRan = KmhSeasonReset.Run("operator-b", out string whyB);
                    release.Set();
                    first.Wait(5000);

                    bool refused = !secondRan && whyB.IndexOf("already running", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool onlyOneEntered = Volatile.Read(ref entered) == 1;
                    bool ok = refused && onlyOneEntered;
                    return (ok, ok ? "the second operator is refused while the first holds the latch"
                                  : $"secondRefused={!secondRan} ({whyB}), enteredTheWipe={Volatile.Read(ref entered)}");
                }
                finally { KmhSeasonReset.BeforeWipeForTest = null; release.Set(); }
            });

            Check("Season reset: the generation floor survives a restart", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                KmhTransaction pre = KmhTransaction.Create(who, "personal_treasury", KmhTxType.Listing, "pre-reset");
                KmhEconomyReset.BumpDataGeneration("selftest season wipe");

                // A crash right after the bump reloads from disk; a floor kept only in memory would admit the stale row.
                long before = KmhEconomyReset.GenerationOf(who);
                KmhEconomyReset.ResetForTest();
                KmhEconomyReset.LoadFromDisk();
                long after = KmhEconomyReset.GenerationOf(who);
                bool stale = KmhTransactionRepository.IsStale(pre);

                KmhEconomyReset.ResetForTest();
                bool ok = after >= before && after > 0 && stale;
                return (ok, ok ? $"floor {after} reloaded from disk; the pre-wipe row is still refused"
                              : $"beforeRestart={before}, afterRestart={after}, staleAfterRestart={stale}");
            });

            // A player's own reset lifts their generation above the floor, so a floor moved by one can stay below it.
            Check("Season reset: a player reset before the wipe still has their pre-wipe rows refused", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                try
                {
                    if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                    KmhEconomyReset.Run(who, "");
                    KmhTransaction pre = KmhTransaction.Create(who, "personal_treasury", KmhTxType.Listing, "pre-wipe");
                    KmhEconomyReset.BumpDataGeneration("selftest season wipe");
                    bool stale = KmhTransactionRepository.IsStale(pre);
                    return (stale, stale ? "the wipe moved past the player's own generation"
                                         : $"stamped {pre.TargetGeneration}, generation after the wipe {KmhEconomyReset.GenerationOf(who)} - THE OLD ROW STILL SETTLES");
                }
                finally
                {
                    Features.Economy.EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                }
            });

            // The restored rows match the restored vaults; closing them unpaid would take what the backup still owed.
            Check("Restore: rows restored with the data stay payable, while actions issued before it are refused", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                try
                {
                    KmhTransaction restored = KmhTransaction.Create(who, "personal_treasury", KmhTxType.Listing, "restored");
                    long before = KmhEconomyReset.Generation;
                    KmhEconomyReset.BumpDataGeneration("selftest restore", staleRows: false);
                    bool payable = !KmhTransactionRepository.IsStale(restored);
                    bool advanced = KmhEconomyReset.Generation > before;
                    string src = KmhSecurityAuditSelfTest.ReadSource("Main.cs");
                    bool wired = src != null && src.Contains("RestoredThisBoot}\", staleRows: false)");
                    bool ok = payable && advanced && wired;
                    return (ok, $"restoredRowPayable={payable} generationAdvanced={advanced} bootRestorePassesStaleRowsFalse={wired}");
                }
                finally { KmhEconomyReset.ResetForTest(); }
            });

            return r;
        }
    }
}
