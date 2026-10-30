using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global.Treasury
{
    // Every transition is persisted before the thing it authorises; one that cannot be saved does not happen,
    // because a phase living only in memory would let a crash move silver twice.
    internal static class ConnectedTreasuryStore
    {
        private static readonly object _lock = new object();
        private static readonly List<ConnectedOp> _ops = new List<ConnectedOp>();
        private static long _nextSeq = 1;

        // Settled ops are kept so a duplicate request is recognised, then pruned so the file stays bounded.
        private const int MaxSettledRetained = 200;

        private sealed class PersistedState
        {
            public long NextSeq { get; set; } = 1;
            public List<ConnectedOp> Ops { get; set; } = new List<ConnectedOp>();
        }

        // Lets a client discard a late, older snapshot instead of letting the display go backwards.
        private static long _revision;
        public static long Revision { get { lock (_lock) return _revision; } }

        public static int OpenCount { get { lock (_lock) { int n = 0; foreach (ConnectedOp o in _ops) if (o.IsOpen) n++; return n; } } }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.ConnectedTreasuryFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _ops.Clear();
                if (s.Ops != null) foreach (ConnectedOp o in s.Ops) if (o != null) _ops.Add(o);
                _nextSeq = Math.Max(1, s.NextSeq);
                foreach (ConnectedOp o in _ops)
                {
                    long seq = SeqOf(o.Id);
                    if (seq >= _nextSeq) _nextSeq = seq + 1;
                }
                _runToken = NewRunToken();
            }
            int open = OpenCount;
            if (open > 0)
                Diagnostics.ServerLog.Warn(
                    $"Connected treasury: {open} federated silver operation(s) were in flight - they are reconciled with Global, never re-run blind.");
        }

        public static bool SaveToDisk()
        {
            lock (_lock) _revision++;
            lock (_lock)
                return JsonFileStore.Save(KmhDataPaths.ConnectedTreasuryFile,
                                             new PersistedState { NextSeq = _nextSeq, Ops = new List<ConnectedOp>(_ops) });
        }

        private static long SeqOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            int at = id.LastIndexOf(':');
            if (at < 0 || at + 1 >= id.Length) return 0;
            // Ids carry a 't' before the hex sequence; unstripped, nothing parses and the floor never moves.
            string tail = id.Substring(at + 1);
            if (tail.StartsWith("t", StringComparison.Ordinal)) tail = tail.Substring(1);
            long v;
            return long.TryParse(tail, System.Globalization.NumberStyles.HexNumber,
                                 System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Namespaced by identity and per load: a restored backup reuses old sequences, and Global answers a seen id
        // with its old result without comparing the amount.
        private static string _runToken = NewRunToken();
        private static string NewRunToken() => Guid.NewGuid().ToString("N").Substring(0, 8);

        private static string NewIdLocked()
            => KmhServerIdentity.Id + ":" + _runToken + ":t" + (_nextSeq++).ToString("x");

        // Null when unwritten: the caller must refuse, since an op nobody wrote down cannot be reconciled after a crash.
        public static ConnectedOp Open(string player, string userId, string kind, long amount, string grant = "")
            => OpenCore(player, userId, kind, amount, ConnectedOpScope.Personal, null, null, grant);

        // Requires the link id, not the name, so a re-issued federation is never moved under its old authority.
        public static ConnectedOp OpenGuild(string player, string linkId, string guild, string kind, long amount)
        {
            if (string.IsNullOrEmpty(linkId) || string.IsNullOrEmpty(guild)) return null;
            return OpenCore(player, linkId, kind, amount, ConnectedOpScope.Guild, linkId, guild);
        }

        private static ConnectedOp OpenCore(string player, string userId, string kind, long amount, string scope,
                                            string linkId, string guild, string grant = "")
        {
            if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(userId)) return null;
            if (amount <= 0) return null;
            if (string.IsNullOrEmpty(KmhServerIdentity.Id))
            {
                Diagnostics.ServerLog.Error(
                    "Connected treasury: refusing to open an operation before this data set's identity is known.");
                return null;
            }

            ConnectedOp op = new ConnectedOp
            {
                Player = player,
                UserId = userId,
                Scope = scope,
                LinkId = linkId ?? "",
                Guild = guild ?? "",
                Kind = kind,
                Amount = amount,
                Grant = grant ?? "",
                Phase = kind == ConnectedOpKind.Deposit ? ConnectedOpPhase.Opening : ConnectedOpPhase.Reserving,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                UpdatedUtcTicks = DateTime.UtcNow.Ticks,
            };

            lock (_lock) { op.Id = NewIdLocked(); _ops.Add(op); }
            if (SaveToDisk()) return op;

            // The sequence is not stepped back: a gap costs nothing, a reissued id is answered with another op's result.
            lock (_lock) _ops.Remove(op);
            Diagnostics.ServerLog.Error(
                $"Connected treasury: refusing {player} a federated operation that could not be written down - no silver was touched.");
            return null;
        }

        // False means it did not happen: nothing that moves silver may proceed. expectFrom makes it a compare-and-set,
        // so a stale reply (a late reserve answer) cannot take Delivering back to Reserved.
        public static bool Advance(string opId, string phase, string reservationId = null, string deliveryId = null,
                                   string detail = null, string expectFrom = null)
        {
            ConnectedOp op;
            string wasPhase, wasRes, wasDelivery, wasDetail;
            lock (_lock)
            {
                op = FindLocked(opId);
                if (op == null) return false;
                if (expectFrom != null && op.Phase != expectFrom) return false;
                wasPhase = op.Phase; wasRes = op.ReservationId; wasDelivery = op.DeliveryId; wasDetail = op.Detail;
                op.Phase = phase;
                if (reservationId != null) op.ReservationId = reservationId;
                if (deliveryId != null) op.DeliveryId = deliveryId;
                if (detail != null) op.Detail = detail;
                op.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            }

            if (SaveToDisk())
            {
                if (ConnectedOpPhase.IsTerminal(phase)) PruneSettled();
                return true;
            }

            lock (_lock)
            {
                op.Phase = wasPhase; op.ReservationId = wasRes; op.DeliveryId = wasDelivery; op.Detail = wasDetail;
            }
            Diagnostics.ServerLog.Error(
                $"Connected treasury: could not record operation {opId} reaching {phase} - it stays at {wasPhase} and is reconciled later.");
            return false;
        }

        // A failed save is ignored: only the phase governs money, and it is unchanged.
        public static void Note(string opId, string detail)
        {
            lock (_lock)
            {
                ConnectedOp op = FindLocked(opId);
                if (op == null) return;
                op.Detail = detail ?? "";
                op.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            }
            SaveToDisk();
        }

        public static ConnectedOp Find(string opId) { lock (_lock) return FindLocked(opId); }

        // One worker per op: the player's request and the reconciler interleaving could reach Owe twice.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _working =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        public static bool TryClaimWork(string opId) => !string.IsNullOrEmpty(opId) && _working.TryAdd(opId, 0);
        public static void ReleaseWork(string opId) { if (!string.IsNullOrEmpty(opId)) _working.TryRemove(opId, out _); }
        internal static bool IsClaimedForTest(string opId) => _working.ContainsKey(opId ?? "");

        private static ConnectedOp FindLocked(string opId)
        {
            if (string.IsNullOrEmpty(opId)) return null;
            foreach (ConnectedOp o in _ops)
                if (string.Equals(o.Id, opId, StringComparison.Ordinal)) return o;
            return null;
        }

        public static List<ConnectedOp> OpenOps()
        {
            List<ConnectedOp> open = new List<ConnectedOp>();
            lock (_lock) foreach (ConnectedOp o in _ops) if (o.IsOpen) open.Add(o);
            return open;
        }

        public static List<ConnectedOp> For(string player)
        {
            List<ConnectedOp> mine = new List<ConnectedOp>();
            if (string.IsNullOrEmpty(player)) return mine;
            lock (_lock)
                foreach (ConnectedOp o in _ops)
                    if (!o.IsGuild && string.Equals(o.Player, player, StringComparison.OrdinalIgnoreCase)) mine.Add(o);
            return mine;
        }

        // Per guild, not per starter: every officer sees one list and none can open a second op as someone else.
        public static List<ConnectedOp> ForLink(string linkId)
        {
            List<ConnectedOp> theirs = new List<ConnectedOp>();
            if (string.IsNullOrEmpty(linkId)) return theirs;
            lock (_lock)
                foreach (ConnectedOp o in _ops)
                    if (o.IsGuild && string.Equals(o.LinkId, linkId, StringComparison.Ordinal)) theirs.Add(o);
            return theirs;
        }

        // One open op per player: queueing two is a mistake, and the pending amount stays a single answer.
        public static ConnectedOp OpenOpFor(string player)
        {
            if (string.IsNullOrEmpty(player)) return null;
            lock (_lock)
                foreach (ConnectedOp o in _ops)
                    if (o.IsOpen && !o.IsGuild && string.Equals(o.Player, player, StringComparison.OrdinalIgnoreCase))
                        return o;
            return null;
        }

        // Per federation, not per person: two officers acting at once must not open two ops against one vault.
        public static ConnectedOp OpenOpForLink(string linkId)
        {
            if (string.IsNullOrEmpty(linkId)) return null;
            lock (_lock)
                foreach (ConnectedOp o in _ops)
                    if (o.IsOpen && o.IsGuild && string.Equals(o.LinkId, linkId, StringComparison.Ordinal)) return o;
            return null;
        }

        private static void PruneSettled()
        {
            bool changed = false;
            lock (_lock)
            {
                int settled = 0;
                for (int i = _ops.Count - 1; i >= 0; i--) if (!_ops[i].IsOpen) settled++;
                for (int i = 0; i < _ops.Count && settled > MaxSettledRetained; i++)
                    if (!_ops[i].IsOpen) { _ops.RemoveAt(i); i--; settled--; changed = true; }
            }
            if (changed) SaveToDisk();
        }

        internal static void ClearForTest() { lock (_lock) { _ops.Clear(); _nextSeq = 1; _revision = 0; } }
    }
}
