using System;
using System.Collections.Generic;
using KMHServerAddon.Features.LinkedAccounts.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.LinkedAccounts
{
    // Ids stay server-only and are what authenticates a mutation; the wire snapshot carries display names for UI only.
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

        // One snowflake, one username - two would let dictionary order decide which KMH account acts. A 0 id is a legacy caller.
        public static bool SetLink(string username, string discordName, ulong discordId, out string refusal)
        {
            refusal = "";
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(discordName))
            { refusal = "No username or Discord name."; return false; }

            lock (_lock)
            {
                if (discordId != 0)
                    foreach (KeyValuePair<string, ulong> kv in _ids)
                        if (kv.Value == discordId
                            && !string.Equals(kv.Key, username, StringComparison.OrdinalIgnoreCase))
                        {
                            refusal = $"That Discord account is already linked to '{kv.Key}'. Unlink there first.";
                            return false;
                        }

                _links[username] = discordName;
                if (discordId != 0) _ids[username] = discordId;
                else                _ids.Remove(username);
            }

            // A link nobody wrote down is a link the next restart does not have, so the caller is told.
            if (!SaveToDisk())
            {
                lock (_lock) { _links.Remove(username); _ids.Remove(username); }
                refusal = "The server could not record that link - nothing was changed. Try again shortly.";
                return false;
            }
            Extensibility.KmhEventBus.Instance.RaisePlayerLinked(new KMH.Sdk.Server.Events.PlayerLinkedEvent { Username = username, DiscordDisplay = discordName, DiscordId = discordId });
            return true;
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

        // Legacy links carry no snowflake, and there is no Discord member to reconcile one against.
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

        public static bool IsLinked(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            lock (_lock) { return _links.ContainsKey(username); }
        }

        public static bool TryGetLink(string username, out string display)
        {
            display = null;
            if (string.IsNullOrEmpty(username)) return false;
            lock (_lock) { return _links.TryGetValue(username, out display); }
        }

        // Snowflake only: display names are attacker-settable. A duplicate refuses rather than letting enumeration order pick.
        public static string FindUsernameByDiscordId(ulong discordId)
        {
            if (discordId == 0) return null;
            string found = null;
            lock (_lock)
                foreach (KeyValuePair<string, ulong> kv in _ids)
                {
                    if (kv.Value != discordId) continue;
                    if (found != null)
                    {
                        Diagnostics.ServerLog.Error(
                            $"LinkedAccounts: Discord id {discordId} is linked to more than one username " +
                            $"('{found}' and '{kv.Key}'). Refusing to act as either - unlink one with 'kmh unlink <name>'.");
                        return null;
                    }
                    found = kv.Key;
                }
            return found;
        }

        public static ulong DiscordIdFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            lock (_lock) { return _ids.TryGetValue(username, out ulong id) ? id : 0; }
        }

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
                ReportDuplicateIds();
            }
        }

        // Naming a pre-rule duplicate is the whole job; deleting one at boot would pick a winner without being asked.
        private static void ReportDuplicateIds()
        {
            var byId = new Dictionary<ulong, List<string>>();
            lock (_lock)
                foreach (KeyValuePair<string, ulong> kv in _ids)
                {
                    if (kv.Value == 0) continue;
                    if (!byId.TryGetValue(kv.Value, out List<string> names)) byId[kv.Value] = names = new List<string>();
                    names.Add(kv.Key);
                }

            foreach (KeyValuePair<ulong, List<string>> kv in byId)
            {
                if (kv.Value.Count < 2) continue;
                kv.Value.Sort(StringComparer.OrdinalIgnoreCase);   // reported the same way on every boot
                Diagnostics.ServerLog.Error(
                    $"LinkedAccounts: Discord id {kv.Key} is linked to {kv.Value.Count} usernames " +
                    $"({string.Join(", ", kv.Value)}). That identity is refused until an admin unlinks all but one.");
            }
        }

        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Links      = new Dictionary<string, string>(_links, StringComparer.OrdinalIgnoreCase);
                state.DiscordIds = new Dictionary<string, ulong>(_ids,    StringComparer.OrdinalIgnoreCase);
            }
            return JsonFileStore.Save(KmhDataPaths.LinkedAccountsFile, state);
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _links.Clear(); _ids.Clear(); }
        }

        internal static void SeedDuplicateForTest(string a, string b, ulong id)
        {
            lock (_lock) { _links[a] = "dup"; _links[b] = "dup"; _ids[a] = id; _ids[b] = id; }
        }

        // Pre-snowflake files carry only Links, so DiscordIds can arrive empty and those entries stay name-only.
        private class PersistedState
        {
            public Dictionary<string, string> Links      { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, ulong>  DiscordIds { get; set; } = new Dictionary<string, ulong>();
        }
    }
}
