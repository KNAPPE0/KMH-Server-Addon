using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Treasury
{
    // Authoritative treasury ledger, persisted to KMH-Data/Treasury/Treasury.json. Keyed by OwnerKey:
    // "_personal:<user_lower>" or "<guild_name>". Everything goes through the lock (RWT's chat thread + the sweeper
    // hit it concurrently). Deposit/withdraw amounts are client-claimed - the server can't see caravan inventory,
    // so that trust is inherent to the design
    internal static class TreasuryStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, TreasurySnapshot> _vaults
            = new Dictionary<string, TreasurySnapshot>(StringComparer.OrdinalIgnoreCase);

        // Same constant the patch mod uses to identify personal vaults.
        public const int MaxTransactionLogEntries = 100;

        public static string PersonalKeyFor(string username)
            => "_personal:" + (username ?? "").ToLowerInvariant();

        // Resolve which treasury a given caller is operating on. v1 always
        // returns the caller's personal vault - guild-vault routing lands
        // when the Guild handler ports (then this checks guild membership and routes accordingly)
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
        //
        // Guild vaults are keyed by raw guild name (distinct from the "_personal:" personal-vault namespace). These
        // let guild silver actually be pooled + spent (perk purchases, guild quests). Funded via /kmh guild
        // deposit; spent via WithdrawGuildSilver. The contributor/actor username is recorded in the vault's
        // transaction log for the audit trail, but the vault itself belongs to the guild

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
        }

        // --- persistence ---

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
            lock (_lock)
            {
                state.Vaults = new List<TreasurySnapshot>(_vaults.Values);
            }
            JsonFileStore.Save(KmhDataPaths.TreasuryFile, state);
        }

        private class PersistedState
        {
            public List<TreasurySnapshot> Vaults { get; set; } = new List<TreasurySnapshot>();
        }
    }
}
