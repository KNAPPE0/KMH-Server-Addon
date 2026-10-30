using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Guilds;

namespace KMHServerAddon.Maintenance
{
    // Any client may request this cross-store aggregate, so it is cached and never holds the guild lock over treasury reads.
    internal static class KmhLeaderboardCostSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Leaderboard: a burst of requests computes once, not once each", () =>
            {
                GuildStore.ResetLeaderboardCacheForTest();
                DateTime t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < 200; i++) GuildStore.ComputeLeaderboard(t);
                int computed = GuildStore.LeaderboardComputations;
                return (computed == 1, computed == 1
                    ? "200 request(s) -> 1 computation"
                    : $"200 REQUESTS CAUSED {computed} COMPUTATIONS");
            });

            Check("Leaderboard: the rows refresh once the cache is old enough", () =>
            {
                GuildStore.ResetLeaderboardCacheForTest();
                DateTime t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                GuildStore.ComputeLeaderboard(t);
                GuildStore.ComputeLeaderboard(t + GuildStore.LeaderboardMaxAge - TimeSpan.FromMilliseconds(1));
                bool held = GuildStore.LeaderboardComputations == 1;
                GuildStore.ComputeLeaderboard(t + GuildStore.LeaderboardMaxAge);
                bool refreshed = GuildStore.LeaderboardComputations == 2;
                return (held && refreshed, held && refreshed
                    ? $"stale after {GuildStore.LeaderboardMaxAge.TotalSeconds}s"
                    : $"held={held} refreshed={refreshed}");
            });

            // Handing the cached list itself out would let one caller's edit reach the next caller's rows.
            Check("Leaderboard: each caller gets its own list", () =>
            {
                GuildStore.ResetLeaderboardCacheForTest();
                DateTime t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var a = GuildStore.ComputeLeaderboard(t);
                var b = GuildStore.ComputeLeaderboard(t);
                bool separate = !ReferenceEquals(a, b);
                a.Add(new GuildStore.GuildSummary { Name = "injected" });
                var c = GuildStore.ComputeLeaderboard(t);
                bool unaffected = c.Count == b.Count;
                return (separate && unaffected, separate && unaffected
                    ? "copies, not the cache" : $"separate={separate} unaffected={unaffected}");
            });

            // Observed from inside the treasury read, so the result does not depend on lucky thread interleaving.
            Check("Leaderboard: the guild lock is not held while treasuries are read", () =>
            {
                GuildStore.ResetLeaderboardCacheForTest();
                // Without a guild there is no treasury read to observe, and the check would pass vacuously.
                string probeGuild = "selftest_lb_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                GuildStore.CreateGuild(probeGuild);

                var reading = new ManualResetEventSlim(false);
                var proceed = new ManualResetEventSlim(false);
                bool freeDuringRead = false;
                int reads = 0;

                Features.Treasury.TreasuryStore.OnGuildSilverReadForTest = () =>
                {
                    if (Interlocked.Increment(ref reads) != 1) return;
                    reading.Set();
                    proceed.Wait(3000);
                };
                try
                {
                    Task reader = Task.Run(() => GuildStore.ComputeLeaderboard(DateTime.UtcNow));
                    if (!reading.Wait(3000))
                    {
                        proceed.Set();
                        reader.Wait(3000);
                        return (false, "NO TREASURY READ HAPPENED - THIS CHECK PROVED NOTHING");
                    }

                    freeDuringRead = GuildStore.GuildLockIsFreeForTest(1000);
                    proceed.Set();
                    reader.Wait(5000);
                }
                finally { Features.Treasury.TreasuryStore.OnGuildSilverReadForTest = null; }

                return (freeDuringRead, freeDuringRead
                    ? "guild lock free while a treasury was being read"
                    : "THE GUILD LOCK WAS HELD ACROSS A TREASURY READ");
            });

            Check("Leaderboard: the request is charged as background traffic, not as a player action", () =>
            {
                var c = SubProtocol.KmhTrafficClass.Of(SubProtocol.KmhProtocol.Kind.GuildLeaderboardRequest);
                bool ok = c == SubProtocol.TrafficClass.Background;
                return (ok, ok ? "Background" : $"charged as {c}");
            });

            return r;
        }
    }
}
