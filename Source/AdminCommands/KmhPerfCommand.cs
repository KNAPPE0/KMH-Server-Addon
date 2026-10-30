using System;
using System.Collections.Generic;
using System.Diagnostics;
using KMHServerAddon.Features.Transport;

namespace KMHServerAddon.AdminCommands
{
    internal static class KmhPerfCommand
    {
        public static void Run(Action<string> reply)
        {
            reply("=== Colony Nexus performance ===");

            var t = new Dictionary<string, long>();
            foreach (KeyValuePair<string, long> kv in KmhTransportStats.Snapshot()) t[kv.Key] = kv.Value;
            reply($"Transport: {KmhApiServer.ConnectedCount} connected · {t["connections_accepted"]:N0} accepted since start");
            reply($"  in : {t["frames_in"]:N0} frames, {Kb(t["bytes_in"])}");
            long saved = t["bytes_out_uncompressed"] - t["bytes_out"];
            reply($"  out: {t["frames_out"]:N0} frames in {t["socket_writes"]:N0} writes, {Kb(t["bytes_out"])}"
                + (t["bytes_out_uncompressed"] > 0 && saved > 0
                    ? $" ({Kb(saved)} saved by compression, {t["frames_compressed"]:N0} frames compressed)" : ""));
            reply($"  send queues: peak {Kb(t["send_queue_peak_bytes"])} · slow players dropped {t["slow_peers_dropped"]} · "
                + $"failed writes {t["write_failures"]}");

            reply("Scheduled jobs:");
            foreach (var j in Maintenance.KmhScheduler.Describe())
                reply($"  {j.Name,-22} {j.Runs,6} runs  {j.Failures,3} failed  "
                    + (j.SecondsSinceRun < 0 ? "not run yet" : $"last {j.SecondsSinceRun}s ago")
                    + (string.IsNullOrEmpty(j.LastError) ? "" : $"  ({j.LastError})"));

            Process p = Process.GetCurrentProcess();
            reply($"Process: {p.WorkingSet64 / (1024 * 1024)} MB working set · GC heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB · "
                + $"collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · "
                + $"{p.Threads.Count} threads · {System.Threading.ThreadPool.PendingWorkItemCount} queued work items");
            reply($"Logging: queue {Diagnostics.KmhLogSink.QueueDepth} · dropped {Diagnostics.KmhLogSink.DroppedCount}");
            reply($"Storage: {KMHServerAddon.Persistence.JsonFileStore.DescribeHealth()}");
            PersistenceLines(reply);
        }

        private static void PersistenceLines(Action<string> reply)
        {
            List<KMHServerAddon.Persistence.JsonFileStore.StoreStats> stats = KMHServerAddon.Persistence.JsonFileStore.StatsSnapshot();
            long races = KMHServerAddon.Persistence.JsonFileStore.LiveObjectRaces;
            reply($"Persistence: {stats.Count} file(s) saved since start" + (races > 0 ? $" · {races} live-object race(s) - a store is not snapshotting" : ""));
            double ms = 1000.0 / Stopwatch.Frequency;
            foreach (var s in stats.GetRange(0, Math.Min(12, stats.Count)))
            {
                long n = Math.Max(1, s.Saves);
                reply($"  {s.File,-26} {s.Saves,6} saves  serialize {s.SerializeTicks * ms / n,6:N1} ms avg / {s.MaxSerializeTicks * ms,6:N1} max  "
                    + $"write {s.WriteTicks * ms / n,6:N1} ms avg / {s.MaxWriteTicks * ms,6:N1} max  {Kb(s.Bytes / n)} avg / {Kb(s.MaxBytes)} max"
                    + (s.Coalesced + s.Superseded > 0 ? $"  coalesced {s.Coalesced + s.Superseded}" : "")
                    + (s.Failures > 0 ? $"  FAILED {s.Failures}" : ""));
            }
        }

        private static string Kb(long bytes)
            => bytes >= 10L * 1024 * 1024 ? $"{bytes / (1024 * 1024):N0} MB" : $"{bytes / 1024.0:N1} KB";
    }
}
