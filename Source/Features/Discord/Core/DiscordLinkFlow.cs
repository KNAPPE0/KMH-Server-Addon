using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordLinkFlow
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Pending> _byCode
            = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

        public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

        public static string IssueCodeFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock)
            {
                ReapStaleLocked();

                // Only one code may be outstanding per player, or an older one stays redeemable.
                List<string> remove = new List<string>();
                foreach (KeyValuePair<string, Pending> kv in _byCode)
                {
                    if (string.Equals(kv.Value.Username, username, StringComparison.OrdinalIgnoreCase))
                        remove.Add(kv.Key);
                }
                foreach (string k in remove) _byCode.Remove(k);

                string code = GenerateCode();
                _byCode[code] = new Pending
                {
                    Username  = username,
                    IssuedUtc = DateTime.UtcNow,
                };
                return code;
            }
        }

        public static bool TryConsume(string code, out string username)
        {
            username = null;
            if (string.IsNullOrWhiteSpace(code)) return false;
            lock (_lock)
            {
                ReapStaleLocked();
                if (!_byCode.TryGetValue(code, out Pending p)) return false;
                _byCode.Remove(code);
                username = p.Username;
                return true;
            }
        }

        // I, O, 1 and 0 are left out so a player reading a code aloud cannot produce a different one.
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private static string GenerateCode()
        {
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                byte[] buf  = new byte[6];
                rng.GetBytes(buf);
                char[] code = new char[6];
                for (int i = 0; i < 6; i++)
                {
                    code[i] = Alphabet[buf[i] % Alphabet.Length];
                }
                return new string(code);
            }
        }

        private static void ReapStaleLocked()
        {
            DateTime    cutoff = DateTime.UtcNow - TimeToLive;
            List<string> dead  = null;
            foreach (KeyValuePair<string, Pending> kv in _byCode)
            {
                if (kv.Value.IssuedUtc < cutoff)
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null)
            {
                foreach (string k in dead) _byCode.Remove(k);
            }
        }

        private class Pending
        {
            public string   Username;
            public DateTime IssuedUtc;
        }
    }
}
