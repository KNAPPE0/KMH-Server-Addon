using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Discord
{
    // Embedded so an in-place upgrade, which replaces only the exe, still gets icons only the release zip would carry.
    internal static class DiscordIconAssets
    {
        private const string Prefix = "KMH.Icons.";

        internal static IEnumerable<string> Embedded()
        {
            foreach (string name in typeof(DiscordIconAssets).Assembly.GetManifestResourceNames())
                if (name.StartsWith(Prefix, StringComparison.Ordinal)) yield return name.Substring(Prefix.Length);
        }

        // The embedded copy, not whatever is on disk: this is what a fresh install and a repair both get.
        internal static byte[] Read(string file)
        {
            using (Stream src = typeof(DiscordIconAssets).Assembly.GetManifestResourceStream(Prefix + file))
            {
                if (src == null) return null;
                using (var ms = new MemoryStream())
                {
                    src.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        // SHA-256 prefixes (16 hex) of stock logos KMH shipped and later retired, so an untouched copy can be replaced.
        internal static readonly string[] RetiredLogos = { "e45c808f784e5f69", "4fc815aa2f9f8994" };

        internal static string ShortHash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant().Substring(0, 16);
        }

        // An untouched copy of a retired stock logo; anything else on disk is the owner's own art.
        internal static bool IsRetiredLogo(string file, string path)
        {
            if (!string.Equals(file, DiscordIcons.Logo + ".png", StringComparison.OrdinalIgnoreCase)) return false;
            try { return Array.IndexOf(RetiredLogos, ShortHash(File.ReadAllBytes(path))) >= 0; }
            catch { return false; }
        }

        public static int Provision() => Provision(KmhDataPaths.IconsDir, IsRetiredLogo);

        internal static int Provision(string dir, Func<string, string, bool> retired)
        {
            int written = 0, upgraded = 0, failed = 0;
            try
            {
                Assembly asm = typeof(DiscordIconAssets).Assembly;
                Directory.CreateDirectory(dir);
                foreach (string file in Embedded())
                {
                    string target = Path.Combine(dir, file);
                    // Never overwrite the owner's art: the folder is editable. Only a stock logo KMH retired is replaced.
                    bool upgrade = File.Exists(target);
                    if (upgrade && !retired(file, target)) continue;
                    try
                    {
                        using (Stream src = asm.GetManifestResourceStream(Prefix + file))
                        {
                            if (src == null) continue;
                            string tmp = target + ".tmp";
                            using (FileStream dst = File.Create(tmp)) src.CopyTo(dst);
                            File.Move(tmp, target, overwrite: true);
                        }
                        if (upgrade) upgraded++; else written++;
                    }
                    catch (Exception ex) { failed++; ServerLog.Verbose($"Discord: could not write icon {file}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { ServerLog.Warn($"Discord: icon provisioning skipped: {ex.Message}"); return 0; }

            // Every icon missing is a first install, not a repair worth an owner's attention.
            if (written > 0 && written + failed == Embedded().Count())
                ServerLog.Diag($"Discord: installed the {written} default embed icons into KMH-Data/Icons.");
            else if (written > 0)
                ServerLog.Info($"Discord: restored {written} missing embed icon(s) into KMH-Data/Icons (your own replacements were left alone).");
            if (upgraded > 0)
                ServerLog.Info("Discord: replaced the retired KMH logo in KMH-Data/Icons with the Colony Nexus logo.");
            if (failed > 0)
                ServerLog.Warn($"Discord: {failed} embed icon(s) could not be written - embeds fall back to no thumbnail.");
            return written + upgraded;
        }

        // Per file, not per folder: the folder exists on every booted server whether or not it holds any art.
        public static List<string> MissingStockIcons()
        {
            var missing = new List<string>();
            try
            {
                foreach (string file in Embedded())
                    if (!File.Exists(Path.Combine(KmhDataPaths.IconsDir, file))) missing.Add(file);
            }
            catch { }
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            return missing;
        }
    }
}
