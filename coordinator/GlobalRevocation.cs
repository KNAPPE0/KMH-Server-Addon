using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KmhDiscordCoordinator
{
    public static class GlobalRevocation
    {
        private sealed class Entry
        {
            public bool EverConfirmed;
            public bool Allowed;
            public DateTimeOffset LastGoodAt;
            public DateTimeOffset CheckedAt;
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        }

        private static readonly ConcurrentDictionary<string, Entry> Cache
            = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private static string _baseUrl = "";
        private static TimeSpan _interval = TimeSpan.FromMinutes(5);
        private static TimeSpan _grace = TimeSpan.FromMinutes(30);

        public static void Configure(string globalBaseUrl, int intervalSeconds, int graceSeconds)
        {
            _baseUrl = (globalBaseUrl ?? "").TrimEnd('/');
            if (intervalSeconds > 0) _interval = TimeSpan.FromSeconds(intervalSeconds);
            if (graceSeconds > 0) _grace = TimeSpan.FromSeconds(graceSeconds);
        }

        public static TimeSpan Grace => _grace;

        // A never-confirmed server is checked now, so a Global outage cannot admit one that was never trusted.
        public static async Task<bool> AllowRegistrationAsync(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId)) return false;
            if (_baseUrl.Length == 0) return true;

            Entry e = Cache.GetOrAdd(serverId, _ => new Entry());
            if (e.EverConfirmed) return Allowed(serverId);

            await e.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (e.EverConfirmed) return Allowed(serverId);
                await CheckAsync(serverId, e).ConfigureAwait(false);
                return e.EverConfirmed && e.Allowed;
            }
            finally { e.Gate.Release(); }
        }

        // The grace window bounds how long an outage can keep a stale standing alive.
        public static bool Allowed(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId) || _baseUrl.Length == 0) return true;
            if (!Cache.TryGetValue(serverId, out Entry e) || !e.EverConfirmed) return false;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - e.CheckedAt > _interval) _ = RefreshAsync(serverId, e);
            if (!e.Allowed) return false;
            if (now - e.LastGoodAt > _grace)
            {
                CoordLog.Warn($"{serverId}: Global unreachable for longer than the {_grace.TotalMinutes:N0}-minute "
                            + "authorization lease - dropping its route until Global answers again");
                return false;
            }
            return true;
        }

        private static async Task RefreshAsync(string serverId, Entry e)
        {
            if (!await e.Gate.WaitAsync(0).ConfigureAwait(false)) return;
            try { await CheckAsync(serverId, e).ConfigureAwait(false); }
            finally { e.Gate.Release(); }
        }

        private static async Task CheckAsync(string serverId, Entry e)
        {
            e.CheckedAt = DateTimeOffset.UtcNow;
            try
            {
                using HttpResponseMessage res = await Http
                    .GetAsync($"{_baseUrl}/api/v1/directory/servers/{Uri.EscapeDataString(serverId)}")
                    .ConfigureAwait(false);

                // 404 and 410 are the directory saying this server is gone; any other failure is not evidence.
                if (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.Gone)
                {
                    if (e.Allowed || !e.EverConfirmed) CoordLog.Warn($"Global no longer lists {serverId}");
                    e.EverConfirmed = true;
                    e.Allowed = false;
                    return;
                }
                if (res.IsSuccessStatusCode)
                {
                    if (!e.Allowed && e.EverConfirmed) CoordLog.Info($"Global lists {serverId} again");
                    e.EverConfirmed = true;
                    e.Allowed = true;
                    e.LastGoodAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                CoordLog.Warn($"revocation check for {serverId} did not complete: {ex.GetType().Name}");
            }
        }
    }
}
