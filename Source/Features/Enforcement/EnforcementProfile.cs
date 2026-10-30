using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Enforcement
{
    internal static class EnforcementProfile
    {
        private static byte[] _zip = Array.Empty<byte>();
        private static string _hash = "";
        private static long   _updatedTicks;
        private static int    _fileCount;
        private static long   _revision;

        public static bool   HasProfile   => _zip != null && _zip.Length > 0;
        public static string Hash         => _hash;
        public static int    FileCount    => _fileCount;
        public static long   UpdatedTicks => _updatedTicks;

        // Seeded from the stored write time, so it never goes backwards across a restart.
        public static long   Revision     => _revision;

        private static string ZipPath
        {
            get
            {
                string enforcementDir = Directory.GetParent(KmhDataPaths.EnforcementProfileDir)?.FullName
                                        ?? KmhDataPaths.EnforcementProfileDir;
                return Path.Combine(enforcementDir, "Profile.zip");
            }
        }

        public static int Reload()
        {
            try
            {
                if (File.Exists(ZipPath))
                {
                    _zip          = File.ReadAllBytes(ZipPath);
                    _hash         = Sha256Hex(_zip);
                    _updatedTicks = File.GetLastWriteTimeUtc(ZipPath).Ticks;
                    _fileCount    = CountEntries(_zip);
                    _revision     = _updatedTicks / TimeSpan.TicksPerSecond;
                }
                else Clear();
            }
            catch (Exception ex) { ServerLog.Warn($"Enforcement: profile reload failed: {ex.Message}"); Clear(); }
            return _fileCount;
        }

        internal static Func<string> FailWriteForTest;

        // False keeps the old profile active: publishing one the disk refused hands clients rules that vanish on restart.
        public static bool SetProfile(byte[] zip, string hash, long updatedTicks)
        {
            byte[] oldZip = _zip; string oldHash = _hash;
            long oldTicks = _updatedTicks; int oldCount = _fileCount; long oldRev = _revision;

            _zip          = zip ?? Array.Empty<byte>();
            _hash         = string.IsNullOrEmpty(hash) ? Sha256Hex(_zip) : hash;
            _updatedTicks = updatedTicks > 0 ? updatedTicks : DateTime.UtcNow.Ticks;
            _fileCount    = CountEntries(_zip);
            _revision     = Math.Max(_revision + 1, _updatedTicks / TimeSpan.TicksPerSecond);
            try
            {
                string injected = FailWriteForTest?.Invoke();
                if (injected != null) throw new IOException(injected);
                Directory.CreateDirectory(Path.GetDirectoryName(ZipPath));
                // Written aside then renamed: a direct write dying partway would truncate the only stored profile.
                string tmp = ZipPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try { File.WriteAllBytes(tmp, _zip); File.Move(tmp, ZipPath, overwrite: true); }
                catch { try { File.Delete(tmp); } catch { } throw; }
                return true;
            }
            catch (Exception ex)
            {
                _zip = oldZip; _hash = oldHash; _updatedTicks = oldTicks; _fileCount = oldCount; _revision = oldRev;
                ServerLog.Warn($"Enforcement: profile save failed, the previous profile stays active: {ex.Message}");
                return false;
            }
        }

        public static byte[] SerializeBytes() => _zip ?? Array.Empty<byte>();

        private static void Clear() { _zip = Array.Empty<byte>(); _hash = ""; _updatedTicks = 0; _fileCount = 0; _revision = 0; }

        private static int CountEntries(byte[] zip)
        {
            if (zip == null || zip.Length == 0) return 0;
            try
            {
                using var ms = new MemoryStream(zip);
                using var za = new ZipArchive(ms, ZipArchiveMode.Read);
                return za.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
            }
            catch { return 0; }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null) return "";
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(bytes);
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
