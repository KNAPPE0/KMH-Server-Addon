using System;
using System.Collections.Generic;
using KMHServerAddon.Features.LinkedAccounts.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.LinkedAccounts
{
    // username -> Discord identity. Security: mutating commands authenticate against the stable snowflake Id, not the
    // volatile display name (Ids stay server-only; the wire snapshot ships display names for UI rendering only).
    internal static class LinkedAccountsStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _links
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, ulong> _ids
            = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        public static LinkedAccountsSnapshot BuildSnapshot()
        {
            lock (_lock)
            {
                return new LinkedAccountsSnapshot
                {
                    Links = new Dictionary<string, string>(_links, StringComparer.OrdinalIgnoreCase),
                };
            }
        }

        // Bind an in-game username to a Discord identity. discordId may be 0
        // for legacy callers that haven't been updated to pass it (those
        // links remain display-name-only, so handle-rename will break them until re-linked)
        public static void SetLink(string username, string discordName, ulong discordId)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(discordName)) return;
            lock (_lock)
            {
                _links[username] = discordName;
                if (discordId != 0) _ids[username] = discordId;
                else                _ids.Remove(username);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaisePlayerLinked(new KMH.Sdk.Server.Events.PlayerLinkedEvent { Username = username, DiscordDisplay = discordName, DiscordId = discordId });
        }

        public static void Unlink(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            string previousDisplay = null;
            ulong  previousId      = 0;
            lock (_lock)
            {
                _links.TryGetValue(username, out previousDisplay);
                _ids.TryGetValue(username, out previousId);
                _links.Remove(username);
                _ids.Remove(username);
            }
            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaisePlayerUnlinked(new KMH.Sdk.Server.Events.PlayerUnlinkedEvent { Username = username, PreviousDiscordDisplay = previousDisplay ?? "", DiscordId = previousId });
        }

        // Every current link as (username, discordId) - used by the Discord guild-role sync to reconcile all members.
        public static List<KeyValuePair<string, ulong>> AllLinked()
        {
            lock (_lock)
            {
                List<KeyValuePair<string, ulong>> outList = new List<KeyValuePair<string, ulong>>(_ids.Count);
                foreach (KeyValuePair<string, ulong> kv in _ids)
                    if (kv.Value != 0) outList.Add(kv);
                return outList;
            }
        }

        // Cheap membership check - used by /kmh unlink to differentiate "you have no link" from "we just removed
        // it"
        public static bool IsLinked(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            lock (_lock) { return _links.ContainsKey(username); }
        }

        // Read a single link without rebuilding the whole snapshot. Used by /kmh link status, !kmh-whois, and the
        // announce-channel composer ("X unlinked - was: Y") so we can include the previous display in the message
        // without a snapshot round-trip
        public static bool TryGetLink(string username, out string display)
        {
            display = null;
            if (string.IsNullOrEmpty(username)) return false;
            lock (_lock) { return _links.TryGetValue(username, out display); }
        }

        // Reverse lookup for the Discord side: a Discord user runs !kmh-unlink and we need to find which in-game
        // username they're bound to. Case-insensitive compare - Discord display names aren't case-sensitive in any
        // user-visible way
        public static string FindUsernameByDiscord(string discordName)
        {
            if (string.IsNullOrEmpty(discordName)) return null;
            lock (_lock)
            {
                foreach (KeyValuePair<string, string> kv in _links)
                {
                    if (string.Equals(kv.Value, discordName, StringComparison.OrdinalIgnoreCase))
                        return kv.Key;
                }
            }
            return null;
        }

        // Preferred reverse lookup for the Discord side - works across handle renames, doesn't care about case or
        // discriminator. Returns null when no link has the supplied snowflake id (e.g., a legacy link from before
        // Id storage landed - callers fall back to the display-name lookup)
        public static string FindUsernameByDiscordId(ulong discordId)
        {
            if (discordId == 0) return null;
            lock (_lock)
            {
                foreach (KeyValuePair<string, ulong> kv in _ids)
                {
                    if (kv.Value == discordId) return kv.Key;
                }
            }
            return null;
        }

        public static ulong DiscordIdFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            lock (_lock) { return _ids.TryGetValue(username, out ulong id) ? id : 0; }
        }

        // --- persistence ---

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.LinkedAccountsFile, out PersistedState state) && state?.Links != null)
            {
                lock (_lock)
                {
                    _links = new Dictionary<string, string>(state.Links, StringComparer.OrdinalIgnoreCase);
                    _ids   = state.DiscordIds != null
                        ? new Dictionary<string, ulong>(state.DiscordIds, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
                }
                Diagnostics.ServerLog.Info(
                    $"LinkedAccounts: loaded {state.Links.Count} link(s) from disk " +
                    $"({_ids.Count} with snowflake id)");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Links      = new Dictionary<string, string>(_links, StringComparer.OrdinalIgnoreCase);
                state.DiscordIds = new Dictionary<string, ulong>(_ids,    StringComparer.OrdinalIgnoreCase);
            }
            JsonFileStore.Save(KmhDataPaths.LinkedAccountsFile, state);
        }

        // On-disk schema. Older files (pre-snowflake) have only the Links field - DiscordIds deserializes to null
        // and we treat it as an empty map (those entries stay display-name-only until re-linked)
        private class PersistedState
        {
            public Dictionary<string, string> Links      { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, ulong>  DiscordIds { get; set; } = new Dictionary<string, ulong>();
        }
    }
}
