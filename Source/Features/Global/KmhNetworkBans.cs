using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    internal sealed class NetworkBanEntry
    {
        public string UserId { get; set; } = "";
        // Kept as Global sends it; a game server never learns a player's Steam id, so nothing matches on it.
        public long   SteamId64 { get; set; }
        public string Reason { get; set; } = "";
        // 0 is permanent. Compared against the clock at admission, never at write time.
        public long   UntilUnix { get; set; }
    }

    internal sealed class NetworkBanFile
    {
        public int SchemaVersion { get; set; } = 1;
        public string UpdatedUtc { get; set; } = "";
        public List<NetworkBanEntry> Bans { get; set; } = new List<NetworkBanEntry>();
    }

    // On disk so a restart, or a Global outage, does not quietly re-admit everybody.
    internal static class KmhNetworkBans
    {
        private static readonly object _lock = new object();
        private static NetworkBanFile _file;

        private static NetworkBanFile File_
        {
            get
            {
                lock (_lock)
                {
                    if (_file != null) return _file;
                    _file = JsonFileStore.TryLoad(KmhDataPaths.NetworkBansFile, out NetworkBanFile loaded) && loaded != null
                          ? loaded : new NetworkBanFile();
                    _file.Bans = _file.Bans ?? new List<NetworkBanEntry>();
                    return _file;
                }
            }
        }

        public static void Reload() { lock (_lock) _file = null; }

        public static int Count { get { lock (_lock) return File_.Bans.Count; } }

        // Absent is not "nobody is banned": only an array replaces what is held.
        public static void OnHeartbeatResponse(JToken token)
        {
            if (!(token is JArray arr)) return;

            List<NetworkBanEntry> fresh = new List<NetworkBanEntry>();
            foreach (JToken t in arr)
            {
                if (!(t is JObject o)) continue;
                string id = (string)o["user_id"] ?? "";
                long steam = (long?)o["steam_id64"] ?? 0;
                if (id.Length == 0 && steam == 0) continue;
                fresh.Add(new NetworkBanEntry
                {
                    UserId = id,
                    SteamId64 = steam,
                    Reason = (string)o["reason"] ?? "",
                    UntilUnix = (long?)o["until"] ?? 0,
                });
            }

            lock (_lock)
            {
                int before = File_.Bans.Count;
                _file.Bans = fresh;
                _file.UpdatedUtc = DateTime.UtcNow.ToString("o");
                if (!JsonFileStore.Save(KmhDataPaths.NetworkBansFile, _file))
                    ServerLog.Warn("Global: the network ban list could not be written; it is held in memory only.");
                if (before != fresh.Count)
                    ServerLog.Info($"Global: network ban list updated ({fresh.Count} account(s)).");
            }
        }

        public static NetworkBanEntry Find(string playerGlobalId, long nowUnix)
        {
            if (string.IsNullOrEmpty(playerGlobalId)) return null;
            lock (_lock)
            {
                foreach (NetworkBanEntry b in File_.Bans)
                {
                    if (b == null || !string.Equals(b.UserId, playerGlobalId, StringComparison.Ordinal)) continue;
                    if (b.UntilUnix != 0 && nowUnix >= b.UntilUnix) return null;
                    return b;
                }
                return null;
            }
        }

        internal static void ResetForTest() { lock (_lock) _file = new NetworkBanFile(); }
    }
}
