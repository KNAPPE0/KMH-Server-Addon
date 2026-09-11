using System;
using System.Collections.Generic;
using KMHServerAddon.Features.World.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.World
{
    // Authoritative World Engine state, persisted to KMH-Data/World/World.json. All access under _lock.
    internal static class WorldStore
    {
        private static readonly object _lock = new object();
        private static readonly List<WorldEventDto> _events = new List<WorldEventDto>();
        private static readonly List<ServerQuestDto> _quests = new List<ServerQuestDto>();
        private static long _nextId = 1;

        // Completed/expired quests linger on the board before pruning.
        private static readonly long EndedQuestGraceTicks = TimeSpan.FromMinutes(30).Ticks;

        private sealed class PersistedState
        {
            public List<WorldEventDto> Events { get; set; } = new List<WorldEventDto>();
            public List<ServerQuestDto> Quests { get; set; } = new List<ServerQuestDto>();
            public long NextId { get; set; } = 1;
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.WorldFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _events.Clear(); _quests.Clear();
                if (s.Events != null) _events.AddRange(s.Events);
                if (s.Quests != null) _quests.AddRange(s.Quests);
                _nextId = Math.Max(1, s.NextId);
            }
            Diagnostics.ServerLog.Info($"World: loaded {s.Events?.Count ?? 0} event(s), {s.Quests?.Count ?? 0} server quest(s)");
        }

        public static void ClearForNewSeason()
        {
            lock (_lock) { _events.Clear(); _quests.Clear(); _nextId = 1; }
            SaveToDisk();
        }

        // Admin-cleanup residual detector: active global quests the user has contributed to (read-only).
        public static int GlobalQuestContributionsOf(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int n = 0;
            lock (_lock)
                foreach (ServerQuestDto q in _quests)
                    if (q?.Contributors != null && q.Contributors.ContainsKey(user)) n++;
            return n;
        }

        public static void SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock)
            {
                s.Events.AddRange(_events);
                s.Quests.AddRange(_quests);
                s.NextId = _nextId;
            }
            JsonFileStore.Save(KmhDataPaths.WorldFile, s);
        }

        // One active event per type; new events replace old ones.
        public static WorldEventDto AddEvent(string type, string title, string description,
                                             double magnitude, string target, int durationMinutes)
        {
            long now = DateTime.UtcNow.Ticks;
            WorldEventDto e;
            lock (_lock)
            {
                _events.RemoveAll(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase));
                e = new WorldEventDto
                {
                    Id = _nextId++,
                    Type = type,
                    Title = title ?? "",
                    Description = description ?? "",
                    Magnitude = magnitude,
                    Target = target ?? "",
                    StartedUtcTicks = now,
                    // A real timestamp rather than 0, because 0 reads as "never expires" and the sweep would skip it.
                    EndsUtcTicks = durationMinutes > 0 ? now + TimeSpan.FromMinutes(durationMinutes).Ticks : now,
                };
                _events.Add(e);
            }
            SaveToDisk();
            return e;
        }

        // Removed outright rather than re-added with a 0 duration, which would read as "never expires".
        public static int RemoveEvent(string type)
        {
            int n;
            lock (_lock) { n = _events.RemoveAll(e => e != null && string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)); }
            if (n > 0) SaveToDisk();
            return n;
        }

        // Drops stranded 0-end rows left in World.json by builds before AddEvent stopped writing that sentinel.
        public static int PurgeEndlessEvents()
        {
            int n;
            lock (_lock) { n = _events.RemoveAll(IsEndless); }
            if (n > 0) SaveToDisk();
            return n;
        }

        // An event with no positive end time can never be swept, so it is stranded rather than merely long-running.
        public static bool IsEndless(WorldEventDto e) => e != null && e.EndsUtcTicks <= 0;

        // Only events that actually ran are returned, because an instantaneous one has no ending to announce.
        public static List<WorldEventDto> CollectEndedEvents(long now)
        {
            List<WorldEventDto> ended = new List<WorldEventDto>();
            bool removedAny = false;
            lock (_lock)
            {
                for (int i = _events.Count - 1; i >= 0; i--)
                {
                    WorldEventDto e = _events[i];
                    if (e.EndsUtcTicks > now) continue;
                    if (e.EndsUtcTicks > e.StartedUtcTicks) ended.Add(e);
                    _events.RemoveAt(i);
                    removedAny = true;
                }
            }
            if (removedAny) SaveToDisk();
            return ended;
        }

        public static List<WorldEventDto> ActiveEvents()
        {
            lock (_lock) { return new List<WorldEventDto>(_events); }
        }

        private static WorldEventDto FindActive(string type, long now)
        {
            lock (_lock)
            {
                foreach (WorldEventDto e in _events)
                    if (string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase) && e.EndsUtcTicks > now)
                        return e;
            }
            return null;
        }

        public static bool IsTaxHoliday()
            => FindActive(WorldEventDto.TaxHoliday, DateTime.UtcNow.Ticks) != null;

        public static double MarketPayoutMultiplier()
        {
            long now = DateTime.UtcNow.Ticks;
            WorldEventDto boom = FindActive(WorldEventDto.MarketBoom, now);
            WorldEventDto crash = FindActive(WorldEventDto.MarketCrash, now);
            double m = 1.0;
            if (boom != null) m *= 1.0 + boom.Magnitude / 100.0;
            if (crash != null) m *= 1.0 - crash.Magnitude / 100.0;
            return m < 0 ? 0 : m;
        }

        public static double MarketPayoutMultiplierFor(string itemDefName)
        {
            double m = MarketPayoutMultiplier();
            WorldEventDto shortage = FindActive(WorldEventDto.ResourceShortage, DateTime.UtcNow.Ticks);
            if (shortage != null
                && (string.IsNullOrEmpty(shortage.Target)
                    || string.Equals(shortage.Target, itemDefName, StringComparison.OrdinalIgnoreCase)))
                m *= 1.0 + shortage.Magnitude / 100.0;
            return m < 0 ? 0 : m;
        }

        public static double WorkerXpMultiplier()
        {
            WorldEventDto e = FindActive(WorldEventDto.DoubleWorkerXp, DateTime.UtcNow.Ticks);
            return e != null && e.Magnitude > 0 ? e.Magnitude / 100.0 : 1.0;
        }

        // Completion result for quest contribution updates.
        public sealed class QuestContribution
        {
            public bool Changed { get; set; }
            public bool Completed { get; set; }
            public ServerQuestDto Quest { get; set; }
            public Dictionary<string, long> Payouts { get; set; }
        }

        // reservedFromPool is the part that came from real house silver, and only that is ever refundable.
        public static ServerQuestDto CreateQuest(string kind, string objective, string targetDef, string title,
                                                 string description, int goalQty, long rewardPool, int durationMinutes,
                                                 long reservedFromPool = 0, string operationType = null,
                                                 string operationSource = null, int targetSiteTile = -1,
                                                 string consequence = null)
        {
            long now = DateTime.UtcNow.Ticks;
            ServerQuestDto q;
            lock (_lock)
            {
                q = new ServerQuestDto
                {
                    Id = _nextId++,
                    Kind = kind ?? ServerQuestDto.KindCooperative,
                    Objective = objective ?? ServerQuestDto.ObjHunt,
                    Title = title ?? "",
                    Description = description ?? "",
                    TargetDefName = targetDef ?? "",
                    GoalQty = Math.Max(1, goalQty),
                    ProgressQty = 0,
                    RewardPool = Math.Max(0, rewardPool),
                    ReservedFromPool = Math.Max(0, Math.Min(reservedFromPool, Math.Max(0, rewardPool))),
                    State = ServerQuestDto.StateActive,
                    Winner = "",
                    EndsUtcTicks = durationMinutes > 0 ? now + TimeSpan.FromMinutes(durationMinutes).Ticks : 0,
                    OperationType = operationType ?? ServerQuestDto.OpNone,
                    OperationSource = operationSource ?? "",
                    TargetSiteTile = targetSiteTile,
                    Consequence = consequence ?? ServerQuestDto.ConsequenceNone,
                };
                _quests.Add(q);
            }
            SaveToDisk();
            return q;
        }

        // Contributions for one operation, copied out so the caller never holds this lock while taking its own.
        public static bool TryGetContributions(long questId, out Dictionary<string, int> qty,
                                               out Dictionary<string, long> firstUtc)
        {
            qty = null; firstUtc = null;
            lock (_lock)
                foreach (ServerQuestDto q in _quests)
                    if (q.Id == questId)
                    {
                        qty = new Dictionary<string, int>(q.Contributors, StringComparer.OrdinalIgnoreCase);
                        firstUtc = new Dictionary<string, long>(q.ContributorFirstUtc, StringComparer.OrdinalIgnoreCase);
                        return true;
                    }
            return false;
        }

        // The live record: never enumerate its contributor dictionaries outside this lock, because a delivery mutates them.
        public static ServerQuestDto FindQuest(long questId)
        {
            lock (_lock)
            {
                foreach (ServerQuestDto q in _quests) if (q.Id == questId) return q;
                return null;
            }
        }

        public static List<ServerQuestDto> ActiveQuests()
        {
            lock (_lock)
            {
                List<ServerQuestDto> list = new List<ServerQuestDto>();
                foreach (ServerQuestDto q in _quests) if (IsActive(q)) list.Add(q);
                return list;
            }
        }

        // Live quests carrying a Frontier consequence - what the director's reference list is reconciled against.
        public static List<long> ActiveOperationIds()
        {
            var outp = new List<long>();
            lock (_lock)
                foreach (ServerQuestDto x in _quests)
                    if (x != null && IsActive(x)
                        && !string.IsNullOrEmpty(x.Consequence)
                        && !string.Equals(x.Consequence, ServerQuestDto.ConsequenceNone, StringComparison.OrdinalIgnoreCase))
                        outp.Add(x.Id);
            return outp;
        }

        // A contested location with no live operation behind it is waiting for something that will never come.
        public static HashSet<int> ActiveOperationTiles()
        {
            var outp = new HashSet<int>();
            lock (_lock)
                foreach (ServerQuestDto x in _quests)
                    if (x != null && IsActive(x) && x.TargetSiteTile >= 0
                        && !string.IsNullOrEmpty(x.Consequence)
                        && !string.Equals(x.Consequence, ServerQuestDto.ConsequenceNone, StringComparison.OrdinalIgnoreCase))
                        outp.Add(x.TargetSiteTile);
            return outp;
        }

        public static bool IsActiveQuest(long questId)
        {
            lock (_lock)
                foreach (ServerQuestDto x in _quests) if (x != null && x.Id == questId) return IsActive(x);
            return false;
        }

        private static bool IsActive(ServerQuestDto q)
            => string.Equals(q.State, ServerQuestDto.StateActive, StringComparison.OrdinalIgnoreCase);

        // Applies a cumulative client tally; max per user keeps it idempotent.
        public static QuestContribution ApplyContribution(long questId, string user, int cumulative)
        {
            QuestContribution r = new QuestContribution();
            if (string.IsNullOrEmpty(user) || cumulative < 0) return r;
            bool save = false;
            lock (_lock)
            {
                ServerQuestDto q = null;
                foreach (ServerQuestDto x in _quests) if (x.Id == questId) { q = x; break; }
                if (q == null || !IsActive(q)) return r;

                // Deliver quests use goods-backed AddDelivery only.
                if (string.Equals(q.Objective, ServerQuestDto.ObjDeliver, StringComparison.OrdinalIgnoreCase)) return r;
                r.Quest = q;

                // Clamp unverifiable client tally to prevent overflow/runaway reward share.
                int capped = Math.Min(cumulative, q.GoalQty);
                q.Contributors.TryGetValue(user, out int prev);
                int val = Math.Max(prev, capped);
                if (val == prev) return r;
                if (!q.ContributorFirstUtc.ContainsKey(user)) q.ContributorFirstUtc[user] = DateTime.UtcNow.Ticks;
                q.Contributors[user] = val;
                r.Changed = true; save = true;

                Dictionary<string, long> payouts = RecomputeAndMaybeComplete(q, user, val);
                if (payouts != null) { r.Completed = true; r.Payouts = payouts; }
            }
            if (save) SaveToDisk();
            return r;
        }

        public sealed class DeliveryResult
        {
            public enum Outcome { Applied, Dropped }
            public Outcome State { get; set; } = Outcome.Dropped;
            public bool Completed { get; set; }
            public int Amount { get; set; }
            // Goods handed over that the objective had no room for. The caller owns giving them back.
            public int Surplus { get; set; }
            public ServerQuestDto Quest { get; set; }
            public Dictionary<string, long> Payouts { get; set; }
        }

        // Read-only, so the caller withdraws exactly this much from the treasury BEFORE asking for credit - progress must be backed by owned goods.
        public static int DeliverableRoom(long questId, string user, string itemDef)
        {
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(itemDef)) return 0;
            lock (_lock)
            {
                ServerQuestDto q = null;
                foreach (ServerQuestDto x in _quests) if (x.Id == questId) { q = x; break; }
                if (q == null || !IsActive(q)) return 0;
                if (!string.Equals(q.Objective, ServerQuestDto.ObjDeliver, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(q.TargetDefName, itemDef, StringComparison.OrdinalIgnoreCase)) return 0;
                q.Contributors.TryGetValue(user, out int prev);
                return Math.Max(0, RoomLeftFor(q, prev));
            }
        }

        // Additive delivery credit for active matching deliver quests only.
        public static DeliveryResult AddDelivery(long questId, string user, string itemDef, int qty)
        {
            DeliveryResult r = new DeliveryResult();
            if (string.IsNullOrEmpty(user) || qty <= 0) return r;
            bool save = false;
            lock (_lock)
            {
                ServerQuestDto q = null;
                foreach (ServerQuestDto x in _quests) if (x.Id == questId) { q = x; break; }
                if (q == null
                    || !string.Equals(q.Objective, ServerQuestDto.ObjDeliver, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(q.TargetDefName, itemDef, StringComparison.OrdinalIgnoreCase))
                    return r;   // unknown / wrong objective / target mismatch

                r.Quest = q;
                if (!IsActive(q)) return r;   // matched but ended - no credit, no refund

                q.Contributors.TryGetValue(user, out int prev);
                // Room only: over-accepting inflates progress past the goal and buys a Frontier claim outright.
                int amount = Math.Min(qty, Math.Max(0, RoomLeftFor(q, prev)));
                if (amount <= 0) { r.Surplus = qty; return r; }   // nothing left to give: all of it goes back

                r.Amount = amount;
                r.Surplus = qty - amount;
                if (!q.ContributorFirstUtc.ContainsKey(user)) q.ContributorFirstUtc[user] = DateTime.UtcNow.Ticks;
                q.Contributors[user] = (int)Math.Min(int.MaxValue, (long)prev + amount);
                r.State = DeliveryResult.Outcome.Applied;
                save = true;

                Dictionary<string, long> payouts = RecomputeAndMaybeComplete(q, user, q.Contributors[user]);
                if (payouts != null) { r.Completed = true; r.Payouts = payouts; }
            }
            if (save) SaveToDisk();
            return r;
        }

        // Cooperative shares one pool; competitive is a race each player runs to the goal on their own.
        internal static int RoomLeftFor(ServerQuestDto q, int contributorSoFar)
        {
            if (q == null) return 0;
            int goal = Math.Max(1, q.GoalQty);
            bool comp = string.Equals(q.Kind, ServerQuestDto.KindCompetitive, StringComparison.OrdinalIgnoreCase);
            long room = comp ? goal - (long)Math.Max(0, contributorSoFar) : goal - (long)Math.Max(0, q.ProgressQty);
            return room <= 0 ? 0 : (int)Math.Min(int.MaxValue, room);
        }

        // Recomputes progress, completes the quest, and returns payouts when finished.
        private static Dictionary<string, long> RecomputeAndMaybeComplete(ServerQuestDto q, string user, int userTotal)
        {
            bool comp = string.Equals(q.Kind, ServerQuestDto.KindCompetitive, StringComparison.OrdinalIgnoreCase);
            q.ProgressQty = comp ? MaxContribution(q) : SumContributions(q);

            bool done = comp ? userTotal >= q.GoalQty : q.ProgressQty >= q.GoalQty;
            if (!done) return null;

            q.State = ServerQuestDto.StateCompleted;
            q.EndsUtcTicks = DateTime.UtcNow.Ticks;   // prune clock
            if (comp) q.Winner = user;
            return comp ? WinnerTakeAll(q, user) : SplitByShare(q);
        }

        // Cancels an active quest and returns the reserved reward for refund.
        public static long CancelQuest(long questId)
        {
            lock (_lock)
            {
                for (int i = 0; i < _quests.Count; i++)
                {
                    ServerQuestDto q = _quests[i];
                    if (q.Id == questId && IsActive(q))
                    {
                        long refund = q.ReservedFromPool; // only real house silver returns; minted vanishes
                        _quests.RemoveAt(i);
                        SaveToDisk();
                        return refund;
                    }
                }
            }
            return -1;
        }

        // Expires unfinished quests and prunes ended quests after the grace window.
        public static List<ServerQuestDto> CollectEndedQuests(long now)
        {
            List<ServerQuestDto> expired = new List<ServerQuestDto>();
            bool changed = false;
            lock (_lock)
            {
                for (int i = _quests.Count - 1; i >= 0; i--)
                {
                    ServerQuestDto q = _quests[i];
                    if (IsActive(q))
                    {
                        if (q.EndsUtcTicks != 0 && q.EndsUtcTicks <= now)
                        {
                            q.State = ServerQuestDto.StateExpired;
                            q.EndsUtcTicks = now;
                            expired.Add(q);
                            changed = true;
                        }
                    }
                    else if (q.EndsUtcTicks != 0 && now - q.EndsUtcTicks >= EndedQuestGraceTicks)
                    {
                        _quests.RemoveAt(i);
                        changed = true;
                    }
                }
            }
            if (changed) SaveToDisk();
            return expired;
        }

        private static int SumContributions(ServerQuestDto q)
        {
            long s = 0;
            foreach (KeyValuePair<string, int> kv in q.Contributors) s += Math.Max(0, kv.Value);
            return (int)Math.Min(int.MaxValue, s);
        }

        private static int MaxContribution(ServerQuestDto q)
        {
            int m = 0;
            foreach (KeyValuePair<string, int> kv in q.Contributors) if (kv.Value > m) m = kv.Value;
            return m;
        }

        private static Dictionary<string, long> WinnerTakeAll(ServerQuestDto q, string winner)
        {
            Dictionary<string, long> p = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (q.RewardPool > 0) p[winner] = q.RewardPool;
            return p;
        }

        // Splits reward by contribution share; remainder goes to the top contributor.
        private static Dictionary<string, long> SplitByShare(ServerQuestDto q)
        {
            Dictionary<string, long> p = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (q.RewardPool <= 0) return p;
            long total = 0;
            foreach (KeyValuePair<string, int> kv in q.Contributors) total += Math.Max(0, kv.Value);
            if (total <= 0) return p;

            long handed = 0; string top = null; int topVal = -1;
            foreach (KeyValuePair<string, int> kv in q.Contributors)
            {
                int v = Math.Max(0, kv.Value);
                long share = q.RewardPool * v / total;
                if (share > 0) p[kv.Key] = share;
                handed += share;
                if (v > topVal) { topVal = v; top = kv.Key; }
            }
            long remainder = q.RewardPool - handed;
            if (remainder > 0 && top != null)
                p[top] = (p.TryGetValue(top, out long e) ? e : 0) + remainder;
            return p;
        }

        // Copied because serialization runs after this lock releases, and a delivery would mutate a quest mid-send.
        public static WorldSnapshot BuildSnapshot()
        {
            WorldSnapshot snap = new WorldSnapshot();
            lock (_lock)
            {
                snap.Revision = Util.KmhSnapshotRevision.Next();
                foreach (WorldEventDto e in _events) snap.Events.Add(e.ShallowClone());
                foreach (ServerQuestDto q in _quests) snap.ServerQuests.Add(CopyQuest(q));   // active + recently-ended
            }
            return snap;
        }

        internal static ServerQuestDto CopyQuest(ServerQuestDto q)
        {
            if (q == null) return null;
            ServerQuestDto c = q.ShallowClone();
            c.Contributors = q.Contributors == null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(q.Contributors, StringComparer.OrdinalIgnoreCase);
            c.ContributorFirstUtc = q.ContributorFirstUtc == null
                ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(q.ContributorFirstUtc, StringComparer.OrdinalIgnoreCase);
            return c;
        }
    }
}