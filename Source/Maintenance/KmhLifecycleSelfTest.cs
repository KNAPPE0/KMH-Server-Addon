using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Enforcement;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Append durability, profile persistence and ordered shutdown: where the server could say a change landed while the disk did not.
    internal static class KmhLifecycleSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Ledger: an append that fails leaves the entry pending, and the retry writes it once", () =>
            {
                TransactionLedger.Flush();
                if (TransactionLedger.PendingCountForTest != 0) return (false, "ledger was not idle");

                string note = "selftest-append-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                TransactionLedger.Record("_selftest", "selftest", "deposit", 42, "", note);

                int held;
                using (TransactionLedger.FailAppendForTest(() => "injected: disk full"))
                {
                    TransactionLedger.Flush();
                    held = TransactionLedger.PendingCountForTest;
                }
                // Dequeuing straight into the write lost the entry from memory and from the file at once.
                bool kept = held == 1;

                TransactionLedger.Flush();
                bool drained = TransactionLedger.PendingCountForTest == 0;

                int written = 0;
                foreach (string line in TransactionLedger.ReadRecent(200))
                    if (line != null && line.Contains(note)) written++;
                bool exactlyOnce = written == 1;

                bool ok = kept && drained && exactlyOnce;
                return (ok, ok ? "held while the disk refused, then written exactly once"
                              : $"heldPending={held} (expected 1), drained={drained}, durableCopies={written} (expected 1)");
            });

            Check("Enforcement: a profile the disk refused is not the active one", () =>
            {
                string before = EnforcementProfile.Hash;
                byte[] candidate = MinimalZip();

                bool published;
                try
                {
                    EnforcementProfile.FailWriteForTest = () => "injected: disk full";
                    published = EnforcementProfile.SetProfile(candidate, null, DateTime.UtcNow.Ticks);
                }
                finally { EnforcementProfile.FailWriteForTest = null; }

                // Reported as published while the disk keeps the old one means clients pull rules that vanish.
                bool unchanged = EnforcementProfile.Hash == before;
                bool ok = !published && unchanged;
                return (ok, ok ? "refused, previous profile still active"
                              : $"published={published}, hashChanged={!unchanged}");
            });

            Check("Shutdown: mutations stop and the scheduler drains before the final flush", () =>
            {
                bool openBefore = KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                KmhShutdown.Begin();

                bool barred    = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                // Recovery stays privileged, or the very steps that reach a durable state are gated against it.
                bool recoveryOk = KmhAdmission.AllowsValueMutation(KmhIngress.Recovery, out _);
                bool drained   = !KmhScheduler.IsBusy;
                bool idempotent = true;
                KmhShutdown.Begin();   // ProcessExit can fire alongside an explicit stop

                KmhShutdown.ResetForTest();
                bool reopened = KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
                bool ok = openBefore && barred && recoveryOk && drained && idempotent && reopened;
                return (ok, ok ? "ordinary mutation barred, recovery still allowed, scheduler idle at flush time"
                              : $"openBefore={openBefore}, barred={barred}, recoveryOk={recoveryOk}, " +
                                $"schedulerIdle={drained}, reopened={reopened}");
            });

            Check("Video queue: a backlog is bounded rather than growing until the process dies", () =>
            {
                // Concurrency was already capped; the backlog was not, and a queued job is never swept.
                bool bounded = Features.Media.KmhVideoHandler.MaxWaitingJobs > 0
                            && Features.Media.KmhVideoHandler.MaxWaitingJobs <= 256;
                bool idle = Features.Media.KmhVideoHandler.WaitingCountForTest <= Features.Media.KmhVideoHandler.MaxWaitingJobs;
                bool ok = bounded && idle;
                return (ok, ok ? $"waiting queue capped at {Features.Media.KmhVideoHandler.MaxWaitingJobs}"
                              : $"bounded={bounded}, withinCap={idle}");
            });

            return r;
        }

        private static byte[] MinimalZip()
        {
            using var ms = new System.IO.MemoryStream();
            using (var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            using (var w = new System.IO.StreamWriter(za.CreateEntry("selftest.txt").Open()))
                w.Write("selftest");
            return ms.ToArray();
        }
    }
}
