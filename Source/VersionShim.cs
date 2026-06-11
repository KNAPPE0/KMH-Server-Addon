// Maps RWT's renamed 26.6.9.1 API onto the names the codebase uses, so one source tree builds against both RWT
// generations (csproj RwtFlavor Old/New)
#if RWT_NEW
global using RTNetwork.Components;
global using RTNetwork.Packets;
global using RTShared;
global using RTShared.Misc;
global using RTShared.Commands;
global using UserFile = RTShared.Files.ServerClient.FL_Player;
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

namespace KMHServerAddon
{
    // Wire-level names that differ between generations.
    internal static class RwtCompat
    {
#if RWT_NEW
        public const PacketHeader ChatHeader = PacketHeader.Chat;
#else
        public const PacketHeader ChatHeader = PacketHeader.ChatManager;
#endif
    }
}
