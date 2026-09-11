using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Comms
{
    // Split from CommsStartup so the apply path never touches RWT's client list and can be tested without a server.
    internal static class CommsPush
    {
        // Without this wiring a reload changes the server's mind and tells nobody.
        public static void Wire()
        {
            CommsStartup.HelloPush = Hello;
            CommsStartup.StaffRolesPush = StaffRoles;
        }

        // Presentation rides the hello, so without a re-send it only reaches a client on connect.
        private static void Hello()
        {
            int sent = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                SubProtocol.KmhHandshakeHandler.SendHelloTo(c);
                sent++;
            }
            ServerLog.Verbose($"Comms: pushed presentation to {sent} connected client(s).");
        }

        // WHO holds a staff role rides the player-stats snapshot rather than the hello.
        private static void StaffRoles() => PlayerStats.PlayerStatsHandler.BroadcastSnapshot();
    }
}
