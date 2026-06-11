using System;
using KMH.Sdk.Server.Apis;
using KMH.Sdk.Server.Events;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Extensibility
{
    // Single-instance event hub that extensions subscribe to and KMH
    // internals raise events on. Implements IKmhEvents so every host
    // instance hands extensions the same singleton - subscribing on one host wires you up to all events
    //
    // Subscriber-throw protection: each event handler is called inside a try/catch so a misbehaving extension can't
    // break dispatch for other extensions
    internal sealed class KmhEventBus : IKmhEvents
    {
        public static KmhEventBus Instance { get; } = new KmhEventBus();
        private KmhEventBus() { }

        public event Action<PlayerJoinedEvent>     PlayerJoined;
        public event Action<PlayerLeftEvent>       PlayerLeft;
        public event Action<PlayerLinkedEvent>     PlayerLinked;
        public event Action<PlayerUnlinkedEvent>   PlayerUnlinked;
        public event Action<MarketplacePostEvent>  MarketplacePost;
        public event Action<MarketplaceBuyEvent>   MarketplaceBuy;
        public event Action<MarketplaceCancelEvent> MarketplaceCancel;
        public event Action<QuestPostedEvent>      QuestPosted;
        public event Action<QuestClaimedEvent>     QuestClaimed;
        public event Action<QuestSubmittedEvent>   QuestSubmitted;
        public event Action<QuestApprovedEvent>    QuestApproved;
        public event Action<QuestCancelledEvent>   QuestCancelled;
        public event Action<TreasuryChangedEvent>  TreasuryChanged;
        public event Action<GuildChangedEvent>     GuildChanged;
        public event Action<ReputationChangedEvent> ReputationChanged;
        public event Action<SiteChangedEvent>      SiteChanged;

        // Raise helpers called by KMH internals. Each one walks the delegate chain manually so one throwing
        // subscriber doesn't stop the others - Action<T>.Invoke would short-circuit on first exception
        internal void RaisePlayerJoined    (PlayerJoinedEvent     e) => SafeRaise(PlayerJoined,     e, nameof(PlayerJoined));
        internal void RaisePlayerLeft      (PlayerLeftEvent       e) => SafeRaise(PlayerLeft,       e, nameof(PlayerLeft));
        internal void RaisePlayerLinked    (PlayerLinkedEvent     e) => SafeRaise(PlayerLinked,     e, nameof(PlayerLinked));
        internal void RaisePlayerUnlinked  (PlayerUnlinkedEvent   e) => SafeRaise(PlayerUnlinked,   e, nameof(PlayerUnlinked));
        internal void RaiseMarketplacePost (MarketplacePostEvent  e) => SafeRaise(MarketplacePost,  e, nameof(MarketplacePost));
        internal void RaiseMarketplaceBuy  (MarketplaceBuyEvent   e) => SafeRaise(MarketplaceBuy,   e, nameof(MarketplaceBuy));
        internal void RaiseMarketplaceCancel(MarketplaceCancelEvent e) => SafeRaise(MarketplaceCancel, e, nameof(MarketplaceCancel));
        internal void RaiseQuestPosted     (QuestPostedEvent      e) => SafeRaise(QuestPosted,     e, nameof(QuestPosted));
        internal void RaiseQuestClaimed    (QuestClaimedEvent     e) => SafeRaise(QuestClaimed,    e, nameof(QuestClaimed));
        internal void RaiseQuestSubmitted  (QuestSubmittedEvent   e) => SafeRaise(QuestSubmitted,  e, nameof(QuestSubmitted));
        internal void RaiseQuestApproved   (QuestApprovedEvent    e) => SafeRaise(QuestApproved,   e, nameof(QuestApproved));
        internal void RaiseQuestCancelled  (QuestCancelledEvent   e) => SafeRaise(QuestCancelled,  e, nameof(QuestCancelled));
        internal void RaiseTreasuryChanged (TreasuryChangedEvent  e) => SafeRaise(TreasuryChanged, e, nameof(TreasuryChanged));
        internal void RaiseGuildChanged    (GuildChangedEvent     e) => SafeRaise(GuildChanged,    e, nameof(GuildChanged));
        internal void RaiseReputationChanged(ReputationChangedEvent e) => SafeRaise(ReputationChanged, e, nameof(ReputationChanged));
        internal void RaiseSiteChanged     (SiteChangedEvent      e) => SafeRaise(SiteChanged,     e, nameof(SiteChanged));

        private static void SafeRaise<T>(Action<T> evt, T payload, string name)
        {
            if (evt == null) return;
            foreach (Delegate d in evt.GetInvocationList())
            {
                try { ((Action<T>)d)(payload); }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Extension event subscriber on '{name}' threw: {ex.Message}");
                }
            }
        }
    }
}
