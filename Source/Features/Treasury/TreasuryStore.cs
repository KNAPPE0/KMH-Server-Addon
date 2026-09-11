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
        private static readonly Dictionary<string, TreasurySnapshot> _vaults
            = new Dictionary<string, TreasurySnapshot>(StringComparer.OrdinalIgnoreCase);

        public const int MaxTransactionLogEntries = 100;

        public static string PersonalKeyFor(string username)
            => "_personal:" + (username ?? "").ToLowerInvariant();

        // The inverse, for a listener that only has the key a change was announced under.
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
            if (string.IsNullOrEmpty(guildName)) return 0;
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

        // This player's own vault: silver plus the trusted value of both item shapes.
        public static long PersonalVaultValue(string username)
        {
            string key = PersonalKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(key, out TreasurySnapshot v)) return 0;
                long total = v.SilverBalance;
                if (v.Items != null)
                    foreach (System.Collections.Generic.KeyValuePair<string, int> kv in v.Items)
                        total += Items.KmhItemSafety.GetTrustedMarketValue((kv.Key ?? "").Split('|')[0]) * kv.Value;
                if (v.ItemPayloads != null)
                    foreach (Items.KmhThingPayload p in v.ItemPayloads)
                        total += Items.KmhItemSafety.GetTrustedMarketValue(p?.DefName ?? "") * System.Math.Max(1, p?.StackCount ?? 1);
                return total;
            }
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

        // The contributor is recorded in the log, but the vault itself belongs to the guild.
        public static bool DepositGuildSilver(string guildName, int amount, string contributorUsername, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = GetOrCreateLocked(guildName, isGuildOwned: true);
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
                                                  string contributorUsername, string note = "")
        {
            if (string.IsNullOrEmpty(marker)) return DepositGuildSilver(guildName, amount, contributorUsername, note);
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = GetOrCreateLocked(guildName, isGuildOwned: true);
                if (IsKnownTxnLocked(v, marker)) return true;
                v.SilverBalance     = AddSilver(v, amount, "guild deposit");
                v.LifetimeSilverIn += amount;
                RecordTransactionLocked(v, contributorUsername, TreasuryTransaction.KindDeposit, amount, "", note);
                v.RecentCommittedTxns.Add(marker);
                while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);
                if (!CommitLocked(guildName, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = guildName, IsGuildOwned = true, Reason = note ?? "" });
            return true;
        }

        public static bool WithdrawGuildSilver(string guildName, int amount, string actorUsername, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
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
                v.SilverBalance     = AddSilver(v, amount, "deposit");
                v.LifetimeSilverIn  += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, amount, "", note);
                if (!CommitLocked(ownerKey, before)) return false;
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
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
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        public static bool DepositItem(string username, string itemDefName, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) == false);
                if (!v.Items.TryGetValue(itemDefName, out int cur)) cur = 0;
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
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        // Records no contribution, or a member could farm standing simply by owning a productive guild site.
        public static bool DepositGuildItem(string guildName, string itemDefName, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(itemDefName) || qty <= 0) return false;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(guildName);
                TreasurySnapshot v = GetOrCreateLocked(guildName, isGuildOwned: true);
                if (!v.Items.TryGetValue(itemDefName, out int cur)) cur = 0;
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
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
            return true;
        }

        // Item form of WithdrawSilverForTxn: the marker lands in the same commit as the stack, so recovery never refunds goods still in the vault.
        public static bool WithdrawItemForTxn(string username, string itemDefName, int qty, string marker, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(itemDefName) || qty <= 0
                || string.IsNullOrEmpty(marker)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsKnownTxnLocked(v, marker)) return true;
                if (!v.Items.TryGetValue(itemDefName, out int cur) || cur < qty) return false;
                int next = cur - qty;
                if (next <= 0) v.Items.Remove(itemDefName);
                else           v.Items[itemDefName] = next;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, qty, itemDefName, note);
                v.RecentCommittedTxns.Add(marker);
                while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);
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
                if (_vaults.TryGetValue(ownerKey, out TreasurySnapshot known) && IsKnownTxnLocked(known, marker))
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
                if (IsKnownTxnLocked(v, marker)) return true;
                if (v.SilverBalance < amount) return false;
                v.SilverBalance     -= amount;
                v.LifetimeSilverOut += amount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, amount, "", note);
                v.RecentCommittedTxns.Add(marker);
                while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);
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
                return _vaults.TryGetValue(ownerKey, out TreasurySnapshot v) && IsKnownTxnLocked(v, marker);
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
                if (IsKnownTxnLocked(v, txnId)) return true;

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

                v.RecentCommittedTxns.Add(txnId);
                while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);
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
                int remaining = qty;

                // One displayed row represents every stack that looks alike, so the withdraw drains the whole group.
                string wantGroup = null;
                foreach (Items.KmhThingPayload e in v.ItemPayloads)
                    if (string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal))
                    { wantGroup = Items.KmhItemSafety.DisplayKey(e); break; }

                foreach (Items.KmhThingPayload e in new List<Items.KmhThingPayload>(v.ItemPayloads))
                {
                    if (remaining <= 0) break;
                    bool sameRow   = string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal);
                    bool sameGroup = wantGroup != null && Items.KmhItemSafety.DisplayKey(e) == wantGroup;
                    if (!sameRow && !sameGroup) continue;

                    switch (Items.KmhItemService.PlanTake(e.StackCount, remaining, Items.KmhPayloadEscrow.IsSplittable(e)))
                    {
                        case Items.KmhItemService.TakeKind.Whole:
                            granted.Add(e); v.ItemPayloads.Remove(e); remaining -= e.StackCount; break;
                        case Items.KmhItemService.TakeKind.Split:
                            granted.Add(Clone(e, remaining)); e.StackCount -= remaining; remaining = 0; break;
                    }
                }
                if (granted.Count == 0) return null;
                int taken = 0; foreach (var g in granted) taken += g.StackCount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, taken,
                    Items.KmhItemSafety.DescribeStateForLedger(granted[0]), note);
                if (!string.IsNullOrEmpty(marker))
                {
                    v.RecentCommittedTxns.Add(marker);
                    while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);

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

        // Safe to fail: a durable row now names the take, so recovery sees the value accounted for and drops the entry without paying.
        public static bool ClearPendingTake(string username, string takeMarker)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(takeMarker)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return true;
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                int removed = v.PendingTakes.RemoveAll(
                    p => p != null && string.Equals(p.TakeMarker, takeMarker, StringComparison.Ordinal));
                if (removed == 0) return true;
                return CommitLocked(ownerKey, before);
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

        private static List<Items.KmhThingPayload> StripBlobs(List<Items.KmhThingPayload> src)
        {
            List<Items.KmhThingPayload> outList = new List<Items.KmhThingPayload>();
            if (src == null) return outList;
            foreach (Items.KmhThingPayload p in src)
                outList.Add(Items.KmhItemService.CloneWithoutBlob(p));
            return outList;
        }

        // Display only: a modded stack's fingerprint includes its blob, so identical-looking stacks would each be a row.
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
                outList.Add(new Dto.PendingDeposit
                {
                    TxnId = d.TxnId, Username = d.Username, CreatedUtcTicks = d.CreatedUtcTicks, State = d.State,
                    Kind = d.Kind, Silver = d.Silver, ItemDefName = d.ItemDefName, Qty = d.Qty, Note = d.Note,
                    GuildName = d.GuildName, Payloads = StripBlobs(d.Payloads),
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

        // Idempotent, and skips the save entirely when nothing merged.
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

        // Empty when nothing qualifies.
        public static List<Items.KmhThingPayload> TryWithdrawMatchingPayloads(string username, string defName,
            string requiredStuff, int minQuality, bool allowTainted, bool allowDamaged, int qty, string note)
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
                    if (!CommitLocked(ownerKey, before)) return new List<Items.KmhThingPayload>();
                }
            }
            if (granted.Count > 0) RaiseChanged(ownerKey, note);
            return granted;
        }

        private static void RaiseChanged(string ownerKey, string note)
            => Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
            { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });

        // A deposit stays pending until the client confirms its goods removal saved, so value is never in both places.
        private const int MaxRecentCommittedTxns = 256;

        // Unconfirmed deposits awaiting the player's own durable save.
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
            if (string.IsNullOrEmpty(username) || d == null || string.IsNullOrEmpty(d.TxnId)) return false;
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
        public static bool BeginPendingGuildDonation(string username, string guildName, int amount, string txnId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(guildName) || amount <= 0 || string.IsNullOrEmpty(txnId))
            { reason = "Invalid donation request."; return false; }
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
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
                    Silver = amount, GuildName = guildName, Note = $"donation to guild '{guildName}'",
                    State = Dto.PendingDeposit.StatePending, CreatedUtcTicks = DateTime.UtcNow.Ticks,
                });
                if (!CommitLocked(ownerKey, before)) { reason = "The server could not record that donation - nothing was taken. Try again shortly."; return false; }
            }
            RaiseChanged(ownerKey, "donation_pending");
            return true;
        }

        // Total unconfirmed donation silver headed for a guild (counts toward the vault deposit cap).
        public static long PendingGuildDonationTotal(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
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


        // Does a vault row exist for this user? Read-only - unlike the snapshot path, it never creates one.
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

        // Detection only: nothing is reverted or blocked on the strength of it.
        public static long NoteSaveGeneration(string username, long generation)
        {
            if (string.IsNullOrEmpty(username) || generation <= 0) return 0;
            string ownerKey = ResolveOwnerKeyFor(username);
            long seen;
            lock (_lock)
            {
                // No vault means no committed deposits, so there is nothing to protect and nothing to create.
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return 0;
                seen = v.LastSaveGeneration;
                if (generation > seen) v.LastSaveGeneration = generation;
            }
            return seen;
        }

        // Highest save generation recorded for a player, for `kmh audit-player`.
        public static long LastSaveGenerationOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            string key = ResolveOwnerKeyFor(username);
            lock (_lock) { return _vaults.TryGetValue(key, out TreasurySnapshot v) ? v.LastSaveGeneration : 0; }
        }

        // Idempotent: unknown or already-committed ids are ignored.
        public static int ConfirmDeposits(string username, IEnumerable<string> txnIds)
        {
            if (string.IsNullOrEmpty(username) || txnIds == null) return 0;
            HashSet<string> want = new HashSet<string>(txnIds, StringComparer.Ordinal);
            if (want.Count == 0) return 0;
            string ownerKey = ResolveOwnerKeyFor(username);
            int committed = 0; long feeToHouse = 0;
            List<Dto.PendingDeposit> donations = null;
            lock (_lock)
            {
                TreasurySnapshot before = RollbackPointLocked(ownerKey);
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    if (!want.Contains(d.TxnId)) continue;
                    feeToHouse += d.Fee;
                    CommitPendingLocked(v, d, ref donations);
                    committed++;
                }
                // The fee follows the commit, so a deposit that stayed pending is never charged for.
                if (committed > 0 && !CommitLocked(ownerKey, before)) return 0;
            }
            if (committed > 0) RaiseChanged(ownerKey, "deposit_confirmed");
            if (feeToHouse > 0) Marketplace.MarketplaceStore.CreditFeeToHousePool(username, feeToHouse, $"treasury deposit fee ({username})");
            NotifyDonationsFinalized(donations);
            return committed;
        }

        // Post-commit guild bookkeeping for finalized donations (outside the vault lock - GuildStore has its own).
        private static void NotifyDonationsFinalized(List<Dto.PendingDeposit> donations)
        {
            if (donations == null) return;
            foreach (Dto.PendingDeposit d in donations)
                Guilds.GuildStore.FinalizeDonation(d.Username, d.GuildName, d.Silver);
        }

        // unsavedGoodsGone is left alone because reverting it would destroy goods the client has already removed.
        public static (int committed, int reverted) ReconcileDeposits(string username, IEnumerable<string> committed, IEnumerable<string> unsavedGoodsGone, int graceSeconds)
        {
            int didCommit = 0, didRevert = 0; long feeToHouse = 0;
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
                foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    switch (ReconcileVerdictFor(d.TxnId, d.CreatedUtcTicks, have, keep, cutoff))
                    {
                        case ReconcileVerdict.Commit: feeToHouse += d.Fee; CommitPendingLocked(v, d, ref donations); didCommit++; break;
                        case ReconcileVerdict.Revert:
                            RevertPendingLocked(v, d);
                            didRevert++;
                            Diagnostics.ServerLog.Warn($"Treasury: reverted pending deposit {d.TxnId} for {username} " +
                                $"({DescribePending(d)}) - client reported it not durably saved (local rollback).");
                            break;
                    }
                }
                if ((didCommit > 0 || didRevert > 0) && !CommitLocked(ownerKey, before)) return (0, 0);
            }
            if (didCommit > 0 || didRevert > 0) RaiseChanged(ownerKey, "deposit_reconciled");
            if (feeToHouse > 0) Marketplace.MarketplaceStore.CreditFeeToHousePool(username, feeToHouse, $"treasury deposit fee ({username})");
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
        private static void CommitPendingLocked(TreasurySnapshot v, Dto.PendingDeposit d, ref List<Dto.PendingDeposit> donations)
        {
            switch (d.Kind)
            {
                case Dto.PendingDeposit.KindGuildDonate:
                    TreasurySnapshot gv = GetOrCreateLocked(d.GuildName, isGuildOwned: true);
                    gv.SilverBalance     = AddSilver(gv, d.Silver, "guild donation commit");
                    gv.LifetimeSilverIn += d.Silver;
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
            v.RecentCommittedTxns.Add(d.TxnId);
            while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);

            // Blob-stripped, since reversal removes vault payloads by fingerprint rather than by saved XML.
            Dto.PendingDeposit rec = StripPendingBlobs(new List<Dto.PendingDeposit> { d })[0];
            rec.State = Dto.PendingDeposit.StateCommitted;
            rec.CommittedUtcTicks = DateTime.UtcNow.Ticks;
            v.CommittedDeposits.Add(rec);
            while (v.CommittedDeposits.Count > MaxRecentCommittedTxns) v.CommittedDeposits.RemoveAt(0);
        }

        // Guild donations are reported rather than reversed, because other members may already have spent them.
        public static (int reversed, int flagged) ReverseRolledBackCommits(
            string username, IEnumerable<string> clientHas, IEnumerable<string> unsavedGoodsGone, int graceSeconds)
        {
            if (string.IsNullOrEmpty(username)) return (0, 0);
            HashSet<string> have = new HashSet<string>(clientHas ?? new List<string>(), StringComparer.Ordinal);
            HashSet<string> keep = new HashSet<string>(unsavedGoodsGone ?? new List<string>(), StringComparer.Ordinal);
            string ownerKey = ResolveOwnerKeyFor(username);
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(Math.Max(0, graceSeconds)).Ticks;
            int reversed = 0, flagged = 0;
            List<string> report = new List<string>();

            lock (_lock)
            {
                // No vault means nothing was ever committed, so there is nothing to undo.
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v)) return (0, 0);
                foreach (Dto.PendingDeposit c in new List<Dto.PendingDeposit>(v.CommittedDeposits))
                {
                    if (!ShouldUndoCommitted(c.TxnId, c.CommittedUtcTicks, have, keep, cutoff)) continue;
                    if (c.Kind == Dto.PendingDeposit.KindGuildDonate)
                    {
                        flagged++;
                        report.Add($"{c.TxnId} ({DescribePending(c)}) to guild '{c.GuildName}' - NOT auto-reversed");
                        v.CommittedDeposits.Remove(c);
                        continue;
                    }
                    string shortfall = ReverseCommittedLocked(v, c);
                    v.CommittedDeposits.Remove(c);
                    v.RecentCommittedTxns.Remove(c.TxnId);
                    reversed++;
                    report.Add($"{c.TxnId} ({DescribePending(c)}){shortfall}");
                }
            }

            if (reversed > 0 || flagged > 0)
            {
                SaveToDisk();
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

        // Restore-preview / audit: total silver + item units sitting in pending (never spendable) across a vault.
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
                if (!CommitLocked(ownerKey, before)) { consumed.Clear(); return false; }
            }
            Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", System.StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });
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

            // Safe under _lock because the enqueue is lock-free and the file write happens off-thread.
            Persistence.TransactionLedger.Record(v.OwnerKey, username, kind, amount, itemDefName, note);
        }


        // Returns the silver it held, or -1 when there was no vault. Guild vaults are untouched.
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
                Diagnostics.ServerLog.Info($"Treasury: loaded {state.Vaults.Count} vault(s) from disk");
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

        // Season reset: drop every vault - personal AND guild. Caller backs up + logs.
        public static int ClearAllVaults()
        {
            int n;
            lock (_lock) { n = _vaults.Count; _vaults.Clear(); }
            SaveToDisk();
            return n;
        }

        // Anti-shelter reset: drop a guild's vault entirely. Returns the silver it held, or -1 if there was none.
        public static long ClearGuildVault(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return -1;
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
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(toUsername)) return 0;
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
            SaveToDisk();
            if (moved > 0)
                Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
                { OwnerKey = PersonalKeyFor(toUsername), IsGuildOwned = false, Reason = "guild disband refund" });
            return moved;
        }

        // False means the change is in memory only; every value path treats that as a refusal rather than acknowledge a move that restart undoes.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Vaults = new List<TreasurySnapshot>(_vaults.Values);
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
            }
            return JsonFileStore.Save(KmhDataPaths.TreasuryFile, state, seq);
        }

        // The state to put back if the write fails; null means there was no vault, so a failed write must leave none behind either.
        private static TreasurySnapshot RollbackPointLocked(string ownerKey)
            => _vaults.TryGetValue(ownerKey, out TreasurySnapshot v)
             ? JsonFileStore.FromJson<TreasurySnapshot>(JsonFileStore.ToJson(v))
             : null;

        // Called inside _lock once the mutation is applied; the rollback copy costs a fraction of the save it guards.
        private static bool CommitLocked(string ownerKey, TreasurySnapshot before)
        {
            if (SaveToDisk()) return true;
            if (before == null) _vaults.Remove(ownerKey);
            else                _vaults[ownerKey] = before;
            Diagnostics.ServerLog.Warn($"Treasury: rolled back an unsaved change to {ownerKey} - the request was refused rather than acknowledged.");
            return false;
        }

        private class PersistedState
        {
            public List<TreasurySnapshot> Vaults { get; set; } = new List<TreasurySnapshot>();
        }
    }
}
