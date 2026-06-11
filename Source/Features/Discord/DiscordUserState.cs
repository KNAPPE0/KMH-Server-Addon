using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Discord
{
    // Per-user Discord-side decorations: showcase post coordinates, tagline, last-refresh timestamp. Keyed by
    // in-game username - the LinkedAccountsStore stays the canonical user identity map, this store just hangs
    // Discord-specific state off each known username
    //
    // Future Discord features (WTB board state, opt-out flags, custom emoji prefs) add their own fields here
    // without needing a new store
    //
    // Persisted to KMH-Data/Discord/UserState.json so a server restart doesn't lose the message ids that drive
    // edit-in-place
    internal static class DiscordUserState
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, UserState> _users
            = new Dictionary<string, UserState>(StringComparer.OrdinalIgnoreCase);

        public class UserState
        {
            public ulong  ShowcaseChannelId        { get; set; } = 0;
            public ulong  ShowcaseMessageId        { get; set; } = 0;
            public string ShowcaseTagline          { get; set; } = "";
            public long   ShowcaseLastUpdatedTicks { get; set; } = 0;

            // WTB board - parallel shape to showcase: a live message that edits in place + a list of "I'm looking
            // for X" entries
            public ulong  WtbChannelId             { get; set; } = 0;
            public ulong  WtbMessageId             { get; set; } = 0;
            public string WtbTagline               { get; set; } = "";
            public long   WtbLastUpdatedTicks      { get; set; } = 0;
            public List<WtbEntry> WtbEntries       { get; set; } = new List<WtbEntry>();
        }

        public class WtbEntry
        {
            public string ItemDefName        { get; set; } = "";
            public int    MaxQty             { get; set; } = 0;
            public int    MaxUnitPriceSilver { get; set; } = 0;
            public long   AddedUtcTicks      { get; set; } = 0;
        }

        // -- showcase accessors --

        public static void GetShowcase(string username,
                                       out ulong channelId,
                                       out ulong messageId,
                                       out string tagline)
        {
            channelId = 0;
            messageId = 0;
            tagline   = "";
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (_users.TryGetValue(username, out UserState s))
                {
                    channelId = s.ShowcaseChannelId;
                    messageId = s.ShowcaseMessageId;
                    tagline   = s.ShowcaseTagline ?? "";
                }
            }
        }

        public static void SetShowcase(string username, ulong channelId, ulong messageId, string tagline)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                UserState s = GetOrCreateLocked(username);
                s.ShowcaseChannelId        = channelId;
                s.ShowcaseMessageId        = messageId;
                s.ShowcaseTagline          = tagline ?? "";
                s.ShowcaseLastUpdatedTicks = DateTime.UtcNow.Ticks;
            }
            SaveToDisk();
        }

        public static void SetTagline(string username, string tagline)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                UserState s = GetOrCreateLocked(username);
                s.ShowcaseTagline = tagline ?? "";
            }
            SaveToDisk();
        }

        public static void ClearShowcase(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (_users.TryGetValue(username, out UserState s))
                {
                    s.ShowcaseChannelId        = 0;
                    s.ShowcaseMessageId        = 0;
                    s.ShowcaseLastUpdatedTicks = 0;
                    // Keep tagline - player may want it for next showcase.
                }
            }
            SaveToDisk();
        }

        // -- WTB accessors --

        public static void GetWtb(string username,
                                  out ulong channelId,
                                  out ulong messageId,
                                  out string tagline,
                                  out List<WtbEntry> entries)
        {
            channelId = 0;
            messageId = 0;
            tagline   = "";
            entries   = new List<WtbEntry>();
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (_users.TryGetValue(username, out UserState s))
                {
                    channelId = s.WtbChannelId;
                    messageId = s.WtbMessageId;
                    tagline   = s.WtbTagline ?? "";
                    if (s.WtbEntries != null) entries = new List<WtbEntry>(s.WtbEntries);
                }
            }
        }

        public static void SetWtbRef(string username, ulong channelId, ulong messageId)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                UserState s = GetOrCreateLocked(username);
                s.WtbChannelId        = channelId;
                s.WtbMessageId        = messageId;
                s.WtbLastUpdatedTicks = DateTime.UtcNow.Ticks;
            }
            SaveToDisk();
        }

        public static void SetWtbTagline(string username, string tagline)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                GetOrCreateLocked(username).WtbTagline = tagline ?? "";
            }
            SaveToDisk();
        }

        // Add or update (idempotent - re-adding the same defName updates qty + price in place, doesn't create a
        // duplicate row)
        public static bool AddOrUpdateWtb(string username, string defName, int maxQty, int maxUnitPrice, int maxEntries)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(defName)) return false;
            if (maxQty <= 0 || maxUnitPrice <= 0) return false;
            lock (_lock)
            {
                UserState s = GetOrCreateLocked(username);
                if (s.WtbEntries == null) s.WtbEntries = new List<WtbEntry>();

                WtbEntry existing = null;
                foreach (WtbEntry e in s.WtbEntries)
                {
                    if (e != null && string.Equals(e.ItemDefName, defName, StringComparison.OrdinalIgnoreCase))
                    { existing = e; break; }
                }
                if (existing != null)
                {
                    existing.MaxQty             = maxQty;
                    existing.MaxUnitPriceSilver = maxUnitPrice;
                    existing.AddedUtcTicks      = DateTime.UtcNow.Ticks;
                }
                else
                {
                    if (s.WtbEntries.Count >= maxEntries) return false; // caller reports cap
                    s.WtbEntries.Add(new WtbEntry
                    {
                        ItemDefName        = defName,
                        MaxQty             = maxQty,
                        MaxUnitPriceSilver = maxUnitPrice,
                        AddedUtcTicks      = DateTime.UtcNow.Ticks,
                    });
                }
            }
            SaveToDisk();
            return true;
        }

        public static int RemoveWtb(string username, string defName)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(defName)) return 0;
            int removed = 0;
            lock (_lock)
            {
                if (_users.TryGetValue(username, out UserState s) && s.WtbEntries != null)
                {
                    removed = s.WtbEntries.RemoveAll(e =>
                        e != null && string.Equals(e.ItemDefName, defName, StringComparison.OrdinalIgnoreCase));
                }
            }
            if (removed > 0) SaveToDisk();
            return removed;
        }

        public static int ClearWtb(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            int count = 0;
            lock (_lock)
            {
                if (_users.TryGetValue(username, out UserState s) && s.WtbEntries != null)
                {
                    count = s.WtbEntries.Count;
                    s.WtbEntries.Clear();
                }
            }
            if (count > 0) SaveToDisk();
            return count;
        }

        public static void ClearWtbBoard(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            lock (_lock)
            {
                if (_users.TryGetValue(username, out UserState s))
                {
                    s.WtbChannelId        = 0;
                    s.WtbMessageId        = 0;
                    s.WtbLastUpdatedTicks = 0;
                }
            }
            SaveToDisk();
        }

        // Enumerate all users with an active showcase (channel + message both non-zero). Used by future sweep
        // features to refresh stale posts
        public static List<string> ListUsersWithShowcase()
        {
            List<string> result = new List<string>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, UserState> kv in _users)
                {
                    if (kv.Value.ShowcaseChannelId != 0 && kv.Value.ShowcaseMessageId != 0)
                        result.Add(kv.Key);
                }
            }
            return result;
        }

        private static UserState GetOrCreateLocked(string username)
        {
            if (!_users.TryGetValue(username, out UserState s))
            {
                s = new UserState();
                _users[username] = s;
            }
            return s;
        }

        // -- persistence --

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.DiscordUserStateFile, out PersistedState state) && state?.Users != null)
            {
                lock (_lock)
                {
                    _users = new Dictionary<string, UserState>(state.Users, StringComparer.OrdinalIgnoreCase);
                }
                ServerLog.Info($"DiscordUserState: loaded state for {state.Users.Count} user(s)");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Users = new Dictionary<string, UserState>(_users, StringComparer.OrdinalIgnoreCase);
            }
            JsonFileStore.Save(KmhDataPaths.DiscordUserStateFile, state);
        }

        private class PersistedState
        {
            public Dictionary<string, UserState> Users { get; set; } = new Dictionary<string, UserState>();
        }
    }
}
