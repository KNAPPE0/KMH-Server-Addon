using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace KMHServerAddon.Features.Discord
{
    // Short-lived, single-use codes for binding an in-game username to a Discord identity
    //
    // Flow:
    //   1. In-game player runs /kmh link in chat. Patch_PM_Chat_LinkCommands
    // calls IssueCodeFor(username); server echoes the code back to the player via PM_Chat.SendConsoleMessage. TTL =
    // 10 minutes
    //   2. Player runs `!kmh-link <code>` in Discord (DM or allowed guild).
    //      DiscordBridge.OnMessage calls TryConsume(code, out username).
    //   3. On consume, LinkedAccountsStore.SetLink is fired and the
    //      snapshot broadcasts to every connected patch-mod client.
    //
    // Single-use: TryConsume removes the entry. Stale codes are reaped lazily on every Issue / Consume call - the
    // pending map stays small, so a background timer would be overkill
    //
    // Issuing a new code for a username already in flight invalidates the previous one. Player gets exactly one
    // outstanding code at a time
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

                // Drop any previous code this user had outstanding - only one in-flight code per player
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

        // Crockford-style alphabet (no I/O/1/0) for copy-paste-friendly codes. 6 chars over a 31-symbol alphabet =
        // ~887M keyspace, plenty for a 10-minute window at any realistic player count
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
