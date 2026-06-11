using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Quests
{
    // Quest board, persisted to KMH-Data/Quests/Quests.json. Bounty (silver + items) is escrowed from the poster's
    // treasury at Post and flows to the claimer on completion, back to the poster on cancel/expire. Lock-guarded
    // like the other stores
    internal static class QuestStore
    {
        private static readonly object _lock = new object();

        private static readonly Dictionary<long, QuestEntry> _byId = new Dictionary<long, QuestEntry>();
        private static long _nextId = 1;

        private static long _lifetimeQuestsPosted     = 0;
        private static long _lifetimeQuestsCompleted  = 0;
        private static long _lifetimeBountySilverPaid = 0;

        // Forged-packet caps live in config/quests.json (QuestsConfig) so owners can tune them without code

        // Per-caller snapshot. Filters out guild-only quests the caller isn't entitled to see (own guild + allied
        // guilds get visibility). Caller's own posts always pass through - managing what you posted shouldn't
        // depend on still being in the same guild
        public static QuestSnapshot BuildSnapshot(string callerUsername)
        {
            QuestSnapshot s = new QuestSnapshot();
            // Resolve once outside the per-quest loop - see Marketplace Store.BuildSnapshot for the rationale
            string callerGuild = string.IsNullOrEmpty(callerUsername)
                ? null
                : Guilds.GuildStore.CurrentGuildOf(callerUsername);
            lock (_lock)
            {
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

        // Thin wrapper for the SDK + simple callers (DeliverItem / Bounty). Builds a draft and routes through
        // PostDraft so all kinds share one validation + escrow path
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

        // Full post path: per-kind validation + forged-packet caps + bounty silver AND item escrow + per-user
        // open-quest cap. Returns the new quest id or 0 with a human-readable reason. expiresInHours <= 0 uses the
        // default lifetime
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

            // Per-user open-quest cap (a single client's packets are serial, so counting under the lock is
            // sufficient)
            lock (_lock)
            {
                int open = 0;
                foreach (QuestEntry q in _byId.Values)
                    if (string.Equals(q.PosterUsername, posterUsername, StringComparison.OrdinalIgnoreCase)
                        && (q.State == QuestEntry.StateOpen || q.State == QuestEntry.StateClaimed
                            || q.State == QuestEntry.StateSubmitted || q.State == QuestEntry.StatePendingReview))
                        open++;
                if (open >= QuestsConfig.Current.MaxOpenPerUser)
                { reason = $"You already have the max {QuestsConfig.Current.MaxOpenPerUser} active quests."; return 0; }
            }

            // Escrow bounty silver.
            if (draft.BountySilver > 0 &&
                !Treasury.TreasuryStore.WithdrawSilver(posterUsername, draft.BountySilver, note: "quest bounty escrow"))
            { reason = "Your treasury is short on bounty silver."; return 0; }

            // Escrow bounty items (with rollback on shortfall).
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
                    int want = kv.Value > 1_000_000 ? 1_000_000 : kv.Value;

                    if (!Treasury.TreasuryStore.WithdrawItem(posterUsername, itemKey, want, note: "quest bounty escrow"))
                    {
                        // Roll back: return already-escrowed items + the silver.
                        foreach (KeyValuePair<string, int> prev in escrowed)
                            Treasury.TreasuryStore.DepositItem(posterUsername, prev.Key, prev.Value, note: "quest-post-failed refund");
                        if (draft.BountySilver > 0)
                            Treasury.TreasuryStore.DepositSilver(posterUsername, draft.BountySilver, note: "quest-post-failed refund");
                        reason = $"Your treasury is short on bounty item '{itemKey}'.";
                        return 0;
                    }
                    escrowed[itemKey] = want;
                }
            }

            // Solo posters can't scope GuildOnly - downgrade to Public.
            string posterKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(posterUsername);
            string visibility = string.IsNullOrEmpty(draft.Visibility) ? QuestEntry.VisibilityPublic : draft.Visibility;
            bool posterIsGuild = !posterKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase);
            if (visibility == QuestEntry.VisibilityGuildOnly && !posterIsGuild)
                visibility = QuestEntry.VisibilityPublic;

            long now = DateTime.UtcNow.Ticks;
            long assignedId;
            lock (_lock)
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
                    TargetItemDefName = draft.TargetItemDefName ?? "",
                    TargetItemQty     = draft.TargetItemQty,
                    PostedUtcTicks    = now,
                    ExpiresUtcTicks   = now + TimeSpan.FromHours(expiresInHours > 0 ? expiresInHours : 168).Ticks,
                    // Per-kind params.
                    EscortPickupTile        = draft.EscortPickupTile,
                    EscortDropoffTile       = draft.EscortDropoffTile,
                    EscortTargetDescription = (draft.EscortTargetDescription ?? "").Length > 200
                                                ? draft.EscortTargetDescription.Substring(0, 200)
                                                : (draft.EscortTargetDescription ?? ""),
                    DefendColonyTile        = draft.DefendColonyTile,
                    DefendDurationGameTicks = draft.DefendDurationGameTicks,
                    HuntTargetKind          = string.IsNullOrEmpty(draft.HuntTargetKind) ? QuestEntry.HuntNone : draft.HuntTargetKind,
                    HuntTargetDefName       = draft.HuntTargetDefName ?? "",
                    HuntTargetCount         = draft.HuntTargetCount,
                    BuildAtTile             = draft.BuildAtTile,
                    BuildStructureDefName   = draft.BuildStructureDefName ?? "",
                    BuildCount              = draft.BuildCount,
                    ReviewState             = QuestEntry.ReviewNotApplicable,
                };
                _lifetimeQuestsPosted += 1;
            }
            SaveToDisk();
            Reputation.ReputationStore.EnsurePlayer(posterUsername);
            Extensibility.KmhEventBus.Instance.RaiseQuestPosted(new KMH.Sdk.Server.Events.QuestPostedEvent { QuestId = assignedId, Kind = kind, PosterUsername = posterUsername, BountySilver = draft.BountySilver });
            reason = $"Posted quest #{assignedId}: {title}.";
            return assignedId;
        }

        // Per-kind validation + clamping.
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
                    return true;

                case QuestEntry.KindBounty:
                    return true; // free-form

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

        public static bool Claim(string claimerUsername, long questId)
        {
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            bool ok = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q))      goto done;
                if (q.State != QuestEntry.StateOpen)                    goto done;
                if (string.Equals(q.PosterUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    goto done; // can't claim own quest

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

        // Returns true on successful state transition (Completed for DeliverItem auto-verify, Submitted for Bounty
        // manual sign-off). Returns the OTHER party affected by the submission via the out params - handler uses
        // these to push them a fresh treasury snapshot directly instead of waiting for the auto-refresh tick. For
        // DeliverItem auto-complete the poster's treasury changes (items deposited); for Bounty Submit no other
        // treasury changes until Approve
        public static bool Submit(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            // Eligibility check + state transition happen TOGETHER under the lock. For DeliverItem we flip straight
            // to Completed here (before the treasury moves below) so a second concurrent Submit can't also pass the
            // Claimed check on the same quest; on a treasury failure we revert to Claimed. Fields the treasury
            // moves need are captured while we hold the lock
            string kind;
            string targetDef = null, poster = null;
            int    targetQty = 0, bounty = 0;
            Dictionary<string, int> bountyItems = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (!string.Equals(q.ClaimedByUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                kind = q.Kind;

                if (kind == QuestEntry.KindBounty)
                {
                    // Bounty: Claimed -> Submitted (idempotent re-submit ok).
                    if (q.State != QuestEntry.StateClaimed && q.State != QuestEntry.StateSubmitted) return false;
                    q.State = QuestEntry.StateSubmitted;
                }
                else
                {
                    // DeliverItem: only a Claimed quest can submit. Reserve the completion atomically with the
                    // check
                    if (q.State != QuestEntry.StateClaimed) return false;
                    targetDef   = q.TargetItemDefName;
                    targetQty   = q.TargetItemQty;
                    bounty      = q.BountySilver;
                    poster      = q.PosterUsername;
                    bountyItems = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);
                    q.State             = QuestEntry.StateCompleted;
                    q.CompletedUtcTicks = DateTime.UtcNow.Ticks;
                }
            }

            if (kind == QuestEntry.KindBounty)
            {
                SaveToDisk();
                Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = false });
                return true;
            }

            // DeliverItem: server-verifiable. Move items claimer -> poster, pay bounty to claimer. The quest is
            // already marked Completed
            if (!Treasury.TreasuryStore.WithdrawItem(claimerUsername, targetDef, targetQty,
                    note: $"quest #{questId} delivery"))
            {
                // Claimer doesn't have the goods - revert the optimistic completion so the quest stays Claimed and
                // can be retried
                lock (_lock)
                {
                    if (_byId.TryGetValue(questId, out QuestEntry q2))
                    {
                        q2.State             = QuestEntry.StateClaimed;
                        q2.CompletedUtcTicks = 0;
                    }
                }
                return false;
            }
            Treasury.TreasuryStore.DepositItem(poster, targetDef, targetQty,
                note: $"quest #{questId} delivery from {claimerUsername}");

            if (bounty > 0)
            {
                Treasury.TreasuryStore.DepositSilver(claimerUsername, bounty,
                    note: $"quest #{questId} bounty");
            }
            DepositBountyItems(bountyItems, claimerUsername, $"quest #{questId} bounty");

            lock (_lock)
            {
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += bounty;
            }

            // Cross-feature: leaderboard + reputation credit on the claimer.
            PlayerStats.PlayerStatsStore.BumpQuestsCompleted(claimerUsername);
            Reputation.ReputationStore.RecordCompleted(claimerUsername);
            // Poster's treasury just received the delivered items.
            posterAffected = poster;
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = true });
            return true;
        }

        // Server-initiated expiry. Only acts on Open quests - Claimed / Submitted / Completed quests keep their
        // state (claimer might be mid-delivery; we don't want to nuke their progress). Returns the poster's
        // username so the sweeper can push them a treasury update for the bounty refund
        public static bool ExpireQuest(long questId, out string posterAffected)
        {
            posterAffected = null;
            int    refundSilver = 0;
            string poster;
            Dictionary<string, int> refundItems = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.State != QuestEntry.StateOpen) return false;
                refundSilver = q.BountySilver;
                refundItems  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);
                poster       = q.PosterUsername;
                q.State      = QuestEntry.StateExpired;
                // Expired quests drop off the active board same as Cancelled
                // - display-only history is a follow-up if anyone wants it.
                _byId.Remove(questId);
            }
            if (refundSilver > 0 && !string.IsNullOrEmpty(poster))
            {
                Treasury.TreasuryStore.DepositSilver(poster, refundSilver,
                    note: $"quest #{questId} expired - bounty refund");
            }
            DepositBountyItems(refundItems, poster, $"quest #{questId} expired - bounty refund");
            posterAffected = poster;
            SaveToDisk();
            return true;
        }

        // Drop Completed quests older than a day so the board + on-disk file
        // don't grow without bound (Cancelled/Expired are removed immediately;
        // only Completed lingers for a brief "recently done" window). Returns true if anything was pruned
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
                if (drop == null) return false;
                foreach (long id in drop) _byId.Remove(id);
                changed = true;
            }
            if (changed) SaveToDisk();
            return changed;
        }

        // Open quests with ExpiresUtcTicks > 0 and <= now. Used by the periodic sweeper; release-the-lock +
        // iterate-outside pattern so the actual ExpireQuest calls don't hold the store lock
        public static List<long> CollectExpiredOpenIds(long nowTicks)
        {
            List<long> expired = new List<long>();
            lock (_lock)
            {
                foreach (QuestEntry q in _byId.Values)
                {
                    if (q.State == QuestEntry.StateOpen
                        && q.ExpiresUtcTicks > 0
                        && nowTicks >= q.ExpiresUtcTicks)
                    {
                        expired.Add(q.Id);
                    }
                }
            }
            return expired;
        }

        public static bool Cancel(string callerUsername, long questId)
        {
            if (string.IsNullOrEmpty(callerUsername)) return false;

            int    refundSilver  = 0;
            Dictionary<string, int> refundItems = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (!string.Equals(q.PosterUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
                    return false; // only poster can cancel
                if (q.State != QuestEntry.StateOpen) return false; // only Open cancels in v1

                refundSilver = q.BountySilver;
                refundItems  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);
                q.State = QuestEntry.StateCancelled;
                _byId.Remove(questId); // cancelled quests drop off the board immediately
            }

            if (refundSilver > 0)
            {
                Treasury.TreasuryStore.DepositSilver(callerUsername, refundSilver,
                    note: $"quest #{questId} cancel refund");
            }
            DepositBountyItems(refundItems, callerUsername, $"quest #{questId} cancel refund");
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseQuestCancelled(new KMH.Sdk.Server.Events.QuestCancelledEvent { QuestId = questId, PosterUsername = callerUsername, ExpiredAutomatically = false });
            return true;
        }

        // Bounty-kind sign-off. Poster reviews the claimer's work (out of band - chat / Discord / screenshot etc.)
        // and calls this to pay out + mark Completed
        //
        // Rejects when:
        //   - quest not found
        //   - kind != Bounty (DeliverItem auto-completes in Submit)
        //   - state != Submitted
        //   - caller != poster
        // Returns the claimer's username via out param so the handler can push them a fresh treasury snapshot (they
        // just received bounty silver)
        public static bool Approve(string callerUsername, long questId, out string claimerAffected)
        {
            claimerAffected = null;
            if (string.IsNullOrEmpty(callerUsername)) return false;

            int bountySilver;
            string claimer;
            Dictionary<string, int> bountyItems;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                // Poster sign-off applies to Bounty (Submitted) and any kind whose claimer submitted proof /
                // auto-verified (PendingReview)
                if (q.Kind != QuestEntry.KindBounty && q.State != QuestEntry.StatePendingReview) return false;
                if (q.State != QuestEntry.StateSubmitted && q.State != QuestEntry.StatePendingReview) return false;
                if (!string.Equals(q.PosterUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                bountySilver = q.BountySilver;
                claimer      = q.ClaimedByUsername;
                bountyItems  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);

                // Flip to Completed + record the payout atomically with the eligibility check so a concurrent
                // Approve can't double-pay the bounty. The actual silver credit happens after the lock
                // (DepositSilver takes its own lock); it can't fail for valid input, so there's nothing to revert
                q.State              = QuestEntry.StateCompleted;
                q.CompletedUtcTicks  = DateTime.UtcNow.Ticks;
                q.ReviewState        = QuestEntry.ReviewApproved;
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += bountySilver;
            }

            if (bountySilver > 0 && !string.IsNullOrEmpty(claimer))
            {
                Treasury.TreasuryStore.DepositSilver(claimer, bountySilver,
                    note: $"quest #{questId} bounty (approved by {callerUsername})");
            }
            DepositBountyItems(bountyItems, claimer, $"quest #{questId} bounty (approved by {callerUsername})");

            if (!string.IsNullOrEmpty(claimer))
            {
                PlayerStats.PlayerStatsStore.BumpQuestsCompleted(claimer);
                Reputation.ReputationStore.RecordCompleted(claimer);
            }
            claimerAffected = claimer;
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseQuestApproved(new KMH.Sdk.Server.Events.QuestApprovedEvent { QuestId = questId, PosterUsername = callerUsername, ClaimerUsername = claimer ?? "", BountyPaidSilver = bountySilver });
            return true;
        }

        // Deposit a bounty's item bundle to a user. No-op on null/empty.
        private static void DepositBountyItems(Dictionary<string, int> items, string toUser, string note)
        {
            if (items == null || string.IsNullOrEmpty(toUser)) return;
            foreach (KeyValuePair<string, int> kv in items)
                if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key))
                    Treasury.TreasuryStore.DepositItem(toUser, kv.Key, kv.Value, note: note);
        }

        // Claimer drops a quest they claimed (before completion). Returns it to the board (Open) and applies the
        // abandonment reputation penalty
        public static bool Abandon(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;
            string poster = null;
            bool ok = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.State != QuestEntry.StateClaimed && q.State != QuestEntry.StateSubmitted
                    && q.State != QuestEntry.StatePendingReview) return false;
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
                SaveToDisk();
                posterAffected = poster;
                Extensibility.KmhEventBus.Instance.RaiseQuestClaimed(new KMH.Sdk.Server.Events.QuestClaimedEvent { QuestId = questId, ClaimerUsername = "", PosterUsername = poster ?? "" });
            }
            return ok;
        }

        // Client-reported auto-verify for the verifiable kinds (escort / defend / hunt / build). The patched client
        // detected the in-game event and reports it; we trust the report and pay the escrowed bounty out
        // immediately. Client-trusted, but the bounty was offered by the poster and is server-escrowed, so a forged
        // report only collects a bounty the poster put up - they can dispute out of band (admin claw-back)
        public static bool VerifyComplete(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            int bounty; string poster;
            Dictionary<string, int> items;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.Kind != QuestEntry.KindEscort && q.Kind != QuestEntry.KindDefend
                    && q.Kind != QuestEntry.KindHunt && q.Kind != QuestEntry.KindBuild) return false;
                if (q.State != QuestEntry.StateClaimed) return false;
                if (!string.Equals(q.ClaimedByUsername, claimerUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                bounty = q.BountySilver;
                poster = q.PosterUsername;
                items  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);
                q.State             = QuestEntry.StateCompleted;
                q.CompletedUtcTicks = DateTime.UtcNow.Ticks;
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += bounty;
            }

            if (bounty > 0)
                Treasury.TreasuryStore.DepositSilver(claimerUsername, bounty, note: $"quest #{questId} auto-verified bounty");
            DepositBountyItems(items, claimerUsername, $"quest #{questId} auto-verified bounty");

            PlayerStats.PlayerStatsStore.BumpQuestsCompleted(claimerUsername);
            Reputation.ReputationStore.RecordCompleted(claimerUsername);
            posterAffected = poster;
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = true });
            return true;
        }

        // Claimer submits proof (text + optional https image url) for a Custom
        // quest. Moves it to PendingReview; the poster decides via ReviewProof.
        public static bool SubmitProof(string claimerUsername, long questId, string proofText, string proofImageUrl, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            string text = (proofText ?? "").Trim();
            if (text.Length > QuestsConfig.Current.MaxDescriptionLength) text = text.Substring(0, QuestsConfig.Current.MaxDescriptionLength);
            string url = (proofImageUrl ?? "").Trim();
            // Only accept https image links.
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

        // Poster reviews a PendingReview submission. approve -> payout + Complete (+claimer reputation). reject ->
        // back to Open with a note (+claimer -2, poster -1 to deter frivolous rejection)
        public static bool ReviewProof(string posterUsername, long questId, bool approve, string note, out string claimerAffected)
        {
            claimerAffected = null;
            if (string.IsNullOrEmpty(posterUsername)) return false;
            string reviewNote = (note ?? "").Trim();
            if (reviewNote.Length > 256) reviewNote = reviewNote.Substring(0, 256);

            int bounty = 0;
            string claimer;
            Dictionary<string, int> items = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(questId, out QuestEntry q)) return false;
                if (q.State != QuestEntry.StatePendingReview) return false;
                if (!string.Equals(q.PosterUsername, posterUsername, StringComparison.OrdinalIgnoreCase))
                    return false;
                claimer = q.ClaimedByUsername;

                if (approve)
                {
                    bounty = q.BountySilver;
                    items  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);
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
                    // Reject -> the quest returns to the board for a fresh claim.
                    q.State                  = QuestEntry.StateOpen;
                    q.ClaimedByUsername      = "";
                    q.ClaimedUtcTicks        = 0;
                    q.ReviewState            = QuestEntry.ReviewRejected;
                    q.ReviewNote             = reviewNote;
                    q.ReviewSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                    q.ProofText              = "";
                    q.ProofImageUrl          = "";
                }
            }

            if (approve)
            {
                if (bounty > 0 && !string.IsNullOrEmpty(claimer))
                    Treasury.TreasuryStore.DepositSilver(claimer, bounty, note: $"quest #{questId} bounty (reviewed by {posterUsername})");
                DepositBountyItems(items, claimer, $"quest #{questId} bounty (reviewed by {posterUsername})");
                if (!string.IsNullOrEmpty(claimer))
                {
                    PlayerStats.PlayerStatsStore.BumpQuestsCompleted(claimer);
                    Reputation.ReputationStore.RecordCompleted(claimer);
                }
                Extensibility.KmhEventBus.Instance.RaiseQuestApproved(new KMH.Sdk.Server.Events.QuestApprovedEvent { QuestId = questId, PosterUsername = posterUsername, ClaimerUsername = claimer ?? "", BountyPaidSilver = bounty });
            }
            else
            {
                if (!string.IsNullOrEmpty(claimer))
                    Reputation.ReputationStore.RecordProofRejected(claimer);
                // Poster eats a small penalty to discourage frivolous rejection.
                Reputation.ReputationStore.RecordRejectedAsPoster(posterUsername);
            }
            claimerAffected = claimer;
            SaveToDisk();
            return true;
        }

        // --- persistence ---

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.QuestsFile, out PersistedState state) && state != null)
            {
                lock (_lock)
                {
                    _byId.Clear();
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
                Diagnostics.ServerLog.Info($"Quests: loaded {state.Quests?.Count ?? 0} quest(s) from disk");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Quests                    = new List<QuestEntry>(_byId.Values);
                state.NextId                    = _nextId;
                state.LifetimeQuestsPosted      = _lifetimeQuestsPosted;
                state.LifetimeQuestsCompleted   = _lifetimeQuestsCompleted;
                state.LifetimeBountySilverPaid  = _lifetimeBountySilverPaid;
            }
            JsonFileStore.Save(KmhDataPaths.QuestsFile, state);
        }

        private class PersistedState
        {
            public List<QuestEntry> Quests                    { get; set; } = new List<QuestEntry>();
            public long             NextId                    { get; set; } = 1;
            public long             LifetimeQuestsPosted      { get; set; } = 0;
            public long             LifetimeQuestsCompleted   { get; set; } = 0;
            public long             LifetimeBountySilverPaid  { get; set; } = 0;
        }

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
                BountyItems         = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase),
                TargetItemDefName   = q.TargetItemDefName,
                TargetItemQty       = q.TargetItemQty,
                TargetTreasuryKey   = q.TargetTreasuryKey,
                PostedUtcTicks      = q.PostedUtcTicks,
                ExpiresUtcTicks     = q.ExpiresUtcTicks,
                ClaimedByUsername   = q.ClaimedByUsername,
                ClaimedUtcTicks     = q.ClaimedUtcTicks,
                CompletedUtcTicks   = q.CompletedUtcTicks,
                // Per-kind params + proof/review.
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
