using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Notifications.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Notifications
{
    internal static class NotificationStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<NotificationDto>> _byUser
            = new Dictionary<string, List<NotificationDto>>(StringComparer.OrdinalIgnoreCase);

        // Bounded deliberately: the notice is lost, never the goods, which live in Treasury, Mail and Recovery.
        private const int MaxPerUser = 50;

        private static void TrimLocked(string user, List<NotificationDto> list)
        {
            int over = list.Count - MaxPerUser;
            if (over <= 0) return;
            list.RemoveRange(0, over);
            Diagnostics.ServerLog.Warn($"Notifications: {user} is at the {MaxPerUser}-notice cap - dropped the "
                                     + $"{over} oldest. They are still reachable in-game; only the notice is gone.");
        }

        private sealed class PersistedState
        {
            public Dictionary<string, List<NotificationDto>> Users { get; set; }
                = new Dictionary<string, List<NotificationDto>>(StringComparer.OrdinalIgnoreCase);
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.NotificationsFile, out PersistedState s) || s?.Users == null) return;
            int stamped = 0;
            lock (_lock)
            {
                _byUser.Clear();
                foreach (KeyValuePair<string, List<NotificationDto>> kv in s.Users)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || kv.Value.Count == 0) continue;
                    // Notices from older builds carry no id, and an unacknowledgeable one never leaves the queue.
                    foreach (NotificationDto n in kv.Value)
                        if (n != null && string.IsNullOrEmpty(n.Id)) { n.Id = Guid.NewGuid().ToString("N"); stamped++; }
                    _byUser[kv.Key] = new List<NotificationDto>(kv.Value);
                }
            }
            // Written back immediately, or the same notice gets a different id on the next boot and can never be acked.
            if (stamped > 0 && SaveToDisk())
                Diagnostics.ServerLog.Info($"Notifications: gave {stamped} notice(s) from an older build a stable id");
            Diagnostics.ServerLog.Diag($"Notifications: loaded queued notices for {s.Users.Count} user(s)");
        }

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

        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock)
                foreach (KeyValuePair<string, List<NotificationDto>> kv in _byUser)
                    s.Users[kv.Key] = KmhDeepCopy.OfList(kv.Value);
            return JsonFileStore.Save(KmhDataPaths.NotificationsFile, s);
        }

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
                    // Assigned once, here: an ack and the client's dedupe both name this, so it must survive a reload.
                    Id       = Guid.NewGuid().ToString("N"),
                    Tone     = string.IsNullOrEmpty(tone) ? "neutral" : tone,
                    Title    = title ?? "",
                    Body     = body ?? "",
                    UtcTicks = DateTime.UtcNow.Ticks,
                });
                TrimLocked(user, list);
            }
            SaveToDisk();
        }

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

        // Removes only what the client said it holds: a possible duplicate is the price of never losing one.
        public static int AckDelivered(string user, List<string> ids)
        {
            if (string.IsNullOrEmpty(user) || ids == null || ids.Count == 0) return 0;
            var wanted = new HashSet<string>(ids, System.StringComparer.Ordinal);
            int removed;
            lock (_lock)
            {
                if (!_byUser.TryGetValue(user, out List<NotificationDto> list) || list == null) return 0;
                removed = list.RemoveAll(n => n != null && !string.IsNullOrEmpty(n.Id) && wanted.Contains(n.Id));
                if (list.Count == 0) _byUser.Remove(user);
            }
            if (removed > 0) SaveToDisk();
            return removed;
        }

        // Drained notices are older than anything queued since, so they go back at the front.
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
                TrimLocked(user, list);
            }
            // The drain already took these off disk, so a failed re-queue is a real loss, not a delivery.
            if (!SaveToDisk())
                Diagnostics.ServerLog.Error(
                    $"Notifications: {notices.Count} notice(s) for {user} could not be re-queued to disk after a " +
                    "failed hand-off. They are held in memory only and are lost if the server restarts.");
        }
    }
}
