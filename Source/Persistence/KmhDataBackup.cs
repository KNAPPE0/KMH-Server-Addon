using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Persistence
{
    // Copies KMH-Data into timestamped backup folders for boot, migrations, and kmh backup; restore is plain folder copy.
    internal static class KmhDataBackup
    {
        public static bool TryCreate(string reason, out string destDir, out string error)
        {
            destDir = null;
            error   = null;

            string source = KmhDataPaths.Folder;
            if (!Directory.Exists(source)) { error = "KMH-Data does not exist yet (nothing to back up)"; return false; }

            string[] sourceFiles;
            try { sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(KeepInBackup).ToArray(); }
            catch (Exception ex) { error = $"could not enumerate KMH-Data: {ex.Message}"; return false; }

            if (sourceFiles.Length == 0) { error = "KMH-Data is empty (nothing to back up)"; return false; }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string dest  = Path.Combine(KmhDataPaths.BackupRoot, $"{stamp}-{SafeReason(reason)}");
            try
            {
                Directory.CreateDirectory(dest);
                long total = 0;
                int  count = 0;
                StringBuilder manifest = new StringBuilder();
                foreach (string file in sourceFiles)
                {
                    string rel = GetRelative(source, file);
                    string to  = Path.Combine(dest, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, overwrite: true);
                    long len = SafeLen(file);
                    total += len; count++;
                    manifest.AppendLine($"{rel}\t{len}");
                }

                string header =
                    $"KMH-Data backup\nutc: {DateTime.UtcNow:o}\nreason: {reason}\nbuild: " +
                    $"{typeof(KmhDataBackup).Assembly.GetName().Version}\nfiles: {count}\nbytes: {total}\n\n";
                File.WriteAllText(Path.Combine(dest, "manifest.txt"), header + manifest);

                destDir = dest;
                Extensibility.KmhEventBus.Instance.RaiseBackupCreated(new KMH.Sdk.Server.Events.BackupCreatedEvent
                {
                    Name = Path.GetFileName(dest), Utc = StampUtcIso(Path.GetFileName(dest)), Reason = reason ?? "",
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                // Don't leave a half-written backup folder masquerading as a good one.
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                return false;
            }
        }

        // Keep the newest `keep` backups, delete older ones (oldest first). keep <= 0 disables pruning. Returns the
        // number of folders removed. Timestamp-prefixed names sort chronologically, so ordering by name is enough.
        public static int Prune(int keep)
        {
            if (keep <= 0) return 0;
            try
            {
                if (!Directory.Exists(KmhDataPaths.BackupRoot)) return 0;
                List<string> dirs = Directory.GetDirectories(KmhDataPaths.BackupRoot)
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
                int removed = 0;
                foreach (string old in dirs.Skip(keep))
                {
                    try { Directory.Delete(old, recursive: true); removed++; }
                    catch (Exception ex) { ServerLog.Warn($"Backup prune: could not delete {Path.GetFileName(old)}: {ex.Message}"); }
                }
                return removed;
            }
            catch (Exception ex) { ServerLog.Warn($"Backup prune failed: {ex.Message}"); return 0; }
        }

        // Resolve a restore spec to a backup folder path: an exact folder name, "latest", or
        // "before:<iso|yyyyMMdd-HHmmss>" (newest backup at or before that time). Returns null if nothing matches.
        public static string ResolvePath(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            string root = KmhDataPaths.BackupRoot;
            if (!Directory.Exists(root)) return null;
            string s = spec.Trim();

            if (s.Equals("latest", StringComparison.OrdinalIgnoreCase))
                return Directory.GetDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();

            if (s.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseWhen(s.Substring("before:".Length), out DateTime cutoff)) return null;
                return Directory.GetDirectories(root)
                    .Where(d => TryParseStamp(Path.GetFileName(d), out DateTime t) && t <= cutoff)
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
            }

            string exact = Path.Combine(root, s);
            return Directory.Exists(exact) ? exact : null;
        }

        // Roll KMH-Data back to match backup `spec`. Safety-backs up current data first, then makes KMH-Data an exact
        // copy of the backup (files created since are removed - a true rollback). Boot-time only, before any store loads.
        public static bool Restore(string spec, out string safetyBackup, out string error)
        {
            safetyBackup = null; error = null;
            string src = ResolvePath(spec);
            if (src == null) { error = $"no backup matches '{spec}'"; return false; }
            if (!File.Exists(Path.Combine(src, "manifest.txt")))
            { error = $"'{Path.GetFileName(src)}' has no manifest - refusing to restore from it"; return false; }

            string[] backupFiles;
            try { backupFiles = Directory.GetFiles(src, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { error = $"could not read backup: {ex.Message}"; return false; }
            if (backupFiles.Length <= 1) { error = "backup has no data files - refusing to wipe live data"; return false; }

            // Safety net so a bad restore stays reversible.
            if (TryCreate("pre-restore", out string safeDir, out string safeErr)) safetyBackup = Path.GetFileName(safeDir);
            else ServerLog.Warn($"Restore: pre-restore safety backup failed ({safeErr}) - continuing anyway.");

            try
            {
                string dataRoot = KmhDataPaths.Folder;
                Directory.CreateDirectory(dataRoot);
                // Clear current KMH-Data (folder kept; backups are a sibling; Snapshots/ survives the wipe).
                foreach (string entry in Directory.GetFileSystemEntries(dataRoot))
                {
                    if (IsSnapshotArtifact(entry)) continue;
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                foreach (string file in backupFiles)
                {
                    string rel = GetRelative(src, file);
                    if (rel.Equals("manifest.txt", StringComparison.OrdinalIgnoreCase)) continue; // backup metadata, not data
                    string to = Path.Combine(dataRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, overwrite: true);
                }
                ServerLog.Warn($"KMH-Data RESTORED from '{Path.GetFileName(src)}'" +
                               (safetyBackup != null ? $" (previous data saved as '{safetyBackup}')" : ""));
                Extensibility.KmhEventBus.Instance.RaiseRestoreApplied(new KMH.Sdk.Server.Events.RestoreAppliedEvent
                {
                    BackupName = Path.GetFileName(src), SafetyBackup = safetyBackup ?? "",
                });
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ISO-8601 UTC parsed from a backup's yyyyMMdd-HHmmss name prefix, or "" if it doesn't parse.
        public static string StampUtcIso(string backupName)
            => TryParseStamp(backupName, out DateTime t) ? t.ToString("o") : "";

        private static bool TryParseStamp(string folderName, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrEmpty(folderName) || folderName.Length < 15) return false;
            return DateTime.TryParseExact(folderName.Substring(0, 15), "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
        }

        private static bool TryParseWhen(string s, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (TryParseStamp(s.Trim(), out utc)) return true;
            return DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
        }

        public readonly struct BackupInfo
        {
            public readonly string Name;
            public readonly long   Bytes;
            public readonly int    Files;
            public BackupInfo(string name, long bytes, int files) { Name = name; Bytes = bytes; Files = files; }
        }

        public static List<BackupInfo> List()
        {
            List<BackupInfo> result = new List<BackupInfo>();
            try
            {
                if (!Directory.Exists(KmhDataPaths.BackupRoot)) return result;
                foreach (string dir in Directory.GetDirectories(KmhDataPaths.BackupRoot)
                             .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
                {
                    long bytes = 0; int files = 0;
                    try
                    {
                        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        { bytes += SafeLen(f); files++; }
                    }
                    catch { }
                    result.Add(new BackupInfo(Path.GetFileName(dir), bytes, files));
                }
            }
            catch (Exception ex) { ServerLog.Warn($"Backup list failed: {ex.Message}"); }
            return result;
        }

        private static bool KeepInBackup(string path)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(".kmh-selftest.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(".kmh-snapshot-request", StringComparison.OrdinalIgnoreCase)) return false;   // transient marker
            if (IsSnapshotArtifact(path)) return false;   // Snapshots/ is an archive, not live state - keep backups lean
            if (IsUnder(path, "Debug")) return false;     // client debug logs are transient diagnostics
            return true;
        }

        // Snapshots/ is a recovery archive: never backed up (bloat), never deleted by a rollback.
        private static bool IsSnapshotArtifact(string path) => IsUnder(path, "Snapshots");

        private static bool IsUnder(string path, string folder)
        {
            string marker = Path.DirectorySeparatorChar + folder + Path.DirectorySeparatorChar;
            return path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
                || path.TrimEnd(Path.DirectorySeparatorChar).EndsWith(Path.DirectorySeparatorChar + folder, StringComparison.OrdinalIgnoreCase);
        }

        // Net472 has no Path.GetRelativePath; KMH-Data paths are always under `root`, so a substring is exact.
        private static string GetRelative(string root, string full)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : Path.GetFileName(full);
        }

        private static long SafeLen(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

        private static string SafeReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "manual";
            StringBuilder sb = new StringBuilder(reason.Length);
            foreach (char c in reason) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            string s = sb.ToString().Trim('-');
            return s.Length == 0 ? "manual" : (s.Length > 40 ? s.Substring(0, 40) : s);
        }
    }
}
