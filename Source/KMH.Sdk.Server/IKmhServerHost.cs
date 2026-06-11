using System;
using KMH.Sdk.Server.Apis;

namespace KMH.Sdk.Server
{
    /// <summary>
    /// Stable API surface handed to every loaded extension. Everything
    /// an extension is permitted to touch lives on this interface; the
    /// underlying KMHServerAddon internals stay invisible.
    /// </summary>
    /// <remarks>
    /// The SDK never exposes RWT types (Shared.*, TCPNetwork.*,
    /// GameServer.*) directly - that would couple every extension to
    /// the specific RWT release the SDK was compiled against, which
    /// defeats the whole point. Where extensions need to react to
    /// per-client events (joins / leaves / commands) the SDK hands
    /// them an <see cref="IKmhClient"/> wrapper instead of a raw
    /// ServerClient.
    /// </remarks>
    public interface IKmhServerHost
    {
        // -- Identity / diagnostics --

        /// <summary>
        /// Version of the KMHServerAddon assembly currently running.
        /// Useful for "this extension requires KMH >= X.Y" guards.
        /// </summary>
        string KmhVersion { get; }

        /// <summary>
        /// Version of the SDK assembly currently loaded. Extensions
        /// can compare against their compile-time SDK version to
        /// fail-fast on incompatible deployments.
        /// </summary>
        string SdkVersion { get; }

        /// <summary>
        /// Harmony instance id KMH uses for its own patches. If your
        /// extension installs Harmony patches, use a DIFFERENT id (e.g.
        /// <c>"yourname.kmhextension.auctions"</c>) so the Harmony
        /// tracker keeps the two patch sets distinguishable in logs.
        /// </summary>
        string KmhHarmonyId { get; }

        // -- Stable APIs --

        ITreasuryApi        Treasury        { get; }
        IMarketplaceApi     Marketplace     { get; }
        IQuestApi           Quests          { get; }
        IGuildApi           Guilds          { get; }
        ILinkedAccountsApi  LinkedAccounts  { get; }
        IPlayerStatsApi     PlayerStats     { get; }
        IItemLabelsApi      ItemLabels      { get; }
        IReputationApi      Reputation      { get; }
        ISitesApi           Sites           { get; }
        IServerLog          Log             { get; }
        IKmhEvents          Events          { get; }

        /// <summary>
        /// Allocate a per-extension storage scope, rooted at
        /// <c>kmh-data/extensions/&lt;extension-name&gt;/</c>. The
        /// folder is created on first use. Use this for any per-
        /// extension persisted state so backups + uninstalls stay
        /// clean (one folder per extension to copy / delete).
        /// </summary>
        /// <param name="extensionName">
        /// Folder-safe name. The loader sanitises this before use.
        /// </param>
        IExtensionStorage StorageFor(string extensionName);

        // -- Protocol surface --

        /// <summary>
        /// Register a handler for a wire kind. Kinds you own should be
        /// namespaced (e.g. <c>"yourname.auction.bid"</c>) to avoid
        /// collisions with KMH's own <c>kmh.*</c> kinds or with other
        /// extensions.
        /// </summary>
        void RegisterHandler(string kind, Action<IKmhClient, IKmhEnvelope> handler);

        /// <summary>
        /// Send a typed payload to a specific client. Serialises
        /// <paramref name="data"/> via Newtonsoft.Json under the hood.
        /// Returns false if the client has disappeared between the
        /// caller's reference and the send.
        /// </summary>
        bool Send(IKmhClient client, string kind, object data);

        /// <summary>
        /// Send a typed payload to a specific username if they're
        /// connected and verified. Returns false if they're offline.
        /// </summary>
        bool SendToUsername(string username, string kind, object data);

        /// <summary>
        /// Send the same payload to every verified connected client.
        /// Use sparingly - broadcasts on every action don't scale.
        /// </summary>
        void Broadcast(string kind, object data);

        // -- Discord surface --

        /// <summary>
        /// Register a Discord chat command. When a user types
        /// <c>&lt;prefix&gt;&lt;command&gt; ...</c> in a channel the KMH bot
        /// watches AND no core KMH command claims it, your handler fires
        /// with an <see cref="IKmhDiscordCommand"/> context.
        /// </summary>
        /// <param name="command">
        /// Command word without the prefix, e.g. <c>"auction-bid"</c>. The
        /// <c>"kmh-"</c> prefix is reserved for KMH core and will be
        /// refused. Namespace yours so two extensions don't collide.
        /// </param>
        /// <param name="handler">
        /// Invoked with an <see cref="IKmhDiscordCommand"/> context when the
        /// command fires. Runs on the Discord gateway thread - keep it quick.
        /// </param>
        /// <remarks>
        /// No-op (logged) when the Discord bridge isn't configured - your
        /// extension still loads fine on a server running without Discord.
        /// </remarks>
        void RegisterDiscordCommand(string command, Action<IKmhDiscordCommand> handler);
    }
}
