using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Global
{
    // Keyed on the exact ServerClient, never the username: the player may already be back on a new session.
    internal static class KmhVerifiedPlayers
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<ServerClient, string> _byClient = new Dictionary<ServerClient, string>();
        private static readonly Dictionary<ServerClient, string> _names = new Dictionary<ServerClient, string>();

        // Empty when this session has not proven anything, which is never the same as "not banned".
        public static string IdFor(ServerClient client)
        {
            if (client == null) return "";
            lock (_lock) return _byClient.TryGetValue(client, out string id) ? id : "";
        }

        public static string NameFor(ServerClient client)
        {
            if (client == null) return "";
            lock (_lock) return _names.TryGetValue(client, out string n) ? n : "";
        }

        public static void Record(ServerClient client, string playerGlobalId, string username)
        {
            if (client == null || string.IsNullOrEmpty(playerGlobalId)) return;
            lock (_lock)
            {
                _byClient[client] = playerGlobalId;
                _names[client] = username ?? "";
            }
        }

        public static void Forget(ServerClient client)
        {
            if (client == null) return;
            lock (_lock) { _byClient.Remove(client); _names.Remove(client); _unverified.Remove(client); }
        }

        public static int LiveCount { get { lock (_lock) return _byClient.Count; } }

        // Wording only: grants and refuses nothing, so a lying client changes only the sentence it is shown.
        public const string SignedOut = "signed_out", LocalAddress = "local_address", OtherAddress = "other_address",
                            Unconfirmed = "unconfirmed", TicketRefused = "ticket_refused";
        private static readonly Dictionary<ServerClient, string> _unverified = new Dictionary<ServerClient, string>();

        public static void NoteUnverified(ServerClient client, string why)
        {
            if (client == null) return;
            switch (why)
            {
                case SignedOut: case LocalAddress: case OtherAddress: case Unconfirmed: case TicketRefused: break;
                default: why = Unconfirmed; break;
            }
            lock (_lock) _unverified[client] = why;
        }

        public static string UnverifiedWhy(ServerClient client)
        {
            if (client == null) return "";
            lock (_lock) return _unverified.TryGetValue(client, out string why) ? why : "";
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _byClient.Clear(); _names.Clear(); _unverified.Clear(); }
        }
    }
}
