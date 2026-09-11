namespace KMHServerAddon.Maintenance
{
    // Stores mark on commit, broadcasters clear the mark, so a path that forgets to announce costs one sweeper tick.
    internal static class KmhInvalidation
    {
        private static volatile bool _marketplaceDirty;
        private static volatile bool _auctionsDirty;

        internal static void MarketplaceCommitted() => _marketplaceDirty = true;
        internal static void AuctionsCommitted()    => _auctionsDirty = true;

        internal static void MarketplaceAnnounced() => _marketplaceDirty = false;
        internal static void AuctionsAnnounced()    => _auctionsDirty = false;

        internal static bool MarketplacePending => _marketplaceDirty;
        internal static bool AuctionsPending    => _auctionsDirty;

        // The broadcasts clear their own marks, so a drain that pushes nothing is the steady state.
        internal static void Drain()
        {
            if (_marketplaceDirty) Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
            if (_auctionsDirty)    Features.Auctions.AuctionHandler.BroadcastSnapshot();
        }

        private static bool _wired;

        // Discord/SDK/admin/sweeper vault changes hold no client to answer; the store announced them to nobody.
        internal static void Wire()
        {
            if (_wired) return;
            _wired = true;
            Extensibility.KmhEventBus.Instance.TreasuryChanged += OnTreasuryChanged;
        }

        private static void OnTreasuryChanged(KMH.Sdk.Server.Events.TreasuryChangedEvent e)
        {
            if (e == null || e.IsGuildOwned || string.IsNullOrEmpty(e.OwnerKey)) return;
            string user = Features.Treasury.TreasuryStore.UsernameOfOwnerKey(e.OwnerKey);
            if (string.IsNullOrEmpty(user)) return;
            try
            {
                SubProtocol.KmhRouter.SendToUsername(user, SubProtocol.KmhProtocol.Kind.TreasurySnapshot,
                                                     Features.Treasury.TreasuryStore.GetSnapshotFor(user));
            }
            catch (System.Exception ex)
            {
                Diagnostics.ServerLog.Verbose($"Invalidation: could not push a treasury snapshot to '{user}': {ex.Message}");
            }
        }
    }
}
