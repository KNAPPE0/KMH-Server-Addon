using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Economy;

namespace KMHServerAddon.Maintenance
{
    // Handlers before their stores, workers past a failed start, stale interactions: each acts on state the server does not have.
    internal static class KmhStartupSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Readiness: player value moves are refused until the stores are loaded", () =>
            {
                KmhReadyState was = KmhReadiness.State;
                try
                {
                    KmhReadiness.ResetForTest(KmhReadyState.Starting);
                    bool startingBlocks = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _)
                                       && !KmhAdmission.AllowsValueMutation(KmhIngress.Discord, out _)
                                       && !KmhAdmission.AllowsValueMutation(KmhIngress.Sdk, out _)
                                       && !KmhAdmission.AllowsValueMutation(KmhIngress.Scheduler, out _);
                    // Repair has to work on a server that failed to start, or the failure cannot be fixed in place.
                    bool repairOk = KmhAdmission.AllowsValueMutation(KmhIngress.Admin, out _)
                                 && KmhAdmission.AllowsValueMutation(KmhIngress.Recovery, out _);

                    KmhReadiness.Failed("selftest: a store failed to load");
                    bool failedBlocks = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                    bool visible = KmhReadiness.Describe().Contains("failed");
                    // A failed start is terminal for this process: Ready must not quietly paper over it.
                    KmhReadiness.Ready();
                    bool staysFailed = !KmhReadiness.IsReady;

                    KmhReadiness.ResetForTest(KmhReadyState.Starting);
                    KmhReadiness.Ready();
                    bool resumes = KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                    bool ok = startingBlocks && repairOk && failedBlocks && visible && staysFailed && resumes;
                    return (ok, ok ? "blocked while starting and after a failure; admin repair open; Ready resumes"
                                  : $"startingBlocks={startingBlocks}, repairOk={repairOk}, failedBlocks={failedBlocks}, " +
                                    $"visible={visible}, staysFailed={staysFailed}, resumesWhenReady={resumes}");
                }
                finally { KmhReadiness.ResetForTest(was); if (was == KmhReadyState.Ready) KmhReadiness.Ready(); }
            });

            Check("Readiness: reporting a failed boot never reaches the host's console API", () =>
            {
                KmhReadyState was = KmhReadiness.State;
                try
                {
                    // The reported failure may BE an incompatible host, so the report must not call into it - a throw here kills the process.
                    KmhReadiness.ResetForTest(KmhReadyState.Starting);
                    KmhReadiness.Failed("selftest: host API mismatch");
                    bool held = KmhReadiness.State == KmhReadyState.Failed
                             && KmhReadiness.Describe().Contains("failed");
                    // Reported once, it must also stay refusing - a Failed boot cannot be talked into Ready.
                    KmhReadiness.Ready();
                    bool staysFailed = !KmhReadiness.IsReady
                                    && !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                    bool ok = held && staysFailed;
                    return (ok, ok ? "the failure is recorded without throwing, and keeps refusing players"
                                  : $"stateHeld={held}, staysFailed={staysFailed}");
                }
                finally { KmhReadiness.ResetForTest(was); if (was == KmhReadyState.Ready) KmhReadiness.Ready(); }
            });

            Check("Stale actions: an interaction from before a data reset is refused, not remapped", () =>
            {
                long before = KmhEconomyReset.Generation;
                bool currentOk = Features.Discord.KmhStaleAction.StillCurrent(before);

                // A season roll restarts listing ids at 1, so the same number now means a different listing.
                KmhEconomyReset.BumpDataGeneration("selftest");
                bool staleRefused = !Features.Discord.KmhStaleAction.StillCurrent(before);
                bool freshOk = Features.Discord.KmhStaleAction.StillCurrent(KmhEconomyReset.Generation);

                bool ok = currentOk && staleRefused && freshOk;
                return (ok, ok ? "a pre-reset interaction is refused; one built after it still works"
                              : $"currentAccepted={currentOk}, staleRefused={staleRefused}, freshAccepted={freshOk}");
            });

            Check("Invalidation: a vault changed with no client in hand still reaches its owner", () =>
            {
                string who = "selftest_inval_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                int pushes = 0;
                Action<KMH.Sdk.Server.Events.TreasuryChangedEvent> spy = e =>
                {
                    if (e != null && !e.IsGuildOwned
                        && string.Equals(Features.Treasury.TreasuryStore.UsernameOfOwnerKey(e.OwnerKey), who,
                                         StringComparison.OrdinalIgnoreCase)) pushes++;
                };
                KmhInvalidation.Wire();
                Extensibility.KmhEventBus.Instance.TreasuryChanged += spy;
                try
                {
                    // Exactly the Discord/SDK/admin shape: the store changes with nobody holding a client.
                    if (!Features.Treasury.TreasuryStore.DepositSilver(who, 40, "selftest external")) return (false, "deposit");
                }
                finally { Extensibility.KmhEventBus.Instance.TreasuryChanged -= spy; }

                bool announced = pushes >= 1;
                bool keyMapsBack = Features.Treasury.TreasuryStore.UsernameOfOwnerKey(
                                       Features.Treasury.TreasuryStore.PersonalKeyFor(who))
                                   .Equals(who, StringComparison.OrdinalIgnoreCase);
                Features.Treasury.TreasuryStore.PurgeOwnerForTest(who);
                bool ok = announced && keyMapsBack;
                return (ok, ok ? "the change was announced and resolves back to the owner it belongs to"
                              : $"announced={announced} ({pushes}), ownerKeyResolves={keyMapsBack}");
            });

            Check("Workers: repeated start leaves one scheduler, and stop drains before the flush", () =>
            {
                int before = KmhScheduler.Describe().Count;
                KmhScheduler.Start();
                KmhScheduler.Start();   // a second Start must not run a second loop over the same jobs
                int after = KmhScheduler.Describe().Count;

                bool drained = KmhScheduler.StopAndDrain(TimeSpan.FromSeconds(5));
                bool idle = !KmhScheduler.IsBusy;
                bool sameJobs = after == before;
                bool ok = sameJobs && drained && idle;
                return (ok, ok ? $"{after} job(s) registered once; the scheduler drained before shutdown flushes"
                              : $"jobsBefore={before}, jobsAfter={after}, drained={drained}, idle={idle}");
            });

            return r;
        }
    }
}
