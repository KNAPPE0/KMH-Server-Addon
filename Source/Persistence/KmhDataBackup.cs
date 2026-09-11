using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Persistence
{
    internal static class KmhDataBackup
    {
        public static bool TryCreate(string reason, out string destDir, out string error)
        {
            destDir = null;
            error   = null;

            string source = KmhDataPaths.Folder;
            if (!Directory.Exists(source)) { error = "KMH-Data does not exist yet (nothing to back up)"; return false; }

            // Mutations are held and every store flushed FIRST, or the copy is Treasury at one instant and Auctions at another - a state the server was never in.
            Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.Backup);
            try
            {
                // Before Ready the stores are EMPTY (the pre-restore backup runs at boot), so flushing then would write nothing over everything.
                if (Maintenance.KmhReadiness.IsReady)
                {
                    // FlushAll counts what it managed to write, so the refusals are counted here as reported.
                    int unflushed = 0;
                    Maintenance.KmhDataFlush.FlushAll(e => { unflushed++; Diagnostics.ServerLog.Warn($"Backup: {e}"); });
                    if (unflushed > 0)
                    {
                        error = $"{unflushed} store(s) could not be written before the copy - the backup would not be " +
                                "a coherent point in time. Fix the persistence problem and try again.";
                        return false;
                    }
                }
                return CopyLocked(source, reason, out destDir, out error);
            }
            finally { Maintenance.KmhMaintenanceGate.Release(); }
        }

        private static bool CopyLocked(string source, string reason, out string destDir, out string error)
        {
            destDir = null;
            error   = null;

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
                    // Hashed from the COPY, so a read error during it surfaces as a mismatch at restore instead of a file with merely the right length.
                    manifest.AppendLine($"{rel}\t{len}\t{Sha256(to)}");
                }

                string header =
                    $"KMH-Data backup\nutc: {DateTime.UtcNow:o}\nreason: {reason}\n" +
                    $"backup-id: {Guid.NewGuid():N}\n" +
                    $"assembly: {typeof(KmhDataBackup).Assembly.GetName().Version}\n" +
                    $"build: {KmhVersion.Build} ({KmhVersion.BuildTag})\n" +
                    $"protocol: {KmhVersion.Protocol}\nconfig-schema: {KmhVersion.ConfigSchema}\n" +
                    $"data-schema: {KmhVersion.DataSchema}\n" +
                    $"data-generation: {Features.Economy.KmhEconomyReset.Generation}\n" +
                    $"files: {count}\nbytes: {total}\nhash: sha256\n\n";
                File.WriteAllText(Path.Combine(dest, "manifest.txt"), header + manifest);

                destDir = dest;

                // Not only at boot: the anti-cheat backs up on player action, so copies accrue between restarts.
                int retention = Maintenance.MaintenanceConfig.Current.BackupRetention;
                int pruned = Prune(retention);
                if (pruned > 0)
                    Diagnostics.ServerLog.Verbose($"Backups: pruned {pruned} old backup(s) to the retention of {retention}.");

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

        // Names are timestamp-prefixed, so sorting by name is chronological. keep <= 0 disables pruning.
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

        // "before:" resolves to the newest backup at or before that time.
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

        // Set by a restore that landed this boot (restores run before any store loads), and consumed once the generation store has been read.
        public static string RestoredThisBoot { get; private set; }

        internal static void ClearRestoredThisBoot() => RestoredThisBoot = null;

        // force skips only the pre-restore safety backup, and only when an admin asked for that in so many words.
        public static bool Restore(string spec, out string safetyBackup, out string error, bool force = false)
        {
            safetyBackup = null; error = null;
            string src = ResolvePath(spec);
            if (src == null) { error = $"no backup matches '{spec}'"; return false; }
            string manifestPath = Path.Combine(src, "manifest.txt");
            if (!File.Exists(manifestPath))
            { error = $"'{Path.GetFileName(src)}' has no manifest - refusing to restore from it"; return false; }

            string[] backupFiles;
            try { backupFiles = Directory.GetFiles(src, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { error = $"could not read backup: {ex.Message}"; return false; }
            if (backupFiles.Length <= 1) { error = "backup has no data files - refusing to wipe live data"; return false; }

            // Checked BEFORE anything live is touched, while the current data is still the only copy that matters.
            if (!Verify(src, out string verifyError)) { error = verifyError; return false; }

            // Without this backup a restore from the wrong one is unrecoverable, so normal restores fail closed.
            if (TryCreate("pre-restore", out string safeDir, out string safeErr)) safetyBackup = Path.GetFileName(safeDir);
            else if (!force)
            {
                error = $"the pre-restore safety backup failed ({safeErr}), so restoring would destroy the current " +
                        "data with no way back. Fix that first, or re-run with 'force' to accept the loss.";
                return false;
            }
            else ServerLog.Warn($"Restore: pre-restore safety backup failed ({safeErr}) - continuing because force was given.");

            string dataRoot = KmhDataPaths.Folder;
            string staging  = dataRoot + ".restoring";
            string previous = dataRoot + ".previous";
            try
            {
                // Built off to one side: a crash before the swap leaves live data untouched, a crash during it is finished by FinishInterruptedRestore.
                SafeDeleteDir(staging);
                Directory.CreateDirectory(staging);
                foreach (string file in backupFiles)
                {
                    string rel = GetRelative(src, file);
                    if (rel.Equals("manifest.txt", StringComparison.OrdinalIgnoreCase)) continue; // backup metadata, not data
                    string to = Path.Combine(staging, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, overwrite: true);
                }

                // A backup makes no claim about what it leaves out - the Debug log recording this restore included - so a restore must not destroy it.
                if (Directory.Exists(dataRoot))
                    foreach (string entry in Directory.GetFileSystemEntries(dataRoot))
                        if (SurvivesRestore(entry))
                            MoveInto(entry, staging, dataRoot);

                SafeDeleteDir(previous);
                if (Directory.Exists(dataRoot)) Directory.Move(dataRoot, previous);
                Directory.Move(staging, dataRoot);
                SafeDeleteDir(previous);

                // The generation bump belongs to KmhDataRestore: bumping before EconomyReset.json is read would write 1 over the value just restored.
                RestoredThisBoot = Path.GetFileName(src);

                ServerLog.Warn($"KMH-Data RESTORED from '{Path.GetFileName(src)}'" +
                               (safetyBackup != null ? $" (previous data saved as '{safetyBackup}')" : ""));
                Extensibility.KmhEventBus.Instance.RaiseRestoreApplied(new KMH.Sdk.Server.Events.RestoreAppliedEvent
                {
                    BackupName = Path.GetFileName(src), SafetyBackup = safetyBackup ?? "",
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                SafeDeleteDir(staging);
                // The old data is still the only authority there is - put it back rather than leaving the server with nothing.
                try { if (!Directory.Exists(dataRoot) && Directory.Exists(previous)) Directory.Move(previous, dataRoot); }
                catch (Exception put) { ServerLog.Error($"Restore: could not put the previous KMH-Data back: {put.Message}"); }
                return false;
            }
        }

        // A crash between the two moves leaves the old data authoritative: the restore never completed, so nothing may act as though it had.
        public static void FinishInterruptedRestore()
        {
            string dataRoot = KmhDataPaths.Folder;
            string staging  = dataRoot + ".restoring";
            string previous = dataRoot + ".previous";
            try
            {
                if (!Directory.Exists(dataRoot) && Directory.Exists(previous))
                {
                    Directory.Move(previous, dataRoot);
                    ServerLog.Warn("Restore: a previous restore was interrupted mid-swap - the data from before it " +
                                   "has been put back. Nothing was mixed; run the restore again if you still want it.");
                }
                SafeDeleteDir(staging);
                SafeDeleteDir(previous);
            }
            catch (Exception ex) { ServerLog.Error($"Restore: could not finish an interrupted restore: {ex.Message}"); }
        }

        // Byte-for-byte against the manifest; older manifests carry no hash column and can only be size-checked.
        public static bool Verify(string backupDir, out string error)
        {
            error = null;
            string manifestPath = Path.Combine(backupDir, "manifest.txt");
            string[] lines;
            try { lines = File.ReadAllLines(manifestPath); }
            catch (Exception ex) { error = $"could not read the manifest: {ex.Message}"; return false; }

            var problems = new List<string>();
            int checkedFiles = 0, hashed = 0;
            foreach (string line in lines)
            {
                if (line.Length == 0 || line.IndexOf('\t') < 0) continue;   // header block
                string[] parts = line.Split('\t');
                string rel = parts[0];
                string full = Path.Combine(backupDir, rel);
                if (!File.Exists(full)) { problems.Add($"{rel} (missing)"); continue; }
                checkedFiles++;

                if (parts.Length >= 2 && long.TryParse(parts[1], out long len) && SafeLen(full) != len)
                { problems.Add($"{rel} (size {SafeLen(full)}, manifest says {len})"); continue; }

                if (parts.Length >= 3 && parts[2].Length > 0)
                {
                    hashed++;
                    if (!string.Equals(Sha256(full), parts[2], StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{rel} (contents changed)");
                }
                if (problems.Count >= 8) break;   // enough to act on; the rest would just scroll
            }

            if (problems.Count > 0)
            {
                error = $"'{Path.GetFileName(backupDir)}' does not match its manifest and was NOT restored: "
                      + string.Join(", ", problems);
                return false;
            }
            ServerLog.Verbose($"Restore: verified {checkedFiles} file(s) against the manifest ({hashed} by hash).");
            return true;
        }

        private static string Sha256(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        private static void SafeDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        private static void MoveInto(string entry, string staging, string dataRoot)
        {
            try
            {
                string rel = GetRelative(dataRoot, entry);
                string to  = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (Directory.Exists(entry)) Directory.Move(entry, to);
                else File.Move(entry, to);
            }
            catch (Exception ex) { ServerLog.Warn($"Restore: could not carry '{entry}' across the swap: {ex.Message}"); }
        }

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

        // The same rule the copy uses, exposed so a test can ask about a path without writing a backup.
        internal static bool WouldBackUp(string path) => KeepInBackup(path);

        private static bool KeepInBackup(string path)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(".kmh-selftest.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(".kmh-snapshot-request", StringComparison.OrdinalIgnoreCase)) return false;   // transient marker
            if (IsSnapshotArtifact(path)) return false;   // Snapshots/ is an archive, not live state - keep backups lean
            if (IsUnder(path, "Debug")) return false;     // client debug logs are transient diagnostics
            // Regenerable and wiped at boot anyway, so copying it adds up to CacheMaxMegabytes to every backup.
            if (IsUnder(path, "MediaCache")) return false;
            if (IsUnder(path, "Support")) return false;   // a redacted copy of what this backup already holds
            return true;
        }

        // Snapshots/ is a recovery archive: never backed up (bloat), never deleted by a rollback.
        private static bool IsSnapshotArtifact(string path) => IsUnder(path, "Snapshots");

        // Omitted from backups on purpose, so a rollback carries them across untouched - losing the Debug log destroys the record of the rollback.
        internal static bool SurvivesRestore(string path)
            => IsSnapshotArtifact(path) || IsUnder(path, "Debug")
            || IsUnder(path, "Support") || IsUnder(path, "MediaCache");

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
