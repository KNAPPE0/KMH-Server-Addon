using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhVideoCache
    {
        // Only a test sets this: a sweep run against the real folder would evict what players are watching.
        internal static string FolderOverride;

        public static string Folder => FolderOverride
                                    ?? System.IO.Path.Combine(KmhDataPaths.Folder, "MediaCache", "Video");

        public static string PathFor(string videoId, int height)
        {
            string name = Safe(videoId) + "_" + Math.Max(0, height) + ".mp4";
            return System.IO.Path.Combine(Folder, name);
        }

        public static void EnsureFolder()
        {
            try { Directory.CreateDirectory(Folder); } catch (Exception ex) { ServerLog.Warn($"Video cache: {ex.Message}"); }
        }

        // A file still being written by the helper is not a hit: it would be served as a truncated video.
        public static bool Has(string file) => file.Length > 0 && File.Exists(file) && !File.Exists(file + ".part");

        // Kept beside the video: the title is only learned while fetching, and a cache hit still has to show one.
        public static void RememberTitle(string file, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            try { File.WriteAllText(file + ".title", title); } catch { }
        }

        public static string TitleOf(string file)
        {
            try { return File.Exists(file + ".title") ? File.ReadAllText(file + ".title").Trim() : ""; }
            catch { return ""; }
        }

        // Eviction is by last USE, not by when it was fetched, so the video everyone is watching is the last to go.
        public static void Touch(string file)
        {
            try { if (File.Exists(file)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { }
        }

        public static void Start()
        {
            Maintenance.KmhScheduler.Register("video-cache-sweep", TimeSpan.FromHours(1), Sweep, TimeSpan.FromMinutes(5));
            try
            {
                if (!Directory.Exists(Folder)) return;
                FileInfo[] held = new DirectoryInfo(Folder).GetFiles("*.mp4");
                if (held.Length == 0) return;
                long bytes = 0;
                foreach (FileInfo f in held) bytes += f.Length;
                MediaConfig cfg = MediaConfig.Current;
                ServerLog.Info($"Video cache: {held.Length} file(s), {bytes / (1024 * 1024)}MB of "
                             + $"{cfg.VideoCacheMaxMegabytes}MB, kept {cfg.VideoCacheHours}h.");
            }
            catch { }
        }

        // Age first, then size: a server nobody posts video on should not still be holding last month's downloads.
        public static void Sweep()
        {
            MediaConfig cfg = MediaConfig.Current;
            long limit = (long)Math.Max(1, cfg.VideoCacheMaxMegabytes) * 1024 * 1024;
            int hours = Math.Max(0, cfg.VideoCacheHours);

            try
            {
                if (!Directory.Exists(Folder)) return;
                var files = new List<FileInfo>(new DirectoryInfo(Folder).GetFiles("*.mp4"));
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                int gone = 0;
                long freed = 0, total = 0;
                foreach (FileInfo f in files) total += f.Length;

                DateTime cutoff = DateTime.UtcNow.AddHours(-hours);
                foreach (FileInfo f in files)
                {
                    bool stale = hours > 0 && f.LastWriteTimeUtc < cutoff;
                    if (!stale && total <= limit) continue;
                    long size = f.Length;
                    try
                    {
                        f.Delete();
                        try { File.Delete(f.FullName + ".title"); } catch { }
                        total -= size; freed += size; gone++;
                    }
                    catch (Exception ex) { ServerLog.Verbose($"Video cache: could not evict {f.Name} - {ex.Message}"); }
                }

                // A part-file with no fetch behind it is a download that died with the process.
                foreach (FileInfo f in new DirectoryInfo(Folder).GetFiles("*.part"))
                {
                    if (f.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-2)) continue;
                    try { f.Delete(); gone++; } catch { }
                }

                if (gone > 0)
                    ServerLog.Info($"Video cache: freed {freed / (1024 * 1024)}MB ({gone} file(s)); "
                                 + $"{total / (1024 * 1024)}MB of {limit / (1024 * 1024)}MB still held.");
            }
            catch (Exception ex) { ServerLog.Warn($"Video cache sweep: {ex.Message}"); }
        }

        internal static string Safe(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return "unknown";
            var sb = new System.Text.StringBuilder(videoId.Length);
            foreach (char c in videoId)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
