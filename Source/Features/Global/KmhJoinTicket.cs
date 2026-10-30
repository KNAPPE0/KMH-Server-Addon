using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    // Verified with Global's public key, so a join needs no call to Global and survives an outage.
    internal sealed class KmhJoinTicket
    {
        public string PlayerGlobalId = "", Username = "", ServerId = "", Nonce = "", KeyFingerprint = "", Signature = "";
        public long IssuedAtUnix, ExpiresAtUnix;

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.JoinTicket)
            .Add(PlayerGlobalId)
            .Add(Username)
            .Add(ServerId)
            .Add(IssuedAtUnix)
            .Add(ExpiresAtUnix)
            .Add(Nonce)
            .Build();

        public static KmhJoinTicket Parse(JObject o)
        {
            if (o == null) return null;
            return new KmhJoinTicket
            {
                PlayerGlobalId = (string)o["player_global_id"] ?? "",
                Username       = (string)o["username"] ?? "",
                ServerId       = (string)o["server_id"] ?? "",
                IssuedAtUnix   = (long?)o["issued_at"] ?? 0,
                ExpiresAtUnix  = (long?)o["expires_at"] ?? 0,
                Nonce          = (string)o["nonce"] ?? "",
                KeyFingerprint = (string)o["key_fingerprint"] ?? "",
                Signature      = (string)o["signature"] ?? "",
            };
        }
    }

    internal enum TicketResult { Ok, NoKey, BadSignature, Expired, WrongAudience, Replayed, Malformed }

    // One place decides what "verified" means, so admission and later callers cannot disagree.
    internal static class KmhJoinTicketVerifier
    {
        // A ticket lives two minutes, so this covers that window many times over.
        private const int MaxSeenNonces = 4096;

        private static readonly object _lock = new object();
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> _order = new Queue<string>();

        // Small, because it only has to cover clock skew between Global and this machine, not a retry window.
        private const int SkewSeconds = 60;

        public static string Describe(TicketResult r)
        {
            switch (r)
            {
                case TicketResult.Ok:            return "verified";
                case TicketResult.NoKey:         return "KMH-CON-404 Global's key is not known here yet";
                case TicketResult.BadSignature:  return "KMH-CON-400 signature does not verify";
                case TicketResult.Expired:       return "KMH-CON-401 ticket expired";
                case TicketResult.Replayed:      return "KMH-CON-402 ticket already used";
                case TicketResult.WrongAudience: return "KMH-CON-403 ticket is for another server";
                default:                         return "KMH-CON-400 malformed ticket";
            }
        }

        public static TicketResult Verify(KmhJoinTicket t, string myServerId, DateTimeOffset now)
        {
            if (t == null || t.PlayerGlobalId.Length == 0 || t.Nonce.Length == 0 || t.Signature.Length == 0)
                return TicketResult.Malformed;

            // Audience before signature, so a misconfigured server does not read as an attack.
            if (!string.Equals(t.ServerId, myServerId, StringComparison.Ordinal)) return TicketResult.WrongAudience;

            long unix = now.ToUnixTimeSeconds();
            if (t.ExpiresAtUnix <= unix || t.IssuedAtUnix > unix + SkewSeconds) return TicketResult.Expired;

            string key = KmhDirectoryKey.PublicKey;
            if (string.IsNullOrEmpty(key)) return TicketResult.NoKey;
            if (!string.IsNullOrEmpty(t.KeyFingerprint)
             && !string.Equals(t.KeyFingerprint, KmhDirectoryKey.Fingerprint, StringComparison.Ordinal))
                return TicketResult.BadSignature;

            if (!KmhGlobalSigning.Verify(key, t.Canonical(), t.Signature)) return TicketResult.BadSignature;

            // Last, so a forged ticket cannot burn a nonce a real one would have used.
            lock (_lock)
            {
                if (!_seen.Add(t.Nonce)) return TicketResult.Replayed;
                _order.Enqueue(t.Nonce);
                while (_order.Count > MaxSeenNonces) _seen.Remove(_order.Dequeue());
            }
            return TicketResult.Ok;
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _seen.Clear(); _order.Clear(); }
        }
    }
}
