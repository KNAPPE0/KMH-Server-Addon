using System;
using System.Collections.Generic;
using System.IO;

namespace KMHServerAddon.Features.Enforcement
{
    // Mirrors the client's ConfigManifest; re-applied to every received path since a modified client is the threat.
    internal static class ManagedPath
    {
        internal static readonly HashSet<string> ManagedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xml", ".json" };

        internal static readonly HashSet<string> PersonalFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "KeyPrefs.xml", "Prefs.xml", "Resolution.xml", "UILayout.xml", "Knowledge.xml",
                "LastPlayedVersion.txt", "ModsConfig.xml", "backup_ModsConfig.xml",
                "Docked.xml", "Floating.xml",
                "KMH_ACTIVE_PROFILE.txt", "KMH_ENFORCE_APPLYING",
            };

        internal static readonly string[] PersonalPrefixes = { "backup_", "KMH_" };
        internal static readonly string[] PersonalSuffixes =
        {
            "Cache.xml", "Cache.json", "_KMHPatchMod.xml", "_ModConfigSetter.xml",
        };

        // Flat by construction: separators, drives and traversal segments are refused, so traversal is impossible.
        public static bool IsManagedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
            if (name.IndexOf(':') >= 0 || name == "." || name == "..") return false;
            // Windows' list on every host, so a Linux server refuses what no Windows player could write.
            if (name.IndexOfAny(InvalidNameChars) >= 0 || IsDeviceName(name)) return false;

            if (!ManagedExtensions.Contains(Path.GetExtension(name))) return false;
            if (PersonalFiles.Contains(name)) return false;
            foreach (string p in PersonalPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string s in PersonalSuffixes)
                if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static readonly char[] InvalidNameChars = BuildInvalidNameChars();

        private static char[] BuildInvalidNameChars()
        {
            var list = new List<char> { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            for (char c = '\0'; c < ' '; c++) list.Add(c);
            return list.ToArray();
        }

        // "COM1.xml" or "NUL.json" opens a device on Windows, in any folder.
        internal static bool IsDeviceName(string name)
        {
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && ((stem[3] >= '1' && stem[3] <= '9') || stem[3] == '\u00b9' || stem[3] == '\u00b2' || stem[3] == '\u00b3');
        }
    }
}
