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

        // Spendable personal-vault silver for a user (0 if no vault yet). Used by the P7 treasury-cap check.
        public static long GetPersonalSilver(string username)
        {
            string key = PersonalKeyFor(username);
            lock (_lock) { return _vaults.TryGetValue(key, out TreasurySnapshot v) ? v.SilverBalance : 0; }
        }

        // Confirmed (spendable) personal off-map value: silver + item value (via the trusted catalog). Excludes pending
        // deposits (unconfirmed). For the "KMH Effective Wealth" audit - this value is NOT yet in raid/threat scaling.
        public static long PersonalOffMapValue(string username)
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

        // Dry-run summary for admin cleanup: (silver, item stacks, pending deposits) in a user's personal vault.
        public static (long silver, int itemStacks, int pending) PersonalSummary(string username)
        {
            string key = PersonalKeyFor(username);
            lock (_lock)
            {
                if (!_vaults.TryGetValue(key, out TreasurySnapshot v)) return (0, 0, 0);
                return (v.SilverBalance, (v.Items?.Count ?? 0) + (v.ItemPayloads?.Count ?? 0), v.PendingDeposits?.Count ?? 0);
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
                    // Payload metadata for display; ScribeXml stripped so the snapshot stays under the frame cap (the
                    // blob rides the grant on withdraw instead).
                    ItemPayloads       = StripBlobs(live.ItemPayloads),
                    // Pending (not-yet-spendable) deposits for the client's "pending until saved" display; blobs
                    // stripped. RecentCommittedTxns is intentionally NOT copied - it stays server-side only.
                    PendingDeposits    = StripPendingBlobs(live.PendingDeposits),
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

        // --- state-preserving payload items (complex Things) ---

        // Store a captured item payload. Merges into an existing stack only when RimWorld itself could (metadata-only,
        // identical state); blob-carrying instances are always kept separate so nothing merges unsafely.
        public static bool DepositPayload(string username, Items.KmhThingPayload payload, string note = "")
        {
            if (string.IsNullOrEmpty(username) || payload == null) return false;
            if (!Items.KmhItemSafety.ValidatePayload(payload)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                Items.KmhThingPayload mergeInto = null;
                if (string.IsNullOrEmpty(payload.ScribeXml))
                    foreach (Items.KmhThingPayload e in v.ItemPayloads)
                        if (Items.KmhItemSafety.CanSafelyMerge(e, payload)) { mergeInto = e; break; }
                if (mergeInto != null) mergeInto.StackCount += payload.StackCount;
                else                   v.ItemPayloads.Add(payload);
                RecordTransactionLocked(v, username, TreasuryTransaction.KindDeposit, payload.StackCount,
                    Items.KmhItemSafety.DescribeStateForLedger(payload), note);
            }
            SaveToDisk();
            RaiseChanged(ownerKey, note);
            return true;
        }

        // Withdraw up to `qty` units of a fingerprint. Returns the payloads to materialize (with their blobs), or null
        // if none match. Blob instances are atomic (whole entry); metadata stacks split by count.
        public static List<Items.KmhThingPayload> WithdrawPayloads(string username, string fingerprint, int qty, string note = "")
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(fingerprint) || qty <= 0) return null;
            string ownerKey = ResolveOwnerKeyFor(username);
            List<Items.KmhThingPayload> granted = new List<Items.KmhThingPayload>();
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                int remaining = qty;
                foreach (Items.KmhThingPayload e in new List<Items.KmhThingPayload>(v.ItemPayloads))
                {
                    if (remaining <= 0) break;
                    if (!string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal)) continue;
                    if (e.StackCount <= remaining)
                    {
                        granted.Add(e); v.ItemPayloads.Remove(e); remaining -= e.StackCount;
                    }
                    else if (string.IsNullOrEmpty(e.ScribeXml))   // metadata stack: safe to split
                    {
                        granted.Add(Clone(e, remaining)); e.StackCount -= remaining; remaining = 0;
                    }
                    // blob entry larger than remaining: atomic, can't split - leave it (skip)
                }
                if (granted.Count == 0) return null;
                int taken = 0; foreach (var g in granted) taken += g.StackCount;
                RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, taken,
                    Items.KmhItemSafety.DescribeStateForLedger(granted[0]), note);
            }
            SaveToDisk();
            RaiseChanged(ownerKey, note);
            return granted;
        }

        // Wire copy of payloads with the deep blob removed (metadata only). has_deep encoded via Fidelity stays.
        private static List<Items.KmhThingPayload> StripBlobs(List<Items.KmhThingPayload> src)
        {
            List<Items.KmhThingPayload> outList = new List<Items.KmhThingPayload>();
            if (src == null) return outList;
            foreach (Items.KmhThingPayload p in src)
            {
                Items.KmhThingPayload c = Clone(p, p.StackCount);
                c.ScribeXml = "";   // never send the blob in a snapshot
                outList.Add(c);
            }
            return outList;
        }

        // Wire copy of pending deposits with any payload blobs removed (metadata only, for display).
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

        private static Items.KmhThingPayload Clone(Items.KmhThingPayload p, int stackCount) => new Items.KmhThingPayload
        {
            SchemaVersion = p.SchemaVersion, DefName = p.DefName, StuffDefName = p.StuffDefName, StackCount = stackCount,
            HitPoints = p.HitPoints, MaxHitPoints = p.MaxHitPoints, Quality = p.Quality, Tainted = p.Tainted,
            ScribeXml = p.ScribeXml, Fidelity = p.Fidelity, DisplayLabel = p.DisplayLabel, MarketValue = p.MarketValue,
            Fingerprint = p.Fingerprint, Legacy = p.Legacy, Warnings = new List<string>(p.Warnings ?? new List<string>()),
        };

        // Withdraw up to `qty` units of payloads matching a want's constraints (def + optional stuff, min quality,
        // taint/damage rules). Returns the popped payloads for delivery; empty when nothing qualifies.
        public static List<Items.KmhThingPayload> TryWithdrawMatchingPayloads(string username, string defName,
            string requiredStuff, int minQuality, bool allowTainted, bool allowDamaged, int qty, string note)
        {
            List<Items.KmhThingPayload> granted = new List<Items.KmhThingPayload>();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(defName) || qty <= 0) return granted;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
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
                    if (e.StackCount <= rem) { granted.Add(e); v.ItemPayloads.Remove(e); rem -= e.StackCount; }
                    else if (string.IsNullOrEmpty(e.ScribeXml)) { granted.Add(Clone(e, rem)); e.StackCount -= rem; rem = 0; }
                }
                if (granted.Count > 0)
                {
                    int taken = 0; foreach (var g in granted) taken += g.StackCount;
                    RecordTransactionLocked(v, username, TreasuryTransaction.KindWithdraw, taken,
                        Items.KmhItemSafety.DescribeStateForLedger(granted[0]), note);
                }
            }
            if (granted.Count > 0) { SaveToDisk(); RaiseChanged(ownerKey, note); }
            return granted;
        }

        private static void RaiseChanged(string ownerKey, string note)
            => Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
            { OwnerKey = ownerKey, IsGuildOwned = !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase), Reason = note ?? "" });

        // Durable pending deposits (disconnect/rollback dupe guard): a deposit stays PENDING until the client confirms
        // its goods-removal is durably saved; an unconfirmed txn is reverted, so value can't be in colony AND treasury.
        private const int MaxRecentCommittedTxns = 256;

        // Record a deposit as pending. Idempotent by txn id (a network resend won't double-book). Returns false when
        // already known (pending or recently committed) or invalid.
        public static bool BeginPendingDeposit(string username, Dto.PendingDeposit d)
        {
            if (string.IsNullOrEmpty(username) || d == null || string.IsNullOrEmpty(d.TxnId)) return false;
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsKnownTxnLocked(v, d.TxnId)) return false;   // dedup: already pending or committed
                d.Username        = username;
                d.State           = Dto.PendingDeposit.StatePending;
                d.CreatedUtcTicks = DateTime.UtcNow.Ticks;
                v.PendingDeposits.Add(d);
            }
            SaveToDisk();
            return true;
        }

        // Guild donation: atomically dedup by txn id, debit the donor's spendable silver and book a pending
        // 'guild_donate' entry that rides the same save-confirm pipeline as deposits. The guild vault is credited
        // only at commit; revert/timeout refunds the donor, so the guild can never spend unconfirmed silver.
        public static bool BeginPendingGuildDonation(string username, string guildName, int amount, string txnId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(guildName) || amount <= 0 || string.IsNullOrEmpty(txnId))
            { reason = "Invalid donation request."; return false; }
            string ownerKey = ResolveOwnerKeyFor(username);
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                if (IsKnownTxnLocked(v, txnId)) { reason = "That donation was already received."; return false; }
                if (v.SilverBalance < amount)
                { reason = "You don't have that much silver in your personal vault."; return false; }
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
            }
            SaveToDisk();
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

        // One user's unconfirmed donation silver headed for a guild (for the "yours" line in the Guild Hall).
        public static long PendingDonationTotalFor(string username, string guildName)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(guildName)) return 0;
            string ownerKey = ResolveOwnerKeyFor(username);
            long total = 0;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(ownerKey, out TreasurySnapshot v) || v.PendingDeposits == null) return 0;
                foreach (Dto.PendingDeposit d in v.PendingDeposits)
                    if (d.State == Dto.PendingDeposit.StatePending
                        && d.Kind == Dto.PendingDeposit.KindGuildDonate
                        && string.Equals(d.GuildName, guildName, StringComparison.OrdinalIgnoreCase))
                        total += d.Silver;
            }
            return total;
        }

        private static bool IsKnownTxnLocked(TreasurySnapshot v, string txnId)
        {
            foreach (Dto.PendingDeposit p in v.PendingDeposits)
                if (string.Equals(p.TxnId, txnId, StringComparison.Ordinal)) return true;
            foreach (string t in v.RecentCommittedTxns)
                if (string.Equals(t, txnId, StringComparison.Ordinal)) return true;
            return false;
        }

        // Client reports these txn ids are durably saved locally -> move them from pending into the spendable vault.
        // Idempotent: unknown/already-committed ids are ignored. Returns how many were committed.
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
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    if (!want.Contains(d.TxnId)) continue;
                    feeToHouse += d.Fee;
                    CommitPendingLocked(v, d, ref donations);
                    committed++;
                }
            }
            if (committed > 0) { SaveToDisk(); RaiseChanged(ownerKey, "deposit_confirmed"); }
            if (feeToHouse > 0) Marketplace.MarketplaceStore.CreditHousePool(feeToHouse, $"treasury deposit fee ({username})");
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

        // Full reconcile on (re)connect: `committed` is the client's complete set of durably-saved deposit txns.
        // Commit any pending in the set; revert any pending NOT in the set that's older than the grace window (a local
        // rollback - the client no longer has that txn saved). Young unlisted pendings are left for the timeout sweep
        // so a just-made-but-not-yet-saved deposit isn't wrongly reverted. Returns (committed, reverted).
        public static (int committed, int reverted) ReconcileDeposits(string username, IEnumerable<string> committed, int graceSeconds)
        {
            int didCommit = 0, didRevert = 0; long feeToHouse = 0;
            if (string.IsNullOrEmpty(username)) return (0, 0);
            HashSet<string> have = new HashSet<string>(committed ?? new List<string>(), StringComparer.Ordinal);
            string ownerKey = ResolveOwnerKeyFor(username);
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(Math.Max(0, graceSeconds)).Ticks;
            List<Dto.PendingDeposit> donations = null;
            lock (_lock)
            {
                TreasurySnapshot v = GetOrCreateLocked(ownerKey, !ownerKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase));
                foreach (Dto.PendingDeposit d in new List<Dto.PendingDeposit>(v.PendingDeposits))
                {
                    if (d.State != Dto.PendingDeposit.StatePending) continue;
                    if (have.Contains(d.TxnId)) { feeToHouse += d.Fee; CommitPendingLocked(v, d, ref donations); didCommit++; }
                    else if (d.CreatedUtcTicks < cutoff)
                    {
                        RevertPendingLocked(v, d);
                        didRevert++;
                        Diagnostics.ServerLog.Warn($"Treasury: reverted pending deposit {d.TxnId} for {username} " +
                            $"({DescribePending(d)}) - client reported it not durably saved (local rollback).");
                    }
                    // young + unlisted: leave for the timeout sweep
                }
            }
            if (didCommit > 0 || didRevert > 0) { SaveToDisk(); RaiseChanged(ownerKey, "deposit_reconciled"); }
            if (feeToHouse > 0) Marketplace.MarketplaceStore.CreditHousePool(feeToHouse, $"treasury deposit fee ({username})");
            NotifyDonationsFinalized(donations);
            return (didCommit, didRevert);
        }

        // Sweep every vault: revert pending deposits older than the timeout that were never confirmed. Runs off the
        // periodic sweeper. Returns how many were reverted.
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
                        // Only expire abandoned deposits: an online player will still save + confirm, so a long unsaved session must not lose value
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

        // Undo a pending entry and remove it. Deposits just disappear (the goods rolled back with the client save);
        // a guild donation refunds the donor's spendable silver that was debited at begin. Caller holds _lock.
        private static void RevertPendingLocked(TreasurySnapshot v, Dto.PendingDeposit d)
        {
            if (d.Kind == Dto.PendingDeposit.KindGuildDonate && d.Silver > 0)
            {
                v.SilverBalance    += d.Silver;
                v.LifetimeSilverIn += d.Silver;
                RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, d.Silver, "",
                    $"donation to guild '{d.GuildName}' reverted - refund");
            }
            v.PendingDeposits.Remove(d);
        }

        // Apply a pending deposit to the live spendable vault and remove it from pending. Caller holds _lock.
        // Committed guild donations are appended to `donations` so the caller can run the guild-side bookkeeping
        // (member totals, metrics, existence check) outside this lock.
        private static void CommitPendingLocked(TreasurySnapshot v, Dto.PendingDeposit d, ref List<Dto.PendingDeposit> donations)
        {
            switch (d.Kind)
            {
                case Dto.PendingDeposit.KindGuildDonate:
                    // Donor was debited at begin; credit the guild vault now (same lock - atomic with pending removal).
                    TreasurySnapshot gv = GetOrCreateLocked(d.GuildName, isGuildOwned: true);
                    gv.SilverBalance    += d.Silver;
                    gv.LifetimeSilverIn += d.Silver;
                    RecordTransactionLocked(gv, d.Username, TreasuryTransaction.KindDeposit, d.Silver, "",
                        $"contribution from {d.Username} (save-confirmed)");
                    (donations = donations ?? new List<Dto.PendingDeposit>()).Add(d);
                    break;
                case Dto.PendingDeposit.KindSilver:
                    v.SilverBalance    += d.Silver;
                    v.LifetimeSilverIn += d.Silver;
                    RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, d.Silver, "", d.Note);
                    break;
                case Dto.PendingDeposit.KindItem:
                    if (!v.Items.TryGetValue(d.ItemDefName, out int cur)) cur = 0;
                    v.Items[d.ItemDefName] = cur + d.Qty;
                    RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, d.Qty, d.ItemDefName, d.Note);
                    break;
                case Dto.PendingDeposit.KindPayload:
                    if (d.Payloads != null)
                        foreach (Items.KmhThingPayload p in d.Payloads)
                        {
                            if (p == null || !Items.KmhItemSafety.ValidatePayload(p)) continue;
                            Items.KmhThingPayload mergeInto = null;
                            if (string.IsNullOrEmpty(p.ScribeXml))
                                foreach (Items.KmhThingPayload e in v.ItemPayloads)
                                    if (Items.KmhItemSafety.CanSafelyMerge(e, p)) { mergeInto = e; break; }
                            if (mergeInto != null) mergeInto.StackCount += p.StackCount;
                            else                   v.ItemPayloads.Add(p);
                            RecordTransactionLocked(v, d.Username, TreasuryTransaction.KindDeposit, p.StackCount,
                                Items.KmhItemSafety.DescribeStateForLedger(p), d.Note);
                        }
                    break;
            }
            v.PendingDeposits.Remove(d);
            v.RecentCommittedTxns.Add(d.TxnId);
            while (v.RecentCommittedTxns.Count > MaxRecentCommittedTxns) v.RecentCommittedTxns.RemoveAt(0);
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

        // Fidelity census across every vault's stored items - for `kmh validate treasury`. full = exact-restorable,
        // partial = metadata-only, legacy = pre-payload (state unproven).
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

        // Read-only: the biggest vaults by silver (owner key, silver, guild flag), for the audit report.
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

        // Guild disbanded: move its vault (silver + items) into a member's personal vault and remove it, so nothing is
        // orphaned or resurrectable by re-creating the guild name. Returns the silver moved.
        public static long MoveGuildVaultToPersonal(string guildName, string toUsername)
        {
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(toUsername)) return 0;
            long moved = 0;
            lock (_lock)
            {
                if (!_vaults.TryGetValue(guildName, out TreasurySnapshot gv)) return 0;
                _vaults.Remove(guildName);
                moved = gv.SilverBalance;
                if (moved > 0 || (gv.Items != null && gv.Items.Count > 0))
                {
                    TreasurySnapshot pv = GetOrCreateLocked(PersonalKeyFor(toUsername), isGuildOwned: false);
                    pv.SilverBalance    += gv.SilverBalance;
                    pv.LifetimeSilverIn += gv.SilverBalance;
                    if (gv.Items != null)
                        foreach (KeyValuePair<string, int> kv in gv.Items)
                        {
                            pv.Items.TryGetValue(kv.Key, out int cur);
                            pv.Items[kv.Key] = cur + kv.Value;
                        }
                    RecordTransactionLocked(pv, toUsername, TreasuryTransaction.KindDeposit,
                        (int)Math.Min(moved, int.MaxValue), "", $"guild '{guildName}' vault returned on disband");
                }
            }
            SaveToDisk();
            if (moved > 0)
                Extensibility.KmhEventBus.Instance.RaiseTreasuryChanged(new KMH.Sdk.Server.Events.TreasuryChangedEvent
                { OwnerKey = PersonalKeyFor(toUsername), IsGuildOwned = false, Reason = "guild disband refund" });
            return moved;
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
