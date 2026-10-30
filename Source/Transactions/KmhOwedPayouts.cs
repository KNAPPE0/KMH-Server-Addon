using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Transactions
{
    // Written in the SAME save as the close, credited on its own marker, then cleared: else a crash between loses it.
    public sealed class KmhOwedPayout
    {
        [JsonProperty("marker")]   public string Marker { get; set; } = "";
        [JsonProperty("user")]     public string User { get; set; } = "";
        // A guild vault credit instead of a player's; paid only to that incarnation.
        [JsonProperty("guild")]    public string Guild { get; set; } = "";
        [JsonProperty("guild_id")] public string GuildIdentity { get; set; } = "";
        // Who is paid instead if that guild is gone - the player a guild tax was taken from. Empty: held for the store.
        [JsonProperty("fallback")] public string Fallback { get; set; } = "";
        [JsonProperty("silver")]   public long Silver { get; set; }
        [JsonProperty("items")]    public Dictionary<string, int> Items { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        [JsonProperty("payloads")] public List<KMHServerAddon.Items.KmhThingPayload> Payloads { get; set; } = new List<KMHServerAddon.Items.KmhThingPayload>();
        [JsonProperty("note")]     public string Note { get; set; } = "";
        // Store-specific: where a live settle may send it first (a colony, a listing); a resumed one always pays the vault.
        [JsonProperty("route")]    public string Route { get; set; } = "";
        [JsonProperty("tile")]     public int Tile { get; set; } = -1;
        [JsonProperty("price")]    public int UnitPrice { get; set; }
        // Road work a site cycle produced: laid once on its marker by the roadworks store, never paid to a vault.
        [JsonProperty("work")]     public double Work { get; set; }
        [JsonProperty("created")]  public long CreatedUtcTicks { get; set; }

        public bool HasValue => Silver > 0 || (Items != null && Items.Count > 0) || (Payloads != null && Payloads.Count > 0) || Work > 0;
    }

    internal enum KmhPayResult { Paid, Refused, Failed }

    // One per store: guarded by that store's lock and saved in that store's file, so a record and its close share a write.
    internal sealed class KmhOwedPayouts
    {
        private readonly Dictionary<string, KmhOwedPayout> _owed = new Dictionary<string, KmhOwedPayout>(StringComparer.Ordinal);
        private readonly string _store;
        private readonly object _lock;
        private readonly Func<bool> _save;

        public KmhOwedPayouts(string store, object storeLock, Func<bool> save)
        {
            _store = store;
            _lock = storeLock ?? throw new ArgumentNullException(nameof(storeLock));
            _save = save ?? throw new ArgumentNullException(nameof(save));
        }

        // Fired after each step a restart could stop between, so a test can cut the power anywhere.
        internal Action<string> AtPhaseForTest;

        public int Count { get { lock (_lock) return _owed.Count; } }

        // Caller holds the store lock and saves next. Null when there is nothing to pay.
        public string RecordLocked(KmhOwedPayout p)
        {
            if (p == null || string.IsNullOrEmpty(p.Marker) || !p.HasValue) return null;
            if (string.IsNullOrEmpty(p.User) && string.IsNullOrEmpty(p.Guild) && p.Work <= 0) return null;
            p.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            _owed[p.Marker] = p;
            return p.Marker;
        }

        public void ForgetLocked(string marker) { if (marker != null) _owed.Remove(marker); }

        public List<KmhOwedPayout> SnapshotLocked() => KmhDeepCopy.OfList(_owed.Values);

        public void LoadLocked(List<KmhOwedPayout> rows)
        {
            _owed.Clear();
            _inFlight.Clear();
            if (rows != null)
                foreach (KmhOwedPayout p in rows)
                    if (p != null && !string.IsNullOrEmpty(p.Marker)) _owed[p.Marker] = p;
        }

        public void ClearLocked() { _owed.Clear(); _inFlight.Clear(); }

        // The caller saves: a reset writes this down as one of its steps.
        public int DropForLocked(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            var gone = new List<string>();
            foreach (KmhOwedPayout p in _owed.Values)
                if (string.Equals(p.User, user, StringComparison.OrdinalIgnoreCase)) gone.Add(p.Marker);
            foreach (string m in gone) _owed.Remove(m);
            return gone.Count;
        }

        // Silver and units counted apart, because a conservation check that adds them cannot see a swap.
        public (long Silver, int Units) TotalsLocked(string user = null)
        {
            long s = 0; int n = 0;
            foreach (KmhOwedPayout p in _owed.Values)
            {
                if (user != null && !string.Equals(p.User, user, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(p.Guild, user, StringComparison.OrdinalIgnoreCase)) continue;
                AddTotals(p, ref s, ref n);
            }
            return (s, n);
        }

        private static void AddTotals(KmhOwedPayout p, ref long silver, ref int units)
        {
            silver += Math.Max(0, p.Silver);
            if (p.Items != null) foreach (int q in p.Items.Values) units += Math.Max(0, q);
            if (p.Payloads != null) foreach (var pl in p.Payloads) units += pl?.StackCount ?? 0;
        }

        // Skips credited records (marker in the vault) whose clearing was cut off; asks the treasury outside the lock.
        public (long Silver, int Units) UnpaidTotals(string user)
        {
            List<KmhOwedPayout> rows;
            lock (_lock) rows = new List<KmhOwedPayout>(_owed.Values);
            long s = 0; int n = 0;
            foreach (KmhOwedPayout p in rows)
            {
                bool mine = string.Equals(p.User, user, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Guild, user, StringComparison.OrdinalIgnoreCase);
                if (!mine) continue;
                bool landed = string.IsNullOrEmpty(p.Guild)
                    ? Features.Treasury.TreasuryStore.KnowsTxnMarker(p.User, p.Marker)
                    : Features.Treasury.TreasuryStore.KnowsGuildTxnMarker(p.Guild, p.Marker);
                if (landed) continue;
                AddTotals(p, ref s, ref n);
            }
            return (s, n);
        }

        // On the marker, so a replay after a crash pays nothing twice. Refused means the guild it was owed to is gone.
        public static KmhPayResult PayToVault(KmhOwedPayout p)
        {
            if (p == null || !p.HasValue) return KmhPayResult.Paid;
            if (!string.IsNullOrEmpty(p.Guild))
            {
                string id = string.IsNullOrEmpty(p.GuildIdentity) ? null : p.GuildIdentity;
                if (id == null || !Features.Guilds.GuildIdentity.IsLive(p.Guild, id))
                {
                    if (string.IsNullOrEmpty(p.Fallback)) return KmhPayResult.Refused;
                    // On the same marker in the fallback's own vault, so a replay pays them once too.
                    return Features.Treasury.TreasuryStore.DepositEscrowOnce(p.Fallback, p.Marker, p.Silver, p.Items, p.Payloads,
                        p.Note + $" - returned, guild '{p.Guild}' is gone") ? KmhPayResult.Paid : KmhPayResult.Failed;
                }
                if (p.Silver > 0 && !Features.Treasury.TreasuryStore.DepositGuildSilverOnce(
                        p.Guild, p.Marker, (int)Math.Min(int.MaxValue, p.Silver), p.Guild, note: p.Note, identity: id))
                    return KmhPayResult.Failed;
                if (p.Items != null)
                    foreach (KeyValuePair<string, int> kv in p.Items)
                        if (kv.Value > 0 && !Features.Treasury.TreasuryStore.DepositGuildItemOnce(
                                p.Guild, kv.Key, kv.Value, p.Marker + ":" + kv.Key, p.Note, identity: id))
                            return KmhPayResult.Failed;
                return KmhPayResult.Paid;
            }
            return Features.Treasury.TreasuryStore.DepositEscrowOnce(p.User, p.Marker, p.Silver, p.Items, p.Payloads, p.Note)
                ? KmhPayResult.Paid : KmhPayResult.Failed;
        }

        // Records being paid right now. In memory only: a crash empties it, and boot resumes whatever is still owed.
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);

        // One worker pays a record at a time - a live settle racing the sweep's retry could otherwise credit a guild
        // and then apply the refused-guild fallback to the same record. Null: absent, or another worker holds it.
        private KmhOwedPayout ClaimLocked(string marker)
        {
            if (marker == null || !_owed.TryGetValue(marker, out KmhOwedPayout p) || !_inFlight.Add(marker)) return null;
            return p;
        }

        // Pay outside the store lock, then clear (or hand a refused one to the store) under it. Null: not this worker's.
        private KmhPayResult? PayClaimed(KmhOwedPayout p, Func<KmhOwedPayout, KmhPayResult> pay, Func<KmhOwedPayout, bool> onRefusedLocked)
        {
            KmhPayResult res;
            try { res = (pay ?? PayToVault)(p); }
            catch { lock (_lock) _inFlight.Remove(p.Marker); throw; }
            if (res == KmhPayResult.Failed) { lock (_lock) _inFlight.Remove(p.Marker); return res; }
            try { AtPhaseForTest?.Invoke("credited"); }
            catch { lock (_lock) _inFlight.Remove(p.Marker); throw; }
            lock (_lock)
            {
                _inFlight.Remove(p.Marker);
                if (!_owed.ContainsKey(p.Marker)) return null;
                if (res == KmhPayResult.Refused && (onRefusedLocked == null || !onRefusedLocked(p)))
                {
                    Diagnostics.ServerLog.Warn($"{_store}: {p.Note} is owed to {Who(p)}, which no longer exists - kept as {p.Marker} for an admin.");
                    return res;
                }
                _owed.Remove(p.Marker);
            }
            return res;
        }

        // A failed credit keeps the record: value an operator is told about beats value nobody can find.
        // onRefusedLocked runs under the store lock, so a refused payout is re-homed in the write that clears it.
        public KmhPayResult Settle(string marker, Func<KmhOwedPayout, KmhPayResult> pay = null,
                                   Func<KmhOwedPayout, bool> onRefusedLocked = null)
        {
            if (marker == null) return KmhPayResult.Paid;
            KmhOwedPayout p;
            lock (_lock)
            {
                if (!_owed.ContainsKey(marker)) return KmhPayResult.Paid;
                p = ClaimLocked(marker);
            }
            // The sweep's retry holds it and pays it.
            if (p == null) return KmhPayResult.Failed;
            try { AtPhaseForTest?.Invoke("recorded"); }
            catch { lock (_lock) _inFlight.Remove(marker); throw; }
            KmhPayResult? res = PayClaimed(p, pay, onRefusedLocked);
            if (res == KmhPayResult.Failed)
            {
                Diagnostics.ServerLog.Warn($"{_store}: {p.Note} could not be credited to {Who(p)} - kept as {marker} and retried by the next sweep or boot.");
                return KmhPayResult.Failed;
            }
            if (res == null || res == KmhPayResult.Refused && Contains(marker)) return res ?? KmhPayResult.Paid;
            if (!_save()) Diagnostics.ServerLog.Warn($"{_store}: settled {marker} but could not save that yet - a resumed copy pays nothing twice.");
            AtPhaseForTest?.Invoke("cleared");
            return res.Value;
        }

        public bool Contains(string marker) { lock (_lock) return marker != null && _owed.ContainsKey(marker); }

        // Boot, and the sweep's retry: a record still here was written down and then interrupted, or its credit failed.
        public (int paid, int left) Resume(Func<KmhOwedPayout, KmhPayResult> pay = null, Func<KmhOwedPayout, bool> onRefusedLocked = null, bool quiet = false)
        {
            List<string> markers;
            lock (_lock) markers = new List<string>(_owed.Keys);
            if (markers.Count == 0) return (0, 0);
            int paid = 0; bool changed = false;
            foreach (string m in markers)
            {
                KmhOwedPayout p;
                lock (_lock)
                {
                    p = ClaimLocked(m);
                    if (p == null) continue;
                    // Owed to a save that is being destroyed: dropped with it, never paid into the new one.
                    if (Features.Economy.KmhEconomyReset.Resetting(p.User)) { _inFlight.Remove(m); _owed.Remove(m); changed = true; continue; }
                }
                KmhPayResult? res = PayClaimed(p, pay, onRefusedLocked);
                if (res == KmhPayResult.Paid || res == KmhPayResult.Refused && !Contains(m)) { paid++; changed = true; }
            }
            if (changed) _save();
            int left = Count;
            if (paid > 0) Diagnostics.ServerLog.Warn($"{_store}: resumed {paid} interrupted payout(s).");
            // Boot says it once; the sweep retries silently, or a payout held for an admin would repeat every minute.
            if (left > 0 && !quiet) Diagnostics.ServerLog.Error($"{_store}: {left} payout(s) still cannot be credited - they are kept and retried ('kmh orphans' counts them).");
            return (paid, left);
        }

        private static string Who(KmhOwedPayout p) => string.IsNullOrEmpty(p.Guild) ? p.User : "guild '" + p.Guild + "'";
    }
}
