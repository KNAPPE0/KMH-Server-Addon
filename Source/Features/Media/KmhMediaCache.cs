using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhMediaCache
    {
        internal sealed class Entry
        {
            public string Id = "";
            public string SourceUrl = "";
            public string Mime = "";
            public string ContentHash = "";
            public int    Bytes, Width, Height, Frames;
            public bool   Animated;
            public string File = "";
            public long   CreatedUtcTicks;
            public long   LastUsedUtcTicks;
            public bool   HasBytes => !string.IsNullOrEmpty(File);
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static long _totalBytes;

        // A copy left from before a restart would be served under a media policy that no longer applies.
        internal static void ResetOnBoot()
        {
            lock (_lock)
            {
                _byId.Clear();
                _totalBytes = 0;
                try
                {
                    string dir = KmhDataPaths.MediaCacheDir;
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex) { ServerLog.Warn($"Media cache: could not reset the cache directory: {ex.Message}"); }
            }
        }

        internal static string IdFor(string url)
        {
            using SHA256 sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
            var sb = new StringBuilder(24);
            for (int i = 0; i < 12; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }

        internal static string HashOf(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            byte[] h = sha.ComputeHash(bytes ?? new byte[0]);
            var sb = new StringBuilder(64);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        internal static string Register(string sourceUrl)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl)) return "";
            string id = IdFor(sourceUrl);
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (_byId.TryGetValue(id, out Entry existing)) { existing.LastUsedUtcTicks = now; return id; }
                _byId[id] = new Entry { Id = id, SourceUrl = sourceUrl, CreatedUtcTicks = now, LastUsedUtcTicks = now };
            }
            return id;
        }

        internal static string SourceUrlFor(string id)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(id ?? "", out Entry e)) return "";
                e.LastUsedUtcTicks = DateTime.UtcNow.Ticks;
                return e.SourceUrl;
            }
        }

        internal static Entry Get(string id)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(id ?? "", out Entry e) || !e.HasBytes) return null;
                if (!File.Exists(e.File)) { Forget(e); return null; }
                e.LastUsedUtcTicks = DateTime.UtcNow.Ticks;
                return e;
            }
        }

        internal static byte[] Read(Entry e)
        {
            try { return e != null && File.Exists(e.File) ? File.ReadAllBytes(e.File) : null; }
            catch (Exception ex) { ServerLog.Warn($"Media cache: read failed for {e?.Id}: {ex.Message}"); return null; }
        }

        internal static Entry Store(string id, string sourceUrl, KmhMediaTranscode.Result converted, MediaConfig cfg)
        {
            if (converted == null || !converted.Ok || converted.Bytes == null) return null;

            string hash = HashOf(converted.Bytes);
            string dir = KmhDataPaths.MediaCacheDir;
            string file = Path.Combine(dir, hash + ".bin");

            try
            {
                Directory.CreateDirectory(dir);
                if (!File.Exists(file)) File.WriteAllBytes(file, converted.Bytes);
            }
            catch (Exception ex) { ServerLog.Warn($"Media cache: write failed for {id}: {ex.Message}"); return null; }

            long now = DateTime.UtcNow.Ticks;
            var entry = new Entry
            {
                Id = id, SourceUrl = sourceUrl, Mime = converted.Mime, ContentHash = hash,
                Bytes = converted.Bytes.Length, Width = converted.Width, Height = converted.Height,
                Frames = converted.Frames, Animated = converted.Animated, File = file,
                CreatedUtcTicks = now, LastUsedUtcTicks = now,
            };

            lock (_lock)
            {
                if (_byId.TryGetValue(id, out Entry old) && old.HasBytes) _totalBytes -= old.Bytes;
                _byId[id] = entry;
                _totalBytes += entry.Bytes;
                Sweep(cfg);
            }
            return entry;
        }

        // Both paths evict through Forget, or the bytes stay on disk and the cache only ever grows.
        internal static void Sweep(MediaConfig cfg)
        {
            if (cfg == null) return;
            lock (_lock)
            {
                // Bytes only: a byte-less row is the id -> url registration, and expiring THAT turned an older gif back into a still.
                long cutoff = DateTime.UtcNow.AddMinutes(-cfg.CacheTtlMinutes).Ticks;
                foreach (Entry e in _byId.Values.Where(x => ExpiresOnTtl(x.HasBytes, x.LastUsedUtcTicks, cutoff)).ToList())
                    Forget(e);

                if (_totalBytes > cfg.CacheMaxBytes)
                    foreach (Entry e in _byId.Values.Where(x => x.HasBytes).OrderBy(x => x.LastUsedUtcTicks).ToList())
                    {
                        if (_totalBytes <= cfg.CacheMaxBytes) break;
                        Forget(e);
                    }

                // Registrations are one per distinct media url and cost only memory, but a server runs for months.
                if (_byId.Count <= MaxRegistrations) return;
                foreach (Entry e in _byId.Values.Where(x => !x.HasBytes).OrderBy(x => x.LastUsedUtcTicks).ToList())
                {
                    if (_byId.Count <= MaxRegistrations) break;
                    Forget(e);
                }
            }
        }

        // Chat rings are capped in the thousands, so this is headroom rather than a limit anything should reach.
        internal const int MaxRegistrations = 20_000;

        // The rule alone, so a suite can prove it without sweeping the live cache out from under a running server.
        internal static bool ExpiresOnTtl(bool hasBytes, long lastUsedTicks, long cutoffTicks)
            => hasBytes && lastUsedTicks < cutoffTicks;

        // Caller holds _lock.
        private static void Forget(Entry e)
        {
            if (e == null) return;
            if (e.HasBytes)
            {
                _totalBytes -= e.Bytes;
                // Two urls can convert to identical bytes and share one file, so it goes only when nothing points at it.
                bool sharedElsewhere = _byId.Values.Any(x => !ReferenceEquals(x, e) && x.File == e.File);
                if (!sharedElsewhere)
                {
                    try { if (File.Exists(e.File)) File.Delete(e.File); }
                    catch (Exception ex) { ServerLog.Warn($"Media cache: could not delete {e.File}: {ex.Message}"); }
                }
            }
            _byId.Remove(e.Id);
        }

        internal static (int entries, int withBytes, long bytes) Stats()
        {
            lock (_lock) return (_byId.Count, _byId.Values.Count(x => x.HasBytes), _totalBytes);
        }
    }
}
