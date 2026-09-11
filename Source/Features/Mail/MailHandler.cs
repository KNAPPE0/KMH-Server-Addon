using System.Collections.Generic;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Mail
{
    // Sender identity comes from the authenticated client, never the packet, and attachments settle exactly once.
    internal static class MailHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailSend,     OnSend);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailRequest,  OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailMarkRead, OnMarkRead);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailDelete,   OnDelete);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailAccept,   OnAccept);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailDecline,  OnDecline);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MailRecall,   OnRecall);
        }

        public static void SweepUnclaimedAttachments(long now)
        {
            foreach ((string sender, string recipient, long id, MailStore.AttachTotals t) in MailStore.SweepUnclaimedAttachments(now))
            {
                Diagnostics.ServerLog.Info($"Mail: unclaimed attachment on #{id} returned to {sender} (never read by {recipient}).");
                PushUser(sender);
                SendSnapshotToUsername(sender);
                SendSnapshotToUsername(recipient);
                Features.Notifications.KmhNotify.ToUser(sender, "neutral", "Mail attachment returned",
                    $"{recipient} never opened your mail, so the {Describe(t.Silver, t.ItemUnits, t.GearCount)} you attached is back in your treasury.");
            }
        }

        private static void OnSend(ServerClient client, KmhEnvelope env)
        {
            string from = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(from)) return;
            string to      = env?.GetString("to") ?? "";
            string subject = env?.GetString("subject") ?? "";
            string body    = env?.GetString("body") ?? "";
            long   silver  = env?.GetInt("attach_silver", 0) ?? 0;
            Dictionary<string, int> items      = env?.GetIntMap("attach_items")       ?? new Dictionary<string, int>();
            Dictionary<string, int> payloadSel = env?.GetIntMap("attach_payload_sel") ?? new Dictionary<string, int>();

            var op = new Security.KmhOpClaim("mail.send", from, env);
            if (!op.Begin()) { SendSnapshotToUsername(from); Push(client, from); return; }

            long id = MailStore.Send(from, to, subject, body, silver, items, payloadSel,
                                     out string evictedSender, out MailStore.AttachTotals evicted, out string reason);
            if (id <= 0) { op.Release(); KmhRouter.Notify(client, "negative", reason ?? "Mail couldn't be sent."); return; }

            // A third party lost read mail to make room for this one, so they get the same refund notice as anyone else.
            if (evicted.Any) NotifyRefunded(evictedSender, to, evicted, "had a full inbox, so your mail was removed");

            int units = 0; foreach (KeyValuePair<string, int> kv in items) units += kv.Value;
            int gear  = 0; foreach (KeyValuePair<string, int> kv in payloadSel) gear += kv.Value;
            bool hasAttach = silver > 0 || units > 0 || gear > 0;
            string desc = Describe(silver, units, gear);

            KmhRouter.Notify(client, "positive", hasAttach ? $"Mail sent to {to} with {desc} attached." : $"Mail sent to {to}.");
            if (hasAttach) Push(client, from);   // goods escrowed out of the sender's treasury

            string note = $"You have new mail from {from}" + (string.IsNullOrEmpty(subject) ? "." : $": {subject}");
            if (hasAttach) note += $" ({desc} attached - open Mail to accept.)";
            Features.Notifications.KmhNotify.ToUser(to, "neutral", "New mail", note);

            SendSnapshotToUsername(to);
            SendSnapshotToUsername(from);   // the sender's escrowed-out total changed

            Extensibility.KmhEventBus.Instance.RaiseMailSent(new KMH.Sdk.Server.Events.MailSentEvent
            {
                MailId = id, FromUsername = from, ToUsername = to,
                AttachedSilver = silver, AttachedItems = units, AttachedGear = gear,
            });
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (!string.IsNullOrEmpty(user)) KmhRouter.SendTo(client, KmhProtocol.Kind.MailSnapshot, BuildInboxPayload(user));
        }

        private static void OnMarkRead(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            long id = env?.GetInt("id", 0) ?? 0;
            if (!string.IsNullOrEmpty(user) && id > 0 && MailStore.MarkRead(user, id))
                KmhRouter.SendTo(client, KmhProtocol.Kind.MailSnapshot, BuildInboxPayload(user));
        }

        private static void OnDelete(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            long id = env?.GetInt("id", 0) ?? 0;
            if (string.IsNullOrEmpty(user) || id <= 0) return;
            if (!MailStore.Delete(user, id, out MailStore.AttachTotals t, out string sender)) return;

            KmhRouter.SendTo(client, KmhProtocol.Kind.MailSnapshot, BuildInboxPayload(user));
            if (t.Any) NotifyRefunded(sender, user, t, "discarded your mail");
        }

        private static void OnAccept(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            long id = env?.GetInt("id", 0) ?? 0;
            if (string.IsNullOrEmpty(user) || id <= 0) return;

            if (!MailStore.Claim(user, id, out MailStore.AttachTotals t, out string sender, out string reason))
            { KmhRouter.Notify(client, "negative", reason ?? "Nothing to accept."); return; }

            string desc = Describe(t.Silver, t.ItemUnits, t.GearCount);
            KmhRouter.Notify(client, "positive", $"Accepted {desc}.");
            Push(client, user);
            SendSnapshotToUsername(user);
            SendSnapshotToUsername(sender);   // the sender's escrowed-out total drops
            Features.Notifications.KmhNotify.ToUser(sender, "positive", "Mail accepted", $"{user} accepted the {desc} you mailed.");
        }

        private static void OnDecline(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            long id = env?.GetInt("id", 0) ?? 0;
            if (string.IsNullOrEmpty(user) || id <= 0) return;

            if (!MailStore.Decline(user, id, out MailStore.AttachTotals t, out string sender, out string reason))
            { KmhRouter.Notify(client, "negative", reason ?? "Nothing to return."); return; }

            KmhRouter.Notify(client, "neutral", $"Returned {Describe(t.Silver, t.ItemUnits, t.GearCount)} to {sender}.");
            SendSnapshotToUsername(user);
            NotifyRefunded(sender, user, t, "declined your mail");
        }

        private static void OnRecall(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            long id = env?.GetInt("id", 0) ?? 0;
            if (string.IsNullOrEmpty(user) || id <= 0) return;

            if (!MailStore.Recall(user, id, out MailStore.AttachTotals t, out string recipient, out string reason))
            { KmhRouter.Notify(client, "negative", reason ?? "Nothing to recall."); return; }

            KmhRouter.Notify(client, "positive", $"Recalled {Describe(t.Silver, t.ItemUnits, t.GearCount)} from your mail to {recipient}.");
            Push(client, user);
            SendSnapshotToUsername(user);
            SendSnapshotToUsername(recipient);   // their copy has to stop showing the attachment
        }

        private static void NotifyRefunded(string sender, string actor, MailStore.AttachTotals t, string what)
        {
            if (string.IsNullOrEmpty(sender)) return;
            PushUser(sender);
            SendSnapshotToUsername(sender);
            Features.Notifications.KmhNotify.ToUser(sender, "neutral", "Mail attachment returned",
                $"{actor} {what}; {Describe(t.Silver, t.ItemUnits, t.GearCount)} is back in your treasury.");
        }

        private static string Describe(long silver, int itemUnits, int gearCount)
        {
            List<string> parts = new List<string>();
            if (silver    > 0) parts.Add($"{Util.SilverFmt.Format(silver)} silver");
            if (itemUnits > 0) parts.Add($"{itemUnits} item(s)");
            if (gearCount > 0) parts.Add($"{gearCount} gear");
            return parts.Count > 0 ? string.Join(" + ", parts) : "the attachment";
        }

        public static void SendSnapshotToUsername(string username)
        {
            if (!string.IsNullOrEmpty(username))
                KmhRouter.SendToUsername(username, KmhProtocol.Kind.MailSnapshot, BuildInboxPayload(username));
        }

        private static void Push(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));

        private static void PushUser(string user)
            => KmhRouter.SendToUsername(user, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));

        private static object BuildInboxPayload(string user)
        {
            MailStore.InboxView v = MailStore.BuildView(user);   // one locked pass, not five
            return new
            {
                revision                   = v.Revision,
                messages                   = v.Inbox,
                outgoing                   = v.Outgoing,
                unread                     = v.Unread,
                escrowed_out_silver        = v.EscrowedOutSilver,
                escrowed_out_items         = v.EscrowedOutItems,
                escrowed_out_payload_value = v.EscrowedOutPayloadValue,
            };
        }
    }
}
