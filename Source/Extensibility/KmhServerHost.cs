using System;
using KMH.Sdk.Server;
using KMH.Sdk.Server.Apis;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Extensibility
{
    // One instance per extension rather than a singleton, so the storage scope and log prefix stay extension-specific.
    internal sealed class KmhServerHost : IKmhServerHost
    {
        private readonly string _extensionName;

        public KmhServerHost(string extensionName)
        {
            _extensionName  = extensionName;
            Log             = new ServerLogAdapter(extensionName);
            Treasury        = new TreasuryApiImpl();
            Marketplace     = new MarketplaceApiImpl();
            Quests          = new QuestApiImpl();
            Guilds          = new GuildApiImpl();
            LinkedAccounts  = new LinkedAccountsApiImpl();
            PlayerStats     = new PlayerStatsApiImpl();
            ItemLabels      = new ItemLabelsApiImpl();
            Reputation      = new ReputationApiImpl();
            Sites           = new SitesApiImpl();
            Auctions        = new AuctionApiImpl();
            World           = new WorldApiImpl();
            Events          = KmhEventBus.Instance;
            Hooks           = KmhHooks.Instance;
        }

        public string KmhVersion  => typeof(Main_).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        public string SdkVersion  => typeof(IKmhServerHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        public string KmhHarmonyId => Constants.HarmonyId;

        public ITreasuryApi       Treasury        { get; }
        public IMarketplaceApi    Marketplace     { get; }
        public IQuestApi          Quests          { get; }
        public IGuildApi          Guilds          { get; }
        public ILinkedAccountsApi LinkedAccounts  { get; }
        public IPlayerStatsApi    PlayerStats     { get; }
        public IItemLabelsApi     ItemLabels      { get; }
        public IReputationApi     Reputation      { get; }
        public ISitesApi          Sites           { get; }
        public IAuctionApi        Auctions        { get; }
        public IWorldApi          World           { get; }
        public IServerLog         Log             { get; }
        public IKmhEvents         Events          { get; }
        public KMH.Sdk.Server.Hooks.IKmhHooks Hooks { get; }

        public IExtensionStorage StorageFor(string extensionName)
            => new ExtensionStorageImpl(string.IsNullOrEmpty(extensionName) ? _extensionName : extensionName);

        // Core handlers all live under this prefix, so an extension claiming one could hijack treasury or market traffic.
        private const string ReservedKindPrefix = "kmh.";

        public void RegisterHandler(string kind, Action<IKmhClient, IKmhEnvelope> handler)
        {
            if (string.IsNullOrEmpty(kind) || handler == null) return;

            if (kind.StartsWith(ReservedKindPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ServerLog.Warn(
                    $"[ext:{_extensionName}] refused to register reserved kind '{kind}'. " +
                    $"The '{ReservedKindPrefix}' namespace belongs to KMH core - namespace your own " +
                    $"kinds under your extension name (e.g. '{SafePrefix()}.{kind}').");
                return;
            }

            // First writer wins, and the collision is logged so neither extension silently shadows the other.
            if (KmhRouter.IsRegistered(kind))
            {
                ServerLog.Warn(
                    $"[ext:{_extensionName}] refused to register kind '{kind}' - already claimed by " +
                    $"KMH or another extension. Pick a kind unique to your extension.");
                return;
            }

            KmhRouter.RegisterHandler(kind, (rwtClient, rwtEnvelope) =>
            {
                try
                {
                    handler(new KmhClientAdapter(rwtClient), new KmhEnvelopeAdapter(rwtEnvelope));
                }
                catch (Exception ex)
                {
                    ServerLog.Error($"[ext:{_extensionName}] handler for '{kind}' threw", ex);
                }
            });
        }

        private string SafePrefix()
        {
            string s = (_extensionName ?? "ext").ToLowerInvariant();
            char[] buf = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
                buf[i] = char.IsLetterOrDigit(s[i]) ? s[i] : '_';
            return new string(buf);
        }

        public bool Send(IKmhClient client, string kind, object data)
        {
            if (!(client is KmhClientAdapter adapter) || adapter.Underlying == null) return false;
            return KmhRouter.SendTo(adapter.Underlying, kind, data);
        }

        public bool SendToUsername(string username, string kind, object data)
            => KmhRouter.SendToUsername(username, kind, data);

        public void RegisterDiscordCommand(string command, Action<IKmhDiscordCommand> handler)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null) return;
            // Said out loud, or an extension author has no way to tell an inert command from a broken one.
            if (!Features.Discord.DiscordBridge.IsEnabled)
                ServerLog.Info($"[ext:{_extensionName}] Discord command '{command}' registered, but the Discord bridge is not configured on this server - it will be inert until Discord is enabled.");
            Features.Discord.DiscordExtensionCommands.Register(_extensionName, command, handler);
        }

        public void Broadcast(string kind, object data)
        {
            // Sent per client rather than through KmhRouter, whose broadcasts are scoped per feature and per viewer.
            foreach (TCPNetwork.ServerClient c in TCPNetwork.Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                try { KmhRouter.SendTo(c, kind, data); }
                catch (Exception ex)
                {
                    ServerLog.Verbose($"[ext:{_extensionName}] broadcast send failed: {ex.Message}");
                }
            }
        }

        private sealed class ServerLogAdapter : IServerLog
        {
            private readonly string _prefix;
            public ServerLogAdapter(string extensionName) => _prefix = $"[ext:{extensionName}]";
            public void Info(string m)                                => ServerLog.Info   ($"{_prefix} {m}");
            public void Verbose(string m)                             => ServerLog.Verbose($"{_prefix} {m}");
            public void Warn(string m)                                => ServerLog.Warn   ($"{_prefix} {m}");
            public void Error(string m)                               => ServerLog.Error  ($"{_prefix} {m}");
            public void Error(string m, Exception ex)                 => ServerLog.Error  ($"{_prefix} {m}", ex);
        }

        internal sealed class KmhClientAdapter : IKmhClient
        {
            internal readonly TCPNetwork.ServerClient Underlying;
            public KmhClientAdapter(TCPNetwork.ServerClient c)
            {
                Underlying = c;
                Username   = c?.GetData<UserFile>()?.Username ?? "";
                Ip         = c?.IP ?? "";
                IsVerified = c?.IsVerified ?? false;
            }
            public string Username   { get; }
            public string Ip         { get; }
            public bool   IsVerified { get; }
        }

        internal sealed class KmhEnvelopeAdapter : IKmhEnvelope
        {
            private readonly KmhEnvelope _env;
            public KmhEnvelopeAdapter(KmhEnvelope env) => _env = env;
            public string Kind                                       => _env?.Kind ?? "";
            public int    Version                                    => _env?.Version ?? 0;
            public int    GetInt(string key, int defaultValue = 0)   => _env?.GetInt   (key, defaultValue) ?? defaultValue;
            public string GetString(string key, string defaultValue = null) => _env?.GetString(key, defaultValue) ?? defaultValue;
            public bool   GetBool(string key, bool defaultValue = false)    => _env?.GetBool  (key, defaultValue) ?? defaultValue;
            public T      DataAs<T>() where T : class                => _env?.DataAs<T>();
        }
    }
}
