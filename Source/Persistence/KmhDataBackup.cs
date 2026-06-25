using System;
using System.Collections.Generic;
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
            return true;
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
