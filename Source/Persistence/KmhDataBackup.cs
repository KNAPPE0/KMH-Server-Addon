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
        // Shortened only by the self-test, which holds a move open on purpose.
        internal static TimeSpan QuiesceWait = TimeSpan.FromSeconds(10);

        internal sealed class Outcome
        {
            public DateTime AtUtc;
            public bool     Ok;
            public string   Reason = "";
            public string   Detail = "";
        }

        private static Outcome _last;
        internal static Outcome LastOutcome => System.Threading.Volatile.Read(ref _last);
        private static void Note(Outcome o) => System.Threading.Volatile.Write(ref _last, o);
        internal static void SetLastOutcomeForTest(Outcome o) => Note(o);

        public static bool TryCreate(string reason, out string destDir, out string error)
        {
            bool ok = TryCreateCore(reason, out destDir, out error);
            // An empty KMH-Data is a new server with nothing to copy yet, not a failed backup.
            if (ok || (error ?? "").IndexOf("nothing to back up", StringComparison.OrdinalIgnoreCase) < 0)
                Note(new Outcome
                {
                    AtUtc = DateTime.UtcNow, Ok = ok, Reason = reason ?? "",
                    Detail = ok ? Path.GetFileName(destDir ?? "") : (error ?? ""),
                });
            return ok;
        }

        private static bool TryCreateCore(string reason, out string destDir, out string error)
        {
            destDir = null;
            error   = null;

            string source = KmhDataPaths.Folder;
            if (!Directory.Exists(source)) { error = "KMH-Data does not exist yet (nothing to back up)"; return false; }

            // Hold mutations and flush every store FIRST, or the copy mixes instants into a state that never existed.
            Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.Backup);
            try
            {
                // Before Ready the stores are EMPTY (boot's pre-restore backup); flushing would write nothing over everything.
                if (Maintenance.KmhReadiness.IsReady)
                {
                    // A move admitted before the gate closed (a request, job or Discord purchase) would be half in the copy.
                    if (!Maintenance.KmhValueOps.WaitForIdle(QuiesceWait))
                    {
                        error = $"{Maintenance.KmhValueOps.InFlight} value move(s) were still running after {QuiesceWait.TotalSeconds:0.#}s, " +
                                "so a copy now would not be a coherent point in time. Nothing was copied; try again.";
                        return false;
                    }
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
            try { sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => KeepInBackup(source, f)).ToArray(); }
            catch (Exception ex) { error = $"could not enumerate KMH-Data: {ex.Message}"; return false; }

            if (sourceFiles.Length == 0) { error = "KMH-Data is empty (nothing to back up)"; return false; }

            string dest;
            try { dest = ReserveDir(SafeReason(reason)); }
            catch (Exception ex) { error = $"could not reserve a backup folder: {ex.Message}"; return false; }
            try
            {
                long total = 0;
                int  count = 0;
                StringBuilder manifest = new StringBuilder();
                foreach (string file in sourceFiles)
                {
                    string rel = GetRelative(source, file);
                    string to  = Path.Combine(dest, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, overwrite: true);
                    // A copy takes the folder's access list, not the key's own.
                    if (Features.Global.KmhServerKey.IsKeyFile(to)) Features.Global.KmhServerKey.Restrict(to);
                    long len = SafeLen(file);
                    total += len; count++;
                    // Hashed from the COPY, so a read error during it surfaces as a mismatch at restore.
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
                File.WriteAllText(Path.Combine(dest, ManifestName), header + manifest);

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

        private static readonly HashSet<string> PinnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A backup something is currently relying on has to outrank retention, or creating the next one prunes it.
        public static void Pin(string backupName)
        {
            if (!string.IsNullOrEmpty(backupName)) lock (PinnedNames) PinnedNames.Add(backupName);
        }

        public static void Unpin(string backupName)
        {
            if (!string.IsNullOrEmpty(backupName)) lock (PinnedNames) PinnedNames.Remove(backupName);
        }

        // Also the backup queued for restore at the next boot: the boot backup taken before then must not prune it.
        internal static bool IsPinned(string backupName)
        {
            if (string.IsNullOrEmpty(backupName)) return false;
            lock (PinnedNames) if (PinnedNames.Contains(backupName)) return true;
            try
            {
                return File.Exists(KmhDataPaths.RestoreRequestFile)
                    && string.Equals(Path.GetFileName(File.ReadAllText(KmhDataPaths.RestoreRequestFile).Trim()), backupName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // Player-triggered, so kept in its own bucket: a flood must never push out boot, manual or pre-restore points.
        internal const string AutoResetReason = "pre-auto-economy-reset";

        internal static bool IsPlayerTriggered(string backupName)
            => !string.IsNullOrEmpty(backupName)
            && backupName.IndexOf("-" + AutoResetReason, StringComparison.OrdinalIgnoreCase) >= 0;

        // The timer's own copies, a bucket of their own for the same reason as the player-triggered ones.
        internal const string ScheduledReason = "scheduled";

        internal static bool IsScheduled(string backupName)
            => !string.IsNullOrEmpty(backupName)
            && backupName.IndexOf("-" + ScheduledReason, StringComparison.OrdinalIgnoreCase) >= 0;

        private static int BucketOf(string name) => IsPlayerTriggered(name) ? 1 : IsScheduled(name) ? 2 : 0;

        // Names start with a timestamp, so name order is chronological; each bucket keeps its newest `keep` (<= 0 keeps all).
        internal static List<string> PruneCandidates(IEnumerable<string> names, int keep)
        {
            var doomed = new List<string>();
            if (keep <= 0) return doomed;
            List<string> all = names.ToList();
            foreach (int bucket in new[] { 0, 1, 2 })
                foreach (string name in all.Where(n => BucketOf(n) == bucket)
                                            .OrderByDescending(n => n, StringComparer.Ordinal).Skip(keep))
                    if (!IsPinned(name)) doomed.Add(name);
            return doomed;
        }

        // Skipped on damaged data: copies of broken files would push the last good ones out of the bucket.
        internal static void ScheduledTick()
        {
            if (Maintenance.MaintenanceConfig.Current.BackupEveryHours <= 0) return;
            if (JsonFileStore.DamageReported(out string why)) { ServerLog.Warn($"Scheduled backup skipped - KMH-Data is damaged ({why})."); return; }
            Maintenance.KmhDataFlush.FlushAll();
            if (TryCreate(ScheduledReason, out string dir, out string err)) ServerLog.Diag($"Scheduled backup: {Path.GetFileName(dir)}");
            else ServerLog.Warn($"Scheduled backup failed: {err}");
        }

        // One full copy per window, server-wide: each copy holds the maintenance gate for its whole length.
        internal static readonly TimeSpan AutoResetBackupInterval = TimeSpan.FromMinutes(10);
        private static long _lastAutoResetBackupUtc;

        internal static bool AutoResetBackupDue(long nowUtcTicks)
        {
            long last = System.Threading.Interlocked.Read(ref _lastAutoResetBackupUtc);
            return last == 0 || nowUtcTicks - last >= AutoResetBackupInterval.Ticks;
        }

        internal static void NoteAutoResetBackup(long nowUtcTicks) => System.Threading.Interlocked.Exchange(ref _lastAutoResetBackupUtc, nowUtcTicks);

        internal static void ResetAutoResetCadenceForTest() => System.Threading.Interlocked.Exchange(ref _lastAutoResetBackupUtc, 0);

        public static int Prune(int keep)
        {
            if (keep <= 0) return 0;
            try
            {
                if (!Directory.Exists(KmhDataPaths.BackupRoot)) return 0;
                List<string> names = Directory.GetDirectories(KmhDataPaths.BackupRoot).Where(IsBackupDir)
                    .Select(Path.GetFileName).ToList();
                int removed = 0;
                foreach (string name in PruneCandidates(names, keep))
                {
                    try { Directory.Delete(Path.Combine(KmhDataPaths.BackupRoot, name), recursive: true); removed++; }
                    catch (Exception ex) { ServerLog.Warn($"Backup prune: could not delete {name}: {ex.Message}"); }
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

        // Set by a restore that landed this boot (before any store loads); consumed once the generation store is read.
        public static string RestoredThisBoot { get; private set; }

        // Only once the bump is written: the marker is what makes a boot that died before then bump again.
        internal static void ClearRestoredThisBoot()
        {
            RestoredThisBoot = null;
            try { File.Delete(Path.Combine(KmhDataPaths.Folder, RestoredMarkerName)); }
            catch (Exception ex) { ServerLog.Warn($"Restore: could not clear the restore marker: {ex.Message}"); }
        }

        // force skips only the pre-restore safety backup, and only when an admin asked for that in so many words.
        public static bool Restore(string spec, out string safetyBackup, out string error, bool force = false)
        {
            safetyBackup = null; error = null;
            string src = ResolvePath(spec);
            if (src == null) { error = $"no backup matches '{spec}'"; return false; }
            string manifestPath = ManifestPathFor(src);
            if (!File.Exists(manifestPath))
            { error = $"'{Path.GetFileName(src)}' has no manifest - refusing to restore from it"; return false; }
            // Only an OLD backup's manifest is called manifest.txt. In a new one that name is the owner's own file.
            bool legacyManifest = manifestPath.EndsWith(LegacyManifestName, StringComparison.OrdinalIgnoreCase);

            string[] backupFiles;
            try { backupFiles = Directory.GetFiles(src, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { error = $"could not read backup: {ex.Message}"; return false; }
            if (backupFiles.Length <= 1) { error = "backup has no data files - refusing to wipe live data"; return false; }

            // Checked BEFORE anything live is touched, while the current data is still the only copy that matters.
            if (!Verify(src, out string verifyError)) { error = verifyError; return false; }

            // Without this backup a restore from the wrong one is unrecoverable, so normal restores fail closed. The source
            // is pinned while it is read: the safety backup prunes to retention, which can remove the oldest, this one.
            string sourceName = Path.GetFileName(src);
            Pin(sourceName);
            try
            {
                AfterSourcePinnedForTest?.Invoke(sourceName);
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
                    // Staged aside: a crash before the swap leaves live data intact; FinishInterruptedRestore ends one mid-swap.
                    if (!CarryBack(staging, dataRoot))
                    {
                        error = $"'{staging}' still holds files a failed restore carried out of KMH-Data - move them back by hand first";
                        return false;
                    }
                    SafeDeleteDir(staging);
                    Directory.CreateDirectory(staging);
                    foreach (string file in backupFiles)
                    {
                        string rel = GetRelative(src, file);
                        if (rel.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;   // backup metadata
                        if (legacyManifest && rel.Equals(LegacyManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                        string to = Path.Combine(staging, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(file, to, overwrite: true);
                        if (Features.Global.KmhServerKey.IsKeyFile(to)) Features.Global.KmhServerKey.Restrict(to);
                    }
                    foreach (string live in KmhDataPaths.SanctionFiles)
                    {
                        if (!File.Exists(live)) continue;
                        string to = Path.Combine(staging, GetRelative(dataRoot, live));
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Copy(live, to, overwrite: true);
                    }
                    // Lands with the swap: a boot finding it knows the restore took effect and may still owe the generation bump.
                    File.WriteAllText(Path.Combine(staging, RestoredMarkerName), Path.GetFileName(src));

                    // A backup omits the archive and the Debug log (which records this restore), so they are carried across.
                    if (Directory.Exists(dataRoot))
                        foreach (string entry in Directory.GetFileSystemEntries(dataRoot))
                            if (SurvivesRestore(dataRoot, entry))
                                MoveInto(entry, staging, dataRoot);

                    SafeDeleteDir(previous);
                    if (Directory.Exists(dataRoot)) Directory.Move(dataRoot, previous);
                    Directory.Move(staging, dataRoot);
                    SafeDeleteDir(previous);

                    // Boot bumps the generation after EconomyReset.json is read; bumping here would overwrite the restored value.
                    RestoredThisBoot = Path.GetFileName(src);

                    ServerLog.Warn($"KMH-Data RESTORED from '{Path.GetFileName(src)}'" +
                                   (safetyBackup != null ? $" (previous data saved as '{safetyBackup}')" : "") +
                                   " - the live bans and economy freezes were kept.");
                    Extensibility.KmhEventBus.Instance.RaiseRestoreApplied(new KMH.Sdk.Server.Events.RestoreAppliedEvent
                    {
                        BackupName = Path.GetFileName(src), SafetyBackup = safetyBackup ?? "",
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    // The old data is the only authority left, so put it back; judged by content, as the log sink can
                    // recreate KMH-Data between the two moves.
                    bool back = !Directory.Exists(previous) || HoldsData(dataRoot) || MergeInto(previous, dataRoot);
                    if (!back) ServerLog.Error($"Restore: could not put all of the previous KMH-Data back - what is left is in '{previous}'.");
                    // The archive and logs were carried into staging for the swap, and no backup holds them.
                    if (CarryBack(staging, dataRoot)) SafeDeleteDir(staging);
                    if (back) SafeDeleteDir(previous);
                    return false;
                }
            }
            finally { Unpin(sourceName); }
        }

        internal const string RestoredMarkerName = ".kmh-restored-from";
        internal static Action<string> AfterSourcePinnedForTest;

        // Decided by folder contents, never by KMH-Data existing: the log sink and EnsureFolder recreate it first, and
        // reading that as live data would delete .previous (the only real copy) and the staged restore.
        public static void FinishInterruptedRestore()
        {
            string dataRoot = KmhDataPaths.Folder;
            string staging  = dataRoot + ".restoring";
            string previous = dataRoot + ".previous";
            try
            {
                bool keepPrevious = false;
                if (Directory.Exists(previous) && !HoldsData(dataRoot))
                {
                    // Died between the two moves: .previous is the live data and KMH-Data only this boot's scaffolding.
                    keepPrevious = !MergeInto(previous, dataRoot);
                    if (keepPrevious)
                        ServerLog.Error($"Restore: an interrupted restore left the live data in '{previous}' and not all of it " +
                                        "could be moved back - stop the server and move what is left into KMH-Data by hand.");
                    else
                        ServerLog.Warn("Restore: a previous restore was interrupted mid-swap - the data from before it " +
                                       "has been put back. Nothing was mixed; run the restore again if you still want it.");
                }
                // Otherwise KMH-Data holds real data: the swap landed (.previous is what it replaced) or .previous is a leftover.
                bool keepStaging = !CarryBack(staging, dataRoot);
                if (keepStaging)
                    ServerLog.Error($"Restore: '{staging}' still holds logs or snapshots that could not be moved back into KMH-Data - it was kept.");
                else SafeDeleteDir(staging);
                if (!keepPrevious) SafeDeleteDir(previous);

                string landed = Path.Combine(dataRoot, RestoredMarkerName);
                if (string.IsNullOrEmpty(RestoredThisBoot) && File.Exists(landed))
                {
                    string from = File.ReadAllText(landed).Trim();
                    RestoredThisBoot = from.Length > 0 ? from : "an interrupted restore";
                    ServerLog.Warn($"Restore: the restore from '{RestoredThisBoot}' took effect, but the boot that applied it " +
                                   "stopped before finishing - finishing it now.");
                }
            }
            catch (Exception ex) { ServerLog.Error($"Restore: could not finish an interrupted restore: {ex.Message}"); }
        }

        // Anything beyond what a boot creates before this runs: the folder scaffolding, the Debug log and the write probe.
        internal static bool HoldsData(string dataRoot)
        {
            if (!Directory.Exists(dataRoot)) return false;
            foreach (string f in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
            {
                if (SurvivesRestore(dataRoot, f)) continue;
                if (Path.GetFileName(f).StartsWith(".kmh-selftest", StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        // Moves everything under src into dst, src's copy winning a clash; true when nothing was left behind.
        private static bool MergeInto(string src, string dst)
        {
            if (!Directory.Exists(src)) return true;
            if (!Directory.Exists(dst))
                try { Directory.Move(src, dst); return true; }
                catch (Exception) { Directory.CreateDirectory(dst); }
            bool all = true;
            foreach (string entry in Directory.GetFileSystemEntries(src))
            {
                string to = Path.Combine(dst, Path.GetFileName(entry));
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (Directory.Exists(to)) { all &= MergeInto(entry, to); continue; }
                        Directory.Move(entry, to);
                    }
                    else File.Move(entry, to, overwrite: true);
                }
                catch (Exception ex) { all = false; ServerLog.Error($"Restore: could not move '{entry}' back: {ex.Message}"); }
            }
            if (all) SafeDeleteDir(src);
            return all;
        }

        // The archive and logs ride in staging during a restore and are in no backup, so they always go back to KMH-Data.
        private static bool CarryBack(string staging, string dataRoot)
        {
            if (!Directory.Exists(staging)) return true;
            bool all = true;
            foreach (string entry in Directory.GetFileSystemEntries(staging))
                if (SurvivesRestore(staging, entry) && Directory.Exists(entry))
                    all &= MergeInto(entry, Path.Combine(dataRoot, Path.GetFileName(entry)));
            return all;
        }

        private static Func<string, string> _failVerify;

        // A checksum failure cannot be produced on demand, so the migration's no-verified-backup refusal is tested via this.
        internal static IDisposable FailVerifyForTest(Func<string, string> reason)
        {
            _failVerify = reason;
            return new RestoreVerify();
        }

        private sealed class RestoreVerify : IDisposable { public void Dispose() { _failVerify = null; } }

        // Byte-for-byte against the manifest; older manifests carry no hash column and can only be size-checked.
        public static bool Verify(string backupDir, out string error)
        {
            error = _failVerify?.Invoke(backupDir);
            if (error != null) return false;
            string manifestPath = ManifestPathFor(backupDir);
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
                foreach (string dir in Directory.GetDirectories(KmhDataPaths.BackupRoot).Where(IsBackupDir)
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

        // A name KMH-Data cannot also use: a live file called manifest.txt overwrites a manifest of that name, and the
        // backup then fails its own verification for ever.
        internal const string ManifestName = ".kmh-backup-manifest.txt";
        private const string LegacyManifestName = "manifest.txt";

        // Older backups carry the legacy name and must stay readable and restorable.
        private static string ManifestPathFor(string backupDir)
        {
            string current = Path.Combine(backupDir, ManifestName);
            return File.Exists(current) ? current : Path.Combine(backupDir, LegacyManifestName);
        }

        internal static bool WouldBackUp(string path) => KeepInBackup(KmhDataPaths.Folder, path);
        internal static bool WouldBackUp(string root, string path) => KeepInBackup(root, path);

        private static bool KeepInBackup(string root, string path)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) return false;   // never nest one
            if (name.Equals(".kmh-selftest.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals(".kmh-snapshot-request", StringComparison.OrdinalIgnoreCase)) return false;   // transient marker
            if (IsSnapshotArtifact(root, path)) return false;   // Snapshots/ is an archive, not live state - keep backups lean
            if (IsUnder(root, path, "Debug")) return false;     // client debug logs are transient diagnostics
            // Regenerable and wiped at boot anyway, so copying it adds up to CacheMaxMegabytes to every backup.
            if (IsUnder(root, path, "MediaCache")) return false;
            if (IsUnder(root, path, "Support")) return false;   // a redacted copy of what this backup already holds
            return true;
        }

        // Snapshots/ is a recovery archive: never backed up (bloat), never deleted by a rollback.
        private static bool IsSnapshotArtifact(string root, string path) => IsUnder(root, path, "Snapshots");

        // Omitted from backups, so a rollback carries them across; losing the Debug log loses the rollback's own record.
        internal static bool SurvivesRestore(string path) => SurvivesRestore(KmhDataPaths.Folder, path);

        internal static bool SurvivesRestore(string root, string path)
            => IsSnapshotArtifact(root, path) || IsUnder(root, path, "Debug")
            || IsUnder(root, path, "Support") || IsUnder(root, path, "MediaCache");

        // Only the first folder below root counts, or a server installed under one named Debug filters out every file.
        private static bool IsUnder(string root, string path, string folder)
        {
            string rel = GetRelative(root, path);
            int sep = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            return string.Equals(sep < 0 ? rel : rel.Substring(0, sep), folder, StringComparison.OrdinalIgnoreCase);
        }

        // KMH-Data paths are always under `root`; anything else becomes its file name, never a "..\" path in a backup.
        private static string GetRelative(string root, string full)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : Path.GetFileName(full);
        }

        private static long SafeLen(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

        // Pruning a control folder as if it were a backup deletes the lock and shortens retention by one.
        private static bool IsBackupDir(string dir)
            => !Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal);

        // TryParseStamp reads the first 15 chars and Prune orders on the name, so uniqueness goes after the reason.
        private static string ReserveDir(string safeReason)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string claims = Path.Combine(KmhDataPaths.BackupRoot, ".claims");
            Directory.CreateDirectory(claims);
            for (int attempt = 0; attempt < 25; attempt++)
            {
                string name = attempt == 0
                    ? $"{stamp}-{safeReason}"
                    : $"{stamp}-{safeReason}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
                string candidate = Path.Combine(KmhDataPaths.BackupRoot, name);
                if (Directory.Exists(candidate)) continue;
                try
                {
                    // CreateNew is the lock; CreateDirectory succeeds on an existing folder and claims nothing.
                    using (new FileStream(Path.Combine(claims, name), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                }
                catch (IOException) { continue; }          // another caller holds this exact name
                catch (UnauthorizedAccessException) { }    // cannot claim here at all; fall through and try to create
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            throw new IOException($"no free backup folder for '{stamp}-{safeReason}' after 25 attempts");
        }

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
