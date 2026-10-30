using System;
using System.IO;
using System.Text.RegularExpressions;

namespace KMHServerAddon.Diagnostics
{
    // KMH binds RWT's own net45 Newtonsoft, which on .NET 8 pulls System.Security.Permissions at runtime - an
    // assembly KMH never references and must not ship. Drop it and every JSON write throws as "not writable".
    internal static class DependencyPreflight
    {
        // Named from the exception, never guessed, or the owner is sent to confirm a file that is already there.
        internal static string MissingAssembly(Exception ex)
        {
            for (Exception at = ex; at != null; at = at.InnerException)
            {
                if (at is FileNotFoundException f && !string.IsNullOrEmpty(f.FileName)) return SimpleName(f.FileName);
                if (at is FileLoadException l && !string.IsNullOrEmpty(l.FileName)) return SimpleName(l.FileName);

                Match m = Regex.Match(at.Message ?? "", @"'([A-Za-z0-9_.]+),\s*Version=");
                if (m.Success) return m.Groups[1].Value;
            }
            return "";
        }

        private static string SimpleName(string fullName)
        {
            int comma = fullName.IndexOf(',');
            string name = comma < 0 ? fullName : fullName.Substring(0, comma);
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        // A round-trip, not a load: a reference that resolves and then fails on first use is exactly this bug.
        public static bool Check(out string detail)
        {
            try
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(new { ok = true, n = 1 });
                Newtonsoft.Json.Linq.JObject.Parse(json);
                detail = "Newtonsoft.Json round-trips";
                return true;
            }
            catch (Exception ex)
            {
                detail = Describe(ex);
                return false;
            }
        }

        internal static string Describe(Exception ex)
        {
            string missing = MissingAssembly(ex);
            if (string.IsNullOrEmpty(missing))
                return $"JSON serialization failed at boot ({ex.GetType().Name}: {ex.Message}). Colony Nexus cannot save anything "
                     + "until this is resolved.";

            string where = missing.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase)
                ? "Extract the whole Colony Nexus package so Newtonsoft.Json.dll sits beside KMHServerAddon.exe."
                : $"{missing}.dll ships with the RimWorld Together server, not with Colony Nexus. Copy it from an official RWT "
                + "server download into this folder - a hand-assembled server folder is the usual way it goes missing.";

            return $"{missing} could not be loaded, so nothing can be serialized. {where}";
        }
    }
}
