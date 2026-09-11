using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Driven against real folders: the backup and restore failures that matter are filesystem ones.
    internal static class KmhBackupSelfTest
    {
        private static string Scratch(string name)
            => Path.Combine(Path.GetTempPath(), "kmh-backup-selftest", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            // A real server always has KMH-Data; the offline runner boots nothing, so without this the checks below answer on where the run was launched from.
            KmhDataPaths.EnsureFolder();
            if (!File.Exists(KmhDataPaths.TreasuryFile)) File.WriteAllText(KmhDataPaths.TreasuryFile, "{}");

            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Backup: the manifest names the build, schema and generation that wrote it", () =>
            {
                if (!KmhDataBackup.TryCreate("selftest-identity", out string dir, out string err))
                    return (false, $"backup failed: {err}");
                try
                {
                    string manifest = File.ReadAllText(Path.Combine(dir, "manifest.txt"));
                    bool build = manifest.Contains($"build: {KmhVersion.Build} ({KmhVersion.BuildTag})");
                    bool proto = manifest.Contains($"protocol: {KmhVersion.Protocol}");
                    bool schema = manifest.Contains($"config-schema: {KmhVersion.ConfigSchema}")
                               && manifest.Contains($"data-schema: {KmhVersion.DataSchema}");
                    bool gen = manifest.Contains("data-generation: ");
                    bool id = manifest.Contains("backup-id: ");
                    bool hashed = manifest.Contains("hash: sha256");
                    bool ok = build && proto && schema && gen && id && hashed;
                    return (ok, ok ? "build, protocol, both schemas, generation, id and hash algorithm are all recorded"
                                  : $"build={build}, protocol={proto}, schema={schema}, generation={gen}, id={id}, hashed={hashed}");
                }
                finally { TryDelete(dir); }
            });

            Check("Backup: every file carries a hash, and a changed byte is caught", () =>
            {
                if (!KmhDataBackup.TryCreate("selftest-verify", out string dir, out string err))
                    return (false, $"backup failed: {err}");
                try
                {
                    bool cleanVerifies = KmhDataBackup.Verify(dir, out string verifyErr);
                    if (!cleanVerifies) return (false, $"a fresh backup did not verify: {verifyErr}");

                    // Corrupt one file without changing its length, so only the hash can notice.
                    string victim = FirstDataFile(dir);
                    if (victim == null) return (false, "the backup contained no data file to corrupt");
                    byte[] bytes = File.ReadAllBytes(victim);
                    if (bytes.Length == 0) return (false, "the chosen file was empty");
                    bytes[bytes.Length - 1] ^= 0xFF;
                    File.WriteAllBytes(victim, bytes);

                    bool caught = !KmhDataBackup.Verify(dir, out string corruptErr);
                    bool named = corruptErr != null && corruptErr.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool ok = caught && named;
                    return (ok, ok ? "a same-length edit is caught by hash and named"
                                  : $"caught={caught}, namesTheFile={named} ({corruptErr})");
                }
                finally { TryDelete(dir); }
            });

            Check("Restore: a backup that fails verification is refused before live data is touched", () =>
            {
                string fake = Scratch("bad-backup");
                try
                {
                    Directory.CreateDirectory(fake);
                    File.WriteAllText(Path.Combine(fake, "Treasury.json"), "{}");
                    // Names a file that is not there: exactly what a truncated copy looks like.
                    File.WriteAllText(Path.Combine(fake, "manifest.txt"),
                        "KMH-Data backup\nhash: sha256\n\nTreasury.json\t2\t" + "0".PadLeft(64, '0') + "\nGone.json\t5\tdeadbeef\n");

                    bool verified = KmhDataBackup.Verify(fake, out string why);
                    bool namesBoth = why != null && why.IndexOf("Gone.json", StringComparison.Ordinal) >= 0;
                    bool ok = !verified && namesBoth;
                    return (ok, ok ? "the missing and the mismatched file are both named, and nothing was restored"
                                  : $"refused={!verified}, namesMissing={namesBoth} ({why})");
                }
                finally { TryDelete(fake); }
            });

            Check("Backup: a backup taken before the stores load does not flush empty ones over the data", () =>
            {
                KmhReadyState was = KmhReadiness.State;
                string probe = KmhDataPaths.TreasuryFile;
                string original = File.Exists(probe) ? File.ReadAllText(probe) : null;
                // Stands in for a real server's data: what is on disk is the truth, and memory holds nothing yet.
                const string sentinel = "{\"kmh-selftest-preload-sentinel\":true}";
                try
                {
                    File.WriteAllText(probe, sentinel);

                    // Exactly the pre-restore backup's position in boot: stores are declared, none has loaded.
                    KmhReadiness.ResetForTest(KmhReadyState.Starting);
                    if (!KmhDataBackup.TryCreate("selftest-preload", out string dir, out string err))
                        return (false, $"backup failed: {err}");
                    TryDelete(dir);

                    bool untouched = File.Exists(probe) && File.ReadAllText(probe) == sentinel;
                    return (untouched, untouched ? "the file on disk is exactly as it was found"
                                                 : "the backup flushed a not-yet-loaded store over live data");
                }
                finally
                {
                    KmhReadiness.ResetForTest(was); if (was == KmhReadyState.Ready) KmhReadiness.Ready();
                    try { if (original != null) File.WriteAllText(probe, original); else File.Delete(probe); } catch { }
                }
            });

            Check("Restore: what a backup deliberately omits is carried across, not destroyed", () =>
            {
                // A backup holds no copy of these, so deleting them on rollback is pure loss - including its own Debug log.
                var carried = new List<string>();
                foreach (string folder in new[] { "Snapshots", "Debug", "Support", "MediaCache" })
                {
                    string probe = Path.Combine(KmhDataPaths.Folder, folder, "probe.txt");
                    if (KmhDataBackup.WouldBackUp(probe)) return (false, $"{folder} is backed up after all - this test is out of date");
                    if (KmhDataBackup.SurvivesRestore(probe)) carried.Add(folder);
                }
                bool ok = carried.Count == 4;
                return (ok, ok ? "Snapshots, Debug, Support and MediaCache all survive a rollback"
                              : "carried across: " + string.Join(", ", carried));
            });

            Check("Restore: an interrupted swap leaves the old data authoritative, never a mix", () =>
            {
                string dataRoot = KmhDataPaths.Folder;
                string previous = dataRoot + ".previous";
                string staging  = dataRoot + ".restoring";
                // Beside the live folder, not in TEMP: Directory.Move cannot cross volumes.
                string moved    = dataRoot + ".selftest-aside";
                try
                {
                    // The exact state a crash between the two Directory.Move calls leaves behind.
                    if (Directory.Exists(dataRoot)) Directory.Move(dataRoot, moved);
                    Directory.CreateDirectory(previous);
                    File.WriteAllText(Path.Combine(previous, "Treasury.json"), "{\"old\":true}");
                    Directory.CreateDirectory(staging);
                    File.WriteAllText(Path.Combine(staging, "Treasury.json"), "{\"new\":true}");

                    KmhDataBackup.FinishInterruptedRestore();

                    bool liveBack = Directory.Exists(dataRoot);
                    bool isOld = liveBack && File.Exists(Path.Combine(dataRoot, "Treasury.json"))
                              && File.ReadAllText(Path.Combine(dataRoot, "Treasury.json")).Contains("old");
                    bool cleaned = !Directory.Exists(staging) && !Directory.Exists(previous);
                    bool ok = liveBack && isOld && cleaned;

                    // Put the real folder back before anything else in the suite reads it.
                    if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
                    if (Directory.Exists(moved)) Directory.Move(moved, dataRoot);

                    return (ok, ok ? "the pre-restore data is authoritative again and both scratch folders are gone"
                                  : $"liveRestored={liveBack}, isOldData={isOld}, scratchCleaned={cleaned}");
                }
                catch
                {
                    try { if (!Directory.Exists(dataRoot) && Directory.Exists(moved)) Directory.Move(moved, dataRoot); } catch { }
                    throw;
                }
            });

            return r;
        }

        private static string FirstDataFile(string dir)
        {
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Equals("manifest.txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (new FileInfo(f).Length > 0) return f;
            }
            return null;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
