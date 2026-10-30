using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global
{
    internal sealed class KnownIdentity
    {
        public string GlobalId { get; set; } = "";
        public long   SeenUnix { get; set; }
    }

    internal sealed class KnownIdentityFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, KnownIdentity> ByUsername { get; set; }
            = new Dictionary<string, KnownIdentity>(StringComparer.OrdinalIgnoreCase);
    }

    // Only verified tickets write this, so a ban still binds the name when the player later joins ticketless.
    internal static class KmhKnownIdentities
    {
        internal const int MaxEntries = 20000;
        private const long RefreshSeconds = 86400;

        private static readonly object _lock = new object();
        private static KnownIdentityFile _file;

        private static KnownIdentityFile File_
        {
            get
            {
                lock (_lock)
                {
                    if (_file != null) return _file;
                    _file = JsonFileStore.TryLoad(KmhDataPaths.KnownIdentitiesFile, out KnownIdentityFile loaded) && loaded != null
                          ? loaded : new KnownIdentityFile();
                    _file.ByUsername = new Dictionary<string, KnownIdentity>(
                        _file.ByUsername ?? new Dictionary<string, KnownIdentity>(), StringComparer.OrdinalIgnoreCase);
                    return _file;
                }
            }
        }

        // Untrimmed: RWT keeps "Name " apart from "Name", and trimming would let the padded twin rebind the real player.
        public static void Learn(string username, string globalId, long nowUnix)
        {
            username = username ?? "";
            globalId = (globalId ?? "").Trim();
            if (username.Trim().Length == 0 || globalId.Length == 0) return;
            lock (_lock)
            {
                KnownIdentityFile f = File_;
                if (f.ByUsername.TryGetValue(username, out KnownIdentity k) && k.GlobalId == globalId
                    && nowUnix - k.SeenUnix < RefreshSeconds) return;
                f.ByUsername[username] = new KnownIdentity { GlobalId = globalId, SeenUnix = nowUnix };
                if (f.ByUsername.Count > MaxEntries) EvictOldestLocked(f);
                if (!JsonFileStore.Save(KmhDataPaths.KnownIdentitiesFile, f))
                    ServerLog.Warn("Global: could not write the known-identity list; it is held in memory until the next verified join.");
            }
        }

        public static string IdFor(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return "";
            lock (_lock) return File_.ByUsername.TryGetValue(username, out KnownIdentity k) ? k.GlobalId ?? "" : "";
        }

        private static void EvictOldestLocked(KnownIdentityFile f)
        {
            string oldest = null; long at = long.MaxValue;
            foreach (KeyValuePair<string, KnownIdentity> kv in f.ByUsername)
                if (kv.Value == null || kv.Value.SeenUnix < at) { oldest = kv.Key; at = kv.Value?.SeenUnix ?? long.MinValue; }
            if (oldest != null) f.ByUsername.Remove(oldest);
        }

        internal static void ResetForTest() { lock (_lock) _file = new KnownIdentityFile(); }
    }
}
