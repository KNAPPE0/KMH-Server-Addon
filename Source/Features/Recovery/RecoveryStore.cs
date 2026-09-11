using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Recovery.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Recovery
{
    internal static class RecoveryStore
    {
        private static readonly object _lock = new object();
        private static readonly List<RecoveryRecord> _records = new List<RecoveryRecord>();
        private static long _nextId = 1;

        private const int MaxResolvedRetained = 200;

        private sealed class PersistedState
        {
            public long NextId { get; set; } = 1;
            public List<RecoveryRecord> Records { get; set; } = new List<RecoveryRecord>();
        }

        public static int HeldCount { get { lock (_lock) { int n = 0; foreach (RecoveryRecord r in _records) if (IsHeld(r)) n++; return n; } } }

        private static bool IsHeld(RecoveryRecord r) => RecoveryPrune.IsHeld(r);

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.RecoveryFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _records.Clear();
                if (s.Records != null) _records.AddRange(s.Records);
                _nextId = Math.Max(1, s.NextId);
                foreach (RecoveryRecord r in _records) if (r != null && r.Id >= _nextId) _nextId = r.Id + 1;
                RecoveryPrune.PruneResolved(_records, MaxResolvedRetained);   // shrink a legacy file that predates the cap
            }
            int held = HeldCount;
            if (held > 0) Diagnostics.ServerLog.Warn($"Recovery: loaded {held} held item/silver record(s) awaiting admin triage ('kmh recover list').");

            // Deliberately NOT re-held: only a human can settle whether it arrived, and re-delivering turns one outage into duplicated value.
            List<RecoveryRecord> interrupted = new List<RecoveryRecord>();
            lock (_lock)
                foreach (RecoveryRecord r in _records)
                    if (RecoveryPrune.IsDelivering(r)) interrupted.Add(r);
            foreach (RecoveryRecord r in interrupted)
                Diagnostics.ServerLog.Error($"Recovery #{r.Id}: INTERRUPTED mid-delivery - {Describe(r)}. It may or may " +
                    "not have reached them. Check with the player, then 'kmh recover drop' it or leave it resolved.");
        }

        // False means in-memory only; a triage the disk never took hands the same held value out again after a restart.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock) { s.NextId = _nextId; s.Records = new List<RecoveryRecord>(_records); }
            bool ok = JsonFileStore.Save(KmhDataPaths.RecoveryFile, s);
            // One successful write persists every record, so nothing is memory-only any more.
            if (ok) lock (_lock) _memoryOnly.Clear();
            return ok;
        }

        // Parked value is season-scoped economy state, so it clears with everything else.
        public static void ClearForNewSeason() { lock (_lock) { _records.Clear(); _nextId = 1; } SaveToDisk(); }

        public static int ClearUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int n;
            lock (_lock) n = _records.RemoveAll(r => r != null && string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase));
            if (n > 0) SaveToDisk();
            return n;
        }

        public static long HoldItem(string user, Items.KmhThingPayload payload, string source, string reason)
        {
            if (payload == null) return 0;
            RecoveryRecord r = NewRecord(user, source, reason);
            r.Kind = "item"; r.Payload = payload;
            Commit(r, $"item held: {Items.KmhItemSafety.DescribeStateForLedger(payload)} for '{user}' ({source}: {reason})");
            return r.Id;
        }

        public static long HoldSilver(string user, long amount, string source, string reason)
        {
            if (amount <= 0) return 0;
            RecoveryRecord r = NewRecord(user, source, reason);
            r.Kind = "silver"; r.Silver = amount;
            Commit(r, $"{amount} silver held for '{user}' ({source}: {reason})");
            return r.Id;
        }

        private static RecoveryRecord NewRecord(string user, string source, string reason)
        {
            RecoveryRecord r = new RecoveryRecord { User = user ?? "", Source = source ?? "", Reason = reason ?? "", UtcTicks = DateTime.UtcNow.Ticks };
            lock (_lock) { r.Id = _nextId++; _records.Add(r); }
            return r;
        }

        // Holds whose write failed, so a durable hold is distinguishable from one a restart would lose; cleared by any later successful save.
        private static readonly HashSet<long> _memoryOnly = new HashSet<long>();

        public static int MemoryOnlyHoldCount { get { lock (_lock) return _memoryOnly.Count; } }

        // Recovery is the last net under every other store, so a memory-only hold must never be reported as safe.
        private static void Commit(RecoveryRecord r, string logLine)
        {
            if (!SaveToDisk())
            {
                lock (_lock) _memoryOnly.Add(r.Id);
                Diagnostics.ServerLog.Error(
                    $"Recovery #{r.Id}: {logLine}. THE HOLD IS NOT ON DISK - KMH-Data could not be written, so this " +
                    $"value exists only in memory and is lost if the server stops before the retry succeeds. It is " +
                    $"re-saved every minute until KMH-Data is writable; fix free space and permissions now.");
                return;
            }
            // An ownerless record can never resolve itself - retry and refund both target the owner.
            if (string.IsNullOrEmpty(r.User))
            {
                Diagnostics.ServerLog.Error(
                    $"Recovery #{r.Id}: {logLine}. NO OWNER RECORDED - the value is safe but 'retry'/'refund' cannot " +
                    $"succeed. Reassign it with 'kmh recover drop {r.Id} <player>'. A blank owner means the source " +
                    $"row was malformed - check that store for corruption.");
                return;
            }
            Diagnostics.ServerLog.Warn($"Recovery #{r.Id}: {logLine}. Triage with 'kmh recover retry|refund|drop {r.Id}'.");
        }

        public static List<RecoveryRecord> Snapshot(bool includeResolved)
        {
            lock (_lock)
            {
                List<RecoveryRecord> outl = new List<RecoveryRecord>();
                foreach (RecoveryRecord r in _records) if (includeResolved || IsHeld(r)) outl.Add(r);
                return outl;
            }
        }

        public static List<RecoveryRecord> ForUser(string user)
        {
            List<RecoveryRecord> outl = new List<RecoveryRecord>();
            if (string.IsNullOrEmpty(user)) return outl;
            lock (_lock)
                foreach (RecoveryRecord r in _records)
                    if (IsHeld(r) && string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase)) outl.Add(r);
            return outl;
        }

        public static bool Retry(long id, out string message)
        {
            RecoveryRecord r = ClaimHeld(id);   // claimed atomically, or two concurrent triage calls both deliver it
            if (r == null) { message = $"no held record #{id}"; return false; }
            if (string.IsNullOrEmpty(r.User)) { Unclaim(r); message = $"#{id} has no owner - use 'kmh recover drop {id} <player>'"; return false; }
            bool ok = Deliver(r, r.User);
            if (ok) { Resolve(r, $"retried to {r.User}"); message = $"#{id} delivered to {r.User}"; }
            else    { Unclaim(r); message = $"#{id} still can't reach {r.User} (target invalid) - it stays held"; }
            return ok;
        }

        public static bool RefundAsSilver(long id, out string message)
        {
            RecoveryRecord r = ClaimHeld(id);
            if (r == null) { message = $"no held record #{id}"; return false; }
            if (string.IsNullOrEmpty(r.User)) { Unclaim(r); message = $"#{id} has no owner - use 'kmh recover drop {id} <player>'"; return false; }
            string why = "";
            long silver = r.Kind == "silver" ? r.Silver : PayloadSilver(r.Payload, out why);
            if (silver <= 0)
            {
                Unclaim(r);
                message = $"#{id} cannot be refunded as silver: " + (why.Length > 0 ? why : "no resolvable value; use retry or drop");
                return false;
            }
            if (!Features.Treasury.TreasuryStore.DepositSilver(r.User, (int)Math.Min(int.MaxValue, silver), $"recovery #{id} refund"))
            { Unclaim(r); message = $"#{id} refund to {r.User} failed (target invalid) - it stays held"; return false; }
            Resolve(r, $"refunded {silver} silver to {r.User}");
            message = $"#{id} refunded {silver} silver to {r.User}";
            return true;
        }

        public static bool DropToPlayer(long id, string player, out string message)
        {
            if (string.IsNullOrEmpty(player)) { message = "specify a player"; return false; }
            RecoveryRecord r = ClaimHeld(id);
            if (r == null) { message = $"no held record #{id}"; return false; }
            bool ok = Deliver(r, player);
            if (ok) { Resolve(r, $"dropped to {player}"); message = $"#{id} delivered to {player}"; }
            else    { Unclaim(r); message = $"#{id} could not be delivered to {player}"; }
            return ok;
        }

        // The claim reaches disk BEFORE the value moves: an in-memory mark stops concurrent triage but not a restart, which delivered twice.
        private static RecoveryRecord ClaimHeld(long id)
        {
            lock (_lock)
                foreach (RecoveryRecord r in _records)
                    if (r.Id == id)
                    {
                        RecoveryRecord claimed = TryClaimRecord(r);
                        if (claimed == null) return null;
                        if (SaveToDisk()) return claimed;
                        r.Status = RecoveryPrune.StatusHeld;
                        Diagnostics.ServerLog.Warn($"Recovery #{id}: could not record the claim - it stays held and nothing was delivered.");
                        return null;
                    }
            return null;
        }

        // Not atomic on its own - the safety comes from ClaimHeld holding _lock around this call.
        internal static RecoveryRecord TryClaimRecord(RecoveryRecord r)
        {
            if (r == null || !IsHeld(r)) return null;
            r.Status = RecoveryPrune.StatusDelivering;
            return r;
        }

        private static void Unclaim(RecoveryRecord r)
        {
            lock (_lock) { if (r != null) r.Status = RecoveryPrune.StatusHeld; }
            // Put back on disk too, or the record stays "delivering" there and reads as interrupted at the next boot.
            SaveToDisk();
        }

        // Does not resolve the record - the caller decides that once delivery has actually succeeded.
        private static bool Deliver(RecoveryRecord r, string target)
        {
            if (r.Kind == "silver") return Features.Treasury.TreasuryStore.DepositSilver(target, (int)Math.Min(int.MaxValue, r.Silver), $"recovery #{r.Id}");
            return r.Payload != null && Features.Treasury.TreasuryStore.DepositPayload(target, r.Payload, $"recovery #{r.Id}");
        }

        private static void Resolve(RecoveryRecord r, string how)
        {
            lock (_lock)
            {
                r.Status = RecoveryPrune.StatusResolved; r.Resolution = $"{how} @ {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z";
                RecoveryPrune.PruneResolved(_records, MaxResolvedRetained);
            }
            // Already delivered: an unwritten resolution leaves "delivering" on disk for boot triage - neither a silent double nor a loss.
            if (!SaveToDisk())
                Diagnostics.ServerLog.Error($"Recovery #{r.Id}: DELIVERED but the resolution could not be saved. " +
                    "It will be listed as interrupted at the next boot - confirm with the player before re-issuing it.");
        }

        // Owner-pinned value or nothing: payload MarketValue and the catalog's first-seen figure both rode in from a client.
        private static long PayloadSilver(Items.KmhThingPayload p, out string refusal)
        {
            refusal = "";
            if (p == null || string.IsNullOrEmpty(p.DefName)) { refusal = "the record names no item"; return 0; }

            (long value, bool pinned) = Features.ItemLabels.ItemLabelCache.ValueInfo(p.DefName);
            if (!pinned || value <= 0)
            {
                refusal = $"'{p.DefName}' has no server-owned value" +
                          (value > 0 ? " (the catalog figure came from a client)" : "") +
                          $" - set one with 'kmh catalog {p.DefName} <silver>', or use retry/drop";
                return 0;
            }
            return value * Math.Max(1, p.StackCount);
        }

        public static string Describe(RecoveryRecord r)
        {
            if (r == null) return "?";
            string what = r.Kind == "silver" ? $"{r.Silver} silver" : Items.KmhItemSafety.DescribeStateForLedger(r.Payload);
            string age = new DateTime(r.UtcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm") + "Z";
            string owner = string.IsNullOrEmpty(r.User) ? "(orphaned)" : r.User;
            string state = IsHeld(r) ? "HELD" : "resolved";
            return $"#{r.Id} [{state}] {what} -> {owner}  ({r.Source}; {r.Reason}; {age})" + (IsHeld(r) ? "" : $"  [{r.Resolution}]");
        }
    }
}
