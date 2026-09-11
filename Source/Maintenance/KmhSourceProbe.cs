using System;
using System.IO;

namespace KMHServerAddon.Maintenance
{
    // Null when no source tree ships beside the build, so a guard reports "not asked" instead of failing a player's server.
    internal static class KmhSourceProbe
    {
        public static string Read(string relative)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    string candidate = Path.Combine(dir, "Source", relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return File.ReadAllText(candidate);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }
    }
}
