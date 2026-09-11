using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KMHServerAddon.Maintenance
{
    // Answered against a real archive, because the redactor passing in isolation proves nothing about the bundle.
    internal static class KmhSupportBundleSelfTest
    {
        private const string FakeToken = "MTIzNDU2Nzg5MDEyMzQ1Njc4.GhIjKl.fake-token-for-the-self-test-only";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            string made = null, planted = null, savedName = null;
            var replies = new List<string>();
            try
            {
                KmhRedact.RegisterSecret(FakeToken);

                // Planted in both sources, or "nothing leaked" describes an archive that never had anything to leak.
                planted = PlantConfig();
                int plantedBundles = PlantOldBundles(7);
                savedName = MaintenanceConfig.Current?.ServerName;
                if (MaintenanceConfig.Current != null) MaintenanceConfig.Current.ServerName = "srv-" + FakeToken;

                bool ok = KmhSupportBundle.Create(replies.Add);
                r.Add(("support bundle: it builds", ok, string.Join(" | ", replies)));
                if (!ok) return r;

                foreach (string line in replies)
                {
                    int at = line.IndexOf(".zip", StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    int start = line.IndexOf(": ", StringComparison.Ordinal);
                    if (start < 0) continue;
                    made = line.Substring(start + 2, at + 4 - start - 2).Trim();
                    break;
                }
                if (made == null || !File.Exists(made))
                {
                    r.Add(("support bundle: the reply names the file it wrote", false, made ?? "(not named)"));
                    return r;
                }
                r.Add(("support bundle: the reply names the file it wrote", true, Path.GetFileName(made)));

                var entries = new List<string>();
                var body = new StringBuilder();
                using (ZipArchive zip = ZipFile.OpenRead(made))
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        entries.Add(e.FullName);
                        using StreamReader sr = new StreamReader(e.Open());
                        body.Append(sr.ReadToEnd()).Append('\n');
                    }
                string all = body.ToString();

                r.Add(("support bundle: it carries the identity and feature state a report needs",
                       entries.Contains("README.txt") && entries.Contains("identity.txt")
                       && entries.Contains("features.txt") && entries.Contains("economy.txt"),
                       string.Join(", ", entries)));

                // A binary, a backup or a save would make the bundle unshareable in size and in content.
                bool onlyText = true;
                foreach (string e in entries)
                    if (e.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || e.StartsWith("KMH-Data-Backups", StringComparison.OrdinalIgnoreCase)) onlyText = false;
                r.Add(("support bundle: no binaries or backups are packed", onlyText, string.Join(", ", entries)));

                r.Add(("support bundle: a secret sitting in a config file does not survive into the archive",
                       planted != null && all.IndexOf(FakeToken, StringComparison.Ordinal) < 0
                       && all.IndexOf("fake-token-for-the-self-test-only", StringComparison.Ordinal) < 0,
                       planted == null ? "could not plant a config" : ""));
                r.Add(("support bundle: the planted config was actually packed, so the check had something to find",
                       all.Contains("kmh_support_bundle_selftest"), ""));
                r.Add(("support bundle: a secret pasted into the server name does not survive either",
                       !all.Contains("srv-" + FakeToken) && all.Contains(KmhRedact.Mask), ""));

                r.Add(("support bundle: the README says what was removed and what was left out",
                       all.Contains(KmhRedact.Mask) || all.Contains("redaction"), ""));

                r.Add(("support bundle: the effective economy policy is reported, not the raw fields",
                       all.Contains("EconomyMode"), ""));

                // A bundle per problem report accumulates, and each one is a copy of things the backup already holds.
                r.Add(("support bundle: writing one prunes the older ones, so they cannot pile up",
                       plantedBundles == 7 && BundleCount() <= 5,
                       $"planted {plantedBundles}, {BundleCount()} left"));
                r.Add(("support bundle: the bundle just written is the one kept",
                       File.Exists(made), Path.GetFileName(made)));

                // Both folders are regenerable, and the media cache alone can reach CacheMaxMegabytes.
                r.Add(("support bundle: backups skip the media cache and the bundle folder",
                       !Persistence.KmhDataBackup.WouldBackUp(Path.Combine(Persistence.KmhDataPaths.MediaCacheDir, "x.bin"))
                       && !Persistence.KmhDataBackup.WouldBackUp(Path.Combine(OutputDirOf(), "kmh-support-x.zip")),
                       ""));
                r.Add(("support bundle: a real data file is still backed up, so the exclusion is not blanket",
                       Persistence.KmhDataBackup.WouldBackUp(Persistence.KmhDataPaths.TreasuryFile), ""));
            }
            catch (Exception ex)
            {
                r.Add(("support bundle: it builds", false, ex.GetType().Name + ": " + ex.Message));
            }
            finally
            {
                if (MaintenanceConfig.Current != null) MaintenanceConfig.Current.ServerName = savedName;
                KmhRedact.ClearSecrets();
                try { if (made != null && File.Exists(made)) File.Delete(made); } catch { }
                try { if (planted != null && File.Exists(planted)) File.Delete(planted); } catch { }
                try
                {
                    foreach (string f in Directory.GetFiles(KmhSupportBundle.OutputDir, "kmh-support-selftest-*.zip"))
                        File.Delete(f);
                }
                catch { }
            }

            return r;
        }

        private static string OutputDirOf() => KmhSupportBundle.OutputDir;

        // Older than anything Create will write, so the prune has an unambiguous newest to keep.
        private static int PlantOldBundles(int count)
        {
            int made = 0;
            try
            {
                Directory.CreateDirectory(KmhSupportBundle.OutputDir);
                for (int i = 0; i < count; i++)
                {
                    string f = Path.Combine(KmhSupportBundle.OutputDir, $"kmh-support-selftest-{i:00}.zip");
                    File.WriteAllBytes(f, new byte[] { 0x50, 0x4B, 0x05, 0x06 });
                    File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-(i + 1)));
                    made++;
                }
            }
            catch { }
            return made;
        }

        private static int BundleCount()
        {
            try { return Directory.GetFiles(KmhSupportBundle.OutputDir, "kmh-support-*.zip").Length; }
            catch { return 0; }
        }

        // A throwaway config carrying a token-named field, so the redaction is measured on a real archive entry.
        private static string PlantConfig()
        {
            try
            {
                string dir = Path.Combine(Persistence.KmhDataPaths.Folder, "Config");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "kmh_support_bundle_selftest.json");
                File.WriteAllText(file, "{\n  \"Marker\": \"kmh_support_bundle_selftest\",\n  \"BotToken\": \"" + FakeToken + "\"\n}");
                return file;
            }
            catch { return null; }
        }
    }
}
