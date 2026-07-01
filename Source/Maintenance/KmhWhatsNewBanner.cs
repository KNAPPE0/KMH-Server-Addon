using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Shown once after an update. Main.cs gates this by the saved build stamp.
    internal static class KmhWhatsNewBanner
    {
        private static readonly string[] Highlights =
        {
            "v1.1.1 is a hotfix for packaging, startup safety, and the experimental KMH API transport.",
            "",
            "Packaging / startup:",
            "  - Fixed the release package so required dependencies like Newtonsoft.Json.dll are included properly.",
            "  - Improved startup checks so missing dependencies are reported more clearly instead of looking like a KMH-Data permission issue.",
            "",
            "Transport security:",
            "  - The experimental KMH API transport is now locked down by default.",
            "  - It binds to 127.0.0.1 by default, not 0.0.0.0. Only expose it publicly if you mean to.",
            "  - Auth is required by default.",
            "  - Added connection caps, per-IP caps, idle/frame limits, and failed-auth throttling to reduce flood/DoS risk.",
            "  - Added boot warnings for risky configs, like public bind or auth disabled.",
            "  - IPs stay out of normal logs unless transport debug logging is enabled.",
            "",
            "Fixes / improvements:",
            "  - Fixed a reconnect race where clients could silently fall back to chat-only transport after reconnecting.",
            "  - Added 'kmh transport-test' for a safe, non-mutating check of the transport guards and config.",
            "  - Added owner feature switches in Config/Features.json to turn major KMH systems on/off.",
            "  - Added 'kmh treasury-reset <user|all>' to clear farmed treasury from save-reset abuse (backs up first).",
            "  - Discord bot commands now only respond in their configured channels, not random channels like #general.",
            "  - Players can now create guilds and start Discord linking from the client.",
            "  - Server now handles GuildCreate requests and one-time Discord link codes.",
        };

        public static void Print(string fromBuild)
        {
            string toBuild = KmhProtocol.BuildVersion;

            ServerLog.Info("======================================================================");
            ServerLog.Success(string.IsNullOrWhiteSpace(fromBuild)
                ? $"  KMH Server Addon updated to {toBuild}. What's new:"
                : $"  KMH Server Addon updated from {fromBuild} to {toBuild}. What's new:");
            ServerLog.Info("----------------------------------------------------------------------");

            foreach (string line in Highlights)
            {
                ServerLog.Info(string.IsNullOrEmpty(line) ? "" : "  " + line);
            }

            ServerLog.Info("----------------------------------------------------------------------");
            ServerLog.Info("  Full notes: https://github.com/KNAPPE0/KMH-Server-Addon");
            ServerLog.Info("  This message only shows once per update.");
            ServerLog.Info("======================================================================");
        }
    }
}