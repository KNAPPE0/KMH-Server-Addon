using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Features.PlayerStats;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // PlayerStats is display data, so its changes are batched into one write per tick; value stores never are.
    internal static class KmhPersistenceCoalesceSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            List<(string, bool, string)> r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool, string)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw {ex.GetType().Name}: {ex.Message}")); }
            }
            string user = "coalesce-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string stats = KmhDataPaths.PlayerStatsFile;

            Check("coalesce: many stat changes write nothing until the tick, then once", () =>
            {
                PlayerStatsStore.SaveIfDirty();
                long savesBefore = JsonFileStore.StatsFor(stats).Saves, coalescedBefore = JsonFileStore.StatsFor(stats).Coalesced;
                for (int i = 0; i < 10; i++) PlayerStatsStore.AddSilverDonated(user, 7);
                for (int i = 0; i < 5; i++) PlayerStatsStore.RecordSale(user, 1, 100 + i);
                long pending = PlayerStatsStore.PendingWrites.Stats;
                long savesQueued = JsonFileStore.StatsFor(stats).Saves - savesBefore;
                PlayerStatsStore.SaveIfDirty();
                long saves = JsonFileStore.StatsFor(stats).Saves - savesBefore;
                long coalesced = JsonFileStore.StatsFor(stats).Coalesced - coalescedBefore;
                long donated = Donated(user);
                bool ok = savesQueued == 0 && pending >= 15 && saves == 1 && coalesced == pending - 1 && donated == 70
                       && PlayerStatsStore.PendingWrites.Stats == 0;
                return (ok, $"queued writes={savesQueued}, pending={pending}, writes at tick={saves}, coalesced={coalesced}, on disk={donated}");
            });

            Check("coalesce: a quiet tick writes nothing", () =>
            {
                PlayerStatsStore.SaveIfDirty();
                long before = JsonFileStore.StatsFor(stats).Saves;
                PlayerStatsStore.SaveIfDirty();
                long writes = JsonFileStore.StatsFor(stats).Saves - before;
                return (writes == 0, $"writes={writes}");
            });

            Check("coalesce: kmh save and shutdown still write immediately", () =>
            {
                PlayerStatsStore.AddSilverDonated(user, 5);
                PlayerStatsStore.SaveToDisk();
                bool ok = Donated(user) == 75 && PlayerStatsStore.PendingWrites.Stats == 0;
                return (ok, $"on disk={Donated(user)}, pending={PlayerStatsStore.PendingWrites.Stats}");
            });

            // A failed save counted as written lets a season reset drop its journal over a file that never landed.
            Check("force-save: a store whose write fails is reported, never counted as written", () =>
            {
                var errors = new List<string>();
                int ok;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SeasonsFile || p == KmhDataPaths.GuildContributionsFile
                                                         || p == KmhDataPaths.PlayerStatsFile ? "injected: disk full" : null))
                    ok = KmhDataFlush.FlushAll(errors.Add);
                bool named = errors.Exists(e => e.Contains("Seasons")) && errors.Exists(e => e.Contains("GuildContributions"))
                          && errors.Exists(e => e.Contains("PlayerStats"));
                int after = KmhDataFlush.FlushAll(e => errors.Add("after: " + e));
                return (named && after > ok, $"reported=[{string.Join(" | ", errors)}] written={ok} then {after}");
            });

            Check("coalesce: a failed write stays pending and the next tick retries it", () =>
            {
                PlayerStatsStore.AddSilverDonated(user, 1);
                long pendingAfterFail;
                using (JsonFileStore.FailWritesForTest(p => p == stats ? "injected: disk full" : null))
                {
                    PlayerStatsStore.SaveIfDirty();
                    pendingAfterFail = PlayerStatsStore.PendingWrites.Stats;
                }
                PlayerStatsStore.SaveIfDirty();
                bool ok = pendingAfterFail > 0 && Donated(user) == 76 && PlayerStatsStore.PendingWrites.Stats == 0;
                return (ok, $"pending after failure={pendingAfterFail}, on disk after retry={Donated(user)}");
            });

            Check("coalesce: removing a player is written at once", () =>
            {
                PlayerStatsStore.RemoveUser(user);
                bool ok = Donated(user) == -1;
                return (ok, ok ? "gone from disk without waiting for the tick" : $"still on disk with {Donated(user)}");
            });

            PlayerStatsStore.RemoveUser(user);
            return r;
        }

        private static long Donated(string user)
        {
            if (!File.Exists(KmhDataPaths.PlayerStatsFile)) return -1;
            foreach (JToken e in JObject.Parse(File.ReadAllText(KmhDataPaths.PlayerStatsFile))["Entries"] ?? new JArray())
                if (string.Equals((string)e["username"], user, StringComparison.OrdinalIgnoreCase)) return (long?)e["silver_donated"] ?? 0;
            return -1;
        }
    }
}
