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

        // 0 with a reason on refusal; expiresInHours of 0 or less takes the configured lifetime.
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

            if (draft.BountySilver > 0 &&
                !Treasury.TreasuryStore.WithdrawSilver(posterUsername, draft.BountySilver, note: "quest bounty escrow"))
            { reason = "Your treasury is short on bounty silver."; return 0; }

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
                        foreach (KeyValuePair<string, int> prev in escrowed)
                            Items.KmhPayloadEscrow.DeliverCompact(posterUsername, prev.Key, prev.Value, "quest-post-failed refund", "quest bounty item refund could not be returned");
                        if (draft.BountySilver > 0)
                            Items.KmhPayloadEscrow.DeliverSilver(posterUsername, draft.BountySilver, "quest-post-failed refund", "quest bounty refund could not be credited");
                        reason = $"Your treasury is short on bounty item '{itemKey}'.";
                        return 0;
                    }
                    escrowed[itemKey] = want;
                }
            }

            // A solo poster has no guild to scope to, so GuildOnly would hide the quest from everyone.
            string posterKey = Treasury.TreasuryStore.ResolveOwnerKeyFor(posterUsername);
            string visibility = string.IsNullOrEmpty(draft.Visibility) ? QuestEntry.VisibilityPublic : draft.Visibility;
            bool posterIsGuild = !posterKey.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase);
            if (visibility == QuestEntry.VisibilityGuildOnly && !posterIsGuild)
                visibility = QuestEntry.VisibilityPublic;

            long now = DateTime.UtcNow.Ticks;
            long assignedId = 0;
            bool overCap;
            lock (_lock)
            {
                // Re-counted because the escrow above released the lock: two posts racing for one slot both passed the first check.
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
                    ExpiresUtcTicks   = now + TimeSpan.FromHours(expiresInHours > 0 ? expiresInHours : 168).Ticks,
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
            }
            // Lost the race for the last slot, so the escrow goes straight back rather than being kept for nothing.
            if (overCap)
            {
                RefundBounty(posterUsername, escrowed, draft.BountySilver, "quest post refund (open-quest limit reached)");
                reason = $"You already have the max {QuestsConfig.Current.MaxOpenPerUser} active quests - your bounty was returned.";
                return 0;
            }
            // The bounty already left the poster's treasury, so an unwritten post hands the whole escrow straight back.
            if (!SaveToDisk())
            {
                lock (_lock) { _byId.Remove(assignedId); _lifetimeQuestsPosted -= 1; }
                RefundBounty(posterUsername, escrowed, draft.BountySilver, "quest post could not be saved");
                reason = "The server couldn't save that quest - your bounty was returned. Try again shortly.";
                return 0;
            }
            Reputation.ReputationStore.EnsurePlayer(posterUsername);
            Extensibility.KmhEventBus.Instance.RaiseQuestPosted(new KMH.Sdk.Server.Events.QuestPostedEvent { QuestId = assignedId, Kind = kind, PosterUsername = posterUsername, BountySilver = draft.BountySilver });
            reason = $"Posted quest #{assignedId}: {title}.";
            return assignedId;
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

        // Returns the other affected party, so the handler can push them a fresh treasury snapshot.
        public static bool Submit(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            // The state flips under the same lock as the eligibility check, or a second Submit could also pass it.
            string kind;
            string targetDef = null, poster = null;
            int    targetQty = 0, bounty = 0, targetQual = 0;
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
                    if (q.State != QuestEntry.StateClaimed) return false;
                    targetDef   = q.TargetItemDefName;
                    targetQty   = q.TargetItemQty;
                    targetQual  = q.TargetQualityIndex;
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

            // The quest is already marked Completed above, so a failure here has to revert it.
            List<KeyValuePair<string, int>> compactPaid = null;
            List<Items.KmhThingPayload>     payloadPaid = null;
            if (Treasury.TreasuryStore.TryWithdrawMatching(claimerUsername, targetDef, targetQual, targetQty,
                    $"quest #{questId} delivery", out var deliveredKeys))
            {
                compactPaid = deliveredKeys;
            }
            else
            {
                // The def may be held as full-state payloads instead, which the compact withdraw cannot see.
                List<Items.KmhThingPayload> paid = Treasury.TreasuryStore.TryWithdrawMatchingPayloads(
                    claimerUsername, targetDef, "", targetQual, allowTainted: true, allowDamaged: true, targetQty,
                    $"quest #{questId} delivery");
                int got = 0; foreach (Items.KmhThingPayload p in paid) got += p.StackCount;
                if (got < targetQty)
                {
                    // Reverts the optimistic completion so the quest stays Claimed and can be retried.
                    if (paid.Count > 0) Items.KmhPayloadEscrow.RefundTo(claimerUsername, paid, $"quest #{questId} delivery returned");
                    lock (_lock)
                    {
                        if (_byId.TryGetValue(questId, out QuestEntry q2)) { q2.State = QuestEntry.StateClaimed; q2.CompletedUtcTicks = 0; }
                    }
                    return false;
                }
                payloadPaid = paid;
            }

            // Completion is durable before the bounty is released, or a restart finds the quest still Claimed with a payable escrow.
            bool unwind;
            lock (_lock)
            {
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += bounty;
                unwind = !SaveToDisk();
                if (unwind)
                {
                    _lifetimeQuestsCompleted  -= 1;
                    _lifetimeBountySilverPaid -= bounty;
                    if (_byId.TryGetValue(questId, out QuestEntry q3)) { q3.State = QuestEntry.StateClaimed; q3.CompletedUtcTicks = 0; }
                }
            }
            // Returned outside the lock: the treasury has one of its own, and nesting them is how a deadlock starts.
            if (unwind)
            {
                if (compactPaid != null)
                    foreach (var kv in compactPaid)
                        Items.KmhPayloadEscrow.DeliverCompact(claimerUsername, kv.Key, kv.Value, $"quest #{questId} delivery returned", "quest delivery return failed");
                if (payloadPaid != null) Items.KmhPayloadEscrow.RefundTo(claimerUsername, payloadPaid, $"quest #{questId} delivery returned");
                return false;
            }

            // The claimer has already paid, so a failed hand-off is held rather than dropped.
            if (compactPaid != null)
                foreach (var kv in compactPaid)
                    Items.KmhPayloadEscrow.DeliverCompact(poster, kv.Key, kv.Value,
                        $"quest #{questId} delivery from {claimerUsername}", "quest delivery to poster failed");
            if (payloadPaid != null)
                foreach (Items.KmhThingPayload p in payloadPaid)
                    Items.KmhPayloadEscrow.Deliver(poster, p, $"quest #{questId} delivery from {claimerUsername}", "quest delivery to poster failed");

            if (bounty > 0)
            {
                Items.KmhPayloadEscrow.DeliverSilver(claimerUsername, bounty,
                    $"quest #{questId} bounty", "quest bounty could not be credited");
            }
            DepositBountyItems(bountyItems, claimerUsername, $"quest #{questId} bounty");

            PlayerStats.PlayerStatsStore.RecordContractCompleted(claimerUsername, kind);
            Reputation.ReputationStore.RecordCompleted(claimerUsername);
            posterAffected = poster;
            Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = true });
            return true;
        }

        // Open quests only, since a claimer may be mid-delivery on any later state.
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
                _byId.Remove(questId);
                // Durable before the bounty comes back, or the sweeper expires and refunds the same quest again.
                if (!SaveToDisk()) { q.State = QuestEntry.StateOpen; _byId[questId] = q; return false; }
            }
            Items.KmhPayloadEscrow.DeliverSilver(poster, refundSilver,
                $"quest #{questId} expired - bounty refund", "expired quest bounty could not be refunded");
            DepositBountyItems(refundItems, poster, $"quest #{questId} expired - bounty refund");
            posterAffected = poster;
            SaveToDisk();
            return true;
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
                // Durable before the bounty comes back, or a restart puts the quest back on the board with an escrow the poster already holds.
                if (!SaveToDisk()) { q.State = QuestEntry.StateOpen; _byId[questId] = q; return false; }
            }

            if (refundSilver > 0)
            {
                Items.KmhPayloadEscrow.DeliverSilver(callerUsername, refundSilver,
                    $"quest #{questId} cancel refund", "quest cancel refund could not be credited");
            }
            DepositBountyItems(refundItems, callerUsername, $"quest #{questId} cancel refund");
            Extensibility.KmhEventBus.Instance.RaiseQuestCancelled(new KMH.Sdk.Server.Events.QuestCancelledEvent { QuestId = questId, PosterUsername = callerUsername, ExpiredAutomatically = false });
            return true;
        }

        // Returns the claimer, so the handler can push them a fresh treasury snapshot.
        public static bool Approve(string callerUsername, long questId, out string claimerAffected)
        {
            claimerAffected = null;
            if (string.IsNullOrEmpty(callerUsername)) return false;

            int bountySilver;
            string claimer;
            string kind = "";
            Dictionary<string, int> bountyItems;
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
                bountyItems  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);

                // Recorded atomically with the eligibility check, or a concurrent Approve could double-pay.
                q.State              = QuestEntry.StateCompleted;
                q.CompletedUtcTicks  = DateTime.UtcNow.Ticks;
                q.ReviewState        = QuestEntry.ReviewApproved;
                _lifetimeQuestsCompleted  += 1;
                _lifetimeBountySilverPaid += bountySilver;
                // Approval is durable before the bounty is paid, or a restart returns the quest to Submitted and the same bounty pays twice.
                if (!SaveToDisk())
                {
                    q.State = QuestEntry.StateSubmitted; q.CompletedUtcTicks = 0; q.ReviewState = QuestEntry.ReviewPending;
                    _lifetimeQuestsCompleted  -= 1;
                    _lifetimeBountySilverPaid -= bountySilver;
                    return false;
                }
            }

            // The poster's escrow is already spent, so a bounty that cannot reach the claimer is held, not dropped.
            Items.KmhPayloadEscrow.DeliverSilver(claimer, bountySilver,
                $"quest #{questId} bounty (approved by {callerUsername})", "quest bounty could not be paid");
            DepositBountyItems(bountyItems, claimer, $"quest #{questId} bounty (approved by {callerUsername})");

            if (!string.IsNullOrEmpty(claimer))
            {
                PlayerStats.PlayerStatsStore.RecordContractCompleted(claimer, kind);
                Reputation.ReputationStore.RecordCompleted(claimer);
            }
            claimerAffected = claimer;
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseQuestApproved(new KMH.Sdk.Server.Events.QuestApprovedEvent { QuestId = questId, PosterUsername = callerUsername, ClaimerUsername = claimer ?? "", BountyPaidSilver = bountySilver });
            return true;
        }

        // Refunds run from the sweeper long after posting, so the recipient may be gone by then.
        private static void DepositBountyItems(Dictionary<string, int> items, string toUser, string note)
        {
            if (items == null || string.IsNullOrEmpty(toUser)) return;
            foreach (KeyValuePair<string, int> kv in items)
                if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key))
                    Items.KmhPayloadEscrow.DeliverCompact(toUser, kv.Key, kv.Value, note, "bounty item delivery failed");
        }

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
                PlayerStats.PlayerStatsStore.RecordContractFailed(claimerUsername);
                SaveToDisk();
                posterAffected = poster;
                Extensibility.KmhEventBus.Instance.RaiseQuestClaimed(new KMH.Sdk.Server.Events.QuestClaimedEvent { QuestId = questId, ClaimerUsername = "", PosterUsername = poster ?? "" });
            }
            return ok;
        }

        // Client-trusted, but the bounty is server-escrowed, so a forged report only collects what the poster put up.
        public static bool VerifyComplete(string claimerUsername, long questId, out string posterAffected)
        {
            posterAffected = null;
            if (string.IsNullOrEmpty(claimerUsername)) return false;

            QuestsConfig cfg = QuestsConfig.Current;
            int bounty = 0; string poster;
            bool routedToReview = false;
            Dictionary<string, int> items = null;
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
                if (cfg.AutoVerifyRequiresPosterReview)
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
                    items  = new Dictionary<string, int>(q.BountyItems, StringComparer.OrdinalIgnoreCase);
                    q.State             = QuestEntry.StateCompleted;
                    q.CompletedUtcTicks = DateTime.UtcNow.Ticks;
                    _lifetimeQuestsCompleted  += 1;
                    _lifetimeBountySilverPaid += bounty;
                }
                // Durable before the bounty moves, both for the review route and the immediate payout.
                if (!SaveToDisk())
                {
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

            if (bounty > 0)
                Items.KmhPayloadEscrow.DeliverSilver(claimerUsername, bounty, $"quest #{questId} auto-verified bounty", "quest bounty could not be credited");
            DepositBountyItems(items, claimerUsername, $"quest #{questId} auto-verified bounty");

            PlayerStats.PlayerStatsStore.BumpQuestsCompleted(claimerUsername);
            Reputation.ReputationStore.RecordCompleted(claimerUsername);
            posterAffected = poster;
            Extensibility.KmhEventBus.Instance.RaiseQuestSubmitted(new KMH.Sdk.Server.Events.QuestSubmittedEvent { QuestId = questId, ClaimerUsername = claimerUsername, AutoCompleted = true });
            return true;
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

        // A rejection costs the poster reputation too, to deter frivolous rejections.
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
                    q.State                  = QuestEntry.StateOpen;
                    q.ClaimedByUsername      = "";
                    q.ClaimedUtcTicks        = 0;
                    q.ReviewState            = QuestEntry.ReviewRejected;
                    q.ReviewNote             = reviewNote;
                    q.ReviewSubmittedUtcTicks = DateTime.UtcNow.Ticks;
                    q.ProofText              = "";
                    q.ProofImageUrl          = "";
                }
                // Durable before the bounty is paid, or a restart returns the quest to review and the poster pays it a second time.
                if (!SaveToDisk())
                {
                    q.State = QuestEntry.StatePendingReview;
                    q.ReviewState = QuestEntry.ReviewPending; q.ReviewNote = ""; q.ReviewSubmittedUtcTicks = 0;
                    if (approve)
                    {
                        q.CompletedUtcTicks = 0;
                        _lifetimeQuestsCompleted  -= 1;
                        _lifetimeBountySilverPaid -= bounty;
                    }
                    else { q.ClaimedByUsername = claimer; }
                    return false;
                }
            }

            if (approve)
            {
                Items.KmhPayloadEscrow.DeliverSilver(claimer, bounty,
                    $"quest #{questId} bounty (reviewed by {posterUsername})", "quest bounty could not be paid");
                DepositBountyItems(items, claimer, $"quest #{questId} bounty (reviewed by {posterUsername})");
                if (!string.IsNullOrEmpty(claimer))
                {
                    PlayerStats.PlayerStatsStore.RecordContractCompleted(claimer, "");
                    Reputation.ReputationStore.RecordCompleted(claimer);
                }
                Extensibility.KmhEventBus.Instance.RaiseQuestApproved(new KMH.Sdk.Server.Events.QuestApprovedEvent { QuestId = questId, PosterUsername = posterUsername, ClaimerUsername = claimer ?? "", BountyPaidSilver = bounty });
            }
            else
            {
                if (!string.IsNullOrEmpty(claimer))
                {
                    Reputation.ReputationStore.RecordProofRejected(claimer);
                    PlayerStats.PlayerStatsStore.RecordContractFailed(claimer);
                }
                Reputation.ReputationStore.RecordRejectedAsPoster(posterUsername);
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

        // One definition of "open", used by both cap checks - the pre-escrow one and the re-check that commits.
        private static int OpenCountLocked(string posterUsername)
        {
            int open = 0;
            foreach (QuestEntry q in _byId.Values)
                if (string.Equals(q.PosterUsername, posterUsername, StringComparison.OrdinalIgnoreCase) && HoldsEscrow(q))
                    open++;
            return open;
        }

        // Hands a whole post's escrow back: items under their own keys, then the silver.
        private static void RefundBounty(string posterUsername, Dictionary<string, int> escrowed, int bountySilver, string note)
        {
            if (escrowed != null)
                foreach (KeyValuePair<string, int> kv in escrowed)
                    Items.KmhPayloadEscrow.DeliverCompact(posterUsername, kv.Key, kv.Value, note, "quest bounty item refund could not be returned");
            if (bountySilver > 0)
                Items.KmhPayloadEscrow.DeliverSilver(posterUsername, bountySilver, note, "quest bounty refund could not be credited");
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
                _nextId = 1;
                _lifetimeQuestsPosted = 0;
                _lifetimeQuestsCompleted = 0;
                _lifetimeBountySilverPaid = 0;
            }
            SaveToDisk();
        }

        // False means in-memory only; bounties are escrowed here, so a state change the disk never took leaves one payable twice.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Quests                    = new List<QuestEntry>(_byId.Values);
                state.NextId                    = _nextId;
                state.LifetimeQuestsPosted      = _lifetimeQuestsPosted;
                state.LifetimeQuestsCompleted   = _lifetimeQuestsCompleted;
                state.LifetimeBountySilverPaid  = _lifetimeBountySilverPaid;
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
