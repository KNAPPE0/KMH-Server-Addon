using System;
using System.Collections.Generic;
using Discord.WebSocket;
using KMH.Sdk.Server.Apis;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{
    // Dispatched last and refused the core prefix, so an extension can never shadow a built-in command.
    internal static class DiscordExtensionCommands
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Action<IKmhDiscordCommand>> _handlers
            = new Dictionary<string, Action<IKmhDiscordCommand>>(StringComparer.OrdinalIgnoreCase);

        private const string ReservedPrefix = "kmh-";

        public static bool Register(string extensionName, string command, Action<IKmhDiscordCommand> handler)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null) return false;
            string key = command.Trim().ToLowerInvariant();

            if (key.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ServerLog.Warn($"[ext:{extensionName}] refused Discord command '{key}' - the '{ReservedPrefix}' prefix is reserved for KMH core.");
                return false;
            }

            lock (_lock)
            {
                if (_handlers.ContainsKey(key))
                {
                    ServerLog.Warn($"[ext:{extensionName}] refused Discord command '{key}' - already registered by another extension.");
                    return false;
                }
                _handlers[key] = handler;
            }
            ServerLog.Info($"[ext:{extensionName}] registered Discord command '{key}'");
            return true;
        }

        // A throwing handler is caught, or one bad extension would break the gateway message loop.
        public static bool TryDispatch(SocketMessage message, string cmd, string[] parts)
        {
            Action<IKmhDiscordCommand> handler;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(cmd, out handler)) return false;
            }

            try
            {
                handler(new Context(message, cmd, parts));
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Discord extension command '{cmd}' threw", ex);
            }
            return true;
        }

        // Keeps Discord.NET's own types out of the SDK surface extensions bind against.
        private sealed class Context : IKmhDiscordCommand
        {
            private readonly List<string> _args;

            public Context(SocketMessage message, string cmd, string[] parts)
            {
                Command = cmd;

                // From index 1, because parts[0] is the command word itself.
                _args = new List<string>();
                for (int i = 1; i < parts.Length; i++) _args.Add(parts[i]);
                RawArgs = _args.Count > 0 ? string.Join(" ", _args) : "";

                AuthorDisplay  = message?.Author?.Username ?? "";
                AuthorId       = message?.Author?.Id ?? 0;
                ChannelId      = message?.Channel?.Id ?? 0;
                LinkedUsername = AuthorId != 0
                    ? (LinkedAccountsStore.FindUsernameByDiscordId(AuthorId) ?? "")
                    : "";
            }

            public string                Command        { get; }
            public IReadOnlyList<string> Args           => _args;
            public string                RawArgs        { get; }
            public string                AuthorDisplay  { get; }
            public ulong                 AuthorId       { get; }
            public ulong                 ChannelId      { get; }
            public string                LinkedUsername { get; }

            public void Reply(string text)
            {
                if (string.IsNullOrEmpty(text) || ChannelId == 0) return;
                DiscordBridge.PostToChannel(ChannelId, text);
            }
        }
    }
}
