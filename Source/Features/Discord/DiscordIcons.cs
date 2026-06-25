using System;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Discord
{
    // Resolves Discord embed icons from KMH-Data/Icons - the single icons folder, shipped pre-filled and read
    // straight from there. Names are extension-less; the resolver probes raster formats (Discord can't show .dds),
    // null if none
    internal static class DiscordIcons
    {
        // Semantic icon names (no extension) - drop e.g. kmh_logo.png in the folder.
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

        // Formats Discord renders as an embed thumbnail, best first.
        private static readonly string[] Extensions = { ".png", ".webp", ".gif", ".jpg", ".jpeg" };

        // Path to an existing icon for the name (or the Logo fallback), or null. Honours UseBundledIcons
        public static string ResolveExistingPath(DiscordConfig cfg, string name)
        {
            if (cfg == null || !cfg.UseBundledIcons) return null;
            if (string.IsNullOrWhiteSpace(name)) name = Logo;

            try
            {
                string hit = Probe(KmhDataPaths.IconsDir, name);
                if (hit != null) return hit;

                // Missing-icon fallback: the logo, so embeds still get a thumbnail.
                if (!string.Equals(name, Logo, StringComparison.OrdinalIgnoreCase))
                    return Probe(KmhDataPaths.IconsDir, Logo);
            }
            catch (Exception ex) { ServerLog.Verbose($"Discord: icon resolve failed for '{name}': {ex.Message}"); }
            return null;
        }

        // First existing file for "<name><ext>" across the supported formats. Also accepts a name that already
        // carries its own extension (back-compat)
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
