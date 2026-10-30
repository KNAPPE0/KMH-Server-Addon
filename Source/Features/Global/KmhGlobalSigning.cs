using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KMHServerAddon.Features.Global
{
    // The server half of the pinned signing representation; contract 73 compares it against Global's.
    internal static class KmhGlobalSigning
    {
        public const string Algorithm = "ES256";
        public const int    Version   = 1;
        public const int    SignatureBytes = 64;

        private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

        // Fixed-width R||S. A DER signature verifies as "invalid" with nothing in the log to explain why.
        private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

        public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public static string ExportPublicKey(ECDsa key) => Base64Url(key.ExportSubjectPublicKeyInfo());

        public static string Sign(ECDsa key, byte[] canonical)
            => Base64Url(key.SignData(canonical, Hash, Format));

        // Verifies join tickets with the same pinned hash and P1363 format as Sign, so the halves cannot drift apart.
        public static bool Verify(string publicKey, byte[] canonical, string signature)
        {
            if (canonical == null) return false;
            byte[] sig = FromBase64Url(signature);
            if (sig == null || sig.Length != SignatureBytes) return false;

            byte[] spki = FromBase64Url(publicKey);
            if (spki == null) return false;

            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(spki, out int read);
                // Trailing bytes after a valid key would let two encodings share one fingerprint.
                if (read != spki.Length || key.KeySize != 256) return false;
                return key.VerifyData(canonical, sig, Hash, Format);
            }
            catch (CryptographicException) { return false; }
            catch (ArgumentException) { return false; }
            finally { key.Dispose(); }
        }

        public static string Fingerprint(string publicKey)
        {
            byte[] spki = FromBase64Url(publicKey);
            return spki == null ? "" : ToHex(SHA256.HashData(spki));
        }

        public static string ShortFingerprint(string fingerprint)
            => string.IsNullOrEmpty(fingerprint) || fingerprint.Length < 16
                ? fingerprint
                : $"{fingerprint.Substring(0, 4)}-{fingerprint.Substring(4, 4)}-{fingerprint.Substring(8, 4)}-{fingerprint.Substring(12, 4)}";

        public static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }
            try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    // Built field by field as "<utf8 count>:<value>\n", so a value containing ':' cannot shift a field boundary.
    internal sealed class KmhCanonicalPayload
    {
        private readonly MemoryStream _buffer = new MemoryStream();

        private KmhCanonicalPayload(string domain)
        {
            Add(domain);
            Add(KmhGlobalSigning.Version);
        }

        public static KmhCanonicalPayload For(string domain) => new KmhCanonicalPayload(domain);

        public KmhCanonicalPayload Add(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
            byte[] head = Encoding.UTF8.GetBytes(utf8.Length.ToString(CultureInfo.InvariantCulture) + ":");
            _buffer.Write(head, 0, head.Length);
            _buffer.Write(utf8, 0, utf8.Length);
            _buffer.WriteByte((byte)'\n');
            return this;
        }

        // Invariant culture: a server on a German locale must not sign "1.234" where Global verifies "1234".
        public KmhCanonicalPayload Add(long value) => Add(value.ToString(CultureInfo.InvariantCulture));

        // Count first, then each item in order, so two different lists can never build the same bytes.
        public KmhCanonicalPayload AddList(IList<string> values)
        {
            int n = values == null ? 0 : values.Count;
            Add(n);
            for (int i = 0; i < n; i++) Add(values[i]);
            return this;
        }

        // Must match Global's ModProfileRequest bytes; length prefixes mean no separator can be forged from a mod name.
        public KmhCanonicalPayload AddEntries(IList<Features.Mods.ModEntry> entries)
        {
            int n = entries == null ? 0 : entries.Count;
            Add(n);
            for (int i = 0; i < n; i++)
            {
                Features.Mods.ModEntry e = entries[i];
                Add(e?.Name ?? ""); Add(e?.Role ?? ""); Add(e?.Kind ?? "");
                Add(e?.Id ?? ""); Add(e?.PackageId ?? ""); Add(e?.Author ?? "");
            }
            return this;
        }

        // Must match Global's AddContributions exactly.
        public KmhCanonicalPayload AddContributions(IList<FederationContributionEntry> entries)
        {
            int n = entries == null ? 0 : entries.Count;
            Add(n);
            for (int i = 0; i < n; i++)
            {
                FederationContributionEntry e = entries[i];
                Add(e?.ReportId ?? ""); Add(e?.Category ?? ""); Add(e?.Amount ?? 0);
                Add(e?.Player ?? ""); Add(e?.OccurredAtUnix ?? 0);
            }
            return this;
        }

        // Must match Global's AddBoards exactly.
        public KmhCanonicalPayload AddBoards(IList<LeaderboardPublishBoard> boards)
        {
            int n = boards == null ? 0 : boards.Count;
            Add(n);
            for (int i = 0; i < n; i++)
            {
                LeaderboardPublishBoard b = boards[i];
                Add(b?.Key ?? ""); Add(b?.Title ?? ""); Add(b?.Group ?? ""); Add(b?.Unit ?? "");
                Add(b?.Scope ?? ""); Add(b?.Source ?? ""); Add(b?.Description ?? ""); Add(b?.Total ?? 0);
                int rows = b?.Rows?.Count ?? 0;
                Add(rows);
                for (int k = 0; k < rows; k++)
                {
                    LeaderboardPublishRow r = b.Rows[k];
                    Add(r?.Rank ?? 0); Add(r?.Name ?? ""); Add(r?.Guild ?? ""); Add(r?.Colony ?? "");
                    Add(r?.Value ?? 0); Add(r?.Display ?? ""); Add(r?.Online == true ? 1 : 0);
                }
            }
            return this;
        }

        public byte[] Build() => _buffer.ToArray();

        public static string HashSet(IEnumerable<string> values)
        {
            List<string> items = new List<string>(values ?? Array.Empty<string>());
            items.Sort(StringComparer.Ordinal);
            KmhCanonicalPayload p = For("kmh.set.v1");
            p.Add(items.Count);
            foreach (string s in items) p.Add(s);

            byte[] digest = SHA256.HashData(p.Build());
            StringBuilder sb = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    internal static class KmhSigningDomains
    {
        public const string RegistrationChallenge = "kmh.global.registration.challenge.v1";
        public const string Heartbeat             = "kmh.global.heartbeat.v1";
        public const string KeyRotation           = "kmh.global.key-rotation.v1";
        public const string Metadata              = "kmh.global.metadata.v1";
        // Own domain and endpoint, so deployed servers' metadata bytes never change shape.
        public const string ListingDetails        = "kmh.global.listing-details.v1";
        // Bumped, not edited, on field-list changes: an old signature over new bytes would let a proxy drop a list.
        public const string ModProfile            = "kmh.global.mod-profile.v3";
        public const string Leaderboards          = "kmh.global.leaderboards.v1";
        // Three domains: a deposit signature must never verify as a withdrawal, nor an ack as either.
        public const string TreasuryDeposit       = "kmh.global.treasury-deposit.v1";
        public const string TreasuryReserve       = "kmh.global.treasury-reserve.v1";
        public const string TreasuryAck           = "kmh.global.treasury-ack.v1";

        // Apart from personal domains: a guild deposit never verifies as a player's, nor a link proof as a value move.
        public const string GuildLink    = "kmh.global.guild-link.v1";
        public const string GuildReprove = "kmh.global.guild-reprove.v1";
        // Own domain, or a status request would be byte-identical to a re-proof and usable as one.
        public const string GuildStatus  = "kmh.global.guild-status.v1";
        public const string GuildDeposit = "kmh.global.guild-deposit.v1";
        public const string GuildReserve = "kmh.global.guild-reserve.v1";
        public const string GuildAck     = "kmh.global.guild-ack.v1";
        public const string GuildRevoke  = "kmh.global.guild-revoke.v1";
        public const string GuildInvite       = "kmh.global.guild-invite.v1";
        public const string GuildInviteRevoke = "kmh.global.guild-invite-revoke.v1";

        // Own domain, so an owner registration can never be replayed as an owner-less one.
        public const string BackendRegistration = "kmh.global.backend-registration.v1";
        // Never valid as a registration or a heartbeat.
        public const string ServerRebind        = "kmh.global.server-rebind.v1";

        // One domain per action: a read must never verify as a change, and no change as another.
        public const string FederationCreate       = "kmh.global.federation-create.v1";
        public const string FederationJoin         = "kmh.global.federation-join.v1";
        public const string FederationView         = "kmh.global.federation-view.v1";
        public const string FederationInviteRevoke = "kmh.global.federation-invite-revoke.v1";
        public const string FederationLeave        = "kmh.global.federation-leave.v1";
        public const string FederationRemove       = "kmh.global.federation-remove.v1";
        public const string FederationTransfer     = "kmh.global.federation-transfer.v1";
        public const string FederationUpdate       = "kmh.global.federation-update.v1";
        public const string FederationDisband      = "kmh.global.federation-disband.v1";
        public const string FederationContribute   = "kmh.global.federation-contribute.v1";

        public const string ChatRelay             = "kmh.global.chat-relay.v1";
        public const string ChatRelayRead         = "kmh.global.chat-relay-read.v1";
        public const string Diagnostics           = "kmh.global.diagnostics.v1";
        public const string OperatorConsole       = "kmh.global.operator-console.v1";
        // Never valid as a diagnostics answer.
        public const string Report                = "kmh.global.report.v1";
        // Signed by Global, verified here - the only domain that travels in that direction.
        public const string JoinTicket            = "kmh.global.join-ticket.v1";
    }
}
