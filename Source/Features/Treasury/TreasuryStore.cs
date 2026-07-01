using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Treasury
{
    // Authoritative treasury, persisted to KMH-Data/Treasury/Treasury.json. OwnerKey is "_personal:<user_lower>" or
    // "<guild_name>". All access is under _lock (RWT's chat thread + the sweeper hit it concurrently). Deposit amounts
    // are client-claimed - the server can't see caravan inventory, so that trust is inherent to the design.
    internal static class TreasuryStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, TreasurySnapshot> _vaults
            = new Dictionary<string, TreasurySnapshot>(StringComparer.OrdinalIgnoreCase);

        // Same constant the patch mod uses to identify personal vaults.
        public const int MaxTransactionLogEntries = 100;

        public static string PersonalKeyFor(string username)
            => "_personal:" + (username ?? "").ToLowerInvariant();

        // Which treasury a caller's own actions operate on - always their personal vault. Guild vaults are addressed
        // directly by guild name (DepositGuildSilver / WithdrawGuildSilver), not routed through here.
        public static string ResolveOwnerKeyFor(string username)
        {
            return PersonalKeyFor(username);
        }

        // Read-only silver lookup keyed by guild name. Returns 0 when no vault has been created for that guild yet
        // (treasury is created lazily on first deposit / quest-bounty escrow). Used by the Discord guild
        // leaderboard for the "treasury silver" sort
        public static long GetGuildSilver(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            lock (_lock)
            {
                return _vaults.TryGetValue(guildName, out TreasurySnapshot v) ? v.SilverBalance : 0;
            }
        }

        // --- guild-vault mutations ---
        // Guild vaults are keyed by raw guild name (distinct from the "_personal:" namespace) so guild silver can be
        // pooled + spent. The contributor/actor is recorded in the log, but the vault belongs to the guild.

        public static bool DepositGuildSilver(string guildName, int amount, string contributorUsername, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(guildName, isGuildOwned: true);
                v.SilverBalance    += amount;
                v.LifetimeSilverIn += amount;
                RecordTransactionLocked(v, contributorUsername, TreasuryTransaction.KindDeposit, amount, "", note);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        public static bool WithdrawGuildSilver(string guildName, int amount, string actorUsername, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v) || v.SilverBalance < amount) return false;
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, actorUsername, TreasuryTransaction.KindWithdraw, amount, "", note);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        // Get or create the vault. Always returns a non-null snapshot. Internal - handlers should use
        // GetSnapshotFor (which also fills per-caller permission flags)
        private static TreasurySnapshot GetOrCreateLocked(string ownerKey, bool isGuildOwned)
        {
            if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v))
            {
                v = new TreasurySnapshot
                {
                    OwnerKey     = ownerKey,
                    IsGuildOwned = isGuildOwned,
                };
                _vaults[ownerKey] = v;
            }
            return v;
        }

        // Build a per-caller snapshot copy (so mutation by other threads mid-send can't corrupt what we serialize).
        // Sets CanDeposit / CanWithdraw based on who's asking
        public static TreasurySnapshot GetSnapshotFor(string username)
        {
            string ownerKey   = ResolveOwnerKeyFor(username);
            bool   isGuild    = ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false;

            lock (_lock)
            {
                TreasurySnapshot live = GetOrCreateLocked(ownerKey, isGuild);

                TreasurySnapshot copy = new TreasurySnapshot
                {
                    OwnerKey           = live.OwnerKey,
                    IsGuildOwned       = live.IsGuildOwned,
                    SilverBalance      = live.SilverBalance,
                    LifetimeSilverIn   = live.LifetimeSilverIn,
                    LifetimeSilverOut  = live.LifetimeSilverOut,
                    Items              = new Dictionary<string, int>(live.Items, StringComparer.OrdinalIgnoreCase),
                    RecentTransactions = new List<TreasuryTransaction>(live.RecentTransactions),
                };

                // Permissions: personal vault owner can always deposit + withdraw. Guild-rank checks land with the
                // Guild handler
                bool isOwner = string.Equals(ownerKey, PersonalKeyFor(username), StringComparison.OrdinalIgnoreCase);
                copy.CanDeposit  = isOwner;
                copy.CanWithdraw = isOwner;

                return copy;
            }
        }

        // --- mutations ---

        public static bool DepositSilver(string username, int amount, string note = "")
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                v.SilverBalance     += amount;
                v.LifetimeSilverIn  += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, amount, "", note);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        public static bool WithdrawSilver(string username, int amount, string note = "")
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (v.SilverBalance < amount) return false;
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, amount, "", note);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        public static bool DepositItem(string username, string itemDefName, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (!v.Items.TryGetValue(itemDefName, out int cur)) cur = 0;
                v.Items[itemDefName] = cur + qty;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, qty, itemDefName, note);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        public static bool WithdrawItem(string username, string itemDefName, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (!v.Items.TryGetValue(itemDefName, out int cur) || cur < qty) return false;
                int next = cur - qty;
                if (next <= 0) v.Items.Remove(itemDefName);
                else           v.Items[itemDefName] = next;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, qty, itemDefName, note);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        // Quality-aware withdraw for quest delivery: consume `qty` of any stack whose def matches and whose quality
        // meets the requirement (any material). Lowest qualifying quality is taken first so claimers keep their
        // best gear. Atomic - either the full qty is taken (consumed lists what, per key) or nothing changes
        public static bool TryWithdrawMatching(string username, string targetDefName, int requiredQualityIndex,
                                               int qty, string note, out List<KeyValuePair<string, int>> consumed)
        {
            consumed = new List<KeyValuePair<string, int>>();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(targetDefName) || qty <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);

                // gather qualifying stacks, lowest quality first
                List<KeyValuePair<string, int>> candidates = new List<KeyValuePair<string, int>>();
                foreach (KeyValuePair<string, int> kv in v.Items)
                {
                    Util.ItemKey.Split(kv.Key, out string def, out _, out int q);
                    if (!string.Equals(def, targetDefName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Util.ItemKey.Meets(q, requiredQualityIndex)) continue;
                    candidates.Add(kv);
                }
                candidates.Sort((a, b) =>
                {
                    Util.ItemKey.Split(a.Key, out _, out _, out int qa);
                    Util.ItemKey.Split(b.Key, out _, out _, out int qb);
                    return qa.CompareTo(qb);
                });

                int total = 0;
                foreach (KeyValuePair<string, int> kv in candidates) total += kv.Value;
                if (total < qty) return false;

                int remaining = qty;
                foreach (KeyValuePair<string, int> kv in candidates)
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(kv.Value, remaining);
                    int next = kv.Value - take;
                    if (next <= 0) v.Items.Remove(kv.Key);
                    else           v.Items[kv.Key] = next;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, take, kv.Key, note);
                    consumed.Add(new KeyValuePair<string, int>(kv.Key, take));
                    remaining -= take;
                }
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        private static void RecordTransactionLocked(
            TreasurySnapshot v, string username, string kind, int amount, string itemDefName, string note)
        {
            v.RecentTransactions.Add(new TreasuryTransaction
            {
                UtcTicks    = DateTime.UtcNow.Ticks,
                Username    = username ?? "",
                Kind        = kind,
                Amount      = amount,
                ItemDefName = itemDefName ?? "",
                Note        = note ?? "",
            });
            while (v.RecentTransactions.Count > MaxTransactionLogEntries)
            {
                v.RecentTransactions.RemoveAt(0);
            }

            // Mirror to the durable, server-wide audit ledger. Enqueue is lock-free, so it's safe under _lock - the
            // actual file write happens off-thread. This one hook captures every economy value movement.
            Persistence.TransactionLedger.Record(v.OwnerKey, username, kind, amount, itemDefName, note);
        }

        // --- persistence ---

        // Admin anti-exploit reset: drop a player's personal vault. Returns the silver it held, or -1 if it had none.
        // Guild vaults are untouched. Caller backs up + logs (see the treasury-reset admin command).
        public static long ResetPersonal(string username)
        {
            string key = PersonalKeyFor(username);
            long had;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(key, out TreasurySnapshot v)) return -1;
                had = v.SilverBalance;
                _vaults.Remove(key);
            }
            SaveToDisk();
            return had;
        }

        // Admin reset: drop every personal vault (guild vaults kept). Returns how many were removed.
        public static int ResetAllPersonal()
        {
            int removed;
            lock (_lock)
            {
                var keys = new List<string>();
                foreach (string k in _vaults.Keys)
                    if (k.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase)) keys.Add(k);
                foreach (string k in keys) _vaults.Remove(k);
                removed = keys.Count;
            }
            if (removed > 0) SaveToDisk();
            return removed;
        }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.TreasuryFile, out PersistedState state) && state?.Vaults != null)
            {
                lock (_lock)
                {
                    _vaults.Clear();
                    foreach (TreasurySnapshot v in state.Vaults)
                    {
                        if (v == null || string.IsNullOrEmpty(v.OwnerKey)) continue;
                        // Snapshot's CanDeposit / CanWithdraw flags are per-caller - strip on load so the canonical
                        // stored state doesn't carry stale permission booleans
                        v.CanDeposit  = false;
                        v.CanWithdraw = false;
                        _vaults[v.OwnerKey] = v;
                    }
                }
                Diagnostics.ServerLog.Info($"Treasury: loaded {state.Vaults.Count} vault(s) from disk");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Vaults = new List<TreasurySnapshot>(_vaults.Values);
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
            }
            JsonFileStore.Save(KmhDataPaths.TreasuryFile, state, seq);
        }

        private class PersistedState
        {
            public List<TreasurySnapshot> Vaults { get; set; } = new List<TreasurySnapshot>();
        }
    }
}
