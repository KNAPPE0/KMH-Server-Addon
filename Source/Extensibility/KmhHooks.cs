using System;
using System.Collections.Generic;
using KMH.Sdk.Server.Hooks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Extensibility
{
    // Unlike KmhEventBus a hook can deny an action, so registration is copy-on-write and Check* reads without a lock.
    internal sealed class KmhHooks : IKmhHooks
    {
        public static KmhHooks Instance { get; } = new KmhHooks();
        private KmhHooks() { }

        private readonly object _lock = new object();
        private volatile Func<KmhMarketplaceListingContext, KmhHookVerdict>[] _marketplaceListing =
            Array.Empty<Func<KmhMarketplaceListingContext, KmhHookVerdict>>();
        private volatile Func<KmhMarketplaceVisibilityContext, KmhHookVerdict>[] _marketplaceVisibility =
            Array.Empty<Func<KmhMarketplaceVisibilityContext, KmhHookVerdict>>();
        private volatile Func<KmhAuctionListingContext, KmhHookVerdict>[] _auctionListing =
            Array.Empty<Func<KmhAuctionListingContext, KmhHookVerdict>>();
        private volatile Func<KmhWantContext, KmhHookVerdict>[] _want =
            Array.Empty<Func<KmhWantContext, KmhHookVerdict>>();
        private volatile Func<KmhQuestPostContext, KmhHookVerdict>[] _questPost =
            Array.Empty<Func<KmhQuestPostContext, KmhHookVerdict>>();
        private volatile Func<KmhTreasuryWithdrawContext, KmhHookVerdict>[] _treasuryWithdraw =
            Array.Empty<Func<KmhTreasuryWithdrawContext, KmhHookVerdict>>();

        public void OnMarketplaceListing(Func<KmhMarketplaceListingContext, KmhHookVerdict> hook)
        { if (hook != null) lock (_lock) { _marketplaceListing = Append(_marketplaceListing, hook); } }

        public void OnMarketplaceVisibility(Func<KmhMarketplaceVisibilityContext, KmhHookVerdict> hook)
        { if (hook != null) lock (_lock) { _marketplaceVisibility = Append(_marketplaceVisibility, hook); } }

        public void OnAuctionListing(Func<KmhAuctionListingContext, KmhHookVerdict> hook)
        { if (hook != null) lock (_lock) { _auctionListing = Append(_auctionListing, hook); } }

        public void OnWant(Func<KmhWantContext, KmhHookVerdict> hook)
        { if (hook != null) lock (_lock) { _want = Append(_want, hook); } }

        public void OnQuestPost(Func<KmhQuestPostContext, KmhHookVerdict> hook)
        { if (hook != null) lock (_lock) { _questPost = Append(_questPost, hook); } }

        public void OnTreasuryWithdraw(Func<KmhTreasuryWithdrawContext, KmhHookVerdict> hook)
        { if (hook != null) lock (_lock) { _treasuryWithdraw = Append(_treasuryWithdraw, hook); } }

        public (int marketplace, int auction, int want, int quest, int withdraw, int visibility) Counts()
            => (_marketplaceListing.Length, _auctionListing.Length, _want.Length, _questPost.Length,
                _treasuryWithdraw.Length, _marketplaceVisibility.Length);
        public int TotalRegistered => _marketplaceListing.Length + _auctionListing.Length + _want.Length
                                      + _questPost.Length + _treasuryWithdraw.Length + _marketplaceVisibility.Length;

        public KmhHookVerdict CheckMarketplaceListing(KmhMarketplaceListingContext ctx)
            => Aggregate(_marketplaceListing, ctx, "MarketplaceListing", m => ServerLog.Warn(m));

        // Read once before a snapshot loop, so a server with no visibility hook pays nothing per listing.
        public bool HasMarketplaceVisibility => _marketplaceVisibility.Length > 0;

        // Denies go unlogged because this runs per listing per viewer and would flood the console.
        public bool CheckMarketplaceVisible(string viewer, long listingId, string seller, string itemDefName)
        {
            var hooks = _marketplaceVisibility;
            if (hooks.Length == 0) return true;
            return !Aggregate(hooks, new KmhMarketplaceVisibilityContext(viewer, listingId, seller, itemDefName),
                              "MarketplaceVisibility", m => ServerLog.Warn(m)).Denied;
        }

        public KmhHookVerdict CheckAuctionListing(KmhAuctionListingContext ctx)
            => Aggregate(_auctionListing, ctx, "AuctionListing", m => ServerLog.Warn(m));
        public KmhHookVerdict CheckWant(KmhWantContext ctx)
            => Aggregate(_want, ctx, "Want", m => ServerLog.Warn(m));
        public KmhHookVerdict CheckQuestPost(KmhQuestPostContext ctx)
            => Aggregate(_questPost, ctx, "QuestPost", m => ServerLog.Warn(m));
        public KmhHookVerdict CheckTreasuryWithdraw(KmhTreasuryWithdrawContext ctx)
            => Aggregate(_treasuryWithdraw, ctx, "TreasuryWithdraw", m => ServerLog.Warn(m));

        private static Func<T, KmhHookVerdict>[] Append<T>(Func<T, KmhHookVerdict>[] arr, Func<T, KmhHookVerdict> hook)
        {
            var next = new List<Func<T, KmhHookVerdict>>(arr) { hook };
            return next.ToArray();
        }

        // The visibility hook runs once per listing per viewer, so one broken extension buried the console.
        private const int    HookErrorsPerWindow  = 1;
        private const int    HookErrorWindowSecs  = 60;
        private static readonly object _errLock = new object();
        private static readonly Util.KmhRateWindow _errLog = new Util.KmhRateWindow();
        private static readonly Dictionary<string, int> _errSuppressed = new Dictionary<string, int>(StringComparer.Ordinal);

        // Test seam: stands in for the minute passing, so the suppressed tally can be proven without waiting one.
        internal static void ResetErrorWindowForTest(string name)
        {
            lock (_errLock) _errLog.Forget(name);
        }

        private static void NoteHookError(string name, string message, Action<string> onError)
        {
            if (onError == null) return;
            int suppressed;
            lock (_errLock)
            {
                if (!_errLog.Allow(name, DateTime.UtcNow.Ticks, HookErrorsPerWindow, HookErrorWindowSecs))
                {
                    _errSuppressed.TryGetValue(name, out int n);
                    _errSuppressed[name] = n + 1;
                    return;
                }
                _errSuppressed.TryGetValue(name, out suppressed);
                _errSuppressed.Remove(name);
            }
            onError(suppressed > 0
                ? $"Extension hook '{name}' threw (treated as allow): {message} [+{suppressed} more in the last {HookErrorWindowSecs}s]"
                : $"Extension hook '{name}' threw (treated as allow): {message}");
        }

        // A throwing hook counts as Allow, so a broken extension cannot block every action on the server.
        public static KmhHookVerdict Aggregate<T>(Func<T, KmhHookVerdict>[] hooks, T ctx, string name, Action<string> onError)
        {
            if (hooks == null || hooks.Length == 0) return KmhHookVerdict.Allow;
            foreach (Func<T, KmhHookVerdict> h in hooks)
            {
                KmhHookVerdict v;
                try { v = h(ctx); }
                catch (Exception ex) { NoteHookError(name, ex.Message, onError); continue; }
                if (v.Denied) return v;
            }
            return KmhHookVerdict.Allow;
        }
    }
}
