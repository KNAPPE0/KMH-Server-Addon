using System;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    // A network ban is enforced here because this is the first moment the account is known.
    internal static class KmhJoinTicketHandler
    {
        public static void Register() => KmhRouter.RegisterHandler(KmhProtocol.Kind.JoinTicket, OnTicket);

        // A server cannot opt out of a ban on its own behalf: it is simply never sent the list.
        public static bool Enforcing => KmhNetworkBans.Count > 0 || GlobalSetupState.Current_.IsRegistered;

        private static void OnTicket(ServerClient client, KmhEnvelope env)
        {
            string myId = GlobalConfig.Current.RegisteredServerId ?? "";
            JObject raw = env?.Data?["ticket"] as JObject;
            // A declared no-ticket reason is kept for wording, never answered as a failed ticket.
            string declared = env?.GetString("unverified", "") ?? "";
            if (raw == null && declared.Length > 0)
            {
                KmhVerifiedPlayers.NoteUnverified(client, declared);
                return;
            }
            KmhJoinTicket ticket = KmhJoinTicket.Parse(raw);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            TicketResult result = KmhJoinTicketVerifier.Verify(ticket, myId, now);
            if (result != TicketResult.Ok)
            {
                // Unverified is not refused: refusing everyone whenever Global is unreachable is worse.
                ServerLog.Verbose($"Global: join ticket not accepted for '{Name(client)}' - {KmhJoinTicketVerifier.Describe(result)}.");
                KmhVerifiedPlayers.NoteUnverified(client, KmhVerifiedPlayers.TicketRefused);
                Reply(client, false, KmhJoinTicketVerifier.Describe(result));
                return;
            }

            // Learned before the ban check so a banned player who returns without a ticket is still known.
            KmhKnownIdentities.Learn(Name(client), ticket.PlayerGlobalId, now.ToUnixTimeSeconds());

            NetworkBanEntry ban = KmhNetworkBans.Find(ticket.PlayerGlobalId, now.ToUnixTimeSeconds());
            if (ban != null)
            {
                ServerLog.Warn($"Global: refused '{Name(client)}' - banned from official servers"
                             + (string.IsNullOrEmpty(ban.Reason) ? "." : $" ({ban.Reason})."));
                Reply(client, false, string.IsNullOrEmpty(ban.Reason)
                    ? "You are banned from official Colony Nexus servers."
                    : "You are banned from official Colony Nexus servers: " + ban.Reason);
                client.Listener?.MarkForDisconnect();
                return;
            }

            KmhVerifiedPlayers.Record(client, ticket.PlayerGlobalId, ticket.Username);
            ServerLog.Verbose($"Global: '{Name(client)}' verified as {ticket.PlayerGlobalId}.");
            Reply(client, true, "");
            Federation.FederationService.OnVerifiedJoin(Name(client), ticket.PlayerGlobalId);
        }

        private static void Reply(ServerClient client, bool ok, string reason)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.JoinVerified, new { ok, reason });

        private static string Name(ServerClient client)
            => client?.GetData<UserFile>()?.Username ?? "(unknown)";
    }
}
