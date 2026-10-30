using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Admin
{
    // Needs proof of console access: a badge is client-drawn and Staff.json is editable by anyone with file access,
    // so only the server console opens a window, and it closes on its own.
    internal static class ConfigEditorGrant
    {
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan MaxWindow     = TimeSpan.FromHours(12);

        private sealed class Grant
        {
            public string Username = "";
            public DateTimeOffset ExpiresAt;
            public string By = "";
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Grant> _grants =
            new Dictionary<string, Grant>(Identity.StaffRegistry.AccountComparer);

        // Deliberately not persisted. A restart is the cheapest revoke there is, and a window that survives one
        // outlives the console session that justified it.
        public static bool Allows(string username, DateTimeOffset now)
        {
            // Exact, like the staff lists: a grant for "Name" is not one for the separate account "Name ".
            if (string.IsNullOrWhiteSpace(username)) return false;
            lock (_lock)
            {
                if (!_grants.TryGetValue(username, out Grant g)) return false;
                if (now >= g.ExpiresAt) { _grants.Remove(username); return false; }
                return true;
            }
        }

        public static TimeSpan Clamp(TimeSpan window)
        {
            if (window <= TimeSpan.Zero) return DefaultWindow;
            return window > MaxWindow ? MaxWindow : window;
        }

        public static DateTimeOffset Open(string username, TimeSpan window, string by, DateTimeOffset now)
        {
            string who = (username ?? "").Trim();
            DateTimeOffset until = now + Clamp(window);
            lock (_lock) _grants[who] = new Grant { Username = who, ExpiresAt = until, By = by ?? "" };
            ServerLog.Warn($"Config editor: {by} opened in-game settings access for '{who}' until {until:u}.");
            return until;
        }

        public static bool Close(string username)
        {
            string who = (username ?? "").Trim();
            lock (_lock)
            {
                if (!_grants.Remove(who)) return false;
            }
            ServerLog.Warn($"Config editor: in-game settings access for '{who}' was closed.");
            return true;
        }

        public static void CloseAll()
        {
            lock (_lock) _grants.Clear();
            ServerLog.Warn("Config editor: all in-game settings access was closed.");
        }

        public static List<string> Describe(DateTimeOffset now)
        {
            var lines = new List<string>();
            lock (_lock)
            {
                var expired = new List<string>();
                foreach (KeyValuePair<string, Grant> kv in _grants)
                {
                    if (now >= kv.Value.ExpiresAt) { expired.Add(kv.Key); continue; }
                    int mins = (int)Math.Ceiling((kv.Value.ExpiresAt - now).TotalMinutes);
                    lines.Add($"{kv.Value.Username} - {mins} minute(s) left (opened by {kv.Value.By})");
                }
                foreach (string e in expired) _grants.Remove(e);
            }
            return lines;
        }

        internal static void ResetForTest() { lock (_lock) _grants.Clear(); }
    }
}
