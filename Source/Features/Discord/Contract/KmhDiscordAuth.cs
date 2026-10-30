using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Kmh.Discord.Wire
{
    // Reuses the ES256 identity a server already holds for Global, so the fleet needs no shared password.
    public static class KmhDiscordAuth
    {
        public const int Version = 1;
        private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

        // Fixed-width R||S: a DER signature verifies as "invalid" with nothing in the log to explain why.
        private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

        // How far apart the two clocks may be before a registration is refused as a possible replay.
        public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(2);

        // "<utf8 byte count>:<value>\n" per field, so a value containing ':' cannot shift a field boundary.
        public static byte[] Canonical(string serverId, long generation, string instanceId, string nonce, long issuedAt)
        {
            MemoryStream buf = new MemoryStream();
            Add(buf, KmhDiscordProtocol.SignatureDomain);
            Add(buf, Version.ToString(CultureInfo.InvariantCulture));
            Add(buf, serverId);
            Add(buf, generation.ToString(CultureInfo.InvariantCulture));
            Add(buf, instanceId);
            Add(buf, nonce);
            Add(buf, issuedAt.ToString(CultureInfo.InvariantCulture));
            return buf.ToArray();
        }

        private static void Add(Stream buf, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
            byte[] head = Encoding.UTF8.GetBytes(utf8.Length.ToString(CultureInfo.InvariantCulture) + ":");
            buf.Write(head, 0, head.Length);
            buf.Write(utf8, 0, utf8.Length);
            buf.WriteByte((byte)'\n');
        }

        public static string Sign(ECDsa key, byte[] canonical)
            => key == null ? "" : Base64Url(key.SignData(canonical, Hash, Format));

        public static bool Verify(string publicKeyBase64Url, byte[] canonical, string signature)
        {
            byte[] spki = FromBase64Url(publicKeyBase64Url);
            byte[] sig  = FromBase64Url(signature);
            if (spki == null || sig == null || canonical == null) return false;
            try
            {
                using ECDsa key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);
                return key.VerifyData(canonical, sig, Hash, Format);
            }
            catch (Exception) { return false; }
        }

        public static string NewNonce()
        {
            byte[] b = new byte[16];
            RandomNumberGenerator.Fill(b);
            return Base64Url(b);
        }

        public static string NewSessionToken()
        {
            byte[] b = new byte[32];
            RandomNumberGenerator.Fill(b);
            return Base64Url(b);
        }

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
    }
}
