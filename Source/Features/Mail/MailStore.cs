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
            if (!EscrowGoods(from, attachSilver, items, gearSel, out List<Items.KmhThingPayload> payloads, out long fee, out reason)) return 0;

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
                }
            }
            // The fee goes back with the attachment, so only a committed message ever collects it.
            if (refuseNote != null) { RefundGoods(from, attachSilver + fee, items, payloads, refuseNote); return 0; }
            // Eviction and new message land together: unwritten, the escrow would have no message holding it, so both are undone.
            if (!SaveToDisk())
            {
                lock (_lock) { _byId.Remove(id); if (evicted.Any) RestoreEvictedLocked(evicted); }
                RefundGoods(from, attachSilver + fee, items, payloads, "mail could not be saved");
                reason = "The server couldn't send that - your attachment was returned. Try again shortly.";
                return 0;
            }
            if (evicted.Any)
            {
                RefundGoods(evicted.Sender, evicted.Silver, evicted.Items, evicted.Payloads,
                            $"mail #{evicted.Id} evicted from a full inbox");
                evictedSender = evicted.Sender;
                evictedTotals = new AttachTotals
                {
                    Silver = evicted.Silver, ItemUnits = SumUnits(evicted.Items), GearCount = evicted.Payloads?.Count ?? 0,
                };
            }
            if (fee > 0) Features.Marketplace.MarketplaceStore.CreditFeeToHousePool(from, fee, $"mail attachment fee from {from}");
            return id;
        }

        // All-or-nothing: anything short rolls the whole escrow back.
        private static bool EscrowGoods(string from, long silver, Dictionary<string, int> items,
                                        Dictionary<string, int> payloadSel, out List<Items.KmhThingPayload> payloads,
                                        out long fee, out string reason)
        {
            fee = 0;
            reason = null;
            payloads = new List<Items.KmhThingPayload>();

            if (silver > 0 && !Treasury.TreasuryStore.WithdrawSilver(from, (int)Math.Min(int.MaxValue, silver), note: "mail attachment escrow"))
            { reason = "You don't have that much silver in your treasury to attach."; return false; }

            // Held with the escrow rather than collected now, so a later refusal can return it.
            fee = MailConfig.Current.FeeFor(silver, silver > 0 || (items != null && items.Count > 0) || (payloadSel != null && payloadSel.Count > 0));
            if (fee > 0 && !Treasury.TreasuryStore.WithdrawSilver(from, (int)Math.Min(int.MaxValue, fee), note: "mail attachment fee"))
            {
                RefundGoods(from, silver, null, null, "mail fee unaffordable rollback");
                reason = $"You need another {Util.SilverFmt.Format(fee)} silver to cover the attachment fee.";
                fee = 0;
                return false;
            }

            Dictionary<string, int> takenItems = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in items)
            {
                if (Treasury.TreasuryStore.WithdrawItem(from, kv.Key, kv.Value, note: "mail attachment escrow")) { takenItems[kv.Key] = kv.Value; continue; }
                RefundGoods(from, silver, takenItems, payloads, "mail attachment escrow rollback");
                reason = "You don't have those items in your treasury to attach.";
                return false;
            }

            if (payloadSel != null)
                foreach (KeyValuePair<string, int> kv in payloadSel)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    // Grants up to qty, so an atomic stack it cannot split comes back short and must roll back.
                    List<Items.KmhThingPayload> got = Treasury.TreasuryStore.WithdrawPayloads(from, kv.Key, kv.Value, note: "mail attachment escrow");
                    if (got != null) payloads.AddRange(got);
                    if (SumStacks(got) < kv.Value)
                    {
                        RefundGoods(from, silver, takenItems, payloads, "mail attachment escrow rollback");
                        reason = "You don't have that gear in your treasury to attach.";
                        return false;
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
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out MailMessage m) || !MayReceive(m, username)) { reason = "No such message."; return false; }
                sender = m.FromUsername;
                if (!TryResolveAttachment(m, AttachClaimed)) { reason = "There's nothing to accept on that message."; return false; }
                totals.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                totals.ItemUnits = SumUnits(items); totals.GearCount = payloads?.Count ?? 0;
                // Persist before crediting so a crash loses the credit, not allows a second claim; a failed write must unwind.
                if (!SaveToDisk()) { m.AttachState = AttachEscrowed; reason = "The server couldn't record that - nothing was taken. Try again shortly."; return false; }
            }
            if (totals.Silver > 0)
                Items.KmhPayloadEscrow.DeliverSilver(username, totals.Silver, $"mail #{id} attachment claimed",
                                                     "mail attachment silver could not be credited");
            DepositItems(username, items, $"mail #{id} attachment claimed");
            DepositPayloads(username, payloads, $"mail #{id} attachment claimed");
            return true;
        }

        // Keyed on the sender, so goods can be recovered without waiting on the recipient or the timeout.
        public static bool Recall(string username, long id, out AttachTotals totals, out string recipient, out string reason)
        {
            reason = null; recipient = null; totals = default;
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out MailMessage m) || !MayRecall(m, username)) { reason = "No such message."; return false; }
                recipient = m.ToUsername;
                if (m.ReadUtcTicks > 0) { reason = "They've already read that mail."; return false; }
                if (!TryResolveAttachment(m, AttachRefunded)) { reason = "There's nothing to recall on that message."; return false; }
                totals.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                totals.ItemUnits = SumUnits(items); totals.GearCount = payloads?.Count ?? 0;
                // Refunded persists before crediting, so a crash cannot re-refund; an unwritten one must unwind.
                if (!SaveToDisk()) { m.AttachState = AttachEscrowed; reason = "The server couldn't record that - nothing was returned. Try again shortly."; return false; }
            }
            RefundGoods(username, totals.Silver, items, payloads, $"mail #{id} attachment recalled");
            return true;
        }

        // The message is kept, but an attachment nobody accepts must not strand forever; 0 days disables this.
        public static List<(string Sender, string Recipient, long Id, AttachTotals Totals)> SweepUnclaimedAttachments(long now)
        {
            var returned = new List<(string, string, long, AttachTotals)>();
            int days = MailConfig.Current.UnclaimedAttachmentDays;
            if (days <= 0) return returned;
            long cutoff = now - TimeSpan.FromDays(days).Ticks;

            var pending = new List<(string Sender, long Id, AttachTotals Totals, Dictionary<string, int> Items, List<Items.KmhThingPayload> Payloads, string Recipient)>();
            lock (_lock)
                foreach (MailMessage m in _byId.Values)
                {
                    if (m.AttachState != AttachEscrowed || !HasAttachment(m)) continue;
                    if (m.ReadUtcTicks > 0 || m.SentUtcTicks > cutoff) continue;   // only never-read mail past the window
                    string sender = m.FromUsername, to = m.ToUsername;
                    Dictionary<string, int> items = CopyItems(m.AttachedItems);
                    List<Items.KmhThingPayload> payloads = CopyPayloads(m.AttachedPayloads);
                    AttachTotals t = new AttachTotals { Silver = m.AttachedSilver, ItemUnits = SumUnits(items), GearCount = payloads?.Count ?? 0 };
                    if (!TryResolveAttachment(m, AttachRefunded)) continue;
                    pending.Add((sender, m.Id, t, items, payloads, to));
                }

            if (pending.Count == 0) return returned;
            // Refunded persists before crediting, so a crash cannot re-refund; an unwritten sweep rolls back and waits for the next pass.
            if (!SaveToDisk())
            {
                lock (_lock)
                    foreach (var p in pending)
                        if (_byId.TryGetValue(p.Id, out MailMessage m)) m.AttachState = AttachEscrowed;
                Diagnostics.ServerLog.Warn($"Mail: could not save {pending.Count} unclaimed-attachment return(s) - left escrowed for the next sweep.");
                return returned;
            }
            foreach (var p in pending)
            {
                RefundGoods(p.Sender, p.Totals.Silver, p.Items, p.Payloads, $"mail #{p.Id} attachment unclaimed - returned");
                returned.Add((p.Sender, p.Recipient, p.Id, p.Totals));
            }
            return returned;
        }

        // The state moves under the lock and the credit happens outside it, so a refund can only land once.
        public static bool Decline(string username, long id, out AttachTotals totals, out string sender, out string reason)
        {
            reason = null; sender = null; totals = default;
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out MailMessage m) || !MayReceive(m, username)) { reason = "No such message."; return false; }
                sender = m.FromUsername;
                if (!TryResolveAttachment(m, AttachRefunded)) { reason = "There's nothing to return on that message."; return false; }
                totals.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                totals.ItemUnits = SumUnits(items); totals.GearCount = payloads?.Count ?? 0;
                // Refunded persists before crediting the sender, so a crash cannot re-refund.
                if (!SaveToDisk()) { m.AttachState = AttachEscrowed; reason = "The server couldn't record that - nothing was returned. Try again shortly."; return false; }
            }
            RefundGoods(sender, totals.Silver, items, payloads, $"mail #{id} attachment declined");
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
                            });
                    }
                    _byId.Remove(m.Id);
                    removed++;
                }
                if (removed > 0) SaveToDisk();
            }
            foreach (PendingRefund p in returns)
                RefundGoods(p.Sender, p.Silver, p.Items, p.Payloads, $"mail #{p.Id} returned - recipient was removed");
            return (removed, burned, returns.Count);
        }

        private static Dictionary<string, int> SanitizeItems(Dictionary<string, int> items)
        {
            Dictionary<string, int> outMap = new Dictionary<string, int>();
            if (items == null) return outMap;
            foreach (KeyValuePair<string, int> kv in items)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                outMap[kv.Key] = kv.Value > MaxAttachItemQty ? MaxAttachItemQty : kv.Value;
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
            if (Eq(from, to))               { reason = "You can't mail yourself."; return false; }
            if (string.IsNullOrEmpty(body) && string.IsNullOrEmpty(subject))
            { reason = "Write a subject or a message."; return false; }
            return true;
        }

        // One locked pass, because a snapshot fires on every mail action and on the hub's refresh tick.
        public struct InboxView
        {
            public List<MailMessage>       Inbox;             // received, newest first
            public List<MailMessage>       Outgoing;          // my sent mail still escrowed + unread (the recallable set)
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
            Dictionary<string, int> items = null; List<Items.KmhThingPayload> payloads = null;
            lock (_lock)
                if (_byId.TryGetValue(id, out MailMessage m) && MayReceive(m, username))
                {
                    sender = m.FromUsername;
                    if (TryResolveAttachment(m, AttachRefunded))
                    {
                        refunded.Silver = m.AttachedSilver; items = CopyItems(m.AttachedItems); payloads = CopyPayloads(m.AttachedPayloads);
                        refunded.ItemUnits = SumUnits(items); refunded.GearCount = payloads?.Count ?? 0;
                    }
                    removed = _byId.Remove(id);
                    // Removal persists before the refund, so a crash cannot re-refund and an unwritten discard is never paid out.
                    if (removed && !SaveToDisk())
                    {
                        m.AttachState = AttachEscrowed;
                        _byId[id] = m;
                        return false;
                    }
                }
            if (refunded.Any) RefundGoods(sender, refunded.Silver, items, payloads, $"mail #{id} discarded with attachment");
            return removed;
        }

        // Unread mail is kept at any age, so an unclaimed attachment never times out silently.
        public static int PruneOld(long now)
        {
            long cutoff = now - TimeSpan.FromDays(RetentionDays).Ticks;
            List<long> drop = new List<long>();
            var refunds = new List<(string Sender, long Silver, Dictionary<string, int> Items, List<Items.KmhThingPayload> Payloads, long Id)>();
            lock (_lock)
            {
                foreach (MailMessage m in _byId.Values)
                    if (m.ReadUtcTicks > 0 && m.ReadUtcTicks <= cutoff)
                    {
                        string sender = m.FromUsername;
                        if (TryResolveAttachment(m, AttachRefunded)) refunds.Add((sender, m.AttachedSilver, CopyItems(m.AttachedItems), CopyPayloads(m.AttachedPayloads), m.Id));
                        drop.Add(m.Id);
                    }
                List<MailMessage> dropped = new List<MailMessage>(drop.Count);
                foreach (long id in drop) { if (_byId.TryGetValue(id, out MailMessage d)) dropped.Add(d); _byId.Remove(id); }
                // Removals persist before the refunds, so a crash cannot re-refund; an unwritten prune rolls back for the next pass.
                if (drop.Count > 0 && !SaveToDisk())
                {
                    foreach (MailMessage d in dropped) { d.AttachState = AttachEscrowed; _byId[d.Id] = d; }
                    Diagnostics.ServerLog.Warn($"Mail: could not save the pruning of {drop.Count} old message(s) - kept for the next pass.");
                    return 0;
                }
            }
            foreach ((string sender, long silver, Dictionary<string, int> items, List<Items.KmhThingPayload> payloads, long id) in refunds)
                RefundGoods(sender, silver, items, payloads, $"mail #{id} expired with attachment");
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
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.MailFile, out PersistedState s) || s == null) return;
            lock (_lock)
            {
                _byId.Clear();
                if (s.Messages != null) foreach (MailMessage m in s.Messages) if (m != null && m.Id > 0) _byId[m.Id] = m;
                _nextId = Math.Max(1, s.NextId);
            }
        }

        // False means in-memory only; the persist-before-credit rule above only protects attachments while this can report a failed write.
        public static bool SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock) { s.Messages.AddRange(_byId.Values); s.NextId = _nextId; seq = JsonFileStore.NextSequence(); }
            return JsonFileStore.Save(KmhDataPaths.MailFile, s, seq);
        }

        public static void ClearForNewSeason()
        {
            lock (_lock) { _byId.Clear(); _nextId = 1; _recentSends.Clear(); }
            SaveToDisk();
        }
    }
}
