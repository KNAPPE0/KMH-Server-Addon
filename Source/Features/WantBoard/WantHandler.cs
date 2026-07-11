using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.WantBoard
{
    // kmh.want.* handler. Per-caller snapshot (guild visibility); post/fulfill/cancel are attributed to the
    // authenticated session user, never an envelope field.
    internal static class WantHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WantRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WantPost,    OnPost);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WantFulfill, OnFulfill);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.WantCancel,  OnCancel);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env) => SendSnapshotTo(client);

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            string u = client.GetData<UserFile>()?.Username;
            KmhRouter.SendTo(client, KmhProtocol.Kind.WantSnapshot, WantStore.BuildSnapshot(u));
        }

        public static void BroadcastSnapshot()
            => KmhRouter.BroadcastToInterested(KmhProtocol.Kind.WantSnapshot, u => WantStore.BuildSnapshot(u));

        private static void OnPost(ServerClient client, KmhEnvelope env)
        {
            string buyer = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(buyer)) return;

            string itemDef = env?.GetString("item_def_name") ?? "";
            int qty        = env?.GetInt("qty", 0) ?? 0;
            int unitPrice  = env?.GetInt("unit_price_silver", 0) ?? 0;
            int hours      = env?.GetInt("hours", 0) ?? 0;
            string vis     = env?.GetString("visibility") ?? "public";
            int minQ       = env?.GetInt("min_quality", 0) ?? 0;
            string reqStuff = env?.GetString("required_stuff") ?? "";
            bool allowComplex = env?.GetBool("allow_complex") ?? false;
            bool allowTainted = env?.GetBool("allow_tainted") ?? false;
            bool allowDamaged = env?.GetBool("allow_damaged") ?? false;

            (long id, string reason) = WantStore.Post(buyer, itemDef, qty, unitPrice, hours, vis,
                minQ, reqStuff, allowComplex, allowTainted, allowDamaged);
            KmhRouter.Notify(client, id > 0 ? "positive" : "negative", reason);
            if (id > 0)
            {
                Push(client, buyer);   // silver escrowed out of the buyer's treasury
                BroadcastSnapshot();
            }
        }

        private static void OnFulfill(ServerClient client, KmhEnvelope env)
        {
            string seller = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(seller)) return;
            long id = env?.GetInt("want_id", 0) ?? 0;
            int  qty = env?.GetInt("qty", 0) ?? 0;
            if (id <= 0 || qty <= 0) return;

            WantStore.FulfillResult r = WantStore.Fulfill(seller, id, qty);
            KmhRouter.Notify(client, r.Ok ? "positive" : "negative",
                r.Ok ? $"Delivered {r.FilledQty}x {ItemName(r.ItemDefName)} for {Util.SilverFmt.Format(r.SellerNet)}." : r.Reason);

            if (r.Ok)
            {
                Push(client, seller);    // seller: items out, silver in
                PushUser(r.Buyer);       // buyer: items in
                // Tell the buyer (offline -> queued letter) - they didn't initiate this.
                Notifications.KmhMail.ToUser(r.Buyer, "positive", "Want fulfilled",
                    r.Completed
                        ? $"Your want for {ItemName(r.ItemDefName)} is fully filled - the goods are in your treasury."
                        : $"Someone delivered {r.FilledQty}x {ItemName(r.ItemDefName)} toward your want - it's in your treasury.");
                BroadcastSnapshot();
            }
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            string buyer = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(buyer)) return;
            long id = env?.GetInt("want_id", 0) ?? 0;

            (bool ok, string reason) = WantStore.Cancel(buyer, id);
            KmhRouter.Notify(client, ok ? "positive" : "negative", reason);
            if (ok) { Push(client, buyer); BroadcastSnapshot(); }
        }

        // Admin recovery for a stuck want: remove it and refund the buyer's unspent escrow, push their treasury +
        // notice, rebroadcast. Returns a console/chat-ready summary line.
        public static string AdminCancel(long id)
        {
            WantStore.ExpireOutcome o = WantStore.AdminCancel(id);
            if (!o.Done) return $"Want #{id} not found.";
            PushUser(o.Buyer);
            Notifications.KmhMail.ToUser(o.Buyer, "neutral", "Want cancelled",
                o.Refunded > 0
                    ? $"An admin cancelled your want - {Util.SilverFmt.Format(o.Refunded)} was refunded to your treasury."
                    : "An admin cancelled your want.");
            BroadcastSnapshot();
            return $"Want #{id} cancelled" + (o.Refunded > 0 ? $" - {Util.SilverFmt.Format(o.Refunded)} refunded to {o.Buyer}." : ".");
        }

        // The plain def name from a composed treasury key, for short notice text.
        private static string ItemName(string key)
        {
            Util.ItemKey.Split(key, out string def, out _, out _);
            return def;
        }

        private static void Push(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));

        private static void PushUser(string user)
            => KmhRouter.SendToUsername(user, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));
    }
}
