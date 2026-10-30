using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Quests
{
    // A bounty is escrowed out of the poster's treasury at Post, so it exists outside a vault until it settles.
    internal static class QuestStore
    {
        private static readonly object _lock = new object();

        // Every close writes what it owes in the same save (see KmhOwedPayouts). Below _lock on purpose: it captures it.
        private static readonly Transactions.KmhOwedPayouts _owed = new Transactions.KmhOwedPayouts("Quests", _lock, SaveToDisk);

        // In memory only: deliveries between their hand-over and their completion, which nothing else may close.
        private static readonly HashSet<long> _submitting = new HashSet<long>();
        // Bumped per delivery attempt, so a completion judged outside the lock can tell an attempt began meanwhile.
        private static readonly Dictionary<long, long> _submitEpoch = new Dictionary<long, long>();

        private static readonly Dictionary<long, QuestEntry> _byId = new Dictionary<long, QuestEntry>();
        private static long _nextId = 1;

        private static long _lifetimeQuestsPosted     = 0;
        private static long _lifetimeQuestsCompleted  = 0;
        private static long _lifetimeBountySilverPaid = 0;

        public static (int posted, int claimed) CountForUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return (0, 0);
            int p = 0, c = 0;
            lock (_lock)
                foreach (QuestEntry q in _byId.Values)
                {
                    if (q == null) continue;
                    if (string.Equals(q.PosterUsername, user, System.StringComparison.OrdinalIgnoreCase)) p++;
                    if (string.Equals(q.ClaimedByUsername, user, System.StringComparison.OrdinalIgnoreCase)) c++;
                }
            return (p, c);
        }

        // Deliberately coarse, because being wrong here shows one player another's hidden quest.
        public static HashSet<string> PostersOfNonPublic()
        {
            HashSet<string> posters = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                foreach (QuestEntry q in _byId.Values)
                    if (q != null && !string.IsNullOrEmpty(q.PosterUsername)
                        && !string.Equals(q.Visibility, Guilds.GuildVisibility.Public, System.StringComparison.OrdinalIgnoreCase))
                        posters.Add(q.PosterUsername);
            return posters;
        }

        public static QuestSnapshot BuildSnapshot(string callerUsername)
        {
            QuestSnapshot s = new QuestSnapshot();
            string callerGuild = string.IsNullOrEmpty(callerUsername)
                ? null
                : Guilds.GuildStore.CurrentGuildOf(callerUsername);
            lock (_lock)
            {
                s.Revision = Util.KmhSnapshotRevision.Next();
                s.LifetimeQuestsPosted     = _lifetimeQuestsPosted;
                s.LifetimeQuestsCompleted  = _lifetimeQuestsCompleted;
                s.LifetimeBountySilverPaid = _lifetimeBountySilverPaid;

                s.Quests = new List<QuestEntry>(_byId.Count);
                foreach (QuestEntry q in _byId.Values)
                {
                    bool isMine = !string.IsNullOrEmpty(callerUsername) &&
                                  string.Equals(q.PosterUsername, callerUsername, System.StringComparison.OrdinalIgnoreCase);
                    if (!isMine &&
                        !Guilds.GuildVisibility.IsVisibleTo(q.PosterTreasuryKey, q.Visibility, callerGuild, prefetched: true))
                        continue;

                    s.Quests.Add(CopyLocked(q));
                }
            }
            return s;
        }

        // Routes through PostDraft so every kind shares one validation and escrow path.
        public static long Post(
            string posterUsername,
            string kind,
            string visibility,
            string title,
            string description,
            int    bountySilver,
            string targetItemDefName,
            int    targetItemQty,
            int    expiresInHours = 0)
        {
            QuestEntry draft = new QuestEntry
            {
                Kind              = kind ?? QuestEntry.KindDeliverItem,
                Visibility        = visibility ?? QuestEntry.VisibilityPublic,
                Title             = title,
                Description       = description,
                BountySilver      = bountySilver,
                TargetItemDefName = targetItemDefName,
                TargetItemQty     = targetItemQty,
            };
            return PostDraft(posterUsername, draft, expiresInHours, out _);
        }

        // Bounded: a huge lifetime would throw after escrow or wrap the expiry negative, pinning the bounty for ever.
        internal const int MaxLifetimeHours = 24 * 30;
        internal static int LifetimeHours(int requested) => requested > 0 ? Math.Min(requested, MaxLifetimeHours) : 168;

        public static long PostDraft(string posterUsername, QuestEntry draft, int expiresInHours, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(posterUsername)) { reason = "No poster.";  return 0; }
            if (draft == null)                        { reason = "Empty quest."; return 0; }

            string title = (draft.Title ?? "").Trim();
            string desc  = (draft.Description ?? "").Trim();
            if (string.IsNullOrEmpty(title)) { reason = "Quest needs a title."; return 0; }
            if (title.Length > QuestsConfig.Current.MaxTitleLength)      title = title.Substring(0, QuestsConfig.Current.MaxTitleLength);
            if (desc.Length  > QuestsConfig.Current.MaxDescriptionLength) desc = desc.Substring(0, QuestsConfig.Current.MaxDescriptionLength);

            string kind = string.IsNullOrEmpty(draft.Kind) ? QuestEntry.KindDeliverItem : draft.Kind;
            if (draft.BountySilver < 0) draft.BountySilver = 0;
            if (draft.BountySilver > 10_000_000) draft.BountySilver = 10_000_000;

            if (!ValidateKind(kind, draft, out reason)) return 0;

            lock (_lock)
            {
                int open = OpenCountLocked(posterUsername);
                if (open >= QuestsConfig.Current.MaxOpenPerUser)
                { reason = $"You already have the max {QuestsConfig.Current.MaxOpenPerUser} active quests."; return 0; }
            }

            // Runs before escrow, so a denial leaves no side effects.
            KMH.Sdk.Server.Hooks.KmhHookVerdict hookVerdict = Extensibility.KmhHooks.Instance.CheckQuestPost(
                new KMH.Sdk.Server.Hooks.KmhQuestPostContext(posterUsername, kind, title, draft.BountySilver));
            if (hookVerdict.Denied) { reason = hookVerdict.Reason; return 0; }

            Dictionary<string, int> escrowed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (draft.BountyItems != null && draft.BountyItems.Count > 0)
            {
                int processed = 0;
                foreach (KeyValuePair<string, int> kv in draft.BountyItems)
                {
                    if (++processed > QuestsConfig.Current.MaxBountyItems) break;
                    if (kv.Value <= 0) continue;
                    string itemKey = (kv.Key ?? "").Length > 64 ? kv.Key.Substring(0, 64) : (kv.Key ?? "");
                    if (string.IsNullOrEmpty(itemKey)) continue;
                    escrowed.TryGetValue(itemKey, out int had);
                    escrowed[itemKey] = (int)Math.Min(1_000_000L, (long)had + kv.Value);
                }
            }

            // Row opened before one marked debit, so a crash leaves the bounty in the vault, the quest, or owed back.
            Transactions.KmhTransaction tx = null;
            if (draft.BountySilver > 0 || escrowed.Count > 0)
            {
                if (draft.BountySilver > 0 && Treasury.TreasuryStore.GetPersonalSilver(posterUsername) < draft.BountySilver)
                { reason = "Your treasury is short on bounty silver."; return 0; }
                tx = Transactions.KmhTransactionRepository.OpenTake(
                    posterUsername, Transactions.KmhTxType.Listing, $"quest bounty: {title}", 0, draft.BountySilver, escrowed, null,
                    destination: "quest", targetKind: Transactions.KmhTransactionRepository.TargetQuestPost);
                if (tx == null) { reason = "The server couldn't record that bounty - nothing was taken. Try again shortly."; return 0; }
                if (!Treasury.TreasuryStore.WithdrawBundleForTxn(posterUsername, draft.BountySilver,
                        new List<KeyValuePair<string, int>>(escrowed), tx.TakeMarker, "quest bounty escrow"))
                {
                    Transactions.KmhTransactionRepository.Abort(tx);
                    reason = ShortOn(posterUsername, draft.BountySilver, escrowed);
                    return 0;
                }
            }
            AtPostPhaseForTest?.Invoke("debited");

            // A solo poster has no guild to scope to, so GuildOnly would hide the quest from everyone.
            string posterKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(posterUsername);
            string visibility = Guilds.GuildVisibility.Normalize(draft.Visibility);
            bool posterIsGuild = !posterKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase);
            if (visibility == QuestEntry.VisibilityGuildOnly && !posterIsGuild)
                visibility = QuestEntry.VisibilityPublic;

            long now = DateTime.UtcNow.Ticks;
            long assignedId = 0;
            bool overCap;
            lock (_lock)
            {
                // Re-counted: the escrow released the lock, so two posts racing for one slot can both pass the first check.
                overCap = OpenCountLocked(posterUsername) >= QuestsConfig.Current.MaxOpenPerUser;
                if (!overCap)
                {
                assignedId = _nextId++;
                _byId[assignedId] = new QuestEntry
                {
                    Id                = assignedId,
                    Kind              = kind,
                    State             = QuestEntry.StateOpen,
                    Visibility        = visibility,
                    PosterUsername    = posterUsername,
                    PosterTreasuryKey = posterKey,
                    Title             = title,
                    Description       = desc,
                    BountySilver      = draft.BountySilver,
                    BountyItems       = escrowed,
                    TargetItemDefName  = draft.TargetItemDefName ?? "",
                    TargetItemQty      = draft.TargetItemQty,
                    TargetQualityIndex = Util.ItemKey.Clamp(draft.TargetQualityIndex),
                    PostedUtcTicks    = now,
                    ExpiresUtcTicks   = now + TimeSpan.FromHours(LifetimeHours(expiresInHours)).Ticks,
                    EscortPickupTile        = draft.EscortPickupTile,
                    EscortDropoffTile       = draft.EscortDropoffTile,
                    EscortTargetDescription = (draft.EscortTargetDescription ?? "").Length > 200
                                                ? draft.EscortTargetDescription.Substring(0, 200)
                                                : (draft.EscortTargetDescription ?? ""),
                    DefendColonyTile        = draft.DefendColonyTile,
                    DefendDurationGameTicks = draft.DefendDurationGameTicks,
                    HuntTargetKind          = string.IsNullOrEmpty(draft.HuntTargetKind) ? QuestEntry.HuntNone : Util.KmhSafe.Cap(draft.HuntTargetKind, 32),
                    HuntTargetDefName       = Util.KmhSafe.Cap(draft.HuntTargetDefName ?? "", Util.KmhSafe.MaxDefLength),
                    HuntTargetCount         = draft.HuntTargetCount,
                    BuildAtTile             = draft.BuildAtTile,
                    BuildStructureDefName   = Util.KmhSafe.Cap(draft.BuildStructureDefName ?? "", Util.KmhSafe.MaxDefLength),
                    BuildCount              = draft.BuildCount,
                    ReviewState             = QuestEntry.ReviewNotApplicable,
                };
                if (tx != null) _postTx[assignedId] = tx.Id;
                _lifetimeQuestsPosted += 1;
                }
            }
            // Lost the race for the last slot, so the escrow goes straight back rather than being kept for nothing.
            if (overCap)
            {
                RefundPost(tx);
                reason = $"You already have the max {QuestsConfig.Current.MaxOpenPerUser} active quests - your bounty was returned.";
                return 0;
            }
            // The bounty already left the poster's treasury, so an unwritten post hands the whole escrow straight back.
            if (!SaveToDisk())
            {
                lock (_lock) { _byId.Remove(assignedId); _postTx.Remove(assignedId); _lifetimeQuestsPosted -= 1; }
                RefundPost(tx);
                reason = "The server couldn't save that quest - your bounty was returned. Try again shortly.";
                return 0;
            }
            AtPostPhaseForTest?.Invoke("saved");
            if (tx != null) Transactions.KmhTransactionRepository.Settle(tx);
            Reputation.ReputationStore.EnsurePlayer(posterUsername);
            Extensibility.KmhEventBus.Instance.RaiseQuestPosted(new KMH.Sdk.Server.Events.QuestPostedEvent { QuestId = assignedId, Kind = kind, PosterUsername = posterUsername, BountySilver = draft.BountySilver });
            reason = $"Posted quest #{assignedId}: {title}.";
            return assignedId;
        }

        internal static Action<string> AtPostPhaseForTest;

        // Server-side only, saved with the quests: the ledger row that funded each posted bounty.
        private static readonly Dictionary<long, string> _postTx = new Dictionary<long, string>();

        internal static bool HoldsPost(string txId)
        {
            if (string.IsNullOrEmpty(txId)) return false;
            lock (_lock)
                foreach (KeyValuePair<long, string> kv in _postTx)
                    if (kv.Value == txId && _byId.ContainsKey(kv.Key)) return true;
            return false;
        }

        // On the row's own refund marker, so this and boot recovery return the bounty once between them.
        private static void RefundPost(Transactions.KmhTransaction tx)
        {
            if (tx != null && !Transactions.KmhTransactionRepository.Compensate(tx))
                Diagnostics.ServerLog.Error($"Quests: bounty {tx.Id} could not be returned yet - the row stays open and the next boot returns it.");
        }

        private static string ShortOn(string poster, int silver, Dictionary<string, int> items)
        {
            if (silver > 0 && Treasury.TreasuryStore.GetPersonalSilver(poster) < silver) return "Your treasury is short on bounty silver.";
            Treasury.Dto.TreasurySnapshot v = Treasury.TreasuryStore.GetSnapshotFor(poster);
            foreach (KeyValuePair<string, int> kv in items)
                if (v?.Items == null || !v.Items.TryGetValue(kv.Key, out int have) || have < kv.Value)
                    return $"Your treasury is short on bounty item '{kv.Key}'.";
            return "Your treasury is short on the bounty.";
        }

        private static bool ValidateKind(string kind, QuestEntry d, out string reason)
        {
            reason = null;
            switch (kind)
            {
                case QuestEntry.KindDeliverItem:
                    if (string.IsNullOrEmpty(d.TargetItemDefName)) { reason = "Pick an item to deliver."; return false; }
                    if (d.TargetItemQty <= 0)                      { reason = "Quantity must be > 0.";    return false; }
                    if (d.TargetItemDefName.Length > 64) d.TargetItemDefName = d.TargetItemDefName.Substring(0, 64);
                    if (d.TargetItemQty > 100_000) d.TargetItemQty = 100_000;
                    d.TargetQualityIndex = Util.ItemKey.Clamp(d.TargetQualityIndex);
                    return true;

                case QuestEntry.KindBounty:
                    return true;

                case QuestEntry.KindEscort:
                    if (d.EscortPickupTile  < 0) { reason = "Pick a pickup tile.";  return false; }
                    if (d.EscortDropoffTile < 0) { reason = "Pick a dropoff tile."; return false; }
                    if (d.EscortPickupTile == d.EscortDropoffTile) { reason = "Pickup and dropoff can't be the same tile."; return false; }
                    return true;

                case QuestEntry.KindDefend:
                    if (d.DefendColonyTile < 0)        { reason = "Pick a colony tile to defend."; return false; }
                    if (d.DefendDurationGameTicks <= 0) { reason = "Defense window must be > 0 ticks."; return false; }
                    long maxDefend = 30L * 60_000L; // 30 in-game days
                    if (d.DefendDurationGameTicks > maxDefend) d.DefendDurationGameTicks = maxDefend;
                    return true;

                case QuestEntry.KindHunt:
                    if (string.IsNullOrEmpty(d.HuntTargetKind) || d.HuntTargetKind == QuestEntry.HuntNone)
                        { reason = "Pick what to hunt."; return false; }
                    if (string.IsNullOrEmpty(d.HuntTargetDefName)) { reason = "Pick the hunt target."; return false; }
                    if (d.HuntTargetDefName.Length > 64) d.HuntTargetDefName = d.HuntTargetDefName.Substring(0, 64);
                    if (d.HuntTargetCount <= 0) d.HuntTargetCount = 1;
                    if (d.HuntTargetCount > 50) d.HuntTargetCount = 50;
                    return true;

                case QuestEntry.KindBuild:
                    if (d.BuildAtTile < 0) { reason = "Pick a build tile."; return false; }
                    if (string.IsNullOrEmpty(d.BuildStructureDefName)) { reason = "Pick a structure to build."; return false; }
                    if (d.BuildStructureDefName.Length > 64) d.BuildStructureDefName = d.BuildStructureDefName.Substring(0, 64);
                    if (d.BuildCount <= 0) d.BuildCount = 1;
                    if (d.BuildCount > 100) d.BuildCount = 100;
                    return true;

                case QuestEntry.KindCustom:
                    if ((d.Description ?? "").Trim().Length < 20)
                        { reason = "Custom quests need a 20+ char description so claimers know what to do."; return false; }
                    return true;

                default:
                    reason = $"Unsupported quest kind: {kind}";
                    return false;
            }
        }

        internal static (string Poster, string Claimer, string Title, string State) PartiesOf(long questId)
        {
            lock (_lock)
                return _byId.TryGetValue(questId, out QuestEntry q) && q != null
                    ? (q.PosterUsername ?? "", q.ClaimedByUsername ?? "", q.Title ?? "", q.State ?? "")
                    : ("", "", "", "");
        }

        public static bool Claim(string claimerUsername, long questId)
        {
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            // Resolved before the lock, like the snapshot does: the guild store has a lock of its own.
            string callerGuild = Guilds.GuildStore.CurrentGuildOf(claimerUsername);
            bool ok = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q))      goto done;
                if (q.State != QuestEntry.StateOpen)                    goto done;
                // Ids are sequential, so without a cap one player could claim every bounty on the board.
                if (ActiveClaimsLocked(claimerUsername) >= MaxActiveClaims) goto done;
                // The board hides guild-only quests from outsiders, but ids are sequential, so the claim must check too.
                if (!Guilds.GuildVisibility.IsVisibleTo(q.PosterTreasuryKey, q.Visibility, callerGuild, prefetched: true))
                    goto done;
                if (string.Equals(q.PosterUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    goto done;

                q.State             = QuestEntry.StateClaimed;
                q.ClaimedByUsername = claimerUsername;
                q.ClaimedUtcTicks   = DateTime.UtcNow.Ticks;
                ok = true;
                done: ;
            }
            if (ok)
            {
                SaveToDisk();
                string poster = null;
                lock (_lock) { if (_byId.TryGetValue(questId, out QuestEntry q2)) poster = q2.PosterUsername; }
                Extensibility.KmhEventBus.Instance.RaiseQuestClaimed(new KMH.Sdk.Server.Events.QuestClaimedEvent { QuestId = questId, ClaimerUsername = claimerUsername, PosterUsername = poster ?? "" });
            }
            return ok;
        }

        // The treasury hand-over is a delivery's commit point: after it the quest only rolls forward, never undone.
        public static bool Submit(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            string kind;
            string targetDef = null, poster = null, deliveryMarker = null;
            int    targetQty = 0, targetQual = 0;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (!string.Equals(q.ClaimedByUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                kind = q.Kind;

                if (kind == QuestEntry.KindBounty)
                {
                    // The review clock starts at the first submit only, so re-submitting cannot hold escrow past the timeout.
                    if (q.State != QuestEntry.StateClaimed && q.State != QuestEntry.StateSubmitted) return false;
                    if (q.State == QuestEntry.StateClaimed) q.ProofSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                    q.State = QuestEntry.StateSubmitted;
                }
                // Only a delivery completes on submit; other kinds would pay their whole bounty for delivering nothing.
                else if (kind != QuestEntry.KindDeliverItem) return false;
                else
                {
                    if (q.State != QuestEntry.StateClaimed || _submitting.Contains(questId)) return false;
                    // A requirement of nothing is never met, whatever a hand-edited or pre-validation row says.
                    if (string.IsNullOrEmpty(q.TargetItemDefName) || q.TargetItemQty <= 0) return false;
                    targetDef      = q.TargetItemDefName;
                    targetQty      = q.TargetItemQty;
                    targetQual     = q.TargetQualityIndex;
                    poster         = q.PosterUsername;
                    deliveryMarker = MarkerFor(q, "delivery");
                    // Held in memory, never saved: the quest stays Claimed on disk until its completion is written.
                    _submitting.Add(questId);
                    _submitEpoch[questId] = (_submitEpoch.TryGetValue(questId, out long e) ? e : 0) + 1;
                }
            }

            if (kind == QuestEntry.KindBounty)
            {
                SaveToDisk();
                Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = false });
                return true;
            }

            bool done;
            try
            {
                if (!Treasury.TreasuryStore.HandOverMatchingOnce(claimerUsername, poster, targetDef, targetQual, targetQty,
                        deliveryMarker, $"quest #{questId} delivery from {claimerUsername}", out bool already, out _, out _))
                    return false;
                if (already) Diagnostics.ServerLog.Info($"Quests: #{questId} was already handed over by an earlier attempt - completing it.");
                AtSubmitPhaseForTest?.Invoke("handed-over");
                done = CompleteDelivery(questId, claimerUsername);
            }
            finally { lock (_lock) _submitting.Remove(questId); }
            // Not unwound: the goods are the poster's now, and the quest completes on the next retry, sweep or boot.
            if (!done) return false;

            PlayerStats.PlayerStatsStore.RecordContractCompleted(claimerUsername, kind);
            Reputation.ReputationStore.RecordCompleted(claimerUsername);
            posterAffected = poster;
            Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = true });
            return true;
        }

        internal static Action<string> AtSubmitPhaseForTest;

        // False leaves the quest Claimed with its goods handed over, which every later path completes, never reopens.
        private static bool CompleteDelivery(long questId, string claimer)
        {
            string bountyMarker, deliveryMarker;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q) || q.State != QuestEntry.StateClaimed
                    || !string.Equals(q.ClaimedByUsername, claimer, StringComparison.OrdinalIgnoreCase)) return false;
                q.State             = QuestEntry.StateCompleted;
                q.CompletedUtcTicks = DateTime.UtcNow.Ticks;
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += q.BountySilver;
                deliveryMarker = MarkerFor(q, "delivery");
                bountyMarker = OweLocked(q, "bounty", claimer, q.BountySilver, q.BountyItems, $"quest #{questId} bounty");
                if (!SaveToDisk())
                {
                    _owed.ForgetLocked(bountyMarker);
                    q.State = QuestEntry.StateClaimed; q.CompletedUtcTicks = 0;
                    _lifetimeQuestsCompleted  -= 1;
                    _lifetimeBountySilverPaid -= q.BountySilver;
                    Diagnostics.ServerLog.Warn($"Quests: #{questId} was handed over but its completion could not be saved - it completes on the next retry, sweep or boot.");
                    return false;
                }
            }
            AtSubmitPhaseForTest?.Invoke("completed");
            // The completion is on disk, so the hand-over record has done its job.
            Treasury.TreasuryStore.ForgetHandOver(deliveryMarker);
            _owed.Settle(bountyMarker);
            return true;
        }

        // The poster's vault holding the delivery marker proves the goods moved, so the quest completes as Submit would.
        public static int CompleteHandedOverDeliveries()
        {
            var ids = new List<long>();
            lock (_lock)
                foreach (QuestEntry q in _byId.Values)
                    if (q.Kind == QuestEntry.KindDeliverItem && q.State == QuestEntry.StateClaimed) ids.Add(q.Id);
            int completed = 0;
            foreach (long id in ids) if (TryCompleteHandedOver(id)) completed++;
            return completed;
        }

        private static long EpochLocked(long questId) => _submitEpoch.TryGetValue(questId, out long e) ? e : 0;

        // The marker is read outside our lock, so the epoch shows whether a delivery attempt began meanwhile and owns it.
        private static bool TryCompleteHandedOver(long questId)
        {
            string poster, claimer, marker; long epoch;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q) || q.Kind != QuestEntry.KindDeliverItem
                    || q.State != QuestEntry.StateClaimed || _submitting.Contains(questId) || string.IsNullOrEmpty(q.ClaimedByUsername)) return false;
                poster = q.PosterUsername; claimer = q.ClaimedByUsername; marker = MarkerFor(q, "delivery"); epoch = EpochLocked(questId);
            }
            if (!Treasury.TreasuryStore.KnowsHandOver(marker)) return false;
            lock (_lock)
            {
                if (_submitting.Contains(questId) || EpochLocked(questId) != epoch) return false;
                _submitting.Add(questId);
            }
            bool ok;
            try { ok = CompleteDelivery(questId, claimer); }
            finally { lock (_lock) _submitting.Remove(questId); }
            if (!ok) return false;
            PlayerStats.PlayerStatsStore.RecordContractCompleted(claimer, QuestEntry.KindDeliverItem);
            Reputation.ReputationStore.RecordCompleted(claimer);
            Diagnostics.ServerLog.Warn($"Quests: completed delivery #{questId} for {claimer} - its goods reached {poster} before an interruption.");
            return true;
        }

        // True when the goods already left the claimer (it completed); the caller must still see epoch under its lock.
        private static bool HandedOverFirst(long questId, out long epoch)
        {
            lock (_lock) epoch = EpochLocked(questId);
            if (!TryCompleteHandedOver(questId)) return false;
            lock (_lock) epoch = EpochLocked(questId);
            return true;
        }

        // Unique per quest instance: ids restart after a season wipe, the posting time does not repeat.
        private static string MarkerFor(QuestEntry q, string kind, string user = "")
            => $"quest:{q.Id}:{q.PostedUtcTicks}:{kind}" + (string.IsNullOrEmpty(user) ? "" : ":" + user.ToLowerInvariant());

        private static string OweLocked(QuestEntry q, string kind, string user, long silver, Dictionary<string, int> items, string note)
        {
            var owedItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (items != null)
                foreach (KeyValuePair<string, int> kv in items)
                    if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key)) owedItems[kv.Key] = kv.Value;
            return _owed.RecordLocked(new Transactions.KmhOwedPayout
            {
                Marker = MarkerFor(q, kind, user), User = user ?? "", Silver = Math.Max(0, silver), Items = owedItems, Note = note,
            });
        }

        // Boot, before anything can touch quests: finish hand-overs, then pay what was written down and interrupted.
        public static void RecoverOnBoot()
        {
            int finished = CompleteHandedOverDeliveries();
            // What is left belongs to a delivery that is still claimed, or to one whose completion is already on disk.
            var open = new HashSet<string>(StringComparer.Ordinal);
            lock (_lock)
            {
                foreach (QuestEntry q in _byId.Values)
                    if (q.Kind == QuestEntry.KindDeliverItem && q.State == QuestEntry.StateClaimed) open.Add(MarkerFor(q, "delivery"));
            }
            // Outside our lock: the treasury has its own.
            Treasury.TreasuryStore.PruneHandOvers(open.Contains);
            (int paid, int left) = _owed.Resume();
            if (finished > 0 || paid > 0 || left > 0)
                Diagnostics.ServerLog.Warn($"Quests: recovery completed {finished} handed-over delivery(ies), paid {paid} interrupted payout(s), {left} still owed.");
        }

        public static int RetryOwed() => _owed.Count == 0 ? 0 : _owed.Resume(quiet: true).paid;
        internal static int OwedCount => _owed.Count;

        internal static (long Silver, int Units) EscrowedForTest(string poster)
        {
            long s = 0; int n = 0;
            lock (_lock)
                foreach (QuestEntry q in _byId.Values)
                {
                    if (!HoldsEscrow(q) || !string.Equals(q.PosterUsername, poster, StringComparison.OrdinalIgnoreCase)) continue;
                    s += q.BountySilver;
                    if (q.BountyItems != null) foreach (int u in q.BountyItems.Values) n += u;
                }
            return (s, n);
        }

        internal static void BackdateClaimForTest(long questId, TimeSpan by)
        {
            lock (_lock) if (_byId.TryGetValue(questId, out QuestEntry q)) q.ClaimedUtcTicks -= by.Ticks;
        }

        internal static string StateForTest(long questId) { lock (_lock) return _byId.TryGetValue(questId, out QuestEntry q) ? q.State : "(gone)"; }

        internal static int OwedCountForTest => _owed.Count;
        internal static (long Silver, int Units) OwedTotalsForTest(string user = null) { lock (_lock) return _owed.TotalsLocked(user); }
        internal static (long Silver, int Units) UnpaidOwedForTest(string user) => _owed.UnpaidTotals(user);
        internal static Action<string> AtPayoutPhaseForTest { set => _owed.AtPhaseForTest = value; }
        internal static string DeliveryMarkerForTest(long questId) { lock (_lock) return _byId.TryGetValue(questId, out QuestEntry q) ? MarkerFor(q, "delivery") : null; }

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropOwedFor(string user) { lock (_lock) return _owed.DropForLocked(user); }

        public static bool ExpireQuest(long questId, out string posterAffected)
        {
            posterAffected = null;
            // Goods already handed over make it a completed delivery, never an expired one with the bounty refunded too.
            if (HandedOverFirst(questId, out long epoch)) return false;
            string poster, refund;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                // A claim holds nothing of the claimer's, so it expires like an open quest; a submission does not.
                if (q.State != QuestEntry.StateOpen && q.State != QuestEntry.StateClaimed) return false;
                // A delivery attempt in flight, or one that began since the check above, rolls forward first.
                if (_submitting.Contains(questId) || EpochLocked(questId) != epoch) return false;
                string wasState = q.State;
                poster       = q.PosterUsername;
                q.State      = QuestEntry.StateExpired;
                _byId.Remove(questId);
                refund = OweLocked(q, "refund", poster, q.BountySilver, q.BountyItems, $"quest #{questId} expired - bounty refund");
                // Durable with its refund owed, or the sweeper expires and refunds the same quest again.
                if (!SaveToDisk()) { _owed.ForgetLocked(refund); q.State = wasState; _byId[questId] = q; return false; }
            }
            _owed.Settle(refund);
            posterAffected = poster;
            return true;
        }

        // Refunds once from any escrow-holding state, review included; a handed-over delivery completes instead.
        internal static bool AdminRefund(long questId, string admin, out string posterAffected, out string why)
        {
            posterAffected = null; why = null;
            if (HandedOverFirst(questId, out long epoch)) { why = "its delivery was already handed over - it completes instead"; return false; }
            string poster, refund;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) { why = "no such quest on the board"; return false; }
                if (!HoldsEscrow(q)) { why = $"it is {q.State} and holds no escrow"; return false; }
                if (_submitting.Contains(questId) || EpochLocked(questId) != epoch) { why = "a delivery is in flight - try again in a moment"; return false; }
                string wasState = q.State;
                poster  = q.PosterUsername;
                q.State = QuestEntry.StateCancelled;
                _byId.Remove(questId);
                refund = OweLocked(q, "refund", poster, q.BountySilver, q.BountyItems, $"quest #{questId} refunded by admin {admin}");
                if (!SaveToDisk()) { _owed.ForgetLocked(refund); q.State = wasState; _byId[questId] = q; why = "the change could not be saved"; return false; }
            }
            _owed.Settle(refund);
            posterAffected = poster;
            Extensibility.KmhEventBus.Instance.RaiseQuestCancelled(new KMH.Sdk.Server.Events.QuestCancelledEvent { QuestId = questId, PosterUsername = poster ?? "", ExpiredAutomatically = false });
            return true;
        }

        // Past the review timeout a submission reopens unpaid, so escrow is never held for ever; expiry then refunds it.
        public static List<(long Id, string Claimer, string Poster)> ReleaseStaleReviews(long nowTicks)
        {
            var released = new List<(long, string, string)>();
            int hours = QuestsConfig.Current.ReviewTimeoutHours;
            if (hours <= 0) return released;
            long cutoff = nowTicks - TimeSpan.FromHours(hours).Ticks;
            bool stamped = false;
            lock (_lock)
            {
                foreach (QuestEntry q in _byId.Values)
                {
                    if (q.State != QuestEntry.StateSubmitted && q.State != QuestEntry.StatePendingReview) continue;
                    if (_submitting.Contains(q.Id)) continue;
                    // A row from before the review clock existed starts it now, rather than being released at once.
                    if (q.ProofSubmittedUtcTicks <= 0) { q.ProofSubmittedUtcTicks = nowTicks; stamped = true; continue; }
                    if (q.ProofSubmittedUtcTicks > cutoff) continue;
                    released.Add((q.Id, q.ClaimedByUsername, q.PosterUsername));
                    q.State                  = QuestEntry.StateOpen;
                    q.ClaimedByUsername      = "";
                    q.ClaimedUtcTicks        = 0;
                    q.ProofSubmittedUtcTicks = 0;
                    q.ProofText              = "";
                    q.ProofImageUrl          = "";
                    q.ReviewState            = QuestEntry.ReviewNotApplicable;
                    q.ReviewNote             = $"Not reviewed within {hours}h - reopened.";
                    q.ReviewSubmittedUtcTicks = nowTicks;
                }
            }
            // Nothing moves here, so a failed save only means the release is redone on a later sweep.
            if (released.Count > 0 || stamped) SaveToDisk();
            return released;
        }

        internal static void BackdateReviewForTest(long questId, TimeSpan by)
        {
            lock (_lock) if (_byId.TryGetValue(questId, out QuestEntry q) && q.ProofSubmittedUtcTicks > 0) q.ProofSubmittedUtcTicks -= by.Ticks;
        }

        // Only Completed lingers, as a brief "recently done" window; everything else is removed immediately.
        public static bool PruneFinalized(long nowTicks)
        {
            long cutoff = nowTicks - TimeSpan.FromDays(1).Ticks;
            bool changed;
            lock (_lock)
            {
                List<long> drop = null;
                foreach (QuestEntry q in _byId.Values)
                    if (q.State == QuestEntry.StateCompleted && q.CompletedUtcTicks > 0 && q.CompletedUtcTicks < cutoff)
                        (drop ??= new List<long>()).Add(q.Id);
                foreach (long id in new List<long>(_submitEpoch.Keys)) if (!_byId.ContainsKey(id) && !_submitting.Contains(id)) _submitEpoch.Remove(id);
                if (drop == null) return false;
                foreach (long id in drop) _byId.Remove(id);
                changed = true;
            }
            if (changed) SaveToDisk();
            return changed;
        }

        // Only a pre-settlement quest still holds its poster's bounty.
        public static long EscrowValueFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long v = 0;
            lock (_lock)
                foreach (Dto.QuestEntry q in _byId.Values)
                {
                    if (q == null || !HoldsEscrow(q)) continue;
                    if (!string.Equals(q.PosterUsername, username, StringComparison.OrdinalIgnoreCase)) continue;
                    v += q.BountySilver;
                    if (q.BountyItems != null)
                        foreach (System.Collections.Generic.KeyValuePair<string, int> kv in q.BountyItems)
                            v += Items.KmhItemSafety.GetTrustedMarketValue((kv.Key ?? "").Split('|')[0]) * kv.Value;
                }
            return v;
        }

        public static List<long> CollectExpiredOpenIds(long nowTicks)
        {
            List<long> expired = new List<long>();
            lock (_lock)
            {
                foreach (QuestEntry q in _byId.Values)
                {
                    if ((q.State == QuestEntry.StateOpen || q.State == QuestEntry.StateClaimed)
                        && q.ExpiresUtcTicks > 0
                        && nowTicks >= q.ExpiresUtcTicks)
                    {
                        expired.Add(q.Id);
                    }
                }
            }
            return expired;
        }

        internal const int MaxActiveClaims = 5;

        private static int ActiveClaimsLocked(string user)
        {
            int n = 0;
            foreach (QuestEntry q in _byId.Values)
                if ((q.State == QuestEntry.StateClaimed || q.State == QuestEntry.StateSubmitted || q.State == QuestEntry.StatePendingReview)
                    && string.Equals(q.ClaimedByUsername, user, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        public static bool Cancel(string callerUsername, long questId)
        {
            if (string.IsNullOrEmpty(callerUsername)) return false;

            string refund;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (!string.Equals(q.PosterUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (q.State != QuestEntry.StateOpen) return false;

                q.State = QuestEntry.StateCancelled;
                _byId.Remove(questId);
                refund = OweLocked(q, "refund", callerUsername, q.BountySilver, q.BountyItems, $"quest #{questId} cancel refund");
                // Durable with its refund owed, or a restart restores the quest with an escrow the poster already holds.
                if (!SaveToDisk()) { _owed.ForgetLocked(refund); q.State = QuestEntry.StateOpen; _byId[questId] = q; return false; }
            }

            _owed.Settle(refund);
            Extensibility.KmhEventBus.Instance.RaiseQuestCancelled(new KMH.Sdk.Server.Events.QuestCancelledEvent { QuestId = questId, PosterUsername = callerUsername, ExpiredAutomatically = false });
            return true;
        }

        public static bool Approve(string callerUsername, long questId, out string claimerAffected)
        {
            claimerAffected = null;
            if (string.IsNullOrEmpty(callerUsername)) return false;

            int bountySilver;
            string claimer, owed;
            string kind = "";
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.Kind != QuestEntry.KindBounty && q.State != QuestEntry.StatePendingReview) return false;
                if (q.State != QuestEntry.StateSubmitted && q.State != QuestEntry.StatePendingReview) return false;
                if (!string.Equals(q.PosterUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                bountySilver = q.BountySilver;
                claimer      = q.ClaimedByUsername;
                kind         = q.Kind;
                string wasState = q.State, wasReview = q.ReviewState;

                // Recorded atomically with the eligibility check, or a concurrent Approve could double-pay.
                q.State              = QuestEntry.StateCompleted;
                q.CompletedUtcTicks  = DateTime.UtcNow.Ticks;
                q.ReviewState        = QuestEntry.ReviewApproved;
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += bountySilver;
                owed = OweLocked(q, "bounty", claimer, bountySilver, q.BountyItems, $"quest #{questId} bounty (approved by {callerUsername})");
                // Durable with the bounty owed, or a restart returns the quest to Submitted and it pays twice.
                if (!SaveToDisk())
                {
                    _owed.ForgetLocked(owed);
                    q.State = wasState; q.CompletedUtcTicks = 0; q.ReviewState = wasReview;
                    _lifetimeQuestsCompleted  -= 1;
                    _lifetimeBountySilverPaid -= bountySilver;
                    return false;
                }
            }

            _owed.Settle(owed);

            if (!string.IsNullOrEmpty(claimer))
            {
                PlayerStats.PlayerStatsStore.RecordContractCompleted(claimer, kind);
                Reputation.ReputationStore.RecordCompleted(claimer);
            }
            claimerAffected = claimer;
            SaveToDisk();
            Global.Federation.FederationService.RecordQuestCompletion(claimer, callerUsername);
            Extensibility.KmhEventBus.Instance.RaiseQuestApproved(new KMH.Sdk.Server.Events.QuestApprovedEvent { QuestId = questId, PosterUsername = callerUsername, ClaimerUsername = claimer ?? "", BountyPaidSilver = bountySilver });
            return true;
        }

        public static bool Abandon(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;
            // Reopening a delivery whose goods already left the claimer would cost them the goods and the bounty.
            if (HandedOverFirst(questId, out long epoch)) return false;
            string poster = null;
            bool ok = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.State != QuestEntry.StateClaimed && q.State != QuestEntry.StateSubmitted
                    && q.State != QuestEntry.StatePendingReview) return false;
                if (_submitting.Contains(questId) || EpochLocked(questId) != epoch) return false;
                if (!string.Equals(q.ClaimedByUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                q.State             = QuestEntry.StateOpen;
                q.ClaimedByUsername = "";
                q.ClaimedUtcTicks   = 0;
                q.ReviewState       = QuestEntry.ReviewNotApplicable;
                q.ProofText         = "";
                q.ProofImageUrl     = "";
                poster              = q.PosterUsername;
                ok = true;
            }
            if (ok)
            {
                Reputation.ReputationStore.RecordAbandoned(claimerUsername);
                PlayerStats.PlayerStatsStore.RecordContractFailed(claimerUsername);
                SaveToDisk();
                posterAffected = poster;
                Extensibility.KmhEventBus.Instance.RaiseQuestClaimed(new KMH.Sdk.Server.Events.QuestClaimedEvent { QuestId = questId, ClaimerUsername = "", PosterUsername = poster ?? "" });
            }
            return ok;
        }

        // Client-trusted, so only a small silver-only bounty pays on the report; larger ones wait for review.
        public static bool VerifyComplete(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            QuestsConfig cfg = QuestsConfig.Current;
            int bounty = 0; string poster, owed = null;
            bool routedToReview = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.Kind != QuestEntry.KindEscort && q.Kind != QuestEntry.KindDefend
                    && q.Kind != QuestEntry.KindHunt && q.Kind != QuestEntry.KindBuild) return false;
                if (q.State != QuestEntry.StateClaimed) return false;
                if (!string.Equals(q.ClaimedByUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                // A floor on elapsed time, since the report is client-tracked and a macro could otherwise farm it.
                if (cfg.AutoVerifyMinClaimSeconds > 0 && q.ClaimedUtcTicks > 0
                    && DateTime.UtcNow.Ticks - q.ClaimedUtcTicks < TimeSpan.FromSeconds(cfg.AutoVerifyMinClaimSeconds).Ticks)
                    return false;
                poster = q.PosterUsername;
                if (cfg.AutoVerifyRequiresPosterReview || q.BountySilver > cfg.AutoVerifyMaxBountySilver || HasBountyItems(q))
                {
                    q.State                  = QuestEntry.StatePendingReview;
                    q.ReviewState            = QuestEntry.ReviewPending;
                    q.ProofText              = $"Auto-verified by the game client ({q.Kind}) - awaiting poster sign-off.";
                    q.ProofSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                    routedToReview = true;
                }
                else
                {
                    bounty = q.BountySilver;
                    q.State             = QuestEntry.StateCompleted;
                    q.CompletedUtcTicks = DateTime.UtcNow.Ticks;
                    _lifetimeQuestsCompleted  += 1;
                    _lifetimeBountySilverPaid += bounty;
                    owed = OweLocked(q, "bounty", claimerUsername, bounty, q.BountyItems, $"quest #{questId} auto-verified bounty");
                }
                // Durable before the bounty moves, both for the review route and the immediate payout.
                if (!SaveToDisk())
                {
                    _owed.ForgetLocked(owed);
                    q.State = QuestEntry.StateClaimed; q.CompletedUtcTicks = 0;
                    q.ReviewState = QuestEntry.ReviewNotApplicable; q.ProofSubmittedUtcTicks = 0;
                    if (!routedToReview) { _lifetimeQuestsCompleted -= 1; _lifetimeBountySilverPaid -= bounty; }
                    return false;
                }
            }
            if (routedToReview)
            {
                posterAffected = poster;
                return true;
            }

            _owed.Settle(owed);

            PlayerStats.PlayerStatsStore.BumpQuestsCompleted(claimerUsername);
            Reputation.ReputationStore.RecordCompleted(claimerUsername);
            posterAffected = poster;
            Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = true });
            return true;
        }

        private static bool HasBountyItems(QuestEntry q)
        {
            if (q.BountyItems != null) foreach (int n in q.BountyItems.Values) if (n > 0) return true;
            return false;
        }

        public static bool SubmitProof(string claimerUsername, long questId, string proofText, string proofImageUrl, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            string text = (proofText ?? "").Trim();
            if (text.Length > QuestsConfig.Current.MaxDescriptionLength) text = text.Substring(0, QuestsConfig.Current.MaxDescriptionLength);
            string url = (proofImageUrl ?? "").Trim();
            if (url.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "";
            if (url.Length > 512) url = url.Substring(0, 512);

            string poster = null;
            bool ok = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.State != QuestEntry.StateClaimed && q.State != QuestEntry.StatePendingReview) return false;
                if (!string.Equals(q.ClaimedByUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                q.ProofText              = text;
                q.ProofImageUrl          = url;
                q.ProofSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                q.State                  = QuestEntry.StatePendingReview;
                q.ReviewState            = QuestEntry.ReviewPending;
                poster                   = q.PosterUsername;
                ok = true;
            }
            if (ok) { SaveToDisk(); posterAffected = poster; }
            return ok;
        }

        // Rejecting costs the poster reputation, to deter frivolous rejections; submitted bounties are reviewable too.
        public static bool ReviewProof(string posterUsername, long questId, bool approve, string note, out string claimerAffected)
            => Review(posterUsername, false, questId, approve, note, out claimerAffected, out _);

        // Same transitions as the poster's review, but no reputation is charged to the poster.
        internal static bool AdminReview(string admin, long questId, bool approve, out string claimer, out string poster)
            => Review(admin, true, questId, approve, $"settled by admin {admin}", out claimer, out poster);

        private static bool Review(string actor, bool asAdmin, long questId, bool approve, string note,
                                   out string claimerAffected, out string posterOut)
        {
            claimerAffected = null; posterOut = null;
            if (string.IsNullOrEmpty(actor)) return false;
            string reviewNote = (note ?? "").Trim();
            if (reviewNote.Length > 256) reviewNote = reviewNote.Substring(0, 256);

            int bounty = 0;
            string claimer, owed = null, posterUsername;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.State != QuestEntry.StatePendingReview && q.State != QuestEntry.StateSubmitted) return false;
                if (!asAdmin && !string.Equals(q.PosterUsername, actor, StringComparison.OrdinalIgnoreCase))
                    return false;
                posterUsername = q.PosterUsername;
                claimer = q.ClaimedByUsername;
                string wasState = q.State, wasProof = q.ProofText, wasImage = q.ProofImageUrl;
                long wasClaimed = q.ClaimedUtcTicks, wasSubmitted = q.ProofSubmittedUtcTicks;

                if (approve)
                {
                    bounty = q.BountySilver;
                    owed   = OweLocked(q, "bounty", claimer, bounty, q.BountyItems, $"quest #{questId} bounty (reviewed by {actor})");
                    q.State                  = QuestEntry.StateCompleted;
                    q.CompletedUtcTicks      = DateTime.UtcNow.Ticks;
                    q.ReviewState            = QuestEntry.ReviewApproved;
                    q.ReviewNote             = reviewNote;
                    q.ReviewSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                    _lifetimeQuestsCompleted += 1;
                    _lifetimeBountySilverPaid += bounty;
                }
                else
                {
                    q.State                  = QuestEntry.StateOpen;
                    q.ClaimedByUsername      = "";
                    q.ClaimedUtcTicks        = 0;
                    q.ProofSubmittedUtcTicks = 0;
                    q.ReviewState            = QuestEntry.ReviewRejected;
                    q.ReviewNote             = reviewNote;
                    q.ReviewSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                    q.ProofText              = "";
                    q.ProofImageUrl          = "";
                }
                // Durable with the bounty owed, or a restart returns the quest to review and it pays twice.
                if (!SaveToDisk())
                {
                    _owed.ForgetLocked(owed);
                    q.State = wasState;
                    q.ReviewState = wasState == QuestEntry.StatePendingReview ? QuestEntry.ReviewPending : QuestEntry.ReviewNotApplicable;
                    q.ReviewNote = ""; q.ReviewSubmittedUtcTicks = 0;
                    if (approve)
                    {
                        q.CompletedUtcTicks = 0;
                        _lifetimeQuestsCompleted  -= 1;
                        _lifetimeBountySilverPaid -= bounty;
                    }
                    else
                    {
                        q.ClaimedByUsername = claimer; q.ClaimedUtcTicks = wasClaimed; q.ProofSubmittedUtcTicks = wasSubmitted;
                        q.ProofText = wasProof; q.ProofImageUrl = wasImage;
                    }
                    return false;
                }
            }
            posterOut = posterUsername;

            if (approve)
            {
                _owed.Settle(owed);
                if (!string.IsNullOrEmpty(claimer))
                {
                    PlayerStats.PlayerStatsStore.RecordContractCompleted(claimer, "");
                    Reputation.ReputationStore.RecordCompleted(claimer);
                }
                Global.Federation.FederationService.RecordQuestCompletion(claimer, posterUsername);
                Extensibility.KmhEventBus.Instance.RaiseQuestApproved(new KMH.Sdk.Server.Events.QuestApprovedEvent { QuestId = questId, PosterUsername = posterUsername, ClaimerUsername = claimer ?? "", BountyPaidSilver = bounty });
            }
            else
            {
                if (!string.IsNullOrEmpty(claimer))
                {
                    Reputation.ReputationStore.RecordProofRejected(claimer);
                    PlayerStats.PlayerStatsStore.RecordContractFailed(claimer);
                }
                if (!asAdmin) Reputation.ReputationStore.RecordRejectedAsPoster(posterUsername);
            }
            claimerAffected = claimer;
            return true;
        }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.QuestsFile, out PersistedState state) && state != null)
            {
                lock (_lock)
                {
                    _byId.Clear();
                    _postTx.Clear();
                    _submitting.Clear();
                    _owed.LoadLocked(state.Owed);
                    if (state.PostTx != null) foreach (KeyValuePair<long, string> kv in state.PostTx) _postTx[kv.Key] = kv.Value;
                    if (state.Quests != null)
                    {
                        foreach (QuestEntry q in state.Quests)
                        {
                            if (q == null || q.Id <= 0) continue;
                            _byId[q.Id] = q;
                        }
                    }
                    _nextId                   = Math.Max(state.NextId, 1);
                    _lifetimeQuestsPosted     = state.LifetimeQuestsPosted;
                    _lifetimeQuestsCompleted  = state.LifetimeQuestsCompleted;
                    _lifetimeBountySilverPaid = state.LifetimeBountySilverPaid;
                }
                Diagnostics.ServerLog.Diag($"Quests: loaded {state.Quests?.Count ?? 0} quest(s) from disk");
            }
        }

        // An open quest holds its bounty outside the treasury, so a save reset must drop it or the bounty shelters.
        public static bool HasPosterEscrow(string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock)
                foreach (QuestEntry q in _byId.Values)
                    if (HoldsEscrow(q) && string.Equals(q.PosterUsername, user, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static int PurgePoster(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            // Goods that already reached the poster were the claimer's, so that delivery is paid as Submit would pay it.
            var delivered = new List<long>();
            lock (_lock)
                foreach (QuestEntry q in _byId.Values)
                    if (q.Kind == QuestEntry.KindDeliverItem && q.State == QuestEntry.StateClaimed
                        && string.Equals(q.PosterUsername, user, StringComparison.OrdinalIgnoreCase)) delivered.Add(q.Id);
            foreach (long id in delivered) TryCompleteHandedOver(id);
            List<long> ids = new List<long>();
            lock (_lock)
            {
                foreach (QuestEntry q in _byId.Values)
                    if (HoldsEscrow(q) && string.Equals(q.PosterUsername, user, StringComparison.OrdinalIgnoreCase)) ids.Add(q.Id);
                foreach (long id in ids) _byId.Remove(id);
            }
            if (ids.Count > 0) SaveToDisk();
            return ids.Count;
        }

        internal static int OpenCountForTest(string posterUsername) { lock (_lock) return OpenCountLocked(posterUsername); }

        // A delivery row with no target, as a hand-edited or pre-validation save can hold one.
        internal static bool ClearTargetForTest(long questId)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                q.TargetItemDefName = "";
                q.TargetItemQty = 0;
                return true;
            }
        }

        // Stands up a guild-only row as older saves hold them; PostDraft can no longer make one.
        internal static bool MakeGuildOnlyForTest(long questId, string guildName)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                q.Visibility = QuestEntry.VisibilityGuildOnly;
                q.PosterTreasuryKey = guildName;
                return true;
            }
        }

        // One definition of "open", used by both cap checks - the pre-escrow one and the re-check that commits.
        private static int OpenCountLocked(string posterUsername)
        {
            int open = 0;
            foreach (QuestEntry q in _byId.Values)
                if (string.Equals(q.PosterUsername, posterUsername, StringComparison.OrdinalIgnoreCase) && HoldsEscrow(q))
                    open++;
            return open;
        }

        // States before the bounty settles: it is still escrowed and would be refunded on cancel/expire.
        private static bool HoldsEscrow(QuestEntry q)
            => q != null && (q.State == QuestEntry.StateOpen || q.State == QuestEntry.StateClaimed
                          || q.State == QuestEntry.StateSubmitted || q.State == QuestEntry.StatePendingReview);

        public static void ClearForNewSeason()
        {
            lock (_lock)
            {
                _byId.Clear();
                // A season wipe empties every vault these would pay into, so they go with it.
                _owed.ClearLocked();
                _nextId = 1;
                _lifetimeQuestsPosted = 0;
                _lifetimeQuestsCompleted = 0;
                _lifetimeBountySilverPaid = 0;
            }
            SaveToDisk();
        }

        // False means in-memory only; a state change the disk never took can leave an escrowed bounty payable twice.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Quests                    = KmhDeepCopy.OfList(_byId.Values);
                state.NextId                    = _nextId;
                state.LifetimeQuestsPosted      = _lifetimeQuestsPosted;
                state.LifetimeQuestsCompleted   = _lifetimeQuestsCompleted;
                state.LifetimeBountySilverPaid  = _lifetimeBountySilverPaid;
                foreach (KeyValuePair<long, string> kv in _postTx) if (_byId.ContainsKey(kv.Key)) state.PostTx[kv.Key] = kv.Value;
                state.Owed                      = _owed.SnapshotLocked();
                seq = JsonFileStore.NextSequence(); // ticket under the lock = snapshot order, so an older save can't clobber a newer
            }
            return JsonFileStore.Save(KmhDataPaths.QuestsFile, state, seq);
        }

        private class PersistedState
        {
            public List<QuestEntry> Quests                    { get; set; } = new List<QuestEntry>();
            public long             NextId                    { get; set; } = 1;
            public long             LifetimeQuestsPosted      { get; set; } = 0;
            public long             LifetimeQuestsCompleted   { get; set; } = 0;
            public long             LifetimeBountySilverPaid  { get; set; } = 0;
            public Dictionary<long, string> PostTx            { get; set; } = new Dictionary<long, string>();
            // Absent in older files, which simply had no payout in flight.
            public List<Transactions.KmhOwedPayout> Owed      { get; set; } = new List<Transactions.KmhOwedPayout>();
        }

        internal static QuestEntry CopyForTest(QuestEntry q) => CopyLocked(q);

        private static QuestEntry CopyLocked(QuestEntry q)
        {
            return new QuestEntry
            {
                Id                  = q.Id,
                Kind                = q.Kind,
                State               = q.State,
                Visibility          = q.Visibility,
                PosterUsername      = q.PosterUsername,
                PosterTreasuryKey   = q.PosterTreasuryKey,
                Title               = q.Title,
                Description         = q.Description,
                BountySilver        = q.BountySilver,
                // A hand-edited Quests.json can say null here, and one throw would empty the board for every viewer.
                BountyItems         = q.BountyItems == null
                                      ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                                      : new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase),
                TargetItemDefName   = q.TargetItemDefName,
                TargetItemQty       = q.TargetItemQty,
                TargetQualityIndex  = q.TargetQualityIndex,
                TargetTreasuryKey   = q.TargetTreasuryKey,
                PostedUtcTicks      = q.PostedUtcTicks,
                ExpiresUtcTicks     = q.ExpiresUtcTicks,
                ClaimedByUsername   = q.ClaimedByUsername,
                ClaimedUtcTicks     = q.ClaimedUtcTicks,
                CompletedUtcTicks   = q.CompletedUtcTicks,
                EscortPickupTile        = q.EscortPickupTile,
                EscortDropoffTile       = q.EscortDropoffTile,
                EscortTargetDescription = q.EscortTargetDescription,
                DefendColonyTile        = q.DefendColonyTile,
                DefendDurationGameTicks = q.DefendDurationGameTicks,
                HuntTargetKind          = q.HuntTargetKind,
                HuntTargetDefName       = q.HuntTargetDefName,
                HuntTargetCount         = q.HuntTargetCount,
                BuildAtTile             = q.BuildAtTile,
                BuildStructureDefName   = q.BuildStructureDefName,
                BuildCount              = q.BuildCount,
                ProofText               = q.ProofText,
                ProofImageUrl           = q.ProofImageUrl,
                ProofSubmittedUtcTicks  = q.ProofSubmittedUtcTicks,
                ReviewState             = q.ReviewState,
                ReviewNote              = q.ReviewNote,
                ReviewSubmittedUtcTicks = q.ReviewSubmittedUtcTicks,
            };
        }
    }
}
