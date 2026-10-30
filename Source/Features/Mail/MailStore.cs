using System;
using System.Collections.Generic;
using System.Text;
using KMHServerAddon.Features.Mail.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Mail
{
    // Every identity here is server-set by the caller, never taken from a packet.
    internal static class MailStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<long, MailMessage> _byId = new Dictionary<long, MailMessage>();
        private static long _nextId = 1;

        private static readonly Util.KmhRateWindow _recentSends = new Util.KmhRateWindow();

        public static int MaxSubjectLength  => MailConfig.Current.MaxSubjectLength;
        public static int MaxBodyLength     => MailConfig.Current.MaxBodyLength;
        public static int MaxInboxPerUser   => MailConfig.Current.MaxInboxPerUser;
        public static int MaxSendsPerWindow => MailConfig.Current.MaxSendsPerWindow;
        public static int SendWindowSeconds => MailConfig.Current.SendWindowSeconds;
        public static int RetentionDays     => MailConfig.Current.RetentionDays;

        // Named rather than inlined, because these are the only thing stopping one player claiming another's goods.
        internal static bool MayReceive(MailMessage m, string username)
            => m != null && !string.IsNullOrEmpty(username) && Eq(m.ToUsername, username);

        internal static bool MayRecall(MailMessage m, string username)
            => m != null && !string.IsNullOrEmpty(username) && Eq(m.FromUsername, username);

        // Goods leave the sender's treasury on send and return exactly once, to one side or the other.
        public const int  AttachNone     = 0;
        public const int  AttachEscrowed = 1;

        public static int TotalCount { get { lock (_lock) { return _byId.Count; } } }

        public static long UnclaimedEscrowSilver
        {
            get
            {
                long n = 0;
                lock (_lock)
                    foreach (MailMessage m in _byId.Values)
                        if (m != null && m.AttachState == AttachEscrowed && m.AttachedSilver > 0) n += m.AttachedSilver;
                return n;
            }
        }
        // A sender's still-escrowed attachment: the recipient cannot spend it until they claim it.
        public static long EscrowValueFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long v = 0;
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                {
                    if (m == null || m.AttachState != AttachEscrowed) continue;
                    if (!string.Equals(m.FromUsername, username, StringComparison.OrdinalIgnoreCase)) continue;
                    v += m.AttachedSilver;
                    if (m.AttachedItems != null)
                        foreach (System.Collections.Generic.KeyValuePair<string, int> kv in m.AttachedItems)
                            v += Items.KmhItemSafety.GetTrustedMarketValue((kv.Key ?? "").Split('|')[0]) * kv.Value;
                    if (m.AttachedPayloads != null)
                        foreach (Items.KmhThingPayload p in m.AttachedPayloads)
                            v += Items.KmhItemSafety.GetTrustedMarketValue(p?.DefName ?? "") * Math.Max(1, p?.StackCount ?? 1);
                }
            return v;
        }

        public const int  AttachClaimed  = 2;
        public const int  AttachRefunded = 3;
        public static long MaxAttachSilver    => MailConfig.Current.MaxAttachSilver;
        public static int  MaxAttachItemTypes => MailConfig.Current.MaxAttachItemTypes;
        public static int  MaxAttachGearTypes => MailConfig.Current.MaxAttachGearTypes;
        public static int  MaxAttachItemQty   => MailConfig.Current.MaxAttachItemQty;

        // evictedSender reports a third party whose attachment came back, since only the caller can tell them why.
        public static long Send(string from, string to, string subject, string body, long attachSilver,
                                Dictionary<string, int> attachItems, Dictionary<string, int> attachPayloadSel,
                                out string evictedSender, out AttachTotals evictedTotals, out string reason)
        {
            evictedSender = null; evictedTotals = default;
            if (!TryPrepare(from, to, subject, body, out subject, out body, out reason)) return 0;
            if (attachSilver < 0) attachSilver = 0;
            if (attachSilver > MaxAttachSilver) { reason = "That's more silver than you can attach to one message."; return 0; }

            // Refused before any escrow, so a rejected send can never strand goods.
            if (MailConfig.Current.BlockAppliesToMail && Chat.ChatModerationStore.IsBlocked(to, from))
            { reason = "That player isn't accepting mail from you."; return 0; }

            Dictionary<string, int> items = SanitizeItems(attachItems);
            if (items.Count > MaxAttachItemTypes) { reason = $"You can attach at most {MaxAttachItemTypes} item types to one message."; return 0; }

            // Each entry is its own treasury withdrawal, so an unbounded map means unbounded lock and save work.
            Dictionary<string, int> gearSel = SanitizeItems(attachPayloadSel);
            if (gearSel.Count > MaxAttachGearTypes) { reason = $"You can attach at most {MaxAttachGearTypes} pieces of gear to one message."; return 0; }

            long now = DateTime.UtcNow.Ticks;

            // Escrowed before the store lock, because TreasuryStore holds its own and the two must never nest.
            if (!EscrowGoods(from, attachSilver, items, gearSel, out List<Items.KmhThingPayload> payloads, out long fee,
                             out Transactions.KmhTransaction tx, out reason)) return 0;
            AtSendPhaseForTest?.Invoke("escrowed");

            // The lock only decides accept or reject; the refund happens after it is released.
            long id = 0; string refuseNote = null; PendingRefund evicted = default;
            lock (_lock)
            {
                if (!AllowSendLocked(from, now)) { reason = "You're sending mail too fast - wait a moment."; refuseNote = "mail send rate-limited"; }
                else if (!MakeRoomLocked(to, out evicted)) { reason = "That player's inbox is full right now."; refuseNote = "recipient inbox full"; }
                else
                {
                    id = _nextId++;
                    bool hasAttach = attachSilver > 0 || items.Count > 0 || payloads.Count > 0;
                    _byId[id] = new MailMessage
                    {
                        Id = id, FromUsername = from, ToUsername = to,
                        Subject = subject, Body = body, SentUtcTicks = now, ReadUtcTicks = 0,
                        AttachedSilver   = attachSilver,
                        AttachedItems    = items.Count > 0 ? items : null,
                        AttachedPayloads = payloads.Count > 0 ? payloads : null,
                        AttachState      = hasAttach ? AttachEscrowed : AttachNone,
                    };
                    if (tx != null) _sendTx[id] = tx.Id;
                }
            }
            // The fee goes back with the attachment, so only a committed message ever collects it.
            if (refuseNote != null) { RefundEscrow(tx); return 0; }
            // Eviction and the new message are undone together if unwritten, or the escrow is left with no message.
            if (!SaveToDisk())
            {
                lock (_lock)
                {
                    _byId.Remove(id);
                    _sendTx.Remove(id);
                    if (evicted.Any) RestoreEvictedLocked(evicted);
                    if (evicted.Marker != null) _pendingPayouts.Remove(evicted.Marker);
                }
                RefundEscrow(tx);
                reason = "The server couldn't send that - your attachment was returned. Try again shortly.";
                return 0;
            }
            AtSendPhaseForTest?.Invoke("saved");
            // The fee is paid on the row's key before the row closes, so a crash in between leaves recovery to pay it, once.
            if (tx != null)
            {
                Transactions.KmhTransactionRepository.PayHouseFee(tx);
                AtSendPhaseForTest?.Invoke("fee-paid");
                Transactions.KmhTransactionRepository.Settle(tx);
            }
            if (evicted.Any)
            {
                SettlePayout(evicted.Marker, evicted.Sender, evicted.Silver, evicted.Items, evicted.Payloads,
                             $"mail #{evicted.Id} evicted from a full inbox");
                evictedSender = evicted.Sender;
                evictedTotals = new AttachTotals
                {
                    Silver = evicted.Silver, ItemUnits = SumUnits(evicted.Items), GearCount = evicted.Payloads?.Count ?? 0,
                };
            }
            return id;
        }

        internal static Action<string> AtSendPhaseForTest;

        // Server-side only, saved with the messages: the ledger row that funded each message's attachment.
        private static readonly Dictionary<long, string> _sendTx = new Dictionary<long, string>();

        internal static bool HoldsSend(string txId)
        {
            if (string.IsNullOrEmpty(txId)) return false;
            lock (_lock)
                foreach (KeyValuePair<long, string> kv in _sendTx)
                    if (kv.Value == txId && _byId.ContainsKey(kv.Key)) return true;
            return false;
        }

        // On the row's own refund marker, so this and boot recovery return the attachment once between them.
        private static void RefundEscrow(Transactions.KmhTransaction tx)
        {
            if (tx != null && !Transactions.KmhTransactionRepository.Compensate(tx))
                Diagnostics.ServerLog.Error($"Mail: attachment {tx.Id} could not be returned yet - the row stays open and the next boot returns it.");
        }

        // One row names the whole attachment before anything leaves the vault: silver, fee and items in one marked
        // commit, then each piece of gear on its own marker, which the row lists once it holds that gear.
        private static bool EscrowGoods(string from, long silver, Dictionary<string, int> items,
                                        Dictionary<string, int> payloadSel, out List<Items.KmhThingPayload> payloads,
                                        out long fee, out Transactions.KmhTransaction tx, out string reason)
        {
            fee = 0; tx = null; reason = null;
            payloads = new List<Items.KmhThingPayload>();
            items = items ?? new Dictionary<string, int>();
            bool anyGear = false;
            if (payloadSel != null) foreach (KeyValuePair<string, int> kv in payloadSel) if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) anyGear = true;
            if (silver <= 0 && items.Count == 0 && !anyGear) return true;

            // Held with the escrow rather than collected now, so a later refusal can return it.
            long wantFee = MailConfig.Current.FeeFor(silver, true);
            long have = Treasury.TreasuryStore.GetPersonalSilver(from);
            if (silver > 0 && have < silver) { reason = "You don't have that much silver in your treasury to attach."; return false; }
            if (wantFee > 0 && have < silver + wantFee)
            { reason = $"You need another {Util.SilverFmt.Format(wantFee)} silver to cover the attachment fee."; return false; }
            if (silver + wantFee > int.MaxValue) { reason = "That's more silver than you can attach to one message."; return false; }

            tx = Transactions.KmhTransactionRepository.OpenTake(
                from, Transactions.KmhTxType.Listing, "mail attachment", 0, silver + wantFee, items, null,
                destination: "mail", targetKind: Transactions.KmhTransactionRepository.TargetMailSend, houseFee: wantFee);
            if (tx == null) { reason = "The server couldn't record that attachment - nothing was taken. Try again shortly."; return false; }
            if (!Treasury.TreasuryStore.WithdrawBundleForTxn(from, (int)(silver + wantFee),
                    new List<KeyValuePair<string, int>>(items), tx.TakeMarker, "mail attachment escrow"))
            {
                Transactions.KmhTransactionRepository.Abort(tx); tx = null;
                reason = Treasury.TreasuryStore.GetPersonalSilver(from) < silver + wantFee
                    ? "You don't have that much silver in your treasury to attach."
                    : "You don't have those items in your treasury to attach.";
                return false;
            }
            fee = wantFee;

            if (payloadSel != null)
            {
                int n = 0;
                foreach (KeyValuePair<string, int> kv in payloadSel)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    string marker = tx.Id + ":wp" + n, refund = tx.Id + ":rp" + n;
                    n++;
                    // Grants up to qty, so an atomic stack it cannot split comes back short and must roll back.
                    List<Items.KmhThingPayload> got = Treasury.TreasuryStore.WithdrawPayloadsForTxn(from, kv.Key, kv.Value, marker,
                                                                                                   "mail attachment escrow", refund);
                    bool named = false;
                    if (got != null && SumStacks(got) >= kv.Value)
                    {
                        tx.EscrowPayloads.AddRange(got);
                        tx.ExtraTakeMarkers.Add(marker);
                        named = Transactions.KmhTransactionRepository.Update(tx);
                        if (!named) { foreach (Items.KmhThingPayload p in got) tx.EscrowPayloads.Remove(p); tx.ExtraTakeMarkers.Remove(marker); }
                    }
                    if (!named)
                    {
                        if (got != null && got.Count > 0)
                            Treasury.TreasuryStore.ReturnPendingTake(from, marker, refund, got, "mail attachment escrow rollback");
                        RefundEscrow(tx); tx = null; fee = 0; payloads.Clear();
                        reason = "You don't have that gear in your treasury to attach.";
                        return false;
                    }
                    AtSendPhaseForTest?.Invoke("gear-named");
                    // Safe to fail: the row now names this gear, so recovery drops the entry without paying it.
                    Treasury.TreasuryStore.ClearPendingTake(from, marker);
                    payloads.AddRange(got);
                }
            }
            return true;
        }

        private static void RefundGoods(string user, long silver, Dictionary<string, int> items, List<Items.KmhThingPayload> payloads, string note)
        {
            if (string.IsNullOrEmpty(user)) return;
            if (silver > 0) Items.KmhPayloadEscrow.DeliverSilver(user, silver, note, "mail attachment silver could not be returned");
            DepositItems(user, items, note);
            DepositPayloads(user, payloads, note);
        }

        // Carried out of the store lock, so no treasury call ever runs while it is held.
        private struct PendingRefund
        {
            public string Sender;
            public long   Silver;
            public Dictionary<string, int> Items;
            public List<Items.KmhThingPayload> Payloads;
            public long   Id;
            public string Marker;
            // The evicted message itself, so an unwritable send can put the inbox back exactly as it was.
            public MailMessage Message;
            public bool Any => Silver > 0 || (Items != null && Items.Count > 0) || (Payloads != null && Payloads.Count > 0);
        }

        public struct AttachTotals
        {
            public long Silver;
            public int  ItemUnits;
            public int  GearCount;
            public bool Any => Silver > 0 || ItemUnits > 0 || GearCount > 0;
        }

        // The state moves under the lock and the credit happens outside it, so a claim can only land once.
        public static bool Claim(string username, long id, out AttachTotals totals, out string sender, out string reason)
        {
            reason = null; sender = null; totals = default;
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null; string marker = null;
            string from;
            lock (_lock) from = _byId.TryGetValue(id, out MailMessage peek) && MayReceive(peek, username) ? peek.FromUsername : null;
            // A frozen sender's attachment may be what they minted, so it waits for staff; checked outside this lock so the two never nest.
            if (from != null && Moderation.KmhEconomyGuard.IsFrozen(from))
            { reason = "That attachment is on hold while server staff review the sender's account."; return false; }
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out MailMessage m) || !MayReceive(m, username)) { reason = "No such message."; return false; }
                sender = m.FromUsername;
                if (!TryResolveAttachment(m, AttachClaimed)) { reason = "There's nothing to accept on that message."; return false; }
                totals.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                totals.ItemUnits = SumUnits(items); totals.GearCount = payloads?.Count ?? 0;
                marker = RecordPayoutLocked(username, id, "claim", totals.Silver, items, payloads, $"mail #{id} attachment claimed");
                // Persisted before crediting, so a crash loses the credit rather than allowing a second claim;
                // a failed write must unwind.
                if (!SaveToDisk())
                {
                    m.AttachState = AttachEscrowed;
                    if (marker != null) _pendingPayouts.Remove(marker);
                    reason = "The server couldn't record that - nothing was taken. Try again shortly."; return false;
                }
            }
            SettlePayout(marker, username, totals.Silver, items, payloads, $"mail #{id} attachment claimed");
            return true;
        }

        // Keyed on the sender, so goods can be recovered without waiting on the recipient or the timeout.
        public static bool Recall(string username, long id, out AttachTotals totals, out string recipient, out string reason)
        {
            reason = null; recipient = null; totals = default;
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null; string marker = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out MailMessage m) || !MayRecall(m, username)) { reason = "No such message."; return false; }
                recipient = m.ToUsername;
                if (m.ReadUtcTicks > 0) { reason = "They've already read that mail."; return false; }
                if (!TryResolveAttachment(m, AttachRefunded)) { reason = "There's nothing to recall on that message."; return false; }
                totals.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                totals.ItemUnits = SumUnits(items); totals.GearCount = payloads?.Count ?? 0;
                marker = RecordPayoutLocked(username, id, "recall", totals.Silver, items, payloads, $"mail #{id} attachment recalled");
                // Refunded persists before crediting, so a crash cannot re-refund; an unwritten one must unwind.
                if (!SaveToDisk())
                {
                    m.AttachState = AttachEscrowed;
                    if (marker != null) _pendingPayouts.Remove(marker);
                    reason = "The server couldn't record that - nothing was returned. Try again shortly."; return false;
                }
            }
            SettlePayout(marker, username, totals.Silver, items, payloads, $"mail #{id} attachment recalled");
            return true;
        }

        // The message is kept, but an attachment nobody accepts must not strand forever; 0 days disables this.
        public static List<(string Sender, string Recipient, long Id, AttachTotals Totals)> SweepUnclaimedAttachments(long now)
        {
            var returned = new List<(string, string, long, AttachTotals)>();
            int days = MailConfig.Current.UnclaimedAttachmentDays;
            if (days <= 0) return returned;
            long cutoff = now - TimeSpan.FromDays(days).Ticks;

            var pending = new List<(string Sender, long Id, AttachTotals Totals, Dictionary<string, int> Items, List<Items.KmhThingPayload> Payloads, string Recipient, string Marker)>();
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                {
                    if (m.AttachState != AttachEscrowed || !HasAttachment(m)) continue;
                    if (m.ReadUtcTicks > 0 || m.SentUtcTicks > cutoff) continue;
                    string sender = m.FromUsername, to = m.ToUsername;
                    Dictionary<string, int> items = CopyItems(m.AttachedItems);
                    List<Items.KmhThingPayload> payloads = CopyPayloads(m.AttachedPayloads);
                    AttachTotals t = new AttachTotals { Silver = m.AttachedSilver, ItemUnits = SumUnits(items), GearCount = payloads?.Count ?? 0 };
                    if (!TryResolveAttachment(m, AttachRefunded)) continue;
                    pending.Add((sender, m.Id, t, items, payloads, to,
                                 RecordPayoutLocked(sender, m.Id, "sweep", t.Silver, items, payloads,
                                                    $"mail #{m.Id} attachment unclaimed - returned")));
                }

            if (pending.Count == 0) return returned;
            // Refunded persists before crediting, so a crash cannot re-refund;
            // an unwritten sweep rolls back and waits for the next pass.
            if (!SaveToDisk())
            {
                lock (_lock)
                    foreach (var p in pending)
                    {
                        if (_byId.TryGetValue(p.Id, out MailMessage m)) m.AttachState = AttachEscrowed;
                        if (p.Marker != null) _pendingPayouts.Remove(p.Marker);
                    }
                Diagnostics.ServerLog.Warn($"Mail: could not save {pending.Count} unclaimed-attachment return(s) - left escrowed for the next sweep.");
                return returned;
            }
            foreach (var p in pending)
            {
                SettlePayout(p.Marker, p.Sender, p.Totals.Silver, p.Items, p.Payloads, $"mail #{p.Id} attachment unclaimed - returned");
                returned.Add((p.Sender, p.Recipient, p.Id, p.Totals));
            }
            return returned;
        }

        // The state moves under the lock and the credit happens outside it, so a refund can only land once.
        public static bool Decline(string username, long id, out AttachTotals totals, out string sender, out string reason)
        {
            reason = null; sender = null; totals = default;
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null; string marker = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out MailMessage m) || !MayReceive(m, username)) { reason = "No such message."; return false; }
                sender = m.FromUsername;
                if (!TryResolveAttachment(m, AttachRefunded)) { reason = "There's nothing to return on that message."; return false; }
                totals.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                totals.ItemUnits = SumUnits(items); totals.GearCount = payloads?.Count ?? 0;
                marker = RecordPayoutLocked(sender, id, "decline", totals.Silver, items, payloads, $"mail #{id} attachment declined");
                // Refunded persists before crediting the sender, so a crash cannot re-refund.
                if (!SaveToDisk())
                {
                    m.AttachState = AttachEscrowed;
                    if (marker != null) _pendingPayouts.Remove(marker);
                    reason = "The server couldn't record that - nothing was returned. Try again shortly."; return false;
                }
            }
            SettlePayout(marker, sender, totals.Silver, items, payloads, $"mail #{id} attachment declined");
            return true;
        }

        // Pure, so the claim-once and refund-once guard can be pinned by a test.
        internal static bool TryResolveAttachment(MailMessage m, int target)
        {
            if (m == null || m.AttachState != AttachEscrowed || !HasAttachment(m)) return false;
            if (target != AttachClaimed && target != AttachRefunded) return false;
            m.AttachState = target;
            return true;
        }

        internal static bool HasAttachment(MailMessage m)
            => m != null && (m.AttachedSilver > 0
                || (m.AttachedItems != null && m.AttachedItems.Count > 0)
                || (m.AttachedPayloads != null && m.AttachedPayloads.Count > 0));

        // Close-persist-credit alone lets a crash strand goods in no inbox, vault or row, so each payout is recorded
        // in the same save as the state that closes it.
        internal sealed class PendingPayout
        {
            [Newtonsoft.Json.JsonProperty("marker")]   public string Marker { get; set; }
            [Newtonsoft.Json.JsonProperty("user")]     public string User { get; set; }
            [Newtonsoft.Json.JsonProperty("silver")]   public long Silver { get; set; }
            [Newtonsoft.Json.JsonProperty("items")]    public Dictionary<string, int> Items { get; set; }
            [Newtonsoft.Json.JsonProperty("payloads")] public List<Items.KmhThingPayload> Payloads { get; set; }
            [Newtonsoft.Json.JsonProperty("note")]     public string Note { get; set; }
        }

        private static readonly Dictionary<string, PendingPayout> _pendingPayouts =
            new Dictionary<string, PendingPayout>(StringComparer.Ordinal);

        internal static int PendingPayoutCountForTest { get { lock (_lock) return _pendingPayouts.Count; } }

        // Silver and units counted apart, because a conservation matrix that adds them cannot see a swap.
        internal static (long Silver, int Items) EscrowedTotalsForTest()
        {
            long s = 0; int n = 0;
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                {
                    if (m == null || m.AttachState != AttachEscrowed) continue;
                    s += Math.Max(0, m.AttachedSilver);
                    n += SumUnits(m.AttachedItems) + SumStacks(m.AttachedPayloads);
                }
            return (s, n);
        }

        internal static (long Silver, int Items) PendingPayoutTotalsForTest()
        {
            long s = 0; int n = 0;
            lock (_lock)
                foreach (PendingPayout p in _pendingPayouts.Values)
                {
                    if (p == null) continue;
                    s += Math.Max(0, p.Silver);
                    n += SumUnits(p.Items) + SumStacks(p.Payloads);
                }
            return (s, n);
        }

        // Fired after each step a restart could stop between, so the matrix can cut the power anywhere.
        internal static Action<string> AtPayoutPhaseForTest;

        // A mail id resolves exactly once, so id + recipient make the marker unique.
        private static string RecordPayoutLocked(string user, long id, string kind, long silver,
                                                 Dictionary<string, int> items,
                                                 List<Items.KmhThingPayload> payloads, string note)
        {
            if (string.IsNullOrEmpty(user)) return null;
            if (silver <= 0 && (items == null || items.Count == 0) && (payloads == null || payloads.Count == 0))
                return null;
            string marker = $"mail:{id}:{kind}:{user.ToLowerInvariant()}";
            _pendingPayouts[marker] = new PendingPayout
            {
                Marker = marker, User = user, Silver = silver,
                Items = CopyItems(items), Payloads = CopyPayloads(payloads), Note = note,
            };
            return marker;
        }

        // On the marker, so recovery replaying this pays nothing a second time. A refused write keeps the
        // record: value nobody can find is worse than value an operator is told about and a reboot retries.
        private static void SettlePayout(string marker, string user, long silver, Dictionary<string, int> items,
                                         List<Items.KmhThingPayload> payloads, string note)
        {
            // Fires even with no record, so a change that stops writing one is seen to lose goods here.
            AtPayoutPhaseForTest?.Invoke("payout-recorded");
            if (marker == null) { RefundGoods(user, silver, items, payloads, note); return; }
            if (!Treasury.TreasuryStore.DepositEscrowOnce(user, marker, silver, items, payloads, note))
            {
                Diagnostics.ServerLog.Warn($"Mail: {note} could not be credited to {user} - held as {marker} "
                                         + "and retried at the next boot.");
                return;
            }
            AtPayoutPhaseForTest?.Invoke("payout-credited");
            lock (_lock) _pendingPayouts.Remove(marker);
            SaveToDisk();
            AtPayoutPhaseForTest?.Invoke("payout-cleared");
        }

        // A record left at boot is an interrupted payout; the treasury marker makes replaying a credited one a no-op.
        public static void RecoverPendingPayouts()
        {
            List<PendingPayout> owed;
            lock (_lock) owed = new List<PendingPayout>(_pendingPayouts.Values);
            if (owed.Count == 0) return;

            int paid = 0;
            foreach (PendingPayout p in owed)
            {
                if (p == null || string.IsNullOrEmpty(p.Marker) || string.IsNullOrEmpty(p.User)) continue;
                // Owed to a save that is being destroyed: dropped with it, never paid into the new one.
                if (Economy.KmhEconomyReset.Resetting(p.User)) { lock (_lock) _pendingPayouts.Remove(p.Marker); continue; }
                if (!Treasury.TreasuryStore.DepositEscrowOnce(p.User, p.Marker, p.Silver, p.Items, p.Payloads,
                                                              p.Note ?? "mail payout resumed")) continue;
                lock (_lock) _pendingPayouts.Remove(p.Marker);
                paid++;
            }
            SaveToDisk();
            if (paid > 0)
                Diagnostics.ServerLog.Warn($"Mail: resumed {paid} interrupted attachment payout(s) from a previous run.");
            int left = PendingPayoutCountForTest;
            if (left > 0)
                Diagnostics.ServerLog.Error($"Mail: {left} attachment payout(s) still cannot be credited - "
                                          + "the treasury refused the write. They are retried next boot.");
        }

        internal static void PlantPendingPayoutForTest(string user, long silver)
        {
            lock (_lock) RecordPayoutLocked(user, -Math.Abs(Guid.NewGuid().GetHashCode()) - 1, "selftest", silver, null, null, "selftest payout");
        }

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropPendingPayoutsFor(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int dropped = 0;
            lock (_lock)
                foreach (PendingPayout p in new List<PendingPayout>(_pendingPayouts.Values))
                    if (p != null && Eq(p.User, user) && _pendingPayouts.Remove(p.Marker)) dropped++;
            return dropped;
        }

        // Outgoing escrow sits outside the treasury, so a save reset must burn it or value carries across.
        public static bool HasSenderEscrow(string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                    if (m.AttachState == AttachEscrowed && HasAttachment(m) && Eq(m.FromUsername, user)) return true;
            return false;
        }

        public static int PurgeSender(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int burned = 0;
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                    if (m.AttachState == AttachEscrowed && HasAttachment(m) && Eq(m.FromUsername, user))
                    { m.AttachedSilver = 0; m.AttachedItems = null; m.AttachedPayloads = null; m.AttachState = AttachRefunded; burned++; }
            if (burned > 0) SaveToDisk();
            return burned;
        }

        public static int CountFor(string user)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int n = 0;
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                    if (Eq(m.FromUsername, user) || Eq(m.ToUsername, user)) n++;
            return n;
        }

        // The user's own escrow burns with them, but an unclaimed attachment is still the sender's value, so it goes home.
        public static (int Removed, int Burned, int ReturnedToSenders) PurgeUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return (0, 0, 0);
            int removed = 0, burned = 0;
            bool saved = true;
            var returns = new List<PendingRefund>();
            lock (_lock)
            {
                foreach (MailMessage m in new List<MailMessage>(_byId.Values))
                {
                    bool mine = Eq(m.FromUsername, user), theirs = Eq(m.ToUsername, user);
                    if (!mine && !theirs) continue;
                    if (m.AttachState == AttachEscrowed && HasAttachment(m))
                    {
                        if (mine) { TryResolveAttachment(m, AttachRefunded); burned++; }
                        else if (TryResolveAttachment(m, AttachRefunded))
                            returns.Add(new PendingRefund
                            {
                                Sender = m.FromUsername, Silver = m.AttachedSilver, Id = m.Id,
                                Items = CopyItems(m.AttachedItems), Payloads = CopyPayloads(m.AttachedPayloads),
                                Marker = RecordPayoutLocked(m.FromUsername, m.Id, "purge", m.AttachedSilver,
                                                            m.AttachedItems, m.AttachedPayloads,
                                                            $"mail #{m.Id} returned - recipient was removed"),
                            });
                    }
                    _byId.Remove(m.Id);
                    removed++;
                }
                if (removed > 0) saved = SaveToDisk();
            }
            // Paid only once written: unwritten, a restart brings the escrowed mail back and the unclaimed sweep returns
            // the same goods again under its own marker. The records stay pending, and boot recovery pays them once.
            if (!saved)
            {
                Diagnostics.ServerLog.Warn($"Mail: purging {user}'s mail could not be saved - {returns.Count} return(s) wait for the next boot.");
                return (removed, burned, returns.Count);
            }
            foreach (PendingRefund p in returns)
                SettlePayout(p.Marker, p.Sender, p.Silver, p.Items, p.Payloads, $"mail #{p.Id} returned - recipient was removed");
            return (removed, burned, returns.Count);
        }

        // Case-folded like the vault, so a message never records more of a stack than the vault gave up.
        internal static Dictionary<string, int> SanitizeItems(Dictionary<string, int> items)
        {
            Dictionary<string, int> outMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (items == null) return outMap;
            foreach (KeyValuePair<string, int> kv in items)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                outMap.TryGetValue(kv.Key, out int had);
                int sum = Util.KmhSafe.AddSaturating(had, kv.Value);
                outMap[kv.Key] = sum > MaxAttachItemQty ? MaxAttachItemQty : sum;
            }
            return outMap;
        }

        private static Dictionary<string, int> CopyItems(Dictionary<string, int> items)
            => items == null ? null : new Dictionary<string, int>(items);

        private static int SumUnits(Dictionary<string, int> items)
        {
            int n = 0;
            if (items != null) foreach (KeyValuePair<string, int> kv in items) n += kv.Value;
            return n;
        }

        // A headless server has nowhere to drop a failed deposit, so it parks in the recovery queue instead.
        private static void DepositItems(string user, Dictionary<string, int> items, string note)
        {
            if (items == null || string.IsNullOrEmpty(user)) return;
            foreach (KeyValuePair<string, int> kv in items)
                if (kv.Value > 0) Items.KmhPayloadEscrow.DeliverCompact(user, kv.Key, kv.Value, note, note);
        }

        private static List<Items.KmhThingPayload> CopyPayloads(List<Items.KmhThingPayload> payloads)
            => payloads == null ? null : new List<Items.KmhThingPayload>(payloads);

        // Counts StackCount rather than entries, because a granted list can split or merge stacks.
        internal static int SumStacks(List<Items.KmhThingPayload> payloads)
        {
            int n = 0;
            if (payloads != null) foreach (Items.KmhThingPayload p in payloads) if (p != null) n += p.StackCount;
            return n;
        }

        private static void DepositPayloads(string user, List<Items.KmhThingPayload> payloads, string note)
        {
            if (payloads == null || string.IsNullOrEmpty(user)) return;
            foreach (Items.KmhThingPayload p in payloads) if (p != null) Items.KmhPayloadEscrow.Deliver(user, p, note, note);
        }

        // Pure, so the self-test can exercise it without disk or rate state.
        internal static bool TryPrepare(string from, string to, string subjectIn, string bodyIn,
                                        out string subject, out string body, out string reason)
        {
            reason  = null;
            subject = Clamp(Sanitize(subjectIn), MaxSubjectLength);
            body    = Clamp(Sanitize(bodyIn),    MaxBodyLength);
            if (string.IsNullOrEmpty(from)) { reason = "No sender.";    return false; }
            if (string.IsNullOrEmpty(to))   { reason = "No recipient."; return false; }
            if (to.Length > Util.KmhSafe.MaxUsernameLength) { reason = "That player name is too long."; return false; }
            if (Eq(from, to))               { reason = "You can't mail yourself."; return false; }
            if (string.IsNullOrEmpty(body) && string.IsNullOrEmpty(subject))
            { reason = "Write a subject or a message."; return false; }
            return true;
        }

        // One locked pass, because a snapshot fires on every mail action and on the hub's refresh tick.
        public struct InboxView
        {
            public List<MailMessage>       Inbox;
            public List<MailMessage>       Outgoing;
            public int                     Unread;
            public long                    EscrowedOutSilver;
            public Dictionary<string, int> EscrowedOutItems;
            public long                    EscrowedOutPayloadValue;
            public long                    Revision;   // stamped under the lock; the client drops an older one
        }

        public static InboxView BuildView(string username)
        {
            InboxView v = new InboxView
            {
                Inbox = new List<MailMessage>(), Outgoing = new List<MailMessage>(),
                EscrowedOutItems = new Dictionary<string, int>(),
            };
            if (string.IsNullOrEmpty(username)) return v;

            lock (_lock)
            {
                v.Revision = Util.KmhSnapshotRevision.Next();
                foreach (MailMessage m in _byId.Values)
                {
                    if (MayReceive(m, username))
                    {
                        v.Inbox.Add(m);
                        if (m.ReadUtcTicks == 0) v.Unread++;
                    }
                    if (!MayRecall(m, username) || m.AttachState != AttachEscrowed) continue;

                    if (m.ReadUtcTicks == 0 && HasAttachment(m)) v.Outgoing.Add(m);
                    v.EscrowedOutSilver += m.AttachedSilver > 0 ? m.AttachedSilver : 0;
                    if (m.AttachedItems != null)
                        foreach (KeyValuePair<string, int> kv in m.AttachedItems)
                        { v.EscrowedOutItems.TryGetValue(kv.Key, out int c); v.EscrowedOutItems[kv.Key] = Util.KmhSafe.AddSaturating(c, kv.Value); }
                    if (m.AttachedPayloads != null)
                        foreach (Items.KmhThingPayload p in m.AttachedPayloads) if (p != null) v.EscrowedOutPayloadValue += Math.Max(0, p.MarketValue);
                }
            }

            v.Inbox.Sort((a, b) => b.SentUtcTicks.CompareTo(a.SentUtcTicks));
            v.Outgoing.Sort((a, b) => b.SentUtcTicks.CompareTo(a.SentUtcTicks));
            return v;
        }

        public static bool MarkRead(string username, long id)
        {
            bool changed = false;
            lock (_lock)
                if (_byId.TryGetValue(id, out MailMessage m) && MayReceive(m, username) && m.ReadUtcTicks == 0)
                { m.ReadUtcTicks = DateTime.UtcNow.Ticks; changed = true; }
            if (changed) SaveToDisk();
            return changed;
        }

        // An unclaimed attachment goes back first, so discarding mail never burns the sender's goods.
        public static bool Delete(string username, long id, out AttachTotals refunded, out string sender)
        {
            bool removed = false; refunded = default; sender = null;
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null; string marker = null;
            lock (_lock)
                if (_byId.TryGetValue(id, out MailMessage m) && MayReceive(m, username))
                {
                    sender = m.FromUsername;
                    int prior = m.AttachState;
                    if (TryResolveAttachment(m, AttachRefunded))
                    {
                        refunded.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                        refunded.ItemUnits = SumUnits(items); refunded.GearCount = payloads?.Count ?? 0;
                        // Discard also removes the message, so once saved this record alone names the sender's goods.
                        marker = RecordPayoutLocked(sender, id, "discard", refunded.Silver, items, payloads,
                                                    $"mail #{id} discarded with attachment");
                    }
                    removed = _byId.Remove(id);
                    // Removal persists before the refund, so a crash cannot re-refund
                    // and an unwritten discard is never paid out.
                    if (removed && !SaveToDisk())
                    {
                        // Its own earlier state: forcing Escrowed would reopen an attachment already claimed.
                        m.AttachState = prior;
                        _byId[id] = m;
                        if (marker != null) _pendingPayouts.Remove(marker);
                        return false;
                    }
                }
            if (refunded.Any) SettlePayout(marker, sender, refunded.Silver, items, payloads, $"mail #{id} discarded with attachment");
            return removed;
        }

        // Unread mail is kept at any age, so an unclaimed attachment never times out silently.
        public static int PruneOld(long now)
        {
            long cutoff = now - TimeSpan.FromDays(RetentionDays).Ticks;
            List<long> drop = new List<long>();
            var refunds = new List<(string Sender, long Silver, Dictionary<string, int> Items, List<Items.KmhThingPayload> Payloads, long Id, string Marker)>();
            // Each message's own state before this pass: an unwritten prune puts back exactly that, never Escrowed for
            // all, or an attachment claimed long ago reopens and the sender is paid for it a second time.
            var prior = new Dictionary<long, int>();
            lock (_lock)
            {
                foreach (MailMessage m in _byId.Values)
                    if (m.ReadUtcTicks > 0 && m.ReadUtcTicks <= cutoff)
                    {
                        string sender = m.FromUsername;
                        prior[m.Id] = m.AttachState;
                        if (TryResolveAttachment(m, AttachRefunded))
                            refunds.Add((sender, m.AttachedSilver, CopyItems(m.AttachedItems), CopyPayloads(m.AttachedPayloads), m.Id,
                                         RecordPayoutLocked(sender, m.Id, "prune", m.AttachedSilver,
                                                            m.AttachedItems, m.AttachedPayloads,
                                                            $"mail #{m.Id} expired with attachment")));
                        drop.Add(m.Id);
                    }
                List<MailMessage> dropped = new List<MailMessage>(drop.Count);
                foreach (long id in drop) { if (_byId.TryGetValue(id, out MailMessage d)) dropped.Add(d); _byId.Remove(id); }
                // Removals persist before the refunds, so a crash cannot re-refund;
                // an unwritten prune rolls back for the next pass.
                if (drop.Count > 0 && !SaveToDisk())
                {
                    foreach (MailMessage d in dropped) { d.AttachState = prior[d.Id]; _byId[d.Id] = d; }
                    foreach (var f in refunds) if (f.Marker != null) _pendingPayouts.Remove(f.Marker);
                    Diagnostics.ServerLog.Warn($"Mail: could not save the pruning of {drop.Count} old message(s) - kept for the next pass.");
                    return 0;
                }
            }
            foreach ((string sender, long silver, Dictionary<string, int> items, List<Items.KmhThingPayload> payloads, long id, string marker) in refunds)
                SettlePayout(marker, sender, silver, items, payloads, $"mail #{id} expired with attachment");
            return drop.Count;
        }

        // Only read mail is evicted, so a send is refused rather than dropping something never seen.
        private static bool MakeRoomLocked(string to, out PendingRefund evicted)
        {
            evicted = default;
            int count = 0; MailMessage oldestRead = null;
            foreach (MailMessage m in _byId.Values)
            {
                if (!Eq(m.ToUsername, to)) continue;
                count++;
                if (m.ReadUtcTicks > 0 && (oldestRead == null || m.SentUtcTicks < oldestRead.SentUtcTicks)) oldestRead = m;
            }
            if (count < MaxInboxPerUser) return true;
            if (oldestRead == null) return false;
            if (TryResolveAttachment(oldestRead, AttachRefunded))
                evicted = new PendingRefund
                {
                    Sender = oldestRead.FromUsername, Silver = oldestRead.AttachedSilver,
                    Items = CopyItems(oldestRead.AttachedItems), Payloads = CopyPayloads(oldestRead.AttachedPayloads),
                    Id = oldestRead.Id, Message = oldestRead,
                    Marker = RecordPayoutLocked(oldestRead.FromUsername, oldestRead.Id, "evict", oldestRead.AttachedSilver,
                                                oldestRead.AttachedItems, oldestRead.AttachedPayloads,
                                                $"mail #{oldestRead.Id} evicted from a full inbox"),
                };
            _byId.Remove(oldestRead.Id);
            return true;
        }

        private static void RestoreEvictedLocked(PendingRefund evicted)
        {
            if (evicted.Message == null) return;
            evicted.Message.AttachState = AttachEscrowed;
            _byId[evicted.Id] = evicted.Message;
        }

        private static bool AllowSendLocked(string from, long now)
            => _recentSends.Allow(from, now, MaxSendsPerWindow, SendWindowSeconds);

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            return sb.ToString().Trim();
        }

        private static string Clamp(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s.Substring(0, max) : s);

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private sealed class PersistedState
        {
            public List<MailMessage> Messages { get; set; } = new List<MailMessage>();
            public long NextId { get; set; } = 1;
            // Same file as the messages, so one write covers both the closed attachment and the debt it created.
            public List<PendingPayout> PendingPayouts { get; set; } = new List<PendingPayout>();
            public Dictionary<long, string> SendTx { get; set; } = new Dictionary<long, string>();
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.MailFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _byId.Clear();
                if (s.Messages != null) foreach (MailMessage m in s.Messages) if (m != null && m.Id > 0) _byId[m.Id] = m;
                _nextId = Math.Max(1, s.NextId);
                _pendingPayouts.Clear();
                _sendTx.Clear();
                if (s.SendTx != null) foreach (KeyValuePair<long, string> kv in s.SendTx) _sendTx[kv.Key] = kv.Value;
                if (s.PendingPayouts != null)
                    foreach (PendingPayout p in s.PendingPayouts)
                        if (p != null && !string.IsNullOrEmpty(p.Marker)) _pendingPayouts[p.Marker] = p;
            }
        }

        // False means in-memory only; persist-before-credit protects attachments only while this reports failed writes.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock)
            {
                s.Messages.AddRange(KmhDeepCopy.OfList(_byId.Values));
                s.PendingPayouts.AddRange(KmhDeepCopy.OfList(_pendingPayouts.Values));
                foreach (KeyValuePair<long, string> kv in _sendTx) if (_byId.ContainsKey(kv.Key)) s.SendTx[kv.Key] = kv.Value;
                s.NextId = _nextId;
                seq = JsonFileStore.NextSequence();
            }
            return JsonFileStore.Save(KmhDataPaths.MailFile, s, seq);
        }

        public static void ClearForNewSeason()
        {
            // The reset wipes the treasuries these would pay into, so carrying them across would credit nothing.
            lock (_lock) { _byId.Clear(); _nextId = 1; _recentSends.Clear(); _pendingPayouts.Clear(); }
            SaveToDisk();
        }
    }
}
