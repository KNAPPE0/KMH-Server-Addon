using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Recovery.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Recovery
{
    // Parks item/silver that couldn't reach its owner instead of silently destroying it (prevents item loss on
    // invalid/deleted targets). Admins triage with `kmh recover`. Persisted + backed up. All access under _lock.
    internal static class RecoveryStore
    {
        private static readonly object _lock = new object();
        private static readonly List<RecoveryRecord> _records = new List<RecoveryRecord>();
        private static long _nextId = 1;

        private sealed class PersistedState
        {
            public long NextId { get; set; } = 1;
            public List<RecoveryRecord> Records { get; set; } = new List<RecoveryRecord>();
        }

        // Count of still-held records - drives the boot banner + audit line.
        public static int HeldCount { get { lock (_lock) { int n = 0; foreach (RecoveryRecord r in _records) if (IsHeld(r)) n++; return n; } } }

        private static bool IsHeld(RecoveryRecord r) => r != null && string.Equals(r.Status, "held", StringComparison.OrdinalIgnoreCase);

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.RecoveryFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _records.Clear();
                if (s.Records != null) _records.AddRange(s.Records);
                _nextId = Math.Max(1, s.NextId);
                foreach (RecoveryRecord r in _records) if (r != null && r.Id >= _nextId) _nextId = r.Id + 1;
            }
            int held = HeldCount;
            if (held > 0) Diagnostics.ServerLog.Warn($"Recovery: loaded {held} held item/silver record(s) awaiting admin triage ('kmh recover list').");
        }

        public static void SaveToDisk()
        {
            PersistedState s = new PersistedState();
            lock (_lock) { s.NextId = _nextId; s.Records = new List<RecoveryRecord>(_records); }
            JsonFileStore.Save(KmhDataPaths.RecoveryFile, s);
        }

        // Season reset: parked value is season-scoped economy state, so it clears with everything else.
        public static void ClearForNewSeason() { lock (_lock) { _records.Clear(); _nextId = 1; } SaveToDisk(); }

        public static int ClearUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int n;
            lock (_lock) n = _records.RemoveAll(r => r != null && string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase));
            if (n > 0) SaveToDisk();
            return n;
        }

        // --- intake (called by delivery-failure paths) ---

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

        private static void Commit(RecoveryRecord r, string logLine)
        {
            SaveToDisk();
            Diagnostics.ServerLog.Warn($"Recovery #{r.Id}: {logLine}. Triage with 'kmh recover retry|refund|drop {r.Id}'.");
        }

        // --- admin triage ---

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

        private static RecoveryRecord Find(long id)
        {
            lock (_lock) { foreach (RecoveryRecord r in _records) if (r.Id == id) return r; }
            return null;
        }

        // Re-attempt delivery to the ORIGINAL intended owner (reproduces the source semantics exactly).
        public static bool Retry(long id, out string message)
        {
            RecoveryRecord r = Find(id);
            if (r == null || !IsHeld(r)) { message = $"no held record #{id}"; return false; }
            if (string.IsNullOrEmpty(r.User)) { message = $"#{id} has no owner - use 'kmh recover drop {id} <player>'"; return false; }
            bool ok = Deliver(r, r.User);
            message = ok ? $"#{id} delivered to {r.User}" : $"#{id} still can't reach {r.User} (target invalid) - it stays held";
            if (ok) Resolve(r, $"retried to {r.User}");
            return ok;
        }

        // Convert a held item to its trusted silver value (or move held silver) into the owner's treasury.
        public static bool RefundAsSilver(long id, out string message)
        {
            RecoveryRecord r = Find(id);
            if (r == null || !IsHeld(r)) { message = $"no held record #{id}"; return false; }
            if (string.IsNullOrEmpty(r.User)) { message = $"#{id} has no owner - use 'kmh recover drop {id} <player>'"; return false; }
            long silver = r.Kind == "silver" ? r.Silver : PayloadSilver(r.Payload);
            if (silver <= 0) { message = $"#{id} has no resolvable silver value; use retry or drop"; return false; }
            if (!Features.Treasury.TreasuryStore.DepositSilver(r.User, (int)Math.Min(int.MaxValue, silver), $"recovery #{id} refund"))
            { message = $"#{id} refund to {r.User} failed (target invalid) - it stays held"; return false; }
            Resolve(r, $"refunded {silver} silver to {r.User}");
            message = $"#{id} refunded {silver} silver to {r.User}";
            return true;
        }

        // Hand the held value to a DIFFERENT player (owner gone / admin decision).
        public static bool DropToPlayer(long id, string player, out string message)
        {
            RecoveryRecord r = Find(id);
            if (r == null || !IsHeld(r)) { message = $"no held record #{id}"; return false; }
            if (string.IsNullOrEmpty(player)) { message = "specify a player"; return false; }
            bool ok = Deliver(r, player);
            message = ok ? $"#{id} delivered to {player}" : $"#{id} could not be delivered to {player}";
            if (ok) Resolve(r, $"dropped to {player}");
            return ok;
        }

        // Deposit a record's value into a target user's treasury (item or silver). Does NOT resolve - caller decides.
        private static bool Deliver(RecoveryRecord r, string target)
        {
            if (r.Kind == "silver") return Features.Treasury.TreasuryStore.DepositSilver(target, (int)Math.Min(int.MaxValue, r.Silver), $"recovery #{r.Id}");
            return r.Payload != null && Features.Treasury.TreasuryStore.DepositPayload(target, r.Payload, $"recovery #{r.Id}");
        }

        private static void Resolve(RecoveryRecord r, string how)
        {
            lock (_lock) { r.Status = "resolved"; r.Resolution = $"{how} @ {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z"; }
            SaveToDisk();
        }

        private static long PayloadSilver(Items.KmhThingPayload p)
        {
            if (p == null) return 0;
            long unit = p.MarketValue > 0 ? p.MarketValue : Items.KmhItemSafety.GetTrustedMarketValue(p.DefName);
            return unit > 0 ? unit * Math.Max(1, p.StackCount) : 0;
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
