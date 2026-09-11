using System;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Discord
{
    internal static class DiscordIcons
    {
        public const string Logo          = "kmh_logo";
        public const string Marketplace   = "marketplace";
        public const string Leaderboard   = "leaderboard";
        public const string SiteCreated   = "site_created";
        public const string SiteDestroyed = "site_destroyed";
        public const string Quest         = "quest";
        public const string Guild         = "guild";
        public const string Treasury      = "treasury";
        public const string Warning       = "warning";
        public const string Error         = "error";

        // Ordered best first, and deliberately without .dds, which Discord cannot render.
        private static readonly string[] Extensions = { ".png", ".webp", ".gif", ".jpg", ".jpeg" };

        public static string ResolveExistingPath(DiscordConfig cfg, string name)
        {
            if (cfg == null || !cfg.UseBundledIcons) return null;
            if (string.IsNullOrWhiteSpace(name)) name = Logo;

            try
            {
                string hit = Probe(KmhDataPaths.IconsDir, name);
                if (hit != null) return hit;

                // Falls back to the logo, so a missing icon still leaves the embed with a thumbnail.
                if (!string.Equals(name, Logo, StringComparison.OrdinalIgnoreCase))
                    return Probe(KmhDataPaths.IconsDir, Logo);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: icon resolve failed for '{name}': {ex.Message}"); }
            return null;
        }

        // A name arriving with its own extension is taken as-is, since older configs spelled icons out in full.
        private static string Probe(string root, string name)
        {
            if (Path.HasExtension(name))
            {
                string exact = Path.Combine(root, name);
                return File.Exists(exact) ? exact : null;
            }
            foreach (string ext in Extensions)
            {
                string p = Path.Combine(root, name + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
