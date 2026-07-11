using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Notifications.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Notifications
{
    // Per-user mailbox for notices that fired while the player was offline (auction won/sold/outbid, marketplace
    // sale, etc.). Drained and delivered as in-game letters on next login. Bounded per user; persisted so a server
    // restart doesn't lose a player's missed events. All access under _lock.
    internal static class NotificationStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<NotificationDto>> _byUser
            = new Dictionary<string, List<NotificationDto>>(StringComparer.OrdinalIgnoreCase);

        // Keep the newest N per user so a long-absent player with a busy market can't grow the file unbounded.
        private const int MaxPerUser = 50;

        private sealed class PersistedState
        {
            public Dictionary<string, List<NotificationDto>> Users { get; set; }
                = new Dictionary<string, List<NotificationDto>>(StringComparer.OrdinalIgnoreCase);
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.NotificationsFile, out PersistedState s) || s?.Users == null) return;
            lock (_lock)
            {
                _byUser.Clear();
                foreach (KeyValuePair<string, List<NotificationDto>> kv in s.Users)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null && kv.Value.Count > 0)
                        _byUser[kv.Key] = new List<NotificationDto>(kv.Value);
            }
            Diagnostics.ServerLog.Info($"Notifications: loaded queued notices for {s.Users.Count} user(s)");
        }

        // Season reset: clear all pending mail/notifications.
        public static void ClearForNewSeason()
        {
            lock (_lock) { _byUser.Clear(); }
            SaveToDisk();
        }

        public static int ClearUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int n;
            lock (_lock) { n = _byUser.TryGetValue(user, out var list) ? (list?.Count ?? 0) : 0; _byUser.Remove(user); }
            if (n > 0) SaveToDisk();
            return n;
        }

        public static void SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock)
                foreach (KeyValuePair<string, List<NotificationDto>> kv in _byUser)
                    s.Users[kv.Key] = new List<NotificationDto>(kv.Value);
            JsonFileStore.Save(KmhDataPaths.NotificationsFile, s);
        }

        // Queue a notice for an offline user. Newest-wins past the cap.
        public static void Enqueue(string user, string tone, string title, string body)
        {
            if (string.IsNullOrEmpty(user)) return;
            lock (_lock)
            {
                if (!_byUser.TryGetValue(user, out List<NotificationDto> list))
                {
                    list = new List<NotificationDto>();
                    _byUser[user] = list;
                }
                list.Add(new NotificationDto
                {
                    Tone     = string.IsNullOrEmpty(tone) ? "neutral" : tone,
                    Title    = title ?? "",
                    Body     = body ?? "",
                    UtcTicks = DateTime.UtcNow.Ticks,
                });
                if (list.Count > MaxPerUser) list.RemoveRange(0, list.Count - MaxPerUser);
            }
            SaveToDisk();
        }

        // Return + clear a user's queued notices (delivered on login).
        // Read-only copy of a user's queued mail (does NOT drain) - for snapshots.
        public static List<NotificationDto> PeekForUser(string user)
        {
            List<NotificationDto> outList = new List<NotificationDto>();
            if (string.IsNullOrEmpty(user)) return outList;
            lock (_lock)
                if (_byUser.TryGetValue(user, out List<NotificationDto> list) && list != null)
                    outList.AddRange(list);
            return outList;
        }

        public static List<NotificationDto> Drain(string user)
        {
            if (string.IsNullOrEmpty(user)) return new List<NotificationDto>();
            List<NotificationDto> outList;
            lock (_lock)
            {
                if (!_byUser.TryGetValue(user, out List<NotificationDto> list) || list.Count == 0)
                    return new List<NotificationDto>();
                outList = new List<NotificationDto>(list);
                _byUser.Remove(user);
            }
            SaveToDisk();
            return outList;
        }

        // Put drained notices back if delivery couldn't be handed to the client (e.g. it dropped mid-login), so the
        // mail survives to the next login instead of being silently lost. Drained notices are older -> front.
        public static void Restore(string user, List<NotificationDto> notices)
        {
            if (string.IsNullOrEmpty(user) || notices == null || notices.Count == 0) return;
            lock (_lock)
            {
                if (!_byUser.TryGetValue(user, out List<NotificationDto> list))
                {
                    list = new List<NotificationDto>();
                    _byUser[user] = list;
                }
                list.InsertRange(0, notices);
                if (list.Count > MaxPerUser) list.RemoveRange(0, list.Count - MaxPerUser);
            }
            SaveToDisk();
        }
    }
}
