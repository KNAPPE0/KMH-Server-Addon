using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Auctions
{
    // Every action is attributed to the authenticated session user, never to a field in the envelope.
    internal static class AuctionHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.AuctionRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.AuctionPost,    OnPost);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.AuctionBid,     OnBid);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.AuctionCancel,  OnCancel);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env) => SendSnapshotTo(client);

        public static void SendSnapshotTo(ServerClient client)
        {
            if (client == null) return;
            string u = client.GetData<UserFile>()?.Username;
            KmhRouter.SendTo(client, KmhProtocol.Kind.AuctionSnapshot, AuctionStore.BuildSnapshot(u));
        }

        // Derived once per broadcast, so viewers sharing a key share one build and one serialization.
        public static void BroadcastSnapshot()
        {
            Maintenance.KmhInvalidation.AuctionsAnnounced();
            System.Collections.Generic.HashSet<string> nonPublic = AuctionStore.SellersOfNonPublic();
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.AuctionSnapshot,
                u => Features.Guilds.GuildVisibility.SnapshotShareKey(u, nonPublic),
                u => AuctionStore.BuildSnapshot(u));
        }

        private static void OnPost(ServerClient client, KmhEnvelope env)
        {
            string seller = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(seller)) return;

            string itemDef = env?.GetString("item_def_name") ?? "";
            string stuff   = env?.GetString("stuff_def_name") ?? "";
            int quality    = env?.GetInt("quality_index", 0) ?? 0;
            int qty        = env?.GetInt("qty", 0) ?? 0;
            long startBid  = env?.GetInt("starting_bid", 1) ?? 1;
            long minInc    = env?.GetInt("min_increment", 1) ?? 1;
            long buyout    = env?.GetInt("buyout_silver", 0) ?? 0;
            int hours      = env?.GetInt("hours", 0) ?? 0;
            string vis     = env?.GetString("visibility") ?? "public";
            string fingerprint = env?.GetString("fingerprint") ?? "";

            var op = new Security.KmhOpClaim("auction.post", seller, env);
            if (!op.Begin()) { SendSnapshotTo(client); Push(client, seller); return; }

            (long id, string reason) = string.IsNullOrEmpty(fingerprint)
                ? AuctionStore.Post(seller, itemDef, stuff, quality, qty, startBid, minInc, buyout, hours, vis)
                : AuctionStore.PostPayload(seller, fingerprint, qty, startBid, minInc, buyout, hours, vis);
            if (id <= 0) op.Release();
            KmhRouter.Notify(client, id > 0 ? "positive" : "negative", reason);
            if (id > 0)
            {
                Push(client, seller);     // item escrowed out of the seller's treasury
                BroadcastSnapshot();
            }
        }

        private static void OnBid(ServerClient client, KmhEnvelope env)
        {
            string bidder = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(bidder)) return;
            long id     = env?.GetInt("auction_id", 0) ?? 0;
            long amount = env?.GetInt("amount", 0) ?? 0;
            if (id <= 0 || amount <= 0) return;

            AuctionStore.BidResult r = AuctionStore.Bid(bidder, id, amount);
            KmhRouter.Notify(client, r.Ok ? "positive" : "negative", r.Ok ? "Bid placed." : r.Reason);

            Push(client, bidder);   // reflects the escrow (or its refund on a lost-race reject)
            if (r.Ok)
            {
                if (!string.IsNullOrEmpty(r.OutbidUser))   // refunded previous high bidder - tell them
                {
                    PushUser(r.OutbidUser);
                    NotifyUser(r.OutbidUser, "neutral", "Outbid",
                        $"You were outbid on {r.Auction.Qty}x {ItemName(r.Auction.ItemDefName)} (top bid {Util.SilverFmt.Format(r.Auction.CurrentBid)}).");
                }
                if (r.Settlement != null && r.Settlement.Done)   // buyout settled it
                {
                    foreach (string u in r.Settlement.Affected) PushUser(u);
                    NotifySettled(r.Settlement);
                }
                BroadcastSnapshot();
            }
        }

        public static void NotifySettled(AuctionStore.SettleOutcome o)
        {
            if (o == null || !o.Done) return;
            string item = $"{o.Qty}x {ItemName(o.ItemDefName)}";
            if (o.Sold)
            {
                NotifyUser(o.Winner, "positive", "Auction won", $"You won {item} for {Util.SilverFmt.Format(o.FinalBid)}!");
                NotifyUser(o.Seller, "positive", "Auction sold",
                    $"Your auction ({item}) sold for {Util.SilverFmt.Format(o.FinalBid)} - {Util.SilverFmt.Format(o.SellerNet)} after tax.");
            }
            else
            {
                NotifyUser(o.Seller, "neutral", "Auction ended", $"Your auction ({item}) ended with no bids - returned to your treasury.");
            }
        }

        // Queued when offline, because an auction settles on the sweeper while both parties may be away.
        private static void NotifyUser(string user, string level, string title, string text)
            => Notifications.KmhNotify.ToUser(user, level, title, text);

        private static string ItemName(string key)
        {
            Util.ItemKey.Split(key, out string def, out _, out _);
            return def;
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            string seller = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(seller)) return;
            long id = env?.GetInt("auction_id", 0) ?? 0;

            (bool ok, string reason) = AuctionStore.Cancel(seller, id);
            KmhRouter.Notify(client, ok ? "positive" : "negative", reason);
            if (ok) { Push(client, seller); BroadcastSnapshot(); }
        }

        public static string AdminVoid(long id)
        {
            AuctionStore.VoidOutcome o = AuctionStore.AdminVoid(id);
            if (!o.Done) return $"Auction #{id} not found.";
            foreach (string u in o.Affected) PushUser(u);
            NotifyUser(o.Seller, "neutral", "Auction voided",
                $"An admin voided your auction ({o.Qty}x {ItemName(o.ItemDefName)}) - the item was returned to your treasury.");
            if (!string.IsNullOrEmpty(o.Bidder))
                NotifyUser(o.Bidder, "neutral", "Auction voided",
                    $"An admin voided an auction you led - {Util.SilverFmt.Format(o.RefundedBid)} was refunded to your treasury.");
            BroadcastSnapshot();
            return $"Auction #{id} voided - {o.Qty}x {ItemName(o.ItemDefName)} returned to {o.Seller}" +
                   (o.RefundedBid > 0 ? $", {Util.SilverFmt.Format(o.RefundedBid)} refunded to {o.Bidder}." : ".");
        }

        private static void Push(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));

        private static void PushUser(string user)
            => KmhRouter.SendToUsername(user, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));
    }
}
