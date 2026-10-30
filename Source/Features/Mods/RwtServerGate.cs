using System;
using System.IO;
using Newtonsoft.Json.Linq;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Mods
{
    // Only whether a password exists, never its value, because the answer goes to the directory and every client.
    internal static class RwtServerGate
    {
        private const string FileName = "PasswordConfig.json";

        private static string Path_ =>
            System.IO.Path.Combine(Master.MainPath ?? Directory.GetCurrentDirectory(), "Configs", FileName);

        // Re-read rather than cached: a stale "open" would advertise a join that RWT then refuses.
        public static bool RequiresPassword => Read(Path_);

        internal static bool Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                JToken root = JToken.Parse(File.ReadAllText(path));
                // A file RWT has blanked reads as no password, which matches what RWT itself would then enforce.
                string password = (string)root?["Password"];
                return !string.IsNullOrEmpty(password);
            }
            catch (Exception ex)
            {
                // Fail CLOSED: an unreadable file published as open would advertise a join every player is then refused.
                ServerLog.Verbose($"RWT: could not read {FileName} ({ex.GetType().Name}) - treating the server as password-protected.");
                return true;
            }
        }
    }
}
