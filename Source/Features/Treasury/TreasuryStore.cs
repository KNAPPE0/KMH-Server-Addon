using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Treasury
{
    // Deposit amounts are client-claimed, because the server cannot see caravan inventory.
    internal static class TreasuryStore
    {
        private static readonly object _lock = new object();

        // Fires inside the read, not around the call, so a caller holding another store's lock across it is caught.
        internal static Action OnGuildSilverReadForTest;
        private static readonly Dictionary<string, TreasurySnapshot> _vaults
            = new Dictionary<string, TreasurySnapshot>(StringComparer.OrdinalIgnoreCase);

        public const int MaxTransactionLogEntries = 100;

        public const string PersonalPrefix = "_personal:";

        public static string PersonalKeyFor(string username)
            => PersonalPrefix + (username ?? "").ToLowerInvariant();

        // Guild vaults are addressed by guild name in the same key space, so a guild call must never land on one.
        internal static bool IsPersonalKey(string key)
            => !string.IsNullOrEmpty(key) && key.TrimStart().StartsWith(PersonalPrefix, StringComparison.OrdinalIgnoreCase);

        public static string UsernameOfOwnerKey(string ownerKey)
            => string.IsNullOrEmpty(ownerKey) || !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase)
             ? "" : ownerKey.Substring("_personal:".Length);

        // Always the personal vault; guild vaults are addressed directly by name instead.
        public static string ResolveOwnerKeyFor(string username)
        {
            return PersonalKeyFor(username);
        }

        // 0 when the guild has no vault yet, since vaults are created lazily on first deposit.
        public static long GetGuildSilver(string guildName)
        {
            OnGuildSilverReadForTest?.Invoke();
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)) return 0;
            lock (_lock)
            {
                return _vaults.TryGetValue(guildName, out TreasurySnapshot v) ? v.SilverBalance : 0;
            }
        }

        // Saturates rather than wrapping, and the deposit cap is not a backstop here: 0 means unlimited.
        private static int AddSilver(TreasurySnapshot v, long add, string what)
        {
            int result = Util.KmhSafe.AddSaturating(v.SilverBalance, add, out bool clamped);
            if (clamped)
                Diagnostics.ServerLog.Error($"Treasury: {v.OwnerKey} silver hit the {int.MaxValue:N0} ceiling on {what} - "
                    + "the excess could not be stored. Raise the silver sinks or split the vault.");
            return result;
        }

        // A credit past the ceiling is refused whole, so the caller's fallback (Recovery) holds it instead of it being destroyed.
        private static bool PassesCeiling(TreasurySnapshot v, long current, long add, string what)
        {
            if (current + add <= int.MaxValue) return false;
            Diagnostics.ServerLog.Error($"Treasury: {v.OwnerKey} {what} of {add:N0} refused - it would pass the {int.MaxValue:N0} "
                + "ceiling. Nothing was changed.");
            return true;
        }

        // Item counts take client-claimed quantities, so they wrap even more easily than silver.
        private static int AddQty(TreasurySnapshot v, int current, long add, string itemKey, string what)
        {
            int result = Util.KmhSafe.AddSaturating(current, add, out bool clamped);
            if (clamped)
                Diagnostics.ServerLog.Error($"Treasury: {v.OwnerKey} stack of '{itemKey}' hit the {int.MaxValue:N0} "
                    + $"ceiling on {what} - the excess could not be stored.");
            return result;
        }

        // Deserialization yields the ordinal comparer, so keys differing only in case are summed rather than dropped.
        private static Dictionary<string, int> NormalizeItemKeys(Dictionary<string, int> src)
        {
            Dictionary<string, int> items = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return items;
            foreach (KeyValuePair<string, int> kv in src)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                items.TryGetValue(kv.Key, out int cur);
                items[kv.Key] = Util.KmhSafe.AddSaturating(cur, kv.Value, out _);
            }
            return items;
        }

        public static long GetPersonalSilver(string username)
        {
            string key = PersonalKeyFor(username);
            lock (_lock) { return _vaults.TryGetValue(key, out TreasurySnapshot v) ? v.SilverBalance : 0; }
        }

        // Storyteller threat, Standings and `kmh audit-player` all read this one list, so it cannot drift between them.
        internal static System.Collections.Generic.IEnumerable<System.Func<string, long>> OffMapValueSources()
        {
            yield return Roadworks.RoadworksStore.ReservedSilverFor;
            yield return Sites.SiteStore.StoredValueFor;
            yield return Marketplace.MarketplaceStore.EscrowValueFor;
            yield return Auctions.AuctionStore.EscrowValueFor;
            yield return WantBoard.WantStore.EscrowValueFor;
            yield return Quests.QuestStore.EscrowValueFor;
            yield return Mail.MailStore.EscrowValueFor;
            yield return Guilds.GuildStore.VaultShareFor;
            yield return PersonalVaultValue;
        }

        // Confirmed value only, so an unconfirmed pending deposit cannot inflate it.
        public static long PersonalOffMapValue(string username)
        {
            long total = 0;
            // Every source holds its own lock, so none is called while ours is held.
            foreach (System.Func<string, long> src in OffMapValueSources()) total += src(username);
            return total;
        }

        public static long PersonalVaultValue(string username)
        {
            string key = PersonalKeyFor(username);
            lock (_lock) return _vaults.TryGetValue(key, out TreasurySnapshot v) ? VaultValueLocked(v) : 0;
        }

        // A guild vault valued the same way, goods included: what it holds counts toward raids like anything else off-map.
        public static long GuildVaultValue(string guildName)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)) return 0;
            lock (_lock) return _vaults.TryGetValue(guildName, out TreasurySnapshot v) ? VaultValueLocked(v) : 0;
        }

        private static long VaultValueLocked(TreasurySnapshot v)
        {
            long total = v.SilverBalance;
            if (v.Items != null)
                foreach (KeyValuePair<string, int> kv in v.Items)
                    total += Items.KmhItemSafety.GetTrustedMarketValue((kv.Key ?? "").Split('|')[0]) * kv.Value;
            if (v.ItemPayloads != null)
                foreach (Items.KmhThingPayload p in v.ItemPayloads)
                    total += Items.KmhItemSafety.GetTrustedMarketValue(p?.DefName ?? "") * Math.Max(1, p?.StackCount ?? 1);
            return total;
        }

        public static (Dictionary<string, int> Items, List<Items.KmhThingPayload> Payloads) GuildGoodsFor(string guildName)
        {
            var items = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)) return (items, new List<Items.KmhThingPayload>());
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v)) return (items, new List<Items.KmhThingPayload>());
                if (v.Items != null) foreach (KeyValuePair<string, int> kv in v.Items) if (kv.Value > 0) items[kv.Key] = kv.Value;
                return (items, GroupForDisplay(v.ItemPayloads));
            }
        }

        public static int GuildItemCount(string guildName, string itemKey)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(itemKey)) return 0;
            lock (_lock)
                return _vaults.TryGetValue(guildName, out TreasurySnapshot v) && v.Items != null
                       && v.Items.TryGetValue(itemKey, out int n) ? n : 0;
        }

        // A member's take from the guild vault, stamped with its row's marker in the same commit, and only from the
        // incarnation the caller checked: a guild disbanded and re-created meanwhile is somebody else's vault.
        public static bool WithdrawGuildItemForTxn(string guildName, string identity, string itemKey, int qty,
                                                   string marker, string actor, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(identity)
                || string.IsNullOrEmpty(itemKey) || qty <= 0 || string.IsNullOrEmpty(marker)) return false;
            lock (_lock)
            {
                if (!Guilds.GuildIdentity.IsLive(guildName, identity)) return false;
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v)) return false;
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (v.Items == null || !v.Items.TryGetValue(itemKey, out int cur) || cur < qty) return false;
                TreasurySnapshot before = RollbackPointLocked(guildName);
                int next = cur - qty;
                if (next <= 0) v.Items.Remove(itemKey);
                else           v.Items[itemKey] = next;
                RecordTransactionLocked(v, actor, TreasuryTransaction.KindWithdraw, qty, itemKey, note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(guildName, before)) return false;
            }
            RaiseChanged(guildName, note);
            return true;
        }

        // The stacks a take of qty from that displayed row would remove, copied and left in place. The row naming them
        // is written next, and TakeGuildPayloadsForTxn then removes exactly these or nothing.
        public static List<Items.KmhThingPayload> PlanGuildPayloadTake(string guildName, string fingerprint, int qty)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)) return null;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v)) return null;
                List<(Items.KmhThingPayload Stack, int Units)> plan = PlanPayloadTakeLocked(v.ItemPayloads, fingerprint, qty);
                if (plan.Count == 0) return null;
                var outp = new List<Items.KmhThingPayload>(plan.Count);
                foreach ((Items.KmhThingPayload stack, int units) in plan) outp.Add(Clone(stack, units));
                return outp;
            }
        }

        // Refused, taking nothing, when the vault no longer matches the plan the row was written for.
        public static List<Items.KmhThingPayload> TakeGuildPayloadsForTxn(string guildName, string identity, string fingerprint, int qty,
                                                                         IList<Items.KmhThingPayload> planned, string marker,
                                                                         string actor, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(identity)
                || planned == null || planned.Count == 0 || string.IsNullOrEmpty(marker)) return null;
            var granted = new List<Items.KmhThingPayload>();
            lock (_lock)
            {
                if (!Guilds.GuildIdentity.IsLive(guildName, identity)) return null;
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v) || IsAppliedMarkerLocked(v, marker)) return null;
                List<(Items.KmhThingPayload Stack, int Units)> plan = PlanPayloadTakeLocked(v.ItemPayloads, fingerprint, qty);
                if (plan.Count != planned.Count) return null;
                for (int i = 0; i < plan.Count; i++)
                    if (planned[i] == null || plan[i].Units != planned[i].StackCount
                        || !string.Equals(plan[i].Stack.Fingerprint, planned[i].Fingerprint, StringComparison.Ordinal)) return null;
                TreasurySnapshot before = RollbackPointLocked(guildName);
                int taken = 0;
                foreach ((Items.KmhThingPayload stack, int units) in plan)
                {
                    if (units >= stack.StackCount) { granted.Add(stack); v.ItemPayloads.Remove(stack); }
                    else { granted.Add(Clone(stack, units)); stack.StackCount -= units; }
                    taken += units;
                }
                RecordTransactionLocked(v, actor, TreasuryTransaction.KindWithdraw, taken,
                    Items.KmhItemSafety.DescribeStateForLedger(granted[0]), note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(guildName, before)) return null;
            }
            RaiseChanged(guildName, note);
            return granted;
        }

        // Goods a guild withdrawal took and could not hand over, back into the vault they came from: once, on the row's
        // refund marker, and only while that incarnation lives. False when it does not - the caller decides where then.
        public static bool ReturnGuildGoodsOnce(string guildName, string identity, string marker, IDictionary<string, int> items,
                                                IEnumerable<Items.KmhThingPayload> payloads, string note)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(identity)
                || string.IsNullOrEmpty(marker)) return false;
            lock (_lock)
            {
                if (!Guilds.GuildIdentity.IsLive(guildName, identity)) return false;
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = LiveGuildVaultLocked(guildName, identity, note);
                if (v == null) return false;
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (items != null)
                    foreach (KeyValuePair<string, int> kv in items)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                        if (!v.Items.TryGetValue(kv.Key, out int cur)) cur = 0;
                        v.Items[kv.Key] = AddQty(v, cur, kv.Value, kv.Key, "guild withdrawal returned");
                        RecordTransactionLocked(v, guildName, TreasuryTransaction.KindDeposit, kv.Value, kv.Key, note);
                    }
                if (payloads != null)
                    foreach (Items.KmhThingPayload p in payloads)
                    {
                        if (p == null || !Items.KmhItemSafety.ValidatePayload(p)) continue;
                        AddPayloadLocked(v, p);
                        RecordTransactionLocked(v, guildName, TreasuryTransaction.KindDeposit, p.StackCount,
                            Items.KmhItemSafety.DescribeStateForLedger(p), note);
                    }
                AddMarkerLocked(v, marker);
                if (!CommitLocked(guildName, before)) return false;
            }
            RaiseChanged(guildName, note);
            return true;
        }

        public static (long silver, int itemStacks, int pending) PersonalSummary(string username)
        {
            string key = PersonalKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(key, out TreasurySnapshot v)) return (0, 0, 0);
                return (v.SilverBalance, (v.Items?.Count ?? 0) + (v.ItemPayloads?.Count ?? 0), v.PendingDeposits?.Count ?? 0);
            }
        }

        // Putting silver BACK after a failed withdrawal: the vault is already debited and there is no
        // guild-side recovery hold, so a compensation that cannot be written destroys the silver outright.
        public static bool ReturnGuildSilver(string guildName, int amount, string actor, string note)
        {
            if (DepositGuildSilver(guildName, amount, actor, note)) return true;
            // Disbanded meanwhile, its vault already handed out: held for an admin rather than rebuilding the vault.
            if (amount > 0 && !Guilds.GuildIdentity.IsLive(guildName))
                return Recovery.RecoveryStore.HoldSilver(actor, amount, note, $"guild '{guildName}' disbanded before this could be returned to its vault") > 0;
            Diagnostics.ServerLog.Error(
                $"Treasury: {amount} silver could not be returned to guild '{guildName}' after a failed "
                + $"withdrawal by {actor} ({note}). Value moves are refused until persistence recovers.");
            Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(true);
            return false;
        }

        public static bool DepositGuildSilver(string guildName, int amount, string contributorUsername, string note = "", string identity = null)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = LiveGuildVaultLocked(guildName, identity, note);
                if (v == null) return false;
                if (PassesCeiling(v, v.SilverBalance, amount, "guild deposit")) return false;
                v.SilverBalance     = AddSilver(v, amount, "guild deposit");
                v.LifetimeSilverIn += amount;
                RecordTransactionLocked(v, contributorUsername, TreasuryTransaction.KindDeposit, amount, "", note);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        // Marker written in the SAME commit as the balance, so a settlement replayed after a crash cannot pay the guild twice.
        public static bool DepositGuildSilverOnce(string guildName, string marker, int amount,
                                                  string contributorUsername, string note = "", string identity = null)
        {
            if (string.IsNullOrEmpty(marker)) return DepositGuildSilver(guildName, amount, contributorUsername, note, identity);
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = LiveGuildVaultLocked(guildName, identity, note);
                if (v == null) return false;
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (PassesCeiling(v, v.SilverBalance, amount, "guild deposit")) return false;
                v.SilverBalance     = AddSilver(v, amount, "guild deposit");
                v.LifetimeSilverIn += amount;
                RecordTransactionLocked(v, contributorUsername, TreasuryTransaction.KindDeposit, amount, "", note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        public static bool WithdrawGuildSilver(string guildName, int amount, string actorUsername, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v) || v.SilverBalance < amount) return false;
                TreasurySnapshot before = RollbackPointLocked(guildName);
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, actorUsername, TreasuryTransaction.KindWithdraw, amount, "", note);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        // A member's guild withdrawal in ONE commit: the vault's debit and the member's credit land together or not at
        // all, so a crash leaves the silver in the vault or with the member - never with neither.
        public static bool TransferGuildToMember(string guildName, string identity, string member, int amount, string note)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(member) || amount <= 0) return false;
            string toKey = PersonalKeyFor(member);
            lock (_lock)
            {
                if (!Guilds.GuildIdentity.IsLive(guildName, identity)) return false;
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot gv) || gv.SilverBalance < amount) return false;
                TreasurySnapshot before = RollbackPointLocked(guildName);
                var others = new Dictionary<string, TreasurySnapshot> { { toKey, RollbackPointLocked(toKey) } };
                TreasurySnapshot pv = GetOrCreateLocked(toKey, isGuildOwned: false);
                if (PassesCeiling(pv, pv.SilverBalance, amount, "guild withdrawal"))
                {
                    if (others[toKey] == null) _vaults.Remove(toKey);
                    return false;
                }
                gv.SilverBalance     -= amount;
                gv.LifetimeSilverOut += amount;
                RecordTransactionLocked(gv, member, TreasuryTransaction.KindWithdraw, amount, "", note);
                pv.SilverBalance     = AddSilver(pv, amount, "guild withdrawal");
                pv.LifetimeSilverIn += amount;
                RecordTransactionLocked(pv, member, TreasuryTransaction.KindDeposit, amount, "", note);
                if (!CommitLocked(guildName, before, others)) return false;
            }
            RaiseChanged(guildName, note);
            RaiseChanged(toKey, note);
            return true;
        }

        // The marker lands in the same commit as the balance, so an escrow replayed after a crash takes the silver once;
        // with an identity, the debit lands only while that incarnation holds the name, never on whoever took it next.
        public static bool WithdrawGuildSilverOnce(string guildName, string marker, int amount,
                                                   string actorUsername, string note = "", string identity = null)
        {
            if (string.IsNullOrEmpty(marker)) return WithdrawGuildSilver(guildName, amount, actorUsername, note);
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                if (identity != null && !Guilds.GuildIdentity.IsLive(guildName, identity)) return false;
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v)) return false;
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (v.SilverBalance < amount) return false;
                TreasurySnapshot before = RollbackPointLocked(guildName);
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, actorUsername, TreasuryTransaction.KindWithdraw, amount, "", note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        // Like KnowsTxnMarker, valid inside the capped recovery window only, never as permanent history.
        public static bool KnowsGuildTxnMarker(string guildName, string marker)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(marker)) return false;
            lock (_lock)
                return _vaults.TryGetValue(guildName, out TreasurySnapshot v) && IsAppliedMarkerLocked(v, marker);
        }

        // A guild vault is credited only while that guild lives as the incarnation the caller expects. Checked under _lock
        // (GuildIdentity takes none), so a credit racing a disband either lands before the hand-back or is refused.
        private static TreasurySnapshot LiveGuildVaultLocked(string guildName, string identity, string note)
        {
            if (Guilds.GuildIdentity.IsLive(guildName, identity)) return GetOrCreateLocked(guildName, isGuildOwned: true);
            Diagnostics.ServerLog.Warn($"Treasury: refused a credit to guild '{guildName}' ({note}) - "
                + (Guilds.GuildIdentity.IsLive(guildName) ? "that name now belongs to a different guild." : "it no longer exists."));
            return null;
        }

        private static TreasurySnapshot GetOrCreateLocked(string ownerKey, bool isGuildOwned)
        {
            if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v))
            {
                v = new TreasurySnapshot
                {
                    OwnerKey     = ownerKey,
                    IsGuildOwned = isGuildOwned,
                    Items        = NormalizeItemKeys(null),
                };
                _vaults[ownerKey] = v;
            }
            return v;
        }

        // A copy, so another thread mutating mid-send cannot corrupt what is serialized.
        public static TreasurySnapshot GetSnapshotFor(string username)
        {
            string ownerKey   = ResolveOwnerKeyFor(username);
            bool   isGuild    = ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false;

            lock (_lock)
            {
                // Reading must not create a vault, or every lookup of an unknown name would persist an empty row.
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot live))
                    live = new TreasurySnapshot { OwnerKey = ownerKey, IsGuildOwned = isGuild };

                TreasurySnapshot copy = new TreasurySnapshot
                {
                    OwnerKey           = live.OwnerKey,
                    IsGuildOwned       = live.IsGuildOwned,
                    SilverBalance      = live.SilverBalance,
                    LifetimeSilverIn   = live.LifetimeSilverIn,
                    LifetimeSilverOut  = live.LifetimeSilverOut,
                    Items              = new Dictionary<string, int>(live.Items, StringComparer.OrdinalIgnoreCase),
                    RecentTransactions = new List<TreasuryTransaction>(live.RecentTransactions),
                    // Blobs are stripped so the snapshot stays under the frame cap; they ride the grant on withdraw.
                    ItemPayloads       = GroupForDisplay(live.ItemPayloads),
                    PendingDeposits    = StripPendingBlobs(live.PendingDeposits),
                };

                bool isOwner = string.Equals(ownerKey, PersonalKeyFor(username), StringComparison.OrdinalIgnoreCase);
                copy.CanDeposit  = isOwner;
                copy.CanWithdraw = isOwner;
                copy.Revision    = Util.KmhSnapshotRevision.Next();

                return copy;
            }
        }


        public static bool DepositSilver(string username, int amount, string note = "")
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (PassesCeiling(v, v.SilverBalance, amount, "deposit")) return false;
                v.SilverBalance     = AddSilver(v, amount, "deposit");
                v.LifetimeSilverIn  += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, amount, "", note);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // A deposit whose fee goes to the house pool: the net credit and the owed fee are one commit, then the fee is settled.
        public static bool DepositSilverOwingFee(string username, int net, int fee, string note = "")
        {
            if (string.IsNullOrEmpty(username) || net <= 0 || fee < 0) return false;
            string ownerKey = PersonalKeyFor(username);
            string marker = null;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, isGuildOwned: false);
                if (PassesCeiling(v, v.SilverBalance, net, "deposit")) return false;
                v.SilverBalance     = AddSilver(v, net, "deposit");
                v.LifetimeSilverIn += net;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, net, "", note);
                marker = OweFeeLocked(FeeMarkerFor(ownerKey, Guid.NewGuid().ToString("N")), fee, username, $"treasury deposit fee ({username})");
                if (!CommitLocked(ownerKey, before)) { ForgetFeesLocked(new List<string> { marker }); return false; }
            }
            RaiseChanged(ownerKey, note);
            if (marker != null) SettleOwedFees(new[] { marker });
            return true;
        }

        // A fee charged from a player's own vault for the house pool (the access fee): debit and owed fee in one commit.
        public static bool ChargeHouseFee(string username, int fee, string note)
        {
            if (string.IsNullOrEmpty(username) || fee <= 0) return false;
            string ownerKey = PersonalKeyFor(username);
            string marker;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v) || v.SilverBalance < fee) return false;
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                v.SilverBalance     -= fee;
                v.LifetimeSilverOut += fee;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, fee, "", note);
                marker = OweFeeLocked(FeeMarkerFor(ownerKey, Guid.NewGuid().ToString("N")), fee, username, $"{note} ({username})");
                if (!CommitLocked(ownerKey, before)) { ForgetFeesLocked(new List<string> { marker }); return false; }
            }
            RaiseChanged(ownerKey, note);
            SettleOwedFees(new[] { marker });
            return true;
        }

        public static bool WithdrawSilver(string username, int amount, string note = "")
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (v.SilverBalance < amount) return false;
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, amount, "", note);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        public static bool DepositItem(string username, string itemDefName, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            // A composed key is def|stuff|quality; every key is written to Treasury.json on every commit, for every player.
            if (itemDefName.Length > 2 * Util.KmhSafe.MaxDefLength + 8) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (!v.Items.TryGetValue(itemDefName, out int cur))
                {
                    if (v.Items.Count >= MaxItemKeysPerVault) return false;
                    cur = 0;
                }
                if (PassesCeiling(v, cur, qty, $"'{itemDefName}' deposit")) return false;
                v.Items[itemDefName] = AddQty(v, cur, qty, itemDefName, "item deposit");
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, qty, itemDefName, note);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            // Recorded only after the deposit commits, so a failed deposit cannot credit a contribution.
            if (!ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase))
            {
                long contributed = Math.Max(0, Features.ItemLabels.ItemLabelCache.BaseValue(itemDefName)) * qty;
                Guilds.GuildStore.AddItemsContributed(ownerKey, username, qty);
                Guilds.Contributions.KmhGuildContributionLedger.RecordItem(ownerKey, username, contributed, $"{qty}x {itemDefName}");
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // A reward minted on this server, once per marker: a retry after a crash finds the marker and adds nothing.
        public static bool DepositGuildItemOnce(string guildName, string itemDefName, int qty, string marker, string note = "", string identity = null)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(itemDefName)
                || qty <= 0 || string.IsNullOrEmpty(marker)) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = LiveGuildVaultLocked(guildName, identity, note);
                if (v == null) return false;
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (!v.Items.TryGetValue(itemDefName, out int cur))
                {
                    if (v.Items.Count >= MaxItemKeysPerVault) return false;
                    cur = 0;
                }
                if (PassesCeiling(v, cur, qty, $"'{itemDefName}' reward")) return false;
                v.Items[itemDefName] = AddQty(v, cur, qty, itemDefName, "reward");
                RecordTransactionLocked(v, guildName, TreasuryTransaction.KindDeposit, qty, itemDefName, note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
            { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        public static bool HasGuildMarker(string guildName, string marker)
        {
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(marker)) return false;
            lock (_lock)
                return _vaults.TryGetValue(guildName, out TreasurySnapshot v) && v != null && IsAppliedMarkerLocked(v, marker);
        }

        // Records no contribution, or a member could farm standing simply by owning a productive guild site.
        public static bool DepositGuildItem(string guildName, string itemDefName, int qty, string note = "", string identity = null)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = LiveGuildVaultLocked(guildName, identity, note);
                if (v == null) return false;
                if (!v.Items.TryGetValue(itemDefName, out int cur)) cur = 0;
                if (PassesCeiling(v, cur, qty, $"'{itemDefName}' guild income")) return false;
                v.Items[itemDefName] = AddQty(v, cur, qty, itemDefName, "guild income");
                RecordTransactionLocked(v, guildName, TreasuryTransaction.KindDeposit, qty, itemDefName, note);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
            { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        public static bool WithdrawItem(string username, string itemDefName, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (!v.Items.TryGetValue(itemDefName, out int cur) || cur < qty) return false;
                int next = cur - qty;
                if (next <= 0) v.Items.Remove(itemDefName);
                else           v.Items[itemDefName] = next;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, qty, itemDefName, note);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // The marker lands in the same commit as the stack, so recovery never refunds goods still in the vault.
        public static bool WithdrawItemForTxn(string username, string itemDefName, int qty, string marker, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0
                || string.IsNullOrEmpty(marker)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (!v.Items.TryGetValue(itemDefName, out int cur) || cur < qty) return false;
                int next = cur - qty;
                if (next <= 0) v.Items.Remove(itemDefName);
                else           v.Items[itemDefName] = next;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, qty, itemDefName, note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // Payload form; the marker is what lets recovery distinguish "taken, row not written yet" from "never taken".
        public static List<Items.KmhThingPayload> WithdrawPayloadsForTxn(string username, string fingerprint, int qty,
                                                                        string marker, string note = "",
                                                                        string refundMarker = null)
        {
            if (string.IsNullOrEmpty(marker)) return null;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
                if (_vaults.TryGetValue(ownerKey, out TreasurySnapshot known) && IsAppliedMarkerLocked(known, marker))
                    return null;
            return WithdrawPayloadsInternal(username, fingerprint, qty, marker, note, refundMarker);
        }

        // Recovery reads the marker, not the ledger's intent, so a crash before the ledger write cannot mint or lose a refund.
        public static bool WithdrawSilverForTxn(string username, int amount, string marker, string note = "")
        {
            if (string.IsNullOrEmpty(username) || amount <= 0 || string.IsNullOrEmpty(marker)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (v.SilverBalance < amount) return false;
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, amount, "", note);
                AddMarkerLocked(v, marker);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // Markers are capped, so a very old one reads as unknown: valid inside the recovery window only, never as permanent history.
        public static bool KnowsTxnMarker(string username, string marker)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(marker)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
                return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v) && IsAppliedMarkerLocked(v, marker);
        }

        // Dedup marker written in the SAME commit as the balance, so a replayed compensation cannot pay twice or half-apply.
        public static bool DepositEscrowOnce(string username, string txnId, long silver,
                                             IDictionary<string, int> items,
                                             IEnumerable<Items.KmhThingPayload> payloads, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(txnId)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsAppliedMarkerLocked(v, txnId)) return true;

                if (silver > 0)
                {
                    int chunk = (int)Math.Min(silver, int.MaxValue);
                    v.SilverBalance     = AddSilver(v, chunk, "escrow return");
                    v.LifetimeSilverIn += chunk;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, chunk, "", note);
                }
                if (items != null)
                    foreach (KeyValuePair<string, int> kv in items)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                        if (!v.Items.TryGetValue(kv.Key, out int cur)) cur = 0;
                        v.Items[kv.Key] = AddQty(v, cur, kv.Value, kv.Key, "escrow return");
                        RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, kv.Value, kv.Key, note);
                    }
                if (payloads != null)
                    foreach (Items.KmhThingPayload p in payloads)
                    {
                        if (p == null || !Items.KmhItemSafety.ValidatePayload(p)) continue;
                        AddPayloadLocked(v, p);
                        RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, p.StackCount,
                            Items.KmhItemSafety.DescribeStateForLedger(p), note);
                    }

                AddMarkerLocked(v, txnId);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // Merges only where RimWorld itself could; a blob-carrying instance is always kept separate.
        public static bool DepositPayload(string username, Items.KmhThingPayload payload, string note = "")
        {
            if (string.IsNullOrEmpty(username) || payload == null) return false;
            if (!Items.KmhItemSafety.ValidatePayload(payload)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                AddPayloadLocked(v, payload);
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, payload.StackCount,
                    Items.KmhItemSafety.DescribeStateForLedger(payload), note);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // Null when nothing matches; a blob instance is atomic while a metadata stack can split.
        public static List<Items.KmhThingPayload> WithdrawPayloads(string username, string fingerprint, int qty, string note = "")
            => WithdrawPayloadsInternal(username, fingerprint, qty, null, note);

        private static List<Items.KmhThingPayload> WithdrawPayloadsInternal(string username, string fingerprint, int qty,
                                                                           string marker, string note,
                                                                           string refundMarker = null)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(fingerprint) || qty <= 0) return null;
            string ownerKey = ResolveOwnerKeyFor(username);
            List<Items.KmhThingPayload> granted = new List<Items.KmhThingPayload>();
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));

                foreach ((Items.KmhThingPayload e, int units) in PlanPayloadTakeLocked(v.ItemPayloads, fingerprint, qty))
                {
                    if (units >= e.StackCount) { granted.Add(e); v.ItemPayloads.Remove(e); }
                    else { granted.Add(Clone(e, units)); e.StackCount -= units; }
                }
                if (granted.Count == 0) return null;
                int taken = 0; foreach (var g in granted) taken += g.StackCount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, taken,
                    Items.KmhItemSafety.DescribeStateForLedger(granted[0]), note);
                if (!string.IsNullOrEmpty(marker))
                {
                    AddMarkerLocked(v, marker);

                    // Written in THIS commit: until the caller's transaction row lands, this entry is the only record of what left.
                    if (!string.IsNullOrEmpty(refundMarker))
                        v.PendingTakes.Add(new Dto.PendingTake
                        {
                            TakeMarker    = marker,
                            RefundMarker  = refundMarker,
                            Username      = username,
                            TakenUtcTicks = DateTime.UtcNow.Ticks,
                            Note          = note ?? "",
                            Payloads      = new List<Items.KmhThingPayload>(granted),
                        });
                }
                if (!CommitLocked(ownerKey, before)) return null;
            }
            RaiseChanged(ownerKey, note);
            return granted;
        }

        // One displayed row is one exact state, so a take drains only stacks identical to the one the row names.
        private static List<(Items.KmhThingPayload Stack, int Units)> PlanPayloadTakeLocked(List<Items.KmhThingPayload> stacks,
                                                                                         string fingerprint, int qty)
        {
            var plan = new List<(Items.KmhThingPayload, int)>();
            if (stacks == null || string.IsNullOrEmpty(fingerprint) || qty <= 0) return plan;
            string wantGroup = null;
            foreach (Items.KmhThingPayload e in stacks)
                if (e != null && string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal))
                { wantGroup = Items.KmhItemSafety.DisplayKey(e); break; }
            if (wantGroup == null) return plan;

            int remaining = qty;
            foreach (Items.KmhThingPayload e in stacks)
            {
                if (remaining <= 0) break;
                if (e == null) continue;
                // By content, never the stored fingerprint alone: a client-chosen duplicate must not pull a different item in.
                if (Items.KmhItemSafety.DisplayKey(e) != wantGroup) continue;

                switch (Items.KmhItemService.PlanTake(e.StackCount, remaining, Items.KmhPayloadEscrow.IsSplittable(e)))
                {
                    case Items.KmhItemService.TakeKind.Whole: plan.Add((e, e.StackCount)); remaining -= e.StackCount; break;
                    case Items.KmhItemService.TakeKind.Split: plan.Add((e, remaining)); remaining = 0; break;
                }
            }
            return plan;
        }

        // Safe to fail: a durable row now names the take, so recovery sees the value accounted for and drops the entry without paying.
        public static bool ClearPendingTake(string username, string takeMarker)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(takeMarker)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return true;
                int removed = v.PendingTakes.RemoveAll(
                    p => p != null && string.Equals(p.TakeMarker, takeMarker, StringComparison.Ordinal));
                if (removed == 0) return true;
                // Kept cleared if this save fails: the row naming the take is already written, and a take rolled back to
                // pending once outlived its pruned row, so boot handed the goods back a second time.
                if (!SaveToDisk())
                    Diagnostics.ServerLog.Warn($"Treasury: cleared take {takeMarker} for {ownerKey} in memory only - written by the save retry.");
                return true;
            }
        }

        // Deduplicated on the refund marker, so the caller's failure path and boot recovery can both run it and the goods return once.
        public static bool ReturnPendingTake(string username, string takeMarker, string refundMarker,
                                             IEnumerable<Items.KmhThingPayload> payloads, string note)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(refundMarker)) return false;
            if (!DepositEscrowOnce(username, refundMarker, 0, null, payloads, note)) return false;
            return ClearPendingTake(username, takeMarker);
        }

        // Server-side only: GetSnapshotFor deliberately omits these, so recovery and its tests read them here.
        internal static List<Dto.PendingTake> PendingTakesFor(string username)
        {
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
                return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v) && v.PendingTakes != null
                     ? new List<Dto.PendingTake>(v.PendingTakes)
                     : new List<Dto.PendingTake>();
        }

        // Every take still waiting for its row, across every vault. Read at boot only.
        public static List<Dto.PendingTake> AllPendingTakes()
        {
            var all = new List<Dto.PendingTake>();
            lock (_lock)
                foreach (TreasurySnapshot v in _vaults.Values)
                    if (v?.PendingTakes != null)
                        foreach (Dto.PendingTake p in v.PendingTakes)
                            if (p != null && !string.IsNullOrEmpty(p.TakeMarker)) all.Add(p);
            return all;
        }

        // What a fingerprint withdrawal would really take, for the veto hook, which was told the name the client sent.
        internal static string PayloadDefOf(string username, string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return null;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v) || v.ItemPayloads == null) return null;
                foreach (Items.KmhThingPayload p in v.ItemPayloads)
                    if (p != null && string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal)) return p.DefName;
                return null;
            }
        }

        // The on-disk view: the wire snapshot strips every blob, so it cannot tell a kept deep state from a destroyed one.
        internal static List<Items.KmhThingPayload> StoredPayloadsForTest(string username)
        {
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
                return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v) && v.ItemPayloads != null
                     ? new List<Items.KmhThingPayload>(v.ItemPayloads)
                     : new List<Items.KmhThingPayload>();
        }

        private static List<Items.KmhThingPayload> StripBlobs(List<Items.KmhThingPayload> src)
        {
            List<Items.KmhThingPayload> outList = new List<Items.KmhThingPayload>();
            if (src == null) return outList;
            foreach (Items.KmhThingPayload p in src)
                outList.Add(Items.KmhItemService.CloneWithoutBlob(p));
            return outList;
        }

        // Display only: stacks of one exact state share a row; two saved states never do, or a take would drain both.
        internal static List<Items.KmhThingPayload> GroupForDisplay(List<Items.KmhThingPayload> src)
        {
            var outList = new List<Items.KmhThingPayload>();
            if (src == null) return outList;
            var byKey = new Dictionary<string, Items.KmhThingPayload>(StringComparer.Ordinal);
            foreach (Items.KmhThingPayload p in src)
            {
                if (p == null) continue;
                string key = Items.KmhItemSafety.DisplayKey(p);
                if (byKey.TryGetValue(key, out Items.KmhThingPayload row))
                {
                    row.StackCount = Util.KmhSafe.AddSaturating(row.StackCount, p.StackCount);
                    continue;
                }
                Items.KmhThingPayload copy = Items.KmhItemService.CloneWithoutBlob(p);
                byKey[key] = copy;
                outList.Add(copy);   // the first row of a group represents it and carries its fingerprint
            }
            return outList;
        }

        private static List<Dto.PendingDeposit> StripPendingBlobs(List<Dto.PendingDeposit> src)
        {
            List<Dto.PendingDeposit> outList = new List<Dto.PendingDeposit>();
            if (src == null) return outList;
            foreach (Dto.PendingDeposit d in src)
            {
                // Only the blobs go: a committed record that lost its fee could not give that fee back on a reversal.
                outList.Add(new Dto.PendingDeposit
                {
                    TxnId = d.TxnId, Username = d.Username, CreatedUtcTicks = d.CreatedUtcTicks, CommittedUtcTicks = d.CommittedUtcTicks,
                    State = d.State, Kind = d.Kind, Silver = d.Silver, ItemDefName = d.ItemDefName, Qty = d.Qty, Note = d.Note,
                    SaveGenAtOpen = d.SaveGenAtOpen, Fee = d.Fee, GuildName = d.GuildName, GuildIdentity = d.GuildIdentity,
                    LedgerId = d.LedgerId, ConfirmedGeneration = d.ConfirmedGeneration,
                    Payloads = StripBlobs(d.Payloads),
                });
            }
            return outList;
        }

        private static Items.KmhThingPayload Clone(Items.KmhThingPayload p, int stackCount)
            => Items.KmhItemService.ClonePayload(p, stackCount);

        // Wear is weight-averaged on merge, so stacking never refreshes a worn item. Caller holds _lock.
        private static void AddPayloadLocked(TreasurySnapshot v, Items.KmhThingPayload payload)
        {
            if (payload.Mergeable)
                foreach (Items.KmhThingPayload e in v.ItemPayloads)
                    if (Items.KmhItemSafety.CanMergeFungible(e, payload))
                    { Items.KmhItemSafety.MergeFungible(e, payload); return; }

            if (string.IsNullOrEmpty(payload.ScribeXml))
                foreach (Items.KmhThingPayload e in v.ItemPayloads)
                    if (Items.KmhItemSafety.CanSafelyMerge(e, payload)) { e.StackCount = Util.KmhSafe.AddSaturating(e.StackCount, payload.StackCount); return; }

            v.ItemPayloads.Add(payload);
        }

        public static int CompactFungiblePayloads()
        {
            List<string> changedKeys = new List<string>();
            int totalMerged = 0;
            lock (_lock)
            {
                foreach (KeyValuePair<string, TreasurySnapshot> kv in _vaults)
                {
                    TreasurySnapshot v = kv.Value;
                    if (v?.ItemPayloads == null || v.ItemPayloads.Count < 2) continue;
                    List<Items.KmhThingPayload> outList = new List<Items.KmhThingPayload>(v.ItemPayloads.Count);
                    int mergedHere = 0;
                    foreach (Items.KmhThingPayload p in v.ItemPayloads)
                    {
                        if (p == null) continue;
                        // Only a per-instance vouch is trusted: a def-level one would merge away a stored blob's state.
                        bool merged = false;
                        if (p.Mergeable)
                            foreach (Items.KmhThingPayload e in outList)
                                if (Items.KmhItemSafety.CanMergeFungible(e, p))
                                { Items.KmhItemSafety.MergeFungible(e, p); merged = true; mergedHere++; break; }
                        if (!merged) outList.Add(p);
                    }
                    if (mergedHere > 0) { v.ItemPayloads = outList; totalMerged += mergedHere; changedKeys.Add(kv.Key); }
                }
            }
            if (totalMerged > 0)
            {
                SaveToDisk();
                foreach (string k in changedKeys) RaiseChanged(k, "fungible payloads consolidated");
                Diagnostics.ServerLog.Info($"Treasury: consolidated {totalMerged} legacy fungible payload fragment(s) across {changedKeys.Count} vault(s).");
            }
            return totalMerged;
        }

        // With a marker the take is stamped and held as a pending take in the same commit, like WithdrawPayloadsForTxn.
        public static List<Items.KmhThingPayload> TryWithdrawMatchingPayloads(string username, string defName,
            string requiredStuff, int minQuality, bool allowTainted, bool allowDamaged, int qty, string note,
            string marker = null, string refundMarker = null)
        {
            List<Items.KmhThingPayload> granted = new List<Items.KmhThingPayload>();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(defName) || qty <= 0) return granted;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                int rem = qty;
                foreach (Items.KmhThingPayload e in new List<Items.KmhThingPayload>(v.ItemPayloads))
                {
                    if (rem <= 0) break;
                    if (!string.Equals(e.DefName, defName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(requiredStuff) && !string.Equals(e.StuffDefName ?? "", requiredStuff, StringComparison.OrdinalIgnoreCase)) continue;
                    if (minQuality > 0 && e.Quality < minQuality) continue;
                    if (!allowTainted && e.Tainted) continue;
                    if (!allowDamaged && e.HitPoints >= 0 && e.MaxHitPoints > 0 && e.HitPoints < e.MaxHitPoints) continue;
                    switch (Items.KmhItemService.PlanTake(e.StackCount, rem, Items.KmhPayloadEscrow.IsSplittable(e)))
                    {
                        case Items.KmhItemService.TakeKind.Whole:
                            granted.Add(e); v.ItemPayloads.Remove(e); rem -= e.StackCount; break;
                        case Items.KmhItemService.TakeKind.Split:
                            granted.Add(Clone(e, rem)); e.StackCount -= rem; rem = 0; break;
                    }
                }
                if (granted.Count > 0)
                {
                    int taken = 0; foreach (var g in granted) taken += g.StackCount;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, taken,
                        Items.KmhItemSafety.DescribeStateForLedger(granted[0]), note);
                    if (!string.IsNullOrEmpty(marker))
                    {
                        AddMarkerLocked(v, marker);
                        if (!string.IsNullOrEmpty(refundMarker))
                            v.PendingTakes.Add(new Dto.PendingTake
                            {
                                TakeMarker = marker, RefundMarker = refundMarker, Username = username,
                                TakenUtcTicks = DateTime.UtcNow.Ticks, Note = note ?? "",
                                Payloads = new List<Items.KmhThingPayload>(granted),
                            });
                    }
                    if (!CommitLocked(ownerKey, before)) return new List<Items.KmhThingPayload>();
                }
            }
            if (granted.Count > 0) RaiseChanged(ownerKey, note);
            return granted;
        }

        private static void RaiseChanged(string ownerKey, string note)
            => Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
            { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });

        // Quest hand-overs that committed and whose quest has not yet recorded completing: kept in the treasury file itself,
        // never aged out like a vault marker, and released only once the quest's completion is on disk.
        private static readonly Dictionary<string, long> _handOvers = new Dictionary<string, long>(StringComparer.Ordinal);

        public static bool KnowsHandOver(string marker) { lock (_lock) return marker != null && _handOvers.ContainsKey(marker); }

        // After the quest's completion is saved. A failed write leaves it for the boot prune, which is just as safe.
        public static void ForgetHandOver(string marker)
        {
            bool removed;
            lock (_lock) removed = marker != null && _handOvers.Remove(marker);
            if (removed) SaveToDisk();
        }

        // Boot, after quests rolled forward: anything not belonging to a still-claimed delivery is spent evidence.
        public static int PruneHandOvers(Func<string, bool> stillOpen)
        {
            int n = 0;
            lock (_lock)
                foreach (string m in new List<string>(_handOvers.Keys))
                    if (stillOpen == null || !stillOpen(m)) { _handOvers.Remove(m); n++; }
            if (n > 0) SaveToDisk();
            return n;
        }

        // Moves the claimer's goods to the poster's vault in ONE commit with the marker on both: a crash leaves them on one
        // side, never neither, and a retry moves nothing. Compact stacks first, else full-state payloads; all of qty or none.
        public static bool HandOverMatchingOnce(string from, string to, string defName, int minQuality, int qty, string marker,
                                                string note, out bool alreadyHandedOver,
                                                out List<KeyValuePair<string, int>> movedCompact,
                                                out List<Items.KmhThingPayload> movedPayloads)
        {
            alreadyHandedOver = false;
            movedCompact = new List<KeyValuePair<string, int>>();
            movedPayloads = new List<Items.KmhThingPayload>();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.IsNullOrEmpty(defName) || qty <= 0
                || string.IsNullOrEmpty(marker)) return false;
            string fromKey = ResolveOwnerKeyFor(from), toKey = ResolveOwnerKeyFor(to);
            if (string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase)) return false;
            lock (_lock)
            {
                // The durable record, not the vault's marker window: a retry can never take the claimer's goods twice.
                if (_handOvers.ContainsKey(marker)
                    || (_vaults.TryGetValue(toKey, out TreasurySnapshot already) && IsAppliedMarkerLocked(already, marker)))
                { alreadyHandedOver = true; return true; }
                if (!_vaults.TryGetValue(fromKey, out TreasurySnapshot src)) return false;
                TreasurySnapshot before = RollbackPointLocked(fromKey);
                var others = new Dictionary<string, TreasurySnapshot> { { toKey, RollbackPointLocked(toKey) } };

                List<KeyValuePair<string, int>> plan = PlanMatchingLocked(src, defName, minQuality, qty, "");
                var takes = new List<(Items.KmhThingPayload Entry, int Count)>();
                if (plan == null)
                {
                    int rem = qty;
                    foreach (Items.KmhThingPayload e in src.ItemPayloads)
                    {
                        if (rem <= 0) break;
                        if (e == null || !string.Equals(e.DefName, defName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (minQuality > 0 && e.Quality < minQuality) continue;
                        switch (Items.KmhItemService.PlanTake(e.StackCount, rem, Items.KmhPayloadEscrow.IsSplittable(e)))
                        {
                            case Items.KmhItemService.TakeKind.Whole: takes.Add((e, e.StackCount)); rem -= e.StackCount; break;
                            case Items.KmhItemService.TakeKind.Split: takes.Add((e, rem)); rem = 0; break;
                        }
                    }
                    if (rem > 0) return false;
                }

                TreasurySnapshot dst = GetOrCreateLocked(toKey, isGuildOwned: false);
                if (plan != null)
                    foreach (KeyValuePair<string, int> kv in plan)
                    {
                        int next = src.Items[kv.Key] - kv.Value;
                        if (next <= 0) src.Items.Remove(kv.Key); else src.Items[kv.Key] = next;
                        dst.Items.TryGetValue(kv.Key, out int cur);
                        dst.Items[kv.Key] = AddQty(dst, cur, kv.Value, kv.Key, "quest delivery");
                        RecordTransactionLocked(src, from, TreasuryTransaction.KindWithdraw, kv.Value, kv.Key, note);
                        RecordTransactionLocked(dst, to, TreasuryTransaction.KindDeposit, kv.Value, kv.Key, note);
                        movedCompact.Add(kv);
                    }
                foreach ((Items.KmhThingPayload e, int count) in takes)
                {
                    Items.KmhThingPayload moved = count >= e.StackCount ? e : Clone(e, count);
                    if (count >= e.StackCount) src.ItemPayloads.Remove(e); else e.StackCount -= count;
                    AddPayloadLocked(dst, moved);
                    RecordTransactionLocked(src, from, TreasuryTransaction.KindWithdraw, count, Items.KmhItemSafety.DescribeStateForLedger(moved), note);
                    RecordTransactionLocked(dst, to, TreasuryTransaction.KindDeposit, count, Items.KmhItemSafety.DescribeStateForLedger(moved), note);
                    movedPayloads.Add(moved);
                }
                AddMarkerLocked(src, marker);
                AddMarkerLocked(dst, marker);
                _handOvers[marker] = DateTime.UtcNow.Ticks;
                if (!CommitLocked(fromKey, before, others)) { _handOvers.Remove(marker); movedCompact.Clear(); movedPayloads.Clear(); return false; }
            }
            RaiseChanged(fromKey, note);
            RaiseChanged(toKey, note);
            return true;
        }

        internal const int MaxItemKeysPerVault = 5_000;

        // In memory only: filling a vault to its key cap through real deposits is thousands of durable commits.
        internal static void SeedItemKeysForTest(string username, int count)
        {
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, false);
                for (int i = 0; i < count; i++) v.Items["KmhSelftestKey" + i] = 1;
            }
        }

        // The pending commit adds a key without this check, since by then the goods are out of the colony; preflight asks it.
        internal static bool NewItemKeyOverCap(string username, string itemKey)
        {
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
                return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v) && v.Items.Count >= MaxItemKeysPerVault
                    && !v.Items.ContainsKey(itemKey ?? "");
        }

        private const int MaxRecentCommittedTxns = 256;

        public static int UnconfirmedDepositCount(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v) || v.PendingDeposits == null) return 0;
                int n = 0;
                foreach (Dto.PendingDeposit d in v.PendingDeposits)
                    if (d.State == Dto.PendingDeposit.StatePending) n++;
                return n;
            }
        }

        // Drops a self-test's own pending records so a suite run leaves no value state behind.
        internal static void DiscardPendingForTest(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (_vaults.TryGetValue(ownerKey, out TreasurySnapshot v) && v.PendingDeposits != null)
                    v.PendingDeposits.RemoveAll(x => x.Username == username);
                _vaults.Remove(ownerKey);
            }
        }

        // Idempotent by txn id, so a network resend cannot double-book.
        public static bool BeginPendingDeposit(string username, Dto.PendingDeposit d)
        {
            if (string.IsNullOrEmpty(username) || d == null || !IsClientTxnId(d.TxnId)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsKnownTxnLocked(v, d.TxnId)) return false;
                d.Username        = username;
                d.State           = Dto.PendingDeposit.StatePending;
                d.CreatedUtcTicks = DateTime.UtcNow.Ticks;
                if (d.SaveGenAtOpen <= 0) d.SaveGenAtOpen = v.LastSaveGeneration;
                v.PendingDeposits.Add(d);
                // Approval tells the client to remove the goods; unsaved, the record dies on restart and the goods with it.
                if (!CommitLocked(ownerKey, before)) return false;
            }
            return true;
        }

        // The guild is credited only at commit, so it can never spend unconfirmed silver.
        // expectedIdentity is the incarnation the donor's checks were made against; a different one now holding the name is refused.
        public static bool BeginPendingGuildDonation(string username, string guildName, int amount, string txnId, out string reason,
                                                     string expectedIdentity = null)
        {
            reason = null;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)
                || amount <= 0 || !IsClientTxnId(txnId))
            { reason = "Invalid donation request."; return false; }
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                string identity = Guilds.GuildIdentity.Of(guildName);
                if (string.IsNullOrEmpty(identity)) { reason = "That guild no longer exists."; return false; }
                if (expectedIdentity != null && !string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
                { reason = "Your guild changed while that was processing - nothing was taken."; return false; }
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsKnownTxnLocked(v, txnId)) { reason = "That donation was already received."; return false; }
                if (v.SilverBalance < amount)
                { reason = "You don't have that much silver in your personal vault."; return false; }
                // Re-checked under the booking lock, or two racing donors would both pass and overshoot the cap.
                if (Economy.EconomyAccess.WouldExceedCap(true, GetGuildSilver(guildName) + PendingGuildDonationTotal(guildName),
                                                        amount, out string capReason))
                { reason = capReason; return false; }
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, amount, "",
                    $"donation to guild '{guildName}' (pending save)");
                v.PendingDeposits.Add(new Dto.PendingDeposit
                {
                    TxnId = txnId, Username = username, Kind = Dto.PendingDeposit.KindGuildDonate,
                    Silver = amount, GuildName = guildName, GuildIdentity = identity, Note = $"donation to guild '{guildName}'",
                    State = Dto.PendingDeposit.StatePending, CreatedUtcTicks = DateTime.UtcNow.Ticks,
                });
                if (!CommitLocked(ownerKey, before)) { reason = "The server could not record that donation - nothing was taken. Try again shortly."; return false; }
            }
            RaiseChanged(ownerKey, "donation_pending");
            return true;
        }

        public static long PendingGuildDonationTotal(string guildName)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)) return 0;
            long total = 0;
            lock (_lock)
                foreach (TreasurySnapshot v in _vaults.Values)
                    if (v?.PendingDeposits != null)
                        foreach (Dto.PendingDeposit d in v.PendingDeposits)
                            if (d.State == Dto.PendingDeposit.StatePending
                                && d.Kind == Dto.PendingDeposit.KindGuildDonate
                                && string.Equals(d.GuildName, guildName, StringComparison.OrdinalIgnoreCase))
                                total += d.Silver;
            return total;
        }


        public static bool HasVaultForUser(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            string key = ResolveOwnerKeyFor(username);
            lock (_lock) return _vaults.ContainsKey(key);
        }

        // Refuses unless the vault is completely unused, so it can never delete value.
        public static bool TryRemoveEmptyVault(string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return false;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return false;
                if (!IsUnusedVault(v)) return false;
                _vaults.Remove(ownerKey);
            }
            return true;
        }

        // Never used, not merely empty now: a player who deposited and withdrew still owns an audit trail.
        internal static bool IsUnusedVault(TreasurySnapshot v)
        {
            if (v == null) return false;
            if (v.SilverBalance != 0) return false;
            if (v.LifetimeSilverIn != 0 || v.LifetimeSilverOut != 0) return false;
            if (v.Items != null && v.Items.Count > 0) return false;
            if (v.ItemPayloads != null && v.ItemPayloads.Count > 0) return false;
            if (v.PendingDeposits != null && v.PendingDeposits.Count > 0) return false;
            if (v.CommittedDeposits != null && v.CommittedDeposits.Count > 0) return false;
            if (v.RecentTransactions != null && v.RecentTransactions.Count > 0) return false;
            return true;
        }

        internal static bool HasVaultForTest(string key) { lock (_lock) return _vaults.ContainsKey(key ?? ""); }

        // The shape an older build's disband left: a vault under a name no guild holds any more.
        internal static void PutVaultForTest(string key, int silver)
        {
            lock (_lock) _vaults[key] = new TreasurySnapshot { OwnerKey = key, IsGuildOwned = true, SilverBalance = silver, Items = NormalizeItemKeys(null) };
        }

        internal static int GuildItemForTest(string guild, string def)
        {
            lock (_lock)
                return _vaults.TryGetValue(guild ?? "", out TreasurySnapshot v) && v?.Items != null
                    && v.Items.TryGetValue(def, out int n) ? n : 0;
        }

        // Test seam: durability checks seed a throwaway owner and must leave the shared store as they found it.
        internal static void PurgeOwnerForTest(string owner)
        {
            lock (_lock)
            {
                _vaults.Remove(PersonalKeyFor(owner));
                _vaults.Remove(owner);
            }
        }

        public static int PruneUnusedVaults()
        {
            List<string> candidates = new List<string>();
            lock (_lock)
                foreach (KeyValuePair<string, TreasurySnapshot> kv in _vaults)
                    if (IsUnusedVault(kv.Value)) candidates.Add(kv.Key);

            int removed = 0;
            foreach (string k in candidates) if (TryRemoveEmptyVault(k)) removed++;
            if (removed > 0)
            {
                SaveToDisk();
                Diagnostics.ServerLog.Info($"Treasury: removed {removed} vault(s) that were created but never used.");
            }
            return removed;
        }

        // Server markers (take, refund, payout, federation) all contain ':'; a client id never may. A pending client
        // deposit is not evidence that any server step ran, so the marker check never reads the pending list: a
        // planted pending id equal to a Connected op id made its withdrawal skip the debit while Global credited it.
        internal static bool IsClientTxnId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
                    return false;
            return true;
        }

        private static bool IsAppliedMarkerLocked(TreasurySnapshot v, string marker)
        {
            foreach (string t in v.RecentCommittedTxns)
                if (string.Equals(t, marker, StringComparison.Ordinal)) return true;
            return false;
        }

        // A one-time reward can be offered again by a re-issued link or a stale view, long after 256 other markers;
        // its marker is the only record it was paid, so it outlives the window every other marker shares.
        internal const string OneTimeRewardPrefix = "fedreward:";
        internal const int MaxOneTimeMarkers = 520;

        private static bool IsOneTimeMarker(string marker)
            => marker != null && marker.StartsWith(OneTimeRewardPrefix, StringComparison.Ordinal);

        // Every marker goes through here: one site still trimming oldest-first would evict a reward marker.
        private static void AddMarkerLocked(TreasurySnapshot v, string marker)
        {
            v.RecentCommittedTxns.Add(marker);
            int oneTime = 0, other = 0;
            foreach (string t in v.RecentCommittedTxns) { if (IsOneTimeMarker(t)) oneTime++; else other++; }
            for (int i = 0; i < v.RecentCommittedTxns.Count && (other > MaxRecentCommittedTxns || oneTime > MaxOneTimeMarkers);)
            {
                bool once = IsOneTimeMarker(v.RecentCommittedTxns[i]);
                if (once && oneTime > MaxOneTimeMarkers) { v.RecentCommittedTxns.RemoveAt(i); oneTime--; }
                else if (!once && other > MaxRecentCommittedTxns) { v.RecentCommittedTxns.RemoveAt(i); other--; }
                else i++;
            }
        }

        private static bool IsKnownTxnLocked(TreasurySnapshot v, string txnId)
        {
            foreach (Dto.PendingDeposit p in v.PendingDeposits)
                if (string.Equals(p.TxnId, txnId, StringComparison.Ordinal)) return true;
            foreach (string t in v.RecentCommittedTxns)
                if (string.Equals(t, txnId, StringComparison.Ordinal)) return true;
            return false;
        }

        // A lower generation means the client loaded an older save, where confirmed deposits would duplicate.
        internal static bool SaveGenerationWentBackwards(long seen, long incoming)
            => incoming > 0 && seen > 0 && incoming < seen;

        // Detection only. The watermark is the colony ledger's, or player-wide when the client sends no ledger id.
        public static long NoteSaveGeneration(string username, long generation, string ledgerId = "")
        {
            if (string.IsNullOrEmpty(username) || generation <= 0) return 0;
            string ownerKey = ResolveOwnerKeyFor(username);
            long seen;
            lock (_lock)
            {
                // No vault means no committed deposits, so there is nothing to protect and nothing to create.
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return 0;
                if (string.IsNullOrEmpty(ledgerId))
                {
                    seen = v.LastSaveGeneration;
                    if (generation > seen) v.LastSaveGeneration = generation;
                }
                else
                {
                    v.LedgerGenerations ??= new Dictionary<string, long>(StringComparer.Ordinal);
                    v.LedgerGenerations.TryGetValue(ledgerId, out seen);
                    if (generation > seen) v.LedgerGenerations[ledgerId] = generation;
                    // Bounded: the least-saved colony goes, never the one reporting.
                    while (v.LedgerGenerations.Count > MaxLedgersPerVault)
                    {
                        string drop = null; long low = long.MaxValue;
                        foreach (KeyValuePair<string, long> kv in v.LedgerGenerations)
                            if (kv.Key != ledgerId && kv.Value < low) { low = kv.Value; drop = kv.Key; }
                        if (drop == null) break;
                        v.LedgerGenerations.Remove(drop);
                    }
                }
            }
            return seen;
        }

        internal const int MaxLedgersPerVault = 32;

        internal static int LedgerCountForTest(string username)
        {
            lock (_lock) return _vaults.TryGetValue(ResolveOwnerKeyFor(username), out TreasurySnapshot v) ? v.LedgerGenerations?.Count ?? 0 : 0;
        }

        // A client's colony ledger id, or "" when absent or not the 32-hex form the client mints.
        internal static string LedgerIdOrEmpty(string raw)
        {
            if (raw == null || raw.Length != 32) return "";
            foreach (char c in raw) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return "";
            return raw;
        }

        public static long LastSaveGenerationOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            string key = ResolveOwnerKeyFor(username);
            lock (_lock) { return _vaults.TryGetValue(key, out TreasurySnapshot v) ? v.LastSaveGeneration : 0; }
        }

        // Idempotent: unknown or already-committed ids are ignored. ledgerId and generation name the save that holds them.
        public static int ConfirmDeposits(string username, IEnumerable<string> txnIds, string ledgerId = "", long generation = 0)
        {
            if (string.IsNullOrEmpty(username) || txnIds == null) return 0;
            HashSet<string> want = new HashSet<string>(txnIds, StringComparer.Ordinal);
            if (want.Count == 0) return 0;
            string ownerKey = ResolveOwnerKeyFor(username);
            int committed = 0;
            List<Dto.PendingDeposit> donations = null;
            List<string> fees = null;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                Dictionary<string, TreasurySnapshot> guildsBefore = GuildRollbackPointsLocked(v, d => want.Contains(d.TxnId));
                foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    if (!want.Contains(d.TxnId)) continue;
                    // Owed in this commit, so the deposit and the fee it took for the pool are on disk together.
                    if (d.Fee > 0) (fees ??= new List<string>()).Add(OweFeeLocked(FeeMarkerFor(ownerKey, d.TxnId), d.Fee, username, $"treasury deposit fee ({username})"));
                    CommitPendingLocked(v, d, ref donations, ledgerId, generation);
                    committed++;
                }
                // The fee follows the commit, so a deposit that stayed pending is never charged for.
                if (committed > 0 && !CommitLocked(ownerKey, before, guildsBefore)) { ForgetFeesLocked(fees); return 0; }
            }
            if (committed > 0) RaiseChanged(ownerKey, "deposit_confirmed");
            if (fees != null) SettleOwedFees(fees);
            NotifyDonationsFinalized(donations);
            return committed;
        }

        // Runs outside the vault lock, since GuildStore takes its own.
        private static void NotifyDonationsFinalized(List<Dto.PendingDeposit> donations)
        {
            if (donations == null) return;
            var guilds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dto.PendingDeposit d in donations)
            {
                Guilds.GuildStore.FinalizeDonation(d.Username, d.GuildName, d.Silver, d.GuildIdentity);
                if (!string.IsNullOrEmpty(d.GuildName)) guilds.Add(d.GuildName);
            }
            // The vault's balance changed for every member, so their Halls are told now rather than on their next action.
            foreach (string g in guilds) Guilds.GuildHandler.BroadcastToGuild(g);
        }

        // unsavedGoodsGone is left alone because reverting it would destroy goods the client has already removed.
        public static (int committed, int reverted) ReconcileDeposits(string username, IEnumerable<string> committed, IEnumerable<string> unsavedGoodsGone, int graceSeconds,
                                                                      string ledgerId = "", long generation = 0)
        {
            int didCommit = 0, didRevert = 0;
            List<string> fees = null;
            if (string.IsNullOrEmpty(username)) return (0, 0);
            HashSet<string> have = new HashSet<string>(committed ?? new List<string>(), StringComparer.Ordinal);
            HashSet<string> keep = new HashSet<string>(unsavedGoodsGone ?? new List<string>(), StringComparer.Ordinal);
            string ownerKey = ResolveOwnerKeyFor(username);
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(Math.Max(0, graceSeconds)).Ticks;
            List<Dto.PendingDeposit> donations = null;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                Dictionary<string, TreasurySnapshot> guildsBefore = GuildRollbackPointsLocked(v,
                    d => ReconcileVerdictFor(d.TxnId, d.CreatedUtcTicks, have, keep, cutoff) == ReconcileVerdict.Commit);
                foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    switch (ReconcileVerdictFor(d.TxnId, d.CreatedUtcTicks, have, keep, cutoff))
                    {
                        case ReconcileVerdict.Commit:
                            if (d.Fee > 0) (fees ??= new List<string>()).Add(OweFeeLocked(FeeMarkerFor(ownerKey, d.TxnId), d.Fee, username, $"treasury deposit fee ({username})"));
                            CommitPendingLocked(v, d, ref donations, ledgerId, generation);
                            didCommit++;
                            break;
                        case ReconcileVerdict.Revert:
                            RevertPendingLocked(v, d);
                            didRevert++;
                            Diagnostics.ServerLog.Warn($"Treasury: reverted pending deposit {d.TxnId} for {username} " +
                                $"({DescribePending(d)}) - client reported it not durably saved (local rollback).");
                            break;
                    }
                }
                if ((didCommit > 0 || didRevert > 0) && !CommitLocked(ownerKey, before, guildsBefore)) { ForgetFeesLocked(fees); return (0, 0); }
            }
            if (didCommit > 0 || didRevert > 0) RaiseChanged(ownerKey, "deposit_reconciled");
            if (fees != null) SettleOwedFees(fees);
            NotifyDonationsFinalized(donations);
            return (didCommit, didRevert);
        }

        internal enum ReconcileVerdict { Commit, Keep, Revert }

        // Pure so the item-loss invariant is testable; the clause order is what keeps unsaved goods from reverting.
        internal static ReconcileVerdict ReconcileVerdictFor(string txnId, long createdTicks,
            HashSet<string> committed, HashSet<string> unsavedGoodsGone, long cutoffTicks)
        {
            if (committed != null && committed.Contains(txnId)) return ReconcileVerdict.Commit;
            if (unsavedGoodsGone != null && unsavedGoodsGone.Contains(txnId)) return ReconcileVerdict.Keep;
            return createdTicks < cutoffTicks ? ReconcileVerdict.Revert : ReconcileVerdict.Keep;
        }

        public static int SweepStalePendingDeposits(int timeoutMinutes, Func<string, bool> isOwnerOnline = null)
        {
            if (timeoutMinutes <= 0) return 0;
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(timeoutMinutes).Ticks;
            int reverted = 0;
            lock (_lock)
            {
                foreach (KeyValuePair<string, TreasurySnapshot> kv in _vaults)
                {
                    TreasurySnapshot v = kv.Value;
                    if (v?.PendingDeposits == null || v.PendingDeposits.Count == 0) continue;
                    foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                    {
                        if (d.State != Dto.PendingDeposit.StatePending) continue;
                        if (d.CreatedUtcTicks >= cutoff) continue;
                        // An online player will still save and confirm, so only abandoned deposits expire.
                        if (isOwnerOnline != null && isOwnerOnline(d.Username)) continue;
                        RevertPendingLocked(v, d);
                        reverted++;
                        Diagnostics.ServerLog.Warn($"Treasury: expired pending deposit {d.TxnId} for {d.Username} " +
                            $"({DescribePending(d)}) after {timeoutMinutes}m unconfirmed - reverted (never became spendable).");
                    }
                }
            }
            if (reverted > 0) SaveToDisk();
            return reverted;
        }

        // Test-only: makes a pending donation look like one an older build wrote, with no incarnation.
        internal static bool ForgetPendingGuildIdentityForTest(string username, string txnId)
        {
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return false;
                foreach (Dto.PendingDeposit d in v.PendingDeposits)
                    if (string.Equals(d.TxnId, txnId, StringComparison.Ordinal)) { d.GuildIdentity = ""; return true; }
            }
            return false;
        }

        // A guild name that was freed or is being reused: donations still pending towards an incarnation that no longer
        // holds it go back to the donors. One made to the guild that holds the name now is left alone.
        public static int RevertPendingGuildDonations(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            int reverted = 0;
            lock (_lock)
            {
                foreach (TreasurySnapshot v in _vaults.Values)
                {
                    if (v?.PendingDeposits == null) continue;
                    foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                        if (d.State == Dto.PendingDeposit.StatePending && d.Kind == Dto.PendingDeposit.KindGuildDonate
                            && string.Equals(d.GuildName, guildName, StringComparison.OrdinalIgnoreCase)
                            && !(!string.IsNullOrEmpty(d.GuildIdentity) && Guilds.GuildIdentity.IsLive(d.GuildName, d.GuildIdentity)))
                        { RevertPendingLocked(v, d); reverted++; }
                }
            }
            if (reverted > 0) SaveToDisk();
            return reverted;
        }

        // A donation refunds the donor, who was debited at begin. Caller holds _lock.
        private static void RevertPendingLocked(TreasurySnapshot v, Dto.PendingDeposit d)
        {
            if (d.Kind == Dto.PendingDeposit.KindGuildDonate && d.Silver > 0)
            {
                v.SilverBalance     = AddSilver(v, d.Silver, "donation refund");
                v.LifetimeSilverIn += d.Silver;
                RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, d.Silver, "",
                    $"donation to guild '{d.GuildName}' reverted - refund");
            }
            v.PendingDeposits.Remove(d);
        }

        // Donations are collected into `donations` so guild bookkeeping runs outside this lock. Caller holds _lock.
        private static void CommitPendingLocked(TreasurySnapshot v, Dto.PendingDeposit d, ref List<Dto.PendingDeposit> donations,
                                                string ledgerId = "", long generation = 0)
        {
            switch (d.Kind)
            {
                case Dto.PendingDeposit.KindGuildDonate:
                    // Only the incarnation it was made to. A row from an older build names none, and a name is no proof of
                    // which guild was meant, so that donation goes back to the donor instead.
                    if (string.IsNullOrEmpty(d.GuildIdentity))
                    {
                        Diagnostics.ServerLog.Warn($"Treasury: donation {d.TxnId} from {d.Username} to guild '{d.GuildName}' predates guild "
                            + "identities - returned to the donor rather than credited by name.");
                        RevertPendingLocked(v, d); return;
                    }
                    TreasurySnapshot gv = LiveGuildVaultLocked(d.GuildName, d.GuildIdentity, "donation commit");
                    // Disbanded, or its name re-taken, before the donation was confirmed: the donor gets it back.
                    if (gv == null) { RevertPendingLocked(v, d); return; }
                    string donated = "donation:" + d.TxnId;
                    if (IsAppliedMarkerLocked(gv, donated)) break;
                    gv.SilverBalance     = AddSilver(gv, d.Silver, "guild donation commit");
                    gv.LifetimeSilverIn += d.Silver;
                    AddMarkerLocked(gv, donated);
                    RecordTransactionLocked(gv, d.Username, TreasuryTransaction.KindDeposit, d.Silver, "",
                        $"contribution from {d.Username} (save-confirmed)");
                    (donations = donations ?? new List<Dto.PendingDeposit>()).Add(d);
                    break;
                case Dto.PendingDeposit.KindSilver:
                    v.SilverBalance     = AddSilver(v, d.Silver, "pending deposit commit");
                    v.LifetimeSilverIn += d.Silver;
                    RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, d.Silver, "", d.Note);
                    break;
                case Dto.PendingDeposit.KindItem:
                    if (!v.Items.TryGetValue(d.ItemDefName, out int cur)) cur = 0;
                    v.Items[d.ItemDefName] = AddQty(v, cur, d.Qty, d.ItemDefName, "pending item commit");
                    RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, d.Qty, d.ItemDefName, d.Note);
                    break;
                case Dto.PendingDeposit.KindPayload:
                    if (d.Payloads != null)
                        foreach (Items.KmhThingPayload p in d.Payloads)
                        {
                            if (p == null || !Items.KmhItemSafety.ValidatePayload(p)) continue;
                            AddPayloadLocked(v, p);
                            RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, p.StackCount,
                                Items.KmhItemSafety.DescribeStateForLedger(p), d.Note);
                        }
                    break;
            }
            v.PendingDeposits.Remove(d);
            AddMarkerLocked(v, d.TxnId);

            // Blob-stripped, since reversal removes vault payloads by fingerprint rather than by saved XML.
            Dto.PendingDeposit rec = StripPendingBlobs(new List<Dto.PendingDeposit> { d })[0];
            rec.State = Dto.PendingDeposit.StateCommitted;
            rec.CommittedUtcTicks = DateTime.UtcNow.Ticks;
            rec.LedgerId = ledgerId ?? "";
            rec.ConfirmedGeneration = Math.Max(0, generation);
            v.CommittedDeposits.Add(rec);
            while (v.CommittedDeposits.Count > MaxRecentCommittedTxns) v.CommittedDeposits.RemoveAt(0);
        }

        // Guild donations are reported rather than reversed, because other members may already have spent them.
        public static (int reversed, int flagged) ReverseRolledBackCommits(
            string username, IEnumerable<string> clientHas, IEnumerable<string> unsavedGoodsGone, int graceSeconds,
            string ledgerId = "", long loadedGeneration = 0)
        {
            if (string.IsNullOrEmpty(username)) return (0, 0);
            HashSet<string> have = new HashSet<string>(clientHas ?? new List<string>(), StringComparer.Ordinal);
            HashSet<string> keep = new HashSet<string>(unsavedGoodsGone ?? new List<string>(), StringComparer.Ordinal);
            string ownerKey = ResolveOwnerKeyFor(username);
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(Math.Max(0, graceSeconds)).Ticks;
            int reversed = 0, flagged = 0;
            List<string> report = new List<string>();
            List<string> returns = null;

            lock (_lock)
            {
                // No vault means nothing was ever committed, so there is nothing to undo.
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return (0, 0);
                foreach (Dto.PendingDeposit c in new List<Dto.PendingDeposit>(v.CommittedDeposits))
                {
                    bool undo = string.IsNullOrEmpty(ledgerId)
                        ? ShouldUndoCommitted(c.TxnId, c.CommittedUtcTicks, have, keep, cutoff)
                        : ShouldUndoInLedger(c.TxnId, c.LedgerId, c.ConfirmedGeneration, ledgerId, loadedGeneration, have, keep);
                    if (!undo) continue;
                    if (c.Kind == Dto.PendingDeposit.KindGuildDonate)
                    {
                        flagged++;
                        report.Add($"{c.TxnId} ({DescribePending(c)}) to guild '{c.GuildName}' - NOT auto-reversed");
                        v.CommittedDeposits.Remove(c);
                        continue;
                    }
                    string shortfall = ReverseCommittedLocked(v, c);
                    // The player has the whole amount back, so the pool gives back the fee it took; owed in this save.
                    if (c.Kind == Dto.PendingDeposit.KindSilver && c.Fee > 0)
                        (returns ??= new List<string>()).Add(OweFeeReturnLocked(FeeReturnMarkerFor(ownerKey, c.TxnId), c.Fee, c.Username,
                            $"deposit {c.TxnId} fee returned - client loaded an older save ({c.Username})"));
                    v.CommittedDeposits.Remove(c);
                    v.RecentCommittedTxns.Remove(c.TxnId);
                    reversed++;
                    report.Add($"{c.TxnId} ({DescribePending(c)}){shortfall}");
                }
            }

            if (reversed > 0 || flagged > 0)
            {
                // Unsaved, the return stays owed in memory beside the reversal; its marker lets the sweep pay it only once.
                if (SaveToDisk() && returns != null) SettleOwedFees(returns);
                RaiseChanged(ownerKey, "rollback_reversed");
                Diagnostics.ServerLog.Error(
                    $"Treasury: {username} loaded an OLDER save after confirming deposit(s). Reversed {reversed}, " +
                    $"flagged {flagged} for manual settlement: {string.Join("; ", report)}. " +
                    $"Review with 'kmh audit-player {username}'.");
            }
            return (reversed, flagged);
        }

        // Every clause is a reason NOT to act, so the default answer is to leave the deposit alone.
        internal static bool ShouldUndoCommitted(string txnId, long committedUtcTicks,
                                                 HashSet<string> clientHas, HashSet<string> unsavedGoodsGone, long cutoffTicks)
        {
            if (string.IsNullOrEmpty(txnId)) return false;
            if (clientHas != null && clientHas.Contains(txnId)) return false;
            if (unsavedGoodsGone != null && unsavedGoodsGone.Contains(txnId)) return false;
            return committedUtcTicks <= cutoffTicks;
        }

        // Undo what that colony made durable after the loaded save, however recent: a time grace would keep the duplicate.
        internal static bool ShouldUndoInLedger(string txnId, string commitLedger, long confirmedGeneration, string ledgerId,
                                                long loadedGeneration, HashSet<string> clientHas, HashSet<string> unsavedGoodsGone)
        {
            if (string.IsNullOrEmpty(txnId) || string.IsNullOrEmpty(ledgerId)) return false;
            if (!string.Equals(commitLedger, ledgerId, StringComparison.Ordinal)) return false;
            if (confirmedGeneration <= loadedGeneration) return false;
            if (clientHas != null && clientHas.Contains(txnId)) return false;
            if (unsavedGoodsGone != null && unsavedGoodsGone.Contains(txnId)) return false;
            return true;
        }

        // Returns a note when part was already spent; the balance is never driven negative. Caller holds _lock.
        private static string ReverseCommittedLocked(TreasurySnapshot v, Dto.PendingDeposit c)
        {
            switch (c.Kind)
            {
                case Dto.PendingDeposit.KindSilver:
                {
                    int take = Math.Min(v.SilverBalance, Math.Max(0, c.Silver));
                    v.SilverBalance      = AddSilver(v, -take, "save-rollback reversal");
                    v.LifetimeSilverIn  -= take;
                    RecordTransactionLocked(v, c.Username, TreasuryTransaction.KindWithdraw, take, "",
                        $"deposit {c.TxnId} reversed - client loaded an older save");
                    return take < c.Silver ? $" - only {take} of {c.Silver} silver recovered, rest already spent" : "";
                }
                case Dto.PendingDeposit.KindItem:
                {
                    string key = Util.ItemKey.Compose(c.ItemDefName, "", 0);
                    v.Items.TryGetValue(key, out int held);
                    int take = Math.Min(held, Math.Max(0, c.Qty));
                    if (take > 0)
                    {
                        if (held - take <= 0) v.Items.Remove(key); else v.Items[key] = held - take;
                        RecordTransactionLocked(v, c.Username, TreasuryTransaction.KindWithdraw, take, c.ItemDefName,
                            $"deposit {c.TxnId} reversed - client loaded an older save");
                    }
                    return take < c.Qty ? $" - only {take} of {c.Qty} {c.ItemDefName} recovered, rest already withdrawn" : "";
                }
                case Dto.PendingDeposit.KindPayload:
                {
                    int wanted = 0; foreach (Items.KmhThingPayload p in c.Payloads ?? new List<Items.KmhThingPayload>()) wanted += p?.StackCount ?? 0;
                    int took = 0;
                    foreach (Items.KmhThingPayload want in c.Payloads ?? new List<Items.KmhThingPayload>())
                    {
                        if (want == null) continue;
                        int need = want.StackCount;
                        foreach (Items.KmhThingPayload held in new List<Items.KmhThingPayload>(v.ItemPayloads))
                        {
                            if (need <= 0) break;
                            if (!string.Equals(held.Fingerprint, want.Fingerprint, StringComparison.Ordinal)) continue;
                            switch (Items.KmhItemService.PlanTake(held.StackCount, need, Items.KmhPayloadEscrow.IsSplittable(held)))
                            {
                                case Items.KmhItemService.TakeKind.Whole:
                                    took += held.StackCount; need -= held.StackCount; v.ItemPayloads.Remove(held); break;
                                case Items.KmhItemService.TakeKind.Split:
                                    took += need; held.StackCount -= need; need = 0; break;
                            }
                        }
                    }
                    if (took > 0)
                        RecordTransactionLocked(v, c.Username, TreasuryTransaction.KindWithdraw, took, c.ItemDefName,
                            $"deposit {c.TxnId} reversed - client loaded an older save");
                    return took < wanted ? $" - only {took} of {wanted} unit(s) recovered, rest already withdrawn" : "";
                }
            }
            return "";
        }

        private static string DescribePending(Dto.PendingDeposit d)
        {
            if (d == null) return "";
            switch (d.Kind)
            {
                case Dto.PendingDeposit.KindSilver:      return $"{d.Silver}s";
                case Dto.PendingDeposit.KindItem:        return $"x{d.Qty} {d.ItemDefName}";
                case Dto.PendingDeposit.KindPayload:     return $"x{d.Qty} {d.ItemDefName} (full-state)";
                case Dto.PendingDeposit.KindGuildDonate: return $"{d.Silver}s donation to guild '{d.GuildName}'";
                default: return d.Kind ?? "";
            }
        }

        public static (int count, long silver, long itemUnits) PendingSummaryFor(string username)
        {
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v) || v.PendingDeposits == null) return (0, 0, 0);
                long silver = 0, units = 0;
                foreach (Dto.PendingDeposit d in v.PendingDeposits)
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    silver += d.Silver;
                    units  += d.Qty;
                }
                return (v.PendingDeposits.Count, silver, units);
            }
        }

        // Lowest qualifying quality goes first so a claimer keeps their best gear, and it takes the full qty or nothing.
        public static bool TryWithdrawMatching(string username, string targetDefName, int requiredQualityIndex,
                                               int qty, string note, out List<KeyValuePair<string, int>> consumed,
                                               string requiredStuff = "")
        {
            consumed = new List<KeyValuePair<string, int>>();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(targetDefName) || qty <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);

                List<KeyValuePair<string, int>> plan = PlanMatchingLocked(v, targetDefName, requiredQualityIndex, qty, requiredStuff);
                if (plan == null) return false;
                foreach (KeyValuePair<string, int> kv in plan)
                {
                    int next = v.Items[kv.Key] - kv.Value;
                    if (next <= 0) v.Items.Remove(kv.Key);
                    else           v.Items[kv.Key] = next;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, kv.Value, kv.Key, note);
                    consumed.Add(kv);
                }
                if (!CommitLocked(ownerKey, before)) { consumed.Clear(); return false; }
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // The stacks TryWithdrawMatching would take, lowest quality first, without taking them; null when short.
        public static List<KeyValuePair<string, int>> PlanMatching(string username, string targetDefName,
                                                                   int requiredQualityIndex, int qty, string requiredStuff = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(targetDefName) || qty <= 0) return null;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
                return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v)
                     ? PlanMatchingLocked(v, targetDefName, requiredQualityIndex, qty, requiredStuff) : null;
        }

        private static List<KeyValuePair<string, int>> PlanMatchingLocked(TreasurySnapshot v, string targetDefName,
                                                                           int requiredQualityIndex, int qty, string requiredStuff)
        {
            List<KeyValuePair<string, int>> candidates = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> kv in v.Items)
                if (Util.ItemKey.Matches(kv.Key, targetDefName, requiredStuff, requiredQualityIndex))
                    candidates.Add(kv);
            candidates.Sort((a, b) =>
            {
                Util.ItemKey.Split(a.Key, out _, out _, out int qa);
                Util.ItemKey.Split(b.Key, out _, out _, out int qb);
                return qa.CompareTo(qb);
            });

            long total = 0;
            foreach (KeyValuePair<string, int> kv in candidates) total += kv.Value;
            if (total < qty) return null;

            var plan = new List<KeyValuePair<string, int>>();
            int remaining = qty;
            foreach (KeyValuePair<string, int> kv in candidates)
            {
                if (remaining <= 0) break;
                int take = Math.Min(kv.Value, remaining);
                plan.Add(new KeyValuePair<string, int>(kv.Key, take));
                remaining -= take;
            }
            return plan;
        }

        // All or nothing across several keys, on one marker, so a want fill takes exactly the stacks its row names.
        public static bool WithdrawItemsForTxn(string username, IList<KeyValuePair<string, int>> items, string marker, string note = "")
            => items != null && items.Count > 0 && WithdrawBundleForTxn(username, 0, items, marker, note);

        // Silver and items in one commit on one marker. With nothing to take it still stamps the marker, so the
        // row's refund rule ("pay back only what the vault confirms it gave") holds for everything the row names.
        public static bool WithdrawBundleForTxn(string username, int silver, IList<KeyValuePair<string, int>> items,
                                                string marker, string note = "")
        {
            if (string.IsNullOrEmpty(username) || silver < 0 || string.IsNullOrEmpty(marker)) return false;
            items = items ?? new List<KeyValuePair<string, int>>();
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsAppliedMarkerLocked(v, marker)) return true;
                if (silver > 0 && v.SilverBalance < silver) return false;
                // The vault's own comparer: "Steel" and "sTEEL" are one stack, so they must be one need, or each passes the check alone.
                var need = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> kv in items)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) return false;
                    need.TryGetValue(kv.Key, out int n);
                    need[kv.Key] = Util.KmhSafe.AddSaturating(n, kv.Value);
                }
                foreach (KeyValuePair<string, int> kv in need)
                    if (!v.Items.TryGetValue(kv.Key, out int cur) || cur < kv.Value) return false;
                foreach (KeyValuePair<string, int> kv in need)
                {
                    int next = v.Items[kv.Key] - kv.Value;
                    if (next <= 0) v.Items.Remove(kv.Key);
                    else           v.Items[kv.Key] = next;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, kv.Value, kv.Key, note);
                }
                if (silver > 0)
                {
                    v.SilverBalance -= silver;
                    v.LifetimeSilverOut += silver;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, silver, "", note);
                }
                AddMarkerLocked(v, marker);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            RaiseChanged(ownerKey, note);
            return true;
        }

        // long amount: the display log's int wire field is clamped, but the durable ledger gets the exact figure.
        private static void RecordTransactionLocked(
            TreasurySnapshot v, string username, string kind, long amount, string itemDefName, string note)
        {
            v.RecentTransactions.Add(new TreasuryTransaction
            {
                UtcTicks    = DateTime.UtcNow.Ticks,
                Username    = username ?? "",
                Kind        = kind,
                Amount      = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, amount)),
                ItemDefName = itemDefName ?? "",
                Note        = note ?? "",
            });
            while (v.RecentTransactions.Count > MaxTransactionLogEntries)
            {
                v.RecentTransactions.RemoveAt(0);
            }

            // Held for the save that carries the change: queued here, a refused commit's line reached the file anyway.
            _unsavedLines.Add(new UnsavedLine { Seq = ++_lineSeq, Utc = DateTime.UtcNow, Vault = v.OwnerKey, Actor = username,
                                                Kind = kind, Amount = amount, Item = itemDefName, Note = note });
        }

        private sealed class UnsavedLine
        {
            public long Seq, Amount;
            public DateTime Utc;
            public string Vault, Actor, Kind, Item, Note;
        }

        // In record order. _commitMark is the last line before the commit in progress, set with its rollback point.
        private static readonly List<UnsavedLine> _unsavedLines = new List<UnsavedLine>();
        private static long _lineSeq, _commitMark;

        private static void ReleaseLinesLocked(long upTo)
        {
            int n = 0;
            while (n < _unsavedLines.Count && _unsavedLines[n].Seq <= upTo) n++;
            for (int i = 0; i < n; i++)
            {
                UnsavedLine l = _unsavedLines[i];
                Persistence.TransactionLedger.Record(l.Vault, l.Actor, l.Kind, l.Amount, l.Item, l.Note, l.Utc);
            }
            _unsavedLines.RemoveRange(0, n);
        }


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

        public static int PersonalVaultCount()
        {
            int n = 0;
            lock (_lock) foreach (string k in _vaults.Keys) if (k.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

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

        // full is exact-restorable, partial is metadata-only, legacy is pre-payload with unproven state.
        public static (int full, int partial, int legacy) PayloadHealth()
        {
            int full = 0, partial = 0, legacy = 0;
            lock (_lock)
                foreach (TreasurySnapshot v in _vaults.Values)
                {
                    if (v?.ItemPayloads == null) continue;
                    foreach (Items.KmhThingPayload p in v.ItemPayloads)
                    {
                        if (p == null) continue;
                        if (p.Legacy || string.Equals(p.Fidelity, Items.KmhThingPayload.FidelityLegacy, StringComparison.OrdinalIgnoreCase)) legacy++;
                        else if (string.Equals(p.Fidelity, Items.KmhThingPayload.FidelityMetadata, StringComparison.OrdinalIgnoreCase)) partial++;
                        else full++;
                    }
                }
            return (full, partial, legacy);
        }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.TreasuryFile, out PersistedState state) && state?.Vaults != null)
            {
                lock (_lock)
                {
                    _vaults.Clear();
                    _handOvers.Clear();
                    _owedFees.Clear();
                    _feeReturns.Clear();
                    _unsavedLines.Clear();
                    if (state.HandOvers != null) foreach (KeyValuePair<string, long> kv in state.HandOvers) _handOvers[kv.Key] = kv.Value;
                    if (state.OwedFees != null)
                        foreach (KeyValuePair<string, OwedFee> kv in state.OwedFees)
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null && kv.Value.Amount > 0) _owedFees[kv.Key] = kv.Value;
                    if (state.FeeReturns != null)
                        foreach (KeyValuePair<string, OwedFee> kv in state.FeeReturns)
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null && kv.Value.Amount > 0) _feeReturns[kv.Key] = kv.Value;
                    foreach (TreasurySnapshot v in state.Vaults)
                    {
                        if (v == null || string.IsNullOrEmpty(v.OwnerKey)) continue;
                        // Those flags are per-caller, so stored state must not carry them.
                        v.CanDeposit  = false;
                        v.CanWithdraw = false;
                        v.Items       = NormalizeItemKeys(v.Items);   // JSON hands back an ordinal dictionary
                        _vaults[v.OwnerKey] = v;
                    }
                }
                Diagnostics.ServerLog.Diag($"Treasury: loaded {state.Vaults.Count} vault(s) from disk");
                WarnOnUnreconcilableDeposits();
            }
        }

        // Reconcile only acts on Pending rows, so a row in any other state would hold its value invisibly forever.
        private static void WarnOnUnreconcilableDeposits()
        {
            List<string> stuck = new List<string>();
            lock (_lock)
                foreach (TreasurySnapshot v in _vaults.Values)
                    foreach (Dto.PendingDeposit d in v.PendingDeposits)
                        if (d != null && !string.Equals(d.State, Dto.PendingDeposit.StatePending, StringComparison.OrdinalIgnoreCase))
                            stuck.Add($"{d.TxnId} ({v.OwnerKey}, state '{d.State}')");
            if (stuck.Count == 0) return;
            Diagnostics.ServerLog.Error($"Treasury: {stuck.Count} pending deposit(s) are in a state reconcile cannot act on and " +
                                        $"will never resolve: {string.Join(", ", stuck)}. Their value is held out of the spendable " +
                                        "balance indefinitely - resolve them by hand in Treasury.json.");
        }

        // Server-owned, so it is safe to size a payout against - unlike the colony wealth players report about themselves.
        public static long TotalSilverHeld()
        {
            long total = 0;
            lock (_lock)
                foreach (TreasurySnapshot v in _vaults.Values)
                    if (v != null && v.SilverBalance > 0) total += v.SilverBalance;
            return total;
        }

        public static List<(string OwnerKey, long Silver, bool IsGuild)> TopVaults(int n)
        {
            List<(string, long, bool)> all = new List<(string, long, bool)>();
            lock (_lock)
                foreach (KeyValuePair<string, TreasurySnapshot> kv in _vaults)
                    all.Add((kv.Key, kv.Value.SilverBalance, kv.Value.IsGuildOwned));
            all.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            if (n > 0 && all.Count > n) all = all.GetRange(0, n);
            return all;
        }

        // Drops personal AND guild vaults; the caller backs up and logs first.
        public static int ClearAllVaults()
        {
            int n;
            lock (_lock) { n = _vaults.Count; _vaults.Clear(); _handOvers.Clear(); }
            SaveToDisk();
            return n;
        }

        public static long ClearGuildVault(string guildName)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName)) return -1;
            long had;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot v)) return -1;
                had = v.SilverBalance;
                _vaults.Remove(guildName);
            }
            SaveToDisk();
            return had;
        }

        // Removes the guild vault too, so re-creating the guild name cannot resurrect it.
        public static long MoveGuildVaultToPersonal(string guildName, string toUsername)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || string.IsNullOrEmpty(toUsername)) return 0;
            long moved = 0;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot gv)) return 0;
                _vaults.Remove(guildName);
                moved = gv.SilverBalance;
                bool hasPayloads = gv.ItemPayloads != null && gv.ItemPayloads.Count > 0;
                if (moved > 0 || (gv.Items != null && gv.Items.Count > 0) || hasPayloads)
                {
                    TreasurySnapshot pv = GetOrCreateLocked(PersonalKeyFor(toUsername), isGuildOwned: false);
                    pv.SilverBalance    = AddSilver(pv, gv.SilverBalance, "guild vault transfer");
                    pv.LifetimeSilverIn += gv.SilverBalance;
                    if (gv.Items != null)
                        foreach (KeyValuePair<string, int> kv in gv.Items)
                        {
                            pv.Items.TryGetValue(kv.Key, out int cur);
                            pv.Items[kv.Key] = AddQty(pv, cur, kv.Value, kv.Key, "guild vault transfer");
                        }
                    // Payloads move separately from Items, or gear with state would be lost on disband.
                    if (hasPayloads)
                        foreach (Items.KmhThingPayload p in gv.ItemPayloads)
                            if (p != null) AddPayloadLocked(pv, p);
                    RecordTransactionLocked(pv, toUsername, TreasuryTransaction.KindDeposit,
                        moved, "", $"guild '{guildName}' vault returned on disband");
                }
            }
            // The guild is already gone on disk, so an unwritten hand-back would leave its vault orphaned after a crash.
            if (!SaveToDisk())
            {
                Diagnostics.ServerLog.Error($"Treasury: guild '{guildName}' vault hand-back to {toUsername} is not on disk yet - value moves are refused until a save succeeds. If the server stops first, 'kmh orphans' lists the vault for release.");
                Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(true);
            }
            if (moved > 0)
                Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
                { OwnerKey = PersonalKeyFor(toUsername), IsGuildOwned = false, Reason = "guild disband refund" });
            return moved;
        }

        // A sale's guild tax, once per marker. If the guild it was taken for no longer exists (or the name is now another
        // guild's), it goes back to the payer it was taken from - logged - rather than rebuild or enrich a vault.
        public static bool SettleGuildTaxOnce(string guildName, string identity, string marker, long amount, string payer, string note)
        {
            if (amount <= 0) return true;
            int tax = (int)Math.Min(int.MaxValue, amount);
            string id = string.IsNullOrEmpty(identity) ? null : identity;
            if (Guilds.GuildIdentity.IsLive(guildName, id) || string.IsNullOrEmpty(marker) || string.IsNullOrEmpty(payer))
                return DepositGuildSilverOnce(guildName, marker, tax, payer, note: note, identity: id);
            bool back = DepositEscrowOnce(payer, marker + "-returned", tax, null, null, note + $" - returned, guild '{guildName}' disbanded");
            if (back) Diagnostics.ServerLog.Info($"Treasury: {tax} silver of guild tax went back to {payer} - guild '{guildName}' no longer exists ({note}).");
            return back;
        }

        internal const string OrphanVaultPrefix = "_orphaned:";

        // Before a guild name is (re)used: a vault or pending donation left by an earlier guild of that name is set
        // aside, never inherited. Credits to a dead name are refused, so nothing can re-create it before the new guild commits.
        public static int PrepareGuildName(string guildName)
        {
            if (string.IsNullOrEmpty(guildName) || IsPersonalKey(guildName) || Guilds.GuildIdentity.IsLive(guildName)) return 0;
            int set = 0;
            lock (_lock)
            {
                if (!Guilds.GuildIdentity.IsLive(guildName) && _vaults.TryGetValue(guildName, out TreasurySnapshot old))
                {
                    string key = OrphanVaultPrefix + guildName + ":" + DateTime.UtcNow.Ticks;
                    _vaults.Remove(guildName);
                    old.OwnerKey = key;
                    _vaults[key] = old;
                    set++;
                    Diagnostics.ServerLog.Warn($"Treasury: guild name '{guildName}' is being reused - the vault an earlier guild of that name left ({old.SilverBalance}s, {old.Items?.Count ?? 0} item kind(s)) is set aside as '{key}' for an admin, not inherited.");
                }
            }
            if (set > 0 && !SaveToDisk()) Maintenance.KmhMaintenanceGate.SetPersistenceDegraded(true);
            return set + RevertPendingGuildDonations(guildName);
        }

        // Guild vaults no live guild owns: set aside on reuse, or left by a disband the disk never took.
        public static List<(string Key, long Silver, int ItemKinds, int Payloads)> OrphanVaults()
        {
            var outp = new List<(string, long, int, int)>();
            lock (_lock)
                foreach (KeyValuePair<string, TreasurySnapshot> kv in _vaults)
                {
                    if (kv.Value == null || IsPersonalKey(kv.Key)) continue;
                    if (!kv.Key.StartsWith(OrphanVaultPrefix, StringComparison.Ordinal) && Guilds.GuildIdentity.IsLive(kv.Key)) continue;
                    outp.Add((kv.Key, kv.Value.SilverBalance, kv.Value.Items?.Count ?? 0, kv.Value.ItemPayloads?.Count ?? 0));
                }
            return outp;
        }

        // Admin-only and explicit: an orphaned vault goes to the player the admin names, whole, in one write.
        public static bool ReleaseOrphanVault(string key, string toUsername, string actor, out string message)
        {
            message = null;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(toUsername) || IsPersonalKey(key)) { message = "Usage: kmh treasury release-orphan <vault> <player>"; return false; }
            lock (_lock)
            {
                if (!_vaults.TryGetValue(key, out TreasurySnapshot gv)) { message = $"No vault '{key}'."; return false; }
                if (!key.StartsWith(OrphanVaultPrefix, StringComparison.Ordinal) && Guilds.GuildIdentity.IsLive(key))
                { message = $"'{key}' belongs to a live guild - it is not orphaned."; return false; }
                TreasurySnapshot before = RollbackPointLocked(PersonalKeyFor(toUsername));
                var others = new Dictionary<string, TreasurySnapshot> { { key, JsonFileStore.FromJson<TreasurySnapshot>(JsonFileStore.ToJson(gv)) } };
                _vaults.Remove(key);
                TreasurySnapshot pv = GetOrCreateLocked(PersonalKeyFor(toUsername), isGuildOwned: false);
                pv.SilverBalance    = AddSilver(pv, gv.SilverBalance, "orphan vault release");
                pv.LifetimeSilverIn += gv.SilverBalance;
                if (gv.Items != null)
                    foreach (KeyValuePair<string, int> kv in gv.Items)
                    {
                        pv.Items.TryGetValue(kv.Key, out int cur);
                        pv.Items[kv.Key] = AddQty(pv, cur, kv.Value, kv.Key, "orphan vault release");
                    }
                if (gv.ItemPayloads != null)
                    foreach (Items.KmhThingPayload p in gv.ItemPayloads)
                        if (p != null) AddPayloadLocked(pv, p);
                RecordTransactionLocked(pv, toUsername, TreasuryTransaction.KindDeposit, gv.SilverBalance, "", $"orphaned vault '{key}' released by {actor}");
                if (!CommitLocked(PersonalKeyFor(toUsername), before, others)) { message = "The server could not save that - nothing moved."; return false; }
                message = $"Released '{key}' ({gv.SilverBalance}s, {gv.Items?.Count ?? 0} item kind(s), {gv.ItemPayloads?.Count ?? 0} payload(s)) to {toUsername} - by {actor}.";
            }
            Diagnostics.ServerLog.Info("Treasury: " + message);
            return true;
        }

        // False means in memory only; every value path treats that as a refusal, not a move a restart would undo.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq, upTo;
            lock (_lock)
            {
                state.Vaults = KmhDeepCopy.OfList(_vaults.Values);
                state.HandOvers = new Dictionary<string, long>(_handOvers, StringComparer.Ordinal);
                foreach (KeyValuePair<string, OwedFee> kv in _owedFees)
                    state.OwedFees[kv.Key] = new OwedFee { Payer = kv.Value.Payer, Amount = kv.Value.Amount, Note = kv.Value.Note };
                foreach (KeyValuePair<string, OwedFee> kv in _feeReturns)
                    state.FeeReturns[kv.Key] = new OwedFee { Payer = kv.Value.Payer, Amount = kv.Value.Amount, Note = kv.Value.Note };
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
                upTo = _lineSeq;
            }
            if (!JsonFileStore.Save(KmhDataPaths.TreasuryFile, state, seq)) return false;
            // The audit line lands with the balance it describes: on the flusher's 2 s beat, a kill in between kept the
            // balance and lost the line.
            lock (_lock) ReleaseLinesLocked(upTo);
            Persistence.TransactionLedger.Flush();
            return true;
        }

        // The state to put back if the write fails; null means there was no vault, so a failed write must leave none behind either.
        // Every commit takes one before it records a line, so the mark here is where a refused commit's lines begin.
        private static TreasurySnapshot RollbackPointLocked(string ownerKey)
        {
            _commitMark = _lineSeq;
            return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v)
                 ? JsonFileStore.FromJson<TreasurySnapshot>(JsonFileStore.ToJson(v))
                 : null;
        }

        // Called inside _lock once the mutation is applied; the rollback copy costs a fraction of the save it guards.
        private static bool CommitLocked(string ownerKey, TreasurySnapshot before)
            => CommitLocked(ownerKey, before, null);

        // others are the further vaults this change touched. Restoring only the owner's left a donation's guild
        // credited while the donation itself went back to pending, ready to be committed or refunded again.
        private static bool CommitLocked(string ownerKey, TreasurySnapshot before, Dictionary<string, TreasurySnapshot> others)
        {
            if (SaveToDisk()) return true;
            if (before == null) _vaults.Remove(ownerKey);
            else                _vaults[ownerKey] = before;
            if (others != null)
                foreach (KeyValuePair<string, TreasurySnapshot> kv in others)
                {
                    if (kv.Value == null) _vaults.Remove(kv.Key);
                    else                  _vaults[kv.Key] = kv.Value;
                }
            _unsavedLines.RemoveAll(l => l.Seq > _commitMark);
            Diagnostics.ServerLog.Warn($"Treasury: rolled back an unsaved change to {ownerKey} - the request was refused rather than acknowledged.");
            return false;
        }

        // A null entry means the vault did not exist, so a rollback removes the one the commit created.
        private static Dictionary<string, TreasurySnapshot> GuildRollbackPointsLocked(TreasurySnapshot v, Func<Dto.PendingDeposit, bool> willCommit)
        {
            Dictionary<string, TreasurySnapshot> points = null;
            foreach (Dto.PendingDeposit d in v.PendingDeposits)
            {
                if (d.State != Dto.PendingDeposit.StatePending || d.Kind != Dto.PendingDeposit.KindGuildDonate) continue;
                if (string.IsNullOrEmpty(d.GuildName) || !willCommit(d)) continue;
                points = points ?? new Dictionary<string, TreasurySnapshot>(StringComparer.OrdinalIgnoreCase);
                if (!points.ContainsKey(d.GuildName)) points[d.GuildName] = RollbackPointLocked(d.GuildName);
            }
            return points;
        }

        private class PersistedState
        {
            public List<TreasurySnapshot> Vaults { get; set; } = new List<TreasurySnapshot>();
            // Absent in older files, which had no quest hand-over in flight.
            public Dictionary<string, long> HandOvers { get; set; } = new Dictionary<string, long>();
            // Absent in older files, which credited fees with nothing on disk naming them.
            public Dictionary<string, OwedFee> OwedFees { get; set; } = new Dictionary<string, OwedFee>();
            // Apart from OwedFees, so an older build that reads this file ignores a return rather than crediting it.
            public Dictionary<string, OwedFee> FeeReturns { get; set; } = new Dictionary<string, OwedFee>();
        }

        // A fee a vault commit took for the house pool, written down in that same commit and cleared only once the pool's
        // own save holds it. Two files cannot be saved together, so this is what lets a restart finish the credit - once,
        // because the pool keys the credit on the same marker.
        internal sealed class OwedFee
        {
            public string Payer  { get; set; } = "";
            public long   Amount { get; set; }
            public string Note   { get; set; } = "";
        }

        private static readonly Dictionary<string, OwedFee> _owedFees = new Dictionary<string, OwedFee>(StringComparer.Ordinal);
        // The same journey backwards: a fee the pool gives back for a deposit an older save reversed, debited on its marker.
        private static readonly Dictionary<string, OwedFee> _feeReturns = new Dictionary<string, OwedFee>(StringComparer.Ordinal);

        // Fires between a fee's steps: vault-saved before the pool credit, pool-saved before the clear, return-* for a return.
        internal static Action<string> AtFeePhaseForTest;

        internal static string FeeMarkerFor(string ownerKey, string txnId) => "fee:" + (ownerKey ?? "") + ":" + (txnId ?? "");
        internal static string FeeReturnMarkerFor(string ownerKey, string txnId) => "unfee:" + (ownerKey ?? "") + ":" + (txnId ?? "");

        // Caller holds _lock and saves next; a failed save must forget what this returned (ForgetFeesLocked).
        private static string OweFeeLocked(string marker, long amount, string payer, string note)
        {
            if (amount <= 0 || string.IsNullOrEmpty(marker)) return null;
            _owedFees[marker] = new OwedFee { Payer = payer ?? "", Amount = amount, Note = note ?? "" };
            return marker;
        }

        // Caller holds _lock and saves next, like OweFeeLocked.
        private static string OweFeeReturnLocked(string marker, long amount, string payer, string note)
        {
            if (amount <= 0 || string.IsNullOrEmpty(marker)) return null;
            _feeReturns[marker] = new OwedFee { Payer = payer ?? "", Amount = amount, Note = note ?? "" };
            return marker;
        }

        private static void ForgetFeesLocked(List<string> markers)
        {
            if (markers != null) foreach (string m in markers) if (m != null) _owedFees.Remove(m);
        }

        // For a fee taken in another store's write (a withdrawal row): written down here when the pool would not take it.
        public static bool OweHouseFee(string marker, long amount, string payer, string note)
        {
            if (amount <= 0 || string.IsNullOrEmpty(marker)) return true;
            lock (_lock) OweFeeLocked(marker, amount, payer, note);
            if (SaveToDisk()) return true;
            lock (_lock) _owedFees.Remove(marker);
            return false;
        }

        // Each owed fee goes to the pool on its own marker and is then cleared. A pool that already holds the marker - a
        // crash between its save and the clear - credits nothing and is cleared too. A refusal stays owed for the sweep.
        public static int SettleOwedFees(IEnumerable<string> only = null)
        {
            var due = new List<(string Marker, OwedFee Fee, bool Back)>();
            lock (_lock)
            {
                if (only == null)
                {
                    foreach (KeyValuePair<string, OwedFee> kv in _owedFees) due.Add((kv.Key, kv.Value, false));
                    foreach (KeyValuePair<string, OwedFee> kv in _feeReturns) due.Add((kv.Key, kv.Value, true));
                }
                else
                    foreach (string m in only)
                    {
                        if (m == null) continue;
                        if (_owedFees.TryGetValue(m, out OwedFee f)) due.Add((m, f, false));
                        else if (_feeReturns.TryGetValue(m, out OwedFee g)) due.Add((m, g, true));
                    }
            }
            int paid = 0;
            foreach ((string marker, OwedFee fee, bool back) in due)
            {
                AtFeePhaseForTest?.Invoke(back ? "return-vault-saved" : "vault-saved");
                bool done = back ? Marketplace.MarketplaceStore.TryDebitHousePoolOnce(marker, fee.Amount, fee.Note)
                                 : Marketplace.MarketplaceStore.TryCreditHousePoolOnce(marker, fee.Amount, fee.Note);
                if (!done) continue;
                AtFeePhaseForTest?.Invoke(back ? "return-pool-saved" : "pool-saved");
                lock (_lock) { if (back) _feeReturns.Remove(marker); else _owedFees.Remove(marker); }
                paid++;
            }
            // A marker that outlives a failed clear is harmless: the pool already holds it, so the retry moves nothing.
            if (paid > 0 && !SaveToDisk())
                Diagnostics.ServerLog.Warn($"Treasury: {paid} house-pool fee(s) were settled but their records could not be cleared yet - a retry moves nothing.");
            return paid;
        }

        internal static int OwedFeeCountForTest(string markerPrefix)
        {
            int n = 0;
            lock (_lock)
            {
                foreach (string m in _owedFees.Keys) if (m.StartsWith(markerPrefix ?? "", StringComparison.Ordinal)) n++;
                foreach (string m in _feeReturns.Keys) if (m.StartsWith(markerPrefix ?? "", StringComparison.Ordinal)) n++;
            }
            return n;
        }

        internal static void ForgetOwedFeesForTest(string markerPrefix)
        {
            lock (_lock)
            {
                foreach (string m in new List<string>(_owedFees.Keys))
                    if (m.StartsWith(markerPrefix ?? "", StringComparison.Ordinal)) _owedFees.Remove(m);
                foreach (string m in new List<string>(_feeReturns.Keys))
                    if (m.StartsWith(markerPrefix ?? "", StringComparison.Ordinal)) _feeReturns.Remove(m);
            }
        }
    }
}
