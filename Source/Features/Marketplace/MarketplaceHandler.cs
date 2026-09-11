using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Marketplace
{
    internal static class MarketplaceHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MarketplaceRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MarketplaceBuy,     OnBuy);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MarketplaceCancel,  OnCancel);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.MarketplacePost,    OnPost);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        private static void OnPost(ServerClient client, KmhEnvelope env)
        {
            string username       = client?.GetData<UserFile>()?.Username;
            string itemDefName    = env?.GetString("item_def_name") ?? "";
            string stuffDefName   = env?.GetString("stuff_def_name") ?? "";
            int    qualityIndex   = env?.GetInt("quality_index", 0) ?? 0;
            int    qty            = env?.GetInt("qty", 0) ?? 0;
            int    price          = env?.GetInt("unit_price_silver", 0) ?? 0;
            int    priceMilli     = env?.GetInt("unit_price_milli", -1) ?? -1;   // -1 is an old client: derive from whole silver
            string visibility     = env?.GetString("visibility", "public") ?? "public";
            int    expiresInHours = env?.GetInt("expires_hours", 0) ?? 0;
            string fingerprint    = env?.GetString("fingerprint") ?? "";

            var op = new Security.KmhOpClaim("mkt.post", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); SendTreasurySnapshotTo(client); return; }

            long id;
            string reason;
            // A fingerprint means the item is escrowed from the payload store with its state intact.
            if (!string.IsNullOrEmpty(fingerprint))
                id = MarketplaceStore.PostPayload(username, fingerprint, qty, price, visibility, expiresInHours, out reason, priceMilli);
            else
                id = MarketplaceStore.Post(username, itemDefName, qty, price, visibility, expiresInHours, out reason,
                                            stuffDefName, qualityIndex, priceMilli);
            if (id == 0)
            {
                op.Release();
                ServerLog.Verbose($"Marketplace post rejected for {username}: {reason}");
                KmhRouter.Notify(client, "negative",
                    string.IsNullOrEmpty(reason) ? "Listing failed - check the item, quantity, and price." : reason);
                // The optimistic UI already showed the items as gone, so a failed post has to be corrected.
                SendTreasurySnapshotTo(client);
                return;
            }
            ServerLog.Info($"Marketplace: {username} posted listing #{id} ({qty}x {itemDefName} @ {price}s)");
            KmhRouter.Notify(client, "positive", "Listing posted.");
            BroadcastSnapshot();
            SendTreasurySnapshotTo(client);
        }

        private static void OnBuy(ServerClient client, KmhEnvelope env)
        {
            string username  = client?.GetData<UserFile>()?.Username;
            long   listingId = (long)(env?.GetInt("listing_id", 0) ?? 0);
            int    qty       = env?.GetInt("qty", 0) ?? 0;

            // listing_id is logically long but the envelope int is 32-bit; ids stay well inside Int32 range.
            if (listingId <= 0 || qty <= 0) return;

            var op = new Security.KmhOpClaim("mkt.buy", username, env);
            if (!op.Begin()) { SendSnapshotTo(client); SendTreasurySnapshotTo(client); return; }

            if (MarketplaceStore.Buy(username, listingId, qty, out string sellerUsername, out int boughtQty, out int paidSilver, env?.OpId ?? ""))
            {
                ServerLog.Info($"Marketplace: {username} bought x{boughtQty} of listing #{listingId} for {paidSilver}s");
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client);
                PushTreasuryTo(sellerUsername);
                if (!string.Equals(sellerUsername, username, System.StringComparison.OrdinalIgnoreCase))
                    Features.Notifications.KmhNotify.ToUser(sellerUsername, "positive", "Marketplace sale",
                        "One of your marketplace listings sold - the silver is in your treasury.");
            }
            else
            {
                op.Release();
                ServerLog.Verbose($"Marketplace buy rejected for {username} (listing #{listingId} qty {qty})");
                KmhRouter.Notify(client, "negative", "Purchase failed - not enough silver, or the listing changed.");
                // The optimistic UI already applied the purchase, so a rejection has to be corrected.
                SendSnapshotTo(client);
                SendTreasurySnapshotTo(client);
            }
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            string username  = client?.GetData<UserFile>()?.Username;
            long   listingId = (long)(env?.GetInt("listing_id", 0) ?? 0);
            if (listingId <= 0) return;

            if (MarketplaceStore.Cancel(username, listingId))
            {
                ServerLog.Info($"Marketplace: {username} cancelled listing #{listingId}");
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client);
            }
            else
            {
                ServerLog.Verbose($"Marketplace cancel rejected for {username} (listing #{listingId} not found or not seller)");
                KmhRouter.Notify(client, "negative", "Couldn't cancel that listing - it may be gone or not yours.");
            }
        }

        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Dto.MarketplaceSnapshot snapshot = MarketplaceStore.BuildSnapshot(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.MarketplaceSnapshot, snapshot);
        }

        // The seller set is derived once here, not per recipient, or every viewer costs another full snapshot build.
        internal static void BroadcastSnapshot()
        {
            Maintenance.KmhInvalidation.MarketplaceAnnounced();
            System.Collections.Generic.HashSet<string> guildOnlySellers = MarketplaceStore.SellersWithGuildOnlyListings();
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.MarketplaceSnapshot,
                u => MarketplaceStore.ShareKeyFor(u, guildOnlySellers),
                u => MarketplaceStore.BuildSnapshot(u));
        }

        private static void SendTreasurySnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }

        internal static void PushTreasuryTo(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendToUsername(username, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
