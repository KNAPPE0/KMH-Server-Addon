using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Marketplace
{
    // Server-side handler for kmh.marketplace.* - counterpart to the patch mod's
    // KMHPatch.Features.Marketplace.MarketplaceHandler
    //
    // Snapshot strategy:
    //   - On Request, send caller-scoped snapshot.
    //   - On any mutation (post / buy / cancel), broadcast a fresh
    // snapshot to EVERY verified client so all open marketplace dialogs refresh immediately. Cheap - listings list
    // is small
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
            int    qty            = env?.GetInt("qty", 0) ?? 0;
            int    price          = env?.GetInt("unit_price_silver", 0) ?? 0;
            string visibility     = env?.GetString("visibility", "public") ?? "public";
            int    expiresInHours = env?.GetInt("expires_hours", 0) ?? 0;

            long id = MarketplaceStore.Post(username, itemDefName, qty, price, visibility, expiresInHours, out string reason);
            if (id == 0)
            {
                ServerLog.Verbose($"Marketplace post rejected for {username}: {reason}");
                // Tell the user WHY (price floor, listing cap, treasury short).
                KmhRouter.Notify(client, "negative",
                    string.IsNullOrEmpty(reason) ? "Listing failed - check the item, quantity, and price." : reason);
                // Still resync the caller's treasury so an optimistic UI doesn't show items as gone if the post
                // failed
                SendTreasurySnapshotTo(client);
                return;
            }
            ServerLog.Info($"Marketplace: {username} posted listing #{id} ({qty}x {itemDefName} @ {price}s)");
            KmhRouter.Notify(client, "positive", "Listing posted.");
            BroadcastSnapshot();
            SendTreasurySnapshotTo(client); // poster's treasury changed
        }

        private static void OnBuy(ServerClient client, KmhEnvelope env)
        {
            string username  = client?.GetData<UserFile>()?.Username;
            long   listingId = (long)(env?.GetInt("listing_id", 0) ?? 0);
            int    qty       = env?.GetInt("qty", 0) ?? 0;

            // Envelope int is 32-bit but listing_id is logically long; in practice ids stay well within Int32 range
            // for any plausible session lifetime. JObject path is also fine since GetInt routes through
            // Newtonsoft's coercion
            if (listingId <= 0 || qty <= 0) return;

            if (MarketplaceStore.Buy(username, listingId, qty, out string sellerUsername))
            {
                ServerLog.Info($"Marketplace: {username} bought x{qty} of listing #{listingId}");
                BroadcastSnapshot();
                SendTreasurySnapshotTo(client); // buyer's treasury changed
                // Seller's treasury also changed - push if they're online so their Treasury dialog updates
                // immediately rather than after the 8s auto-refresh tick
                PushTreasuryTo(sellerUsername);
            }
            else
            {
                ServerLog.Verbose($"Marketplace buy rejected for {username} (listing #{listingId} qty {qty})");
                KmhRouter.Notify(client, "negative", "Purchase failed - not enough silver, or the listing changed.");
                // Push corrective snapshots so the patch's optimistic UI gets truth.
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
                SendTreasurySnapshotTo(client); // items refunded to poster's treasury
            }
            else
            {
                ServerLog.Verbose($"Marketplace cancel rejected for {username} (listing #{listingId} not found or not seller)");
                KmhRouter.Notify(client, "negative", "Couldn't cancel that listing - it may be gone or not yours.");
            }
        }

        // --- snapshot delivery helpers ---

        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Dto.MarketplaceSnapshot snapshot = MarketplaceStore.BuildSnapshot(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.MarketplaceSnapshot, snapshot);
        }

        // Push the marketplace snapshot to every verified client. Called after any mutation so all open marketplace
        // dialogs refresh immediately. Internal so Discord-driven mutations (DiscordTrade Commands) can fire the
        // same notification path the in-game wire handlers do
        internal static void BroadcastSnapshot()
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                string username = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(username)) continue;
                Dto.MarketplaceSnapshot snapshot = MarketplaceStore.BuildSnapshot(username);
                KmhRouter.SendTo(c, KmhProtocol.Kind.MarketplaceSnapshot, snapshot);
            }
        }

        // Push a fresh treasury snapshot to the given client. Used after buy / cancel / post since their treasury
        // just changed
        private static void SendTreasurySnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }

        // Send a fresh treasury snapshot to a specific username if they're currently online. No-op if not
        // connected. Caller-scoped so the recipient's CanDeposit / CanWithdraw flags reflect their own permissions
        // on their own vault
        //
        // Internal - Discord-driven mutations call this for both buyer and seller after !kmh-buy / !kmh-sell /
        // !kmh-cancel so their open in-game treasury dialogs refresh immediately
        internal static void PushTreasuryTo(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            Treasury.Dto.TreasurySnapshot snapshot = Treasury.TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendToUsername(username, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
