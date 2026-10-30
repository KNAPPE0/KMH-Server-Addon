using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Moderation
{
    internal sealed class BanEntry
    {
        public string Username   { get; set; } = "";
        // The durable Global identity where the player has one; a rename does not shed a ban keyed on this.
        public string GlobalId   { get; set; } = "";
        public string Ip         { get; set; } = "";
        public string Reason     { get; set; } = "";
        public string By         { get; set; } = "";
        public long   AtUnix     { get; set; }
        // 0 is permanent. Anything else is compared against the clock at admission, never at write time.
        public long   ExpiresUnix { get; set; }

        public bool IsExpired_(long nowUnix) => ExpiresUnix != 0 && nowUnix >= ExpiresUnix;
    }

    internal sealed class BanFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<BanEntry> Bans { get; set; } = new List<BanEntry>();
    }

    // Beside RWT's own ban list, not replacing it: a ban here is set from Discord, survives a rename and can cover an IP.
    internal static class BanStore
    {
        private static readonly object _lock = new object();
        private static BanFile _file;
        private static bool _dirty;

        private static BanFile File_
        {
            get
            {
                lock (_lock)
                {
                    if (_file != null) return _file;
                    _file = JsonFileStore.TryLoad(KmhDataPaths.BansFile, out BanFile loaded) && loaded != null
                          ? loaded : new BanFile();
                    _file.Bans = _file.Bans ?? new List<BanEntry>();
                    return _file;
                }
            }
        }

        public static void Reload() { lock (_lock) { _file = null; _dirty = false; } }

        private static bool SaveLocked()
        {
            try
            {
                if (!JsonFileStore.Save(KmhDataPaths.BansFile, _file)) return false;
                _dirty = false;
                return true;
            }
            catch (Exception ex) { ServerLog.Warn($"Could not save Bans.json: {ex.Message}"); return false; }
        }

        // Retried by the periodic flush; only a change nobody wrote down yet is written.
        public static bool SaveToDisk()
        {
            lock (_lock) return _file == null || !_dirty || SaveLocked();
        }

        // Name, id and address match independently - an alt shares neither name nor id, only the address.
        public static BanEntry Find(string username, string globalId, string ip, long nowUnix)
        {
            lock (_lock)
            {
                foreach (BanEntry b in File_.Bans)
                {
                    if (b == null || b.IsExpired_(nowUnix)) continue;
                    if (Same(b.Username, username)) return b;
                    if (Same(b.GlobalId, globalId)) return b;
                    if (SameIp(b.Ip, ip)) return b;
                }
                return null;
            }
        }

        private static bool Same(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        // Compared without the port, because a reconnect arrives from the same address on a different one.
        internal static bool SameIp(string a, string b)
        {
            string x = HostOnly(a), y = HostOnly(b);
            return x.Length > 0 && y.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        internal static string HostOnly(string address)
        {
            string s = (address ?? "").Trim();
            if (s.Length == 0) return "";
            // IPv6 in brackets keeps its colons; only a trailing :port is stripped.
            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                int close = s.IndexOf(']');
                return close > 0 ? s.Substring(1, close - 1) : s;
            }
            int colon = s.IndexOf(':');
            if (colon < 0) return s;
            // More than one colon and no brackets is a bare IPv6 address, which has no port to strip.
            return s.IndexOf(':', colon + 1) >= 0 ? s : s.Substring(0, colon);
        }

        // A ban that could not be written still holds for this session; saved=false tells the caller to say so.
        public static bool Add(BanEntry entry, out bool saved)
        {
            saved = false;
            if (entry == null) return false;
            entry.Username = (entry.Username ?? "").Trim();
            entry.GlobalId = (entry.GlobalId ?? "").Trim();
            entry.Ip       = HostOnly(entry.Ip);
            if (entry.Username.Length == 0 && entry.GlobalId.Length == 0 && entry.Ip.Length == 0) return false;

            lock (_lock)
            {
                File_.Bans.Add(entry);
                _dirty = true;
                saved = SaveLocked();
            }
            return true;
        }

        // Lifting a ban nobody wrote down would come back on restart, so a failed save puts the entries back.
        public static int Remove(string subject, out bool saved)
        {
            saved = true;
            string s = (subject ?? "").Trim();
            if (s.Length == 0) return 0;
            lock (_lock)
            {
                List<BanEntry> before = File_.Bans;
                bool wasDirty = _dirty;
                File_.Bans = before.Where(b => b != null
                                            && !Same(b.Username, s)
                                            && !Same(b.GlobalId, s)
                                            && !SameIp(b.Ip, s)).ToList();
                int removed = before.Count - File_.Bans.Count;
                if (removed == 0) return 0;
                _dirty = true;
                if (SaveLocked()) return removed;
                File_.Bans = before;
                _dirty = wasDirty;
                saved = false;
                return 0;
            }
        }

        public static List<BanEntry> Active(long nowUnix)
        {
            lock (_lock) return File_.Bans.Where(b => b != null && !b.IsExpired_(nowUnix)).ToList();
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _file = new BanFile(); _dirty = false; }
        }

        internal static bool DirtyForTest { get { lock (_lock) return _dirty; } }
    }
}
