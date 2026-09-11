// The ONLY place RWT's renames live, so feature code never names a generation itself.
#if RWT_NEW
global using RTNetwork.Components;
global using RTNetwork.Packets;
global using RTShared;
global using RTShared.Misc;
global using RTShared.Commands;
global using TCPNetwork = RTNetwork.Components;
global using Shared = RTShared;
#else
global using TCPNetwork;
global using TCPNetwork.Packets;
global using TCPNetwork.Files.Client;
global using Shared;
global using Shared.Misc;
global using Shared.Commands;
#endif

#if RWT_RT
global using RTServer.PacketManagers;
global using RTServer.Core;
global using RTServer.Misc;
#else
global using GameServer.PacketManager;
global using GameServer.Core;
global using GameServer.Misc;
#endif

#if RWT_RT
global using UserFile = RTShared.Files.Player.FL_Player;
#elif RWT_NEW
global using UserFile = RTShared.Files.ServerClient.FL_Player;
#endif

namespace KMHServerAddon
{
    internal static class RwtCompat
    {
#if RWT_NEW
        private const string       ChatHeaderName     = "Chat";
        private const PacketHeader ChatHeaderFallback = PacketHeader.Chat;
#else
        private const string       ChatHeaderName     = "ChatManager";
        private const PacketHeader ChatHeaderFallback = PacketHeader.ChatManager;
#endif
        private static PacketHeader? _chatHeader;

        // Resolve by name at runtime; RWT renumbers PacketHeader between builds so a baked ordinal mis-tags KMH chat
        public static PacketHeader ChatHeader
        {
            get
            {
                if (_chatHeader.HasValue) return _chatHeader.Value;
                try { _chatHeader = (PacketHeader)System.Enum.Parse(typeof(PacketHeader), ChatHeaderName); }
                catch { _chatHeader = ChatHeaderFallback; }
                return _chatHeader.Value;
            }
        }
    }
}
