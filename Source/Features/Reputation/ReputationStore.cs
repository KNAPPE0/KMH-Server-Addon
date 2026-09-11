using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Reputation
{
    // Score is re-derived from the raw counters, so changing a weight reprices everyone on the next load.
    internal static class ReputationStore
    {
        public const string TierTrusted    = "Trusted";
        public const string TierNeutral    = "Neutral";
        public const string TierUnreliable = "Unreliable";

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _entries
            = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        internal sealed class Entry
        {
            public string Username                 { get; set; } = "";
            public int    Score                    { get; set; } = 0;
            public int    QuestsCompleted          { get; set; } = 0;
            public int    QuestsAbandoned          { get; set; } = 0;
            public int    QuestsRejected           { get; set; } = 0; // claimer's proof declined
            public int    QuestsRejectedAsPoster   { get; set; } = 0;
        }

        public static string TierFor(int score)
        {
            ReputationConfig c = ReputationConfig.Current;
            if (score >= c.TrustedScore)    return TierTrusted;
            if (score <  c.UnreliableBelow) return TierUnreliable;
            return TierNeutral;
        }

        private static Entry GetOrCreateLocked(string username)
        {
            if (!_entries.TryGetValue(username, out Entry e))
            {
                e = new Entry { Username = username };
                _entries[username] = e;
            }
            return e;
        }

        private static void RecomputeLocked(Entry e)
        {
            // Weights are signed, so penalties are added rather than subtracted.
            ReputationConfig c = ReputationConfig.Current;
            e.Score = e.QuestsCompleted        * c.CompletedWeight
                    + e.QuestsRejected         * c.ProofRejectedWeight
                    + e.QuestsAbandoned        * c.AbandonedWeight
                    + e.QuestsRejectedAsPoster * c.RejectedAsPosterWeight;
        }

        public static void EnsurePlayer(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            bool added = false;
            lock (_lock)
            {
                if (!_entries.ContainsKey(username)) { GetOrCreateLocked(username); added = true; }
            }
            if (added) SaveToDisk();
        }

        public static void RecordCompleted(string username)        => Bump(username, e => e.QuestsCompleted        += 1);
        public static void RecordAbandoned(string username)        => Bump(username, e => e.QuestsAbandoned        += 1);
        public static void RecordProofRejected(string username)    => Bump(username, e => e.QuestsRejected         += 1);
        public static void RecordRejectedAsPoster(string username) => Bump(username, e => e.QuestsRejectedAsPoster += 1);

        private static void Bump(string username, Action<Entry> mutate)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            int score; string tier;
            lock (_lock)
            {
                Entry e = GetOrCreateLocked(username);
                mutate(e);
                RecomputeLocked(e);
                score = e.Score;
                tier  = TierFor(e.Score);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseReputationChanged(
                new KMH.Sdk.Server.Events.ReputationChangedEvent { Username = username, Score = score, Tier = tier });
        }

        public static (int score, string tier) Get(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return (0, TierNeutral);
            lock (_lock)
            {
                int score = _entries.TryGetValue(username, out Entry e) ? e.Score : 0;
                return (score, TierFor(score));
            }
        }

        public static Dto.ReputationSnapshot BuildSnapshot()
        {
            Dto.ReputationSnapshot snap = new Dto.ReputationSnapshot();
            lock (_lock)
            {
                foreach (Entry e in _entries.Values)
                    snap.Entries.Add(new Dto.ReputationEntryDto { Username = e.Username, Score = e.Score, Tier = TierFor(e.Score) });
            }
            return snap;
        }

        // Copies, so a caller cannot mutate the stored entries.
        public static List<Entry> SnapshotAll()
        {
            lock (_lock)
            {
                List<Entry> result = new List<Entry>(_entries.Count);
                foreach (Entry e in _entries.Values)
                    result.Add(new Entry
                    {
                        Username = e.Username, Score = e.Score,
                        QuestsCompleted = e.QuestsCompleted, QuestsAbandoned = e.QuestsAbandoned,
                        QuestsRejected = e.QuestsRejected, QuestsRejectedAsPoster = e.QuestsRejectedAsPoster,
                    });
                return result;
            }
        }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.ReputationFile, out PersistedState state) && state?.Entries != null)
            {
                lock (_lock)
                {
                    _entries.Clear();
                    foreach (Entry e in state.Entries)
                    {
                        if (string.IsNullOrEmpty(e?.Username)) continue;
                        RecomputeLocked(e);
                        _entries[e.Username] = e;
                    }
                }
                Diagnostics.ServerLog.Info($"Reputation: loaded {state.Entries.Count} entries from disk");
            }
        }

        public static void ClearForNewSeason()
        {
            lock (_lock) { _entries.Clear(); }
            SaveToDisk();
        }

        public static bool RemoveUser(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            bool removed;
            lock (_lock) removed = _entries.Remove(username);
            if (removed) SaveToDisk();
            return removed;
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock) { state.Entries = new List<Entry>(_entries.Values); }
            JsonFileStore.Save(KmhDataPaths.ReputationFile, state);
        }

        private sealed class PersistedState
        {
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }
    }
}
