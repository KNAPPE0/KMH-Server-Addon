using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Reports
{
    // One file per report: an owner can handle any by hand, and a damaged one costs only itself.
    internal static class ReportStore
    {
        private static readonly object _lock = new object();

        // The self-test's own folder, so a run never touches an owner's reports.
        private static string _dirForTest;
        internal static string DirForTest
        {
            get => _dirForTest;
            set { lock (_lock) { _dirForTest = value; _pending = null; } }
        }
        private static string Dir => _dirForTest ?? KmhDataPaths.ReportsDir;

        // Read from disk once, then kept current by Add and Update, so a pass never re-reads every report.
        private static HashSet<string> _pending;

        public static List<string> PendingIds()
        {
            lock (_lock)
            {
                EnsurePendingLocked();
                return new List<string>(_pending);
            }
        }

        private static void EnsurePendingLocked()
        {
            if (_pending != null) return;
            _pending = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(Dir)) return;
            foreach (string f in Directory.GetFiles(Dir, "*.json"))
                if (ReportText.IsReportId(Path.GetFileNameWithoutExtension(f))
                    && JsonFileStore.TryLoad(f, out StoredReport r) && r != null
                    && r.ForwardState == ReportForwardStates.Pending)
                    _pending.Add(r.ReportId);
        }

        private static void TrackLocked(StoredReport r)
        {
            EnsurePendingLocked();
            if (r.ForwardState == ReportForwardStates.Pending) _pending.Add(r.ReportId);
            else _pending.Remove(r.ReportId);
        }

        private static string JsonPath(string id) => Path.Combine(Dir, id + ".json");

        // Counted from the folder, not loaded: status.json asks for this on every export.
        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    if (!Directory.Exists(Dir)) return 0;
                    int n = 0;
                    foreach (string f in Directory.EnumerateFiles(Dir, "*.json"))
                        if (ReportText.IsReportId(Path.GetFileNameWithoutExtension(f))) n++;
                    return n;
                }
            }
        }

        public static int PendingCount { get { lock (_lock) { EnsurePendingLocked(); return _pending.Count; } } }

        public static bool Exists(string id)
        {
            if (!ReportText.IsReportId(id)) return false;
            lock (_lock) return File.Exists(JsonPath(id));
        }

        public static StoredReport Load(string id)
        {
            if (!ReportText.IsReportId(id)) return null;
            lock (_lock)
                return JsonFileStore.TryLoad(JsonPath(id), out StoredReport r) && r != null ? r : null;
        }

        // The screenshot goes first, so a report never names a picture that is not on disk.
        public static bool Add(StoredReport r, byte[] screenshot)
        {
            if (r == null || !ReportText.IsReportId(r.ReportId)) return false;
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string ext = ReportText.ImageExtension(screenshot);
                    if (ext.Length > 0)
                    {
                        string shot = Path.Combine(Dir, r.ReportId + ext);
                        string tmp = shot + ".tmp";
                        File.WriteAllBytes(tmp, screenshot);
                        File.Move(tmp, shot, overwrite: true);
                        r.ScreenshotFile = r.ReportId + ext;
                    }
                    if (!JsonFileStore.Save(JsonPath(r.ReportId), r)) return false;
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Reports: could not save report {ReportText.Short(r.ReportId)} ({ex.Message}).");
                    return false;
                }
                TrackLocked(r);
                PruneLocked(DateTimeOffset.UtcNow);
                return true;
            }
        }

        public static bool Update(StoredReport r)
        {
            if (r == null || !ReportText.IsReportId(r.ReportId)) return false;
            lock (_lock)
            {
                if (!File.Exists(JsonPath(r.ReportId)) || !JsonFileStore.Save(JsonPath(r.ReportId), r)) return false;
                TrackLocked(r);
                return true;
            }
        }

        public static byte[] Screenshot(StoredReport r)
        {
            if (r == null || string.IsNullOrEmpty(r.ScreenshotFile)) return null;
            // Only a name this store wrote: a hand-edited report must not point outside the folder.
            string name = Path.GetFileName(r.ScreenshotFile);
            if (!ReportText.IsReportId(Path.GetFileNameWithoutExtension(name))) return null;
            lock (_lock)
            {
                string path = Path.Combine(Dir, name);
                try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
                catch (IOException) { return null; }
            }
        }

        public static List<StoredReport> List()
        {
            var all = new List<StoredReport>();
            lock (_lock)
            {
                if (!Directory.Exists(Dir)) return all;
                foreach (string f in Directory.GetFiles(Dir, "*.json"))
                    if (ReportText.IsReportId(Path.GetFileNameWithoutExtension(f))
                        && JsonFileStore.TryLoad(f, out StoredReport r) && r != null)
                        all.Add(r);
            }
            all.Sort((a, b) => b.ReceivedAtUnix.CompareTo(a.ReceivedAtUnix));
            return all;
        }

        public static bool Delete(string id)
        {
            if (!ReportText.IsReportId(id)) return false;
            lock (_lock) return DeleteLocked(id);
        }

        private static bool DeleteLocked(string id)
        {
            _pending?.Remove(id);
            bool any = false;
            foreach (string ext in new[] { ".json", ".jpg", ".png" })
            {
                string p = Path.Combine(Dir, id + ext);
                try { if (File.Exists(p)) { File.Delete(p); any = true; } }
                catch (IOException) { }
            }
            return any;
        }

        internal static int Prune(DateTimeOffset now) { lock (_lock) return PruneLocked(now); }

        // By file time, not by parsing: it is within minutes of arrival, and this never reads hundreds of files.
        private static int PruneLocked(DateTimeOffset now)
        {
            if (!Directory.Exists(Dir)) return 0;
            var reports = new List<(string Id, DateTime At)>();
            foreach (string f in Directory.GetFiles(Dir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (!ReportText.IsReportId(id)) continue;
                try { reports.Add((id, File.GetLastWriteTimeUtc(f))); }
                catch (IOException) { }
            }
            reports.Sort((a, b) => b.At.CompareTo(a.At));

            DateTime cutoff = (now - ReportLimits.KeepFor).UtcDateTime;
            int gone = 0;
            for (int i = 0; i < reports.Count; i++)
                if (i >= ReportLimits.KeepCount || reports[i].At < cutoff)
                    if (DeleteLocked(reports[i].Id)) gone++;
            DropOrphansLocked();
            return gone;
        }

        // Add writes a picture and its record under this lock, so a picture with no record, or a half-written one,
        // is what a crash between the two left behind.
        private static void DropOrphansLocked()
        {
            foreach (string f in Directory.GetFiles(Dir))
            {
                string name = Path.GetFileName(f);
                bool partial = name.EndsWith(".tmp", StringComparison.Ordinal);
                string image = partial ? name.Substring(0, name.Length - 4) : name;
                string ext = Path.GetExtension(image);
                if (ext != ".jpg" && ext != ".png") continue;
                string id = Path.GetFileNameWithoutExtension(image);
                if (!ReportText.IsReportId(id) || (!partial && File.Exists(JsonPath(id)))) continue;
                try { File.Delete(f); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
