using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Chat
{
    internal static class ChatRosterHandler
    {
        // Capped so a long-lived server does not send its entire account list.
        private const int MaxOffline = 300;

        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatRosterRequest, OnRequest);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            Roster(out List<string> online, out List<string> offline);

            var seen = new List<long>(offline.Count);
            foreach (string u in offline) seen.Add(PlayerStats.PlayerStatsStore.LastSeenTicks(u));
            var active = new List<long>(online.Count);
            foreach (string u in online) active.Add(PlayerStats.PlayerStatsStore.ActiveSecondsOf(u));

            KmhRouter.SendTo(client, KmhProtocol.Kind.ChatRoster, new { online, offline, seen, active });
            ServerLog.Verbose($"Sent chat roster ({online.Count} online, {offline.Count} offline) to {username}.");
        }

        // Online comes from the connection list, never from anything a client claims about itself.
        internal static void Roster(out List<string> online, out List<string> offline)
        {
            var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (!string.IsNullOrEmpty(u)) here.Add(u);
            }

            online = new List<string>(here);
            online.Sort(StringComparer.OrdinalIgnoreCase);

            // Cut by last seen before sorting, since cutting alphabetically would strand everyone past the Ms.
            offline = new List<string>();
            foreach (string u in PlayerStats.PlayerStatsStore.UsernamesByLastSeen())
            {
                if (here.Contains(u)) continue;
                offline.Add(u);
                if (offline.Count >= MaxOffline) break;
            }
            offline.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }
}
