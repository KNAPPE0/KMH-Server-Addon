using System;
using KMH.Sdk.Server.Apis;

namespace KMH.Sdk.Server
{
    // Extension host API; use SDK wrappers instead of raw RWT types so extensions stay release-safe.
    public interface IKmhServerHost
    {
        // Identity / diagnostics.

        // KMHServerAddon version currently running; useful for extension version guards.
        string KmhVersion { get; }

        // Loaded SDK assembly version; lets extensions fail-fast on incompatible deployments.
        string SdkVersion { get; }

        // KMH's Harmony patch ID; use a different ID for extension Harmony patches.
        string KmhHarmonyId { get; }

        // Stable APIs.

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

        // Creates a sanitized per-extension storage folder under kmh-data/extensions/.
        IExtensionStorage StorageFor(string extensionName);

        // Protocol surface.

        // Registers a namespaced wire-kind handler to avoid KMH/extension collisions.
        void RegisterHandler(string kind, Action<IKmhClient, IKmhEnvelope> handler);

        // Sends a JSON-serialized typed payload to a client; returns false if the client is gone.
        bool Send(IKmhClient client, string kind, object data);

        // Sends a typed payload to a verified online username; returns false if offline.
        bool SendToUsername(string username, string kind, object data);

        // Broadcasts a typed payload to all verified clients; use sparingly for scale.
        void Broadcast(string kind, object data);

        // Discord surface: registers extension commands when the bridge is enabled, rejects reserved kmh-* names, and runs handlers on the Discord gateway thread.
        void RegisterDiscordCommand(string command, Action<IKmhDiscordCommand> handler);
    }
}
