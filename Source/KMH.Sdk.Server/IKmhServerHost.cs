using System;
using KMH.Sdk.Server.Apis;

namespace KMH.Sdk.Server
{
    /// <summary>Extension host API; use SDK wrappers instead of raw RWT types so extensions stay release-safe.</summary>
    public interface IKmhServerHost
    {
        /// <summary>KMHServerAddon version currently running; useful for extension version guards.</summary>
        string KmhVersion { get; }

        /// <summary>Loaded SDK assembly version; lets extensions fail-fast on incompatible deployments.</summary>
        string SdkVersion { get; }

        /// <summary>KMH's Harmony patch ID; use a different ID for extension Harmony patches.</summary>
        string KmhHarmonyId { get; }

        ITreasuryApi        Treasury        { get; }
        IMarketplaceApi     Marketplace     { get; }
        IQuestApi           Quests          { get; }
        IGuildApi           Guilds          { get; }
        ILinkedAccountsApi  LinkedAccounts  { get; }
        IPlayerStatsApi     PlayerStats     { get; }
        IItemLabelsApi      ItemLabels      { get; }
        IReputationApi      Reputation      { get; }
        ISitesApi           Sites           { get; }
        IAuctionApi         Auctions        { get; }
        IWorldApi           World           { get; }
        IServerLog          Log             { get; }
        IKmhEvents          Events          { get; }

        /// <summary>Veto hooks decide whether a pending action is allowed; Events only observe what already happened.</summary>
        Hooks.IKmhHooks     Hooks           { get; }

        /// <summary>Creates a sanitized per-extension storage folder under kmh-data/extensions/.</summary>
        IExtensionStorage StorageFor(string extensionName);

        /// <summary>Registers a namespaced wire-kind handler to avoid KMH/extension collisions.</summary>
        void RegisterHandler(string kind, Action<IKmhClient, IKmhEnvelope> handler);

        /// <summary>Sends a JSON-serialized typed payload to a client; returns false if the client is gone.</summary>
        bool Send(IKmhClient client, string kind, object data);

        /// <summary>Sends a typed payload to a verified online username; returns false if offline.</summary>
        bool SendToUsername(string username, string kind, object data);

        /// <summary>Broadcasts a typed payload to all verified clients; use sparingly for scale.</summary>
        void Broadcast(string kind, object data);

        /// <summary>Discord surface: registers extension commands when the bridge is enabled, rejects reserved kmh-* names, and runs handlers on the Discord gateway thread.</summary>
        void RegisterDiscordCommand(string command, Action<IKmhDiscordCommand> handler);
    }
}
