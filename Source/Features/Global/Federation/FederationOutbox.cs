using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global.Federation
{
    // Persisted before sending, so an outage or crash only delays; bounded, so a long outage drops the oldest, and
    // every loss is counted by cause: queue limit, Global's refusal (over 7 days, unknown player) or an ended link.
    internal static class FederationOutbox
    {
        public const int MaxEntries = 5000;

        internal sealed class Entry
        {
            [JsonProperty("link_id")]     public string LinkId { get; set; } = "";
            [JsonProperty("report_id")]   public string ReportId { get; set; } = "";
            [JsonProperty("category")]    public string Category { get; set; } = "";
            [JsonProperty("amount")]      public int    Amount { get; set; } = 1;
            [JsonProperty("player")]      public string Player { get; set; } = "";
            [JsonProperty("occurred_at")] public long   OccurredAtUnix { get; set; }
        }

        internal sealed class Leave
        {
            [JsonProperty("link_id")] public string LinkId { get; set; } = "";
            [JsonProperty("reason")]  public string Reason { get; set; } = "";
            [JsonProperty("queued")]  public long   QueuedUnix { get; set; }
        }

        private sealed class PersistedState
        {
            public List<Entry> Entries { get; set; } = new List<Entry>();
            public List<Leave> Leaves { get; set; } = new List<Leave>();
            // guild|player -> UTC day, so a member's daily activity is reported once.
            public Dictionary<string, long> ActivityDays { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
            public long Dropped { get; set; }
            // Sent and refused by Global as too old or unverifiable.
            public long Rejected { get; set; }
            // Thrown away here: the link ended, or Global could not read the batch.
            public long Discarded { get; set; }
        }

        private static readonly object _lock = new object();
        private static PersistedState _state = new PersistedState();

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.FederationOutboxFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                s.Entries ??= new List<Entry>();
                s.Leaves ??= new List<Leave>();
                s.ActivityDays ??= new Dictionary<string, long>(StringComparer.Ordinal);
                _state = s;
            }
        }

        public static bool SaveToDisk()
        {
            lock (_lock) return JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state);
        }

        public static int PendingCount { get { lock (_lock) return _state.Entries.Count; } }
        public static int PendingLeaves { get { lock (_lock) return _state.Leaves.Count; } }
        public static long Dropped { get { lock (_lock) return _state.Dropped; } }
        public static long Rejected { get { lock (_lock) return _state.Rejected; } }
        public static long Discarded { get { lock (_lock) return _state.Discarded; } }
        public static long LostTotal { get { lock (_lock) return _state.Dropped + _state.Rejected + _state.Discarded; } }

        // Past 7 days Global no longer counts a report.
        public static long OldestPendingUnix
        {
            get
            {
                long oldest = 0;
                lock (_lock) foreach (Entry e in _state.Entries) if (e.OccurredAtUnix > 0 && (oldest == 0 || e.OccurredAtUnix < oldest)) oldest = e.OccurredAtUnix;
                return oldest;
            }
        }

        public static void NoteRejected(int n)
        {
            if (n <= 0) return;
            lock (_lock) { _state.Rejected += n; JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state); }
        }

        public static int PendingFor(string linkId)
        {
            int n = 0;
            lock (_lock) foreach (Entry e in _state.Entries) if (e.LinkId == linkId) n++;
            return n;
        }

        // False when it could not be written: an unrecorded report would be sent with no id to retry it by.
        public static bool Add(Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.LinkId) || string.IsNullOrEmpty(e.Category)) return false;
            if (string.IsNullOrEmpty(e.ReportId)) e.ReportId = Guid.NewGuid().ToString("N");
            lock (_lock)
            {
                _state.Entries.Add(e);
                // Oldest go first: Global only accepts the most recent week anyway.
                var trimmed = new List<Entry>();
                while (_state.Entries.Count > MaxEntries) { trimmed.Add(_state.Entries[0]); _state.Entries.RemoveAt(0); _state.Dropped++; }
                if (JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state)) return true;
                // Unwritten, so nothing was dropped either: the trimmed reports go back where they were.
                _state.Entries.Remove(e);
                _state.Entries.InsertRange(0, trimmed);
                _state.Dropped -= trimmed.Count;
                return false;
            }
        }

        public static List<Entry> TakeBatch(string linkId, int max)
        {
            var batch = new List<Entry>();
            lock (_lock)
                foreach (Entry e in _state.Entries)
                {
                    if (e.LinkId != linkId) continue;
                    batch.Add(e);
                    if (batch.Count >= max) break;
                }
            return batch;
        }

        public static List<string> LinksWithEntries()
        {
            var ids = new List<string>();
            lock (_lock)
                foreach (Entry e in _state.Entries)
                    if (!ids.Contains(e.LinkId)) ids.Add(e.LinkId);
            return ids;
        }

        public static void Remove(IEnumerable<Entry> sent, bool unreadable = false)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry e in sent) ids.Add(e.ReportId);
            lock (_lock)
            {
                int n = _state.Entries.RemoveAll(e => ids.Contains(e.ReportId));
                if (unreadable) _state.Discarded += n;
                JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state);
            }
        }

        // Nothing for a link that is no longer a member can ever be counted, so holding it only costs space.
        public static int DropFor(string linkId)
        {
            lock (_lock)
            {
                int n = _state.Entries.RemoveAll(e => e.LinkId == linkId);
                _state.Discarded += n;
                if (n > 0) JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state);
                return n;
            }
        }

        public static bool QueueLeave(string linkId, string reason)
        {
            if (string.IsNullOrEmpty(linkId)) return false;
            lock (_lock)
            {
                if (_state.Leaves.Exists(l => l.LinkId == linkId)) return true;
                Leave l = new Leave { LinkId = linkId, Reason = reason ?? "", QueuedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                _state.Leaves.Add(l);
                if (JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state)) return true;
                _state.Leaves.Remove(l);
                return false;
            }
        }

        public static List<Leave> Leaves() { lock (_lock) return new List<Leave>(_state.Leaves); }

        public static void LeaveDelivered(string linkId)
        {
            lock (_lock)
            {
                if (_state.Leaves.RemoveAll(l => l.LinkId == linkId) > 0)
                    JsonFileStore.Save(KmhDataPaths.FederationOutboxFile, _state);
            }
        }

        public static bool FirstActivityToday(string guild, string player, long day)
        {
            string key = (guild ?? "").ToLowerInvariant() + "|" + (player ?? "");
            lock (_lock)
            {
                if (_state.ActivityDays.TryGetValue(key, out long seen) && seen >= day) return false;
                _state.ActivityDays[key] = day;
                // Yesterday's keys answer nothing today.
                if (_state.ActivityDays.Count > 20000)
                {
                    var stale = new List<string>();
                    foreach (var kv in _state.ActivityDays) if (kv.Value < day) stale.Add(kv.Key);
                    foreach (string k in stale) _state.ActivityDays.Remove(k);
                }
                return true;
            }
        }

        internal static void ClearForTest() { lock (_lock) _state = new PersistedState(); }

        // Bypasses Add, which writes the file every time and makes 5000 adds a slow test.
        internal static void FillForTest(string linkId, int count, string prefix)
        {
            lock (_lock)
                for (int i = 0; i < count; i++)
                    _state.Entries.Add(new Entry { LinkId = linkId, Category = "site", ReportId = prefix + i });
        }
    }
}
