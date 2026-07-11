using System;
using System.Text;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Discord;

namespace KMHServerAddon.Maintenance
{
    // Must-read owner notice after the defaults upgrade: console ~5s post-boot + Discord Admin channel if set.
    internal static class KmhOwnerNotice
    {
        public static void ScheduleIfNeeded()
        {
            if (!KmhDefaultsUpgrade.RanThisBoot) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    PrintConsole();
                    await TrySendDiscord().ConfigureAwait(false);
                }
                catch (Exception ex) { ServerLog.Verbose($"Owner notice failed: {ex.Message}"); }
            });
        }

        private static void PrintConsole()
        {
            ServerLog.Warn("================= KMH v1.2.0 UPDATE NOTICE - PLEASE READ =================");
            ServerLog.Warn("This update refreshed the recommended defaults on this server (one-time):");
            foreach (string c in KmhDefaultsUpgrade.Changed)   ServerLog.Warn($"  changed:   {c}");
            foreach (string p in KmhDefaultsUpgrade.Preserved) ServerLog.Warn($"  preserved: {p} (your custom value was kept)");
            if (KmhDefaultsUpgrade.Changed.Count == 0)
                ServerLog.Warn("  (all targeted settings were owner-customized - nothing was changed)");
            ServerLog.Warn("Living world is now ON by default: world events auto-roll and global quests");
            ServerLog.Warn("auto-generate so the server feels alive. Quieter server? In Config/World.json");
            ServerLog.Warn("set AutoRollEvents/AutoGenerateQuests to false (or Features.json LivingWorld).");
            ServerLog.Warn("KMH API transport is now enabled by default and public-ready: auth, connection");
            ServerLog.Warn("caps, failed-auth throttling, timeouts and frame limits are all required by");
            ServerLog.Warn("default - a public bind never means an unsecured bind. For remote players,");
            ServerLog.Warn($"forward TCP port {Features.Transport.TransportConfig.Current.KmhApiPort} and verify with 'kmh transport-test'.");
            ServerLog.Warn("Review KMH-Data/Config/ - your custom settings were preserved. See SETUP.txt.");
            ServerLog.Warn("===========================================================================");
        }

        // Post to the Admin channel if the bridge connects with one configured; wait up to ~60s for Ready.
        private static async Task TrySendDiscord()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null || !cfg.IsEnabled || cfg.AdminChannelId == 0) return;

            for (int i = 0; i < 60; i++)
            {
                if (DiscordBridge.Client?.ConnectionState == global::Discord.ConnectionState.Connected) break;
                await Task.Delay(1000).ConfigureAwait(false);
            }
            if (DiscordBridge.Client?.ConnectionState != global::Discord.ConnectionState.Connected) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("**v1.2.0 updated this server's recommended defaults** (one-time, custom settings preserved):");
            foreach (string c in KmhDefaultsUpgrade.Changed) sb.AppendLine($"• {c}");
            if (KmhDefaultsUpgrade.Changed.Count == 0) sb.AppendLine("• nothing changed - all targeted settings were owner-customized");
            sb.AppendLine();
            sb.AppendLine("🌍 **Living world is now on by default** - world events auto-roll and global quests auto-generate. " +
                          "Disable in `Config/World.json` for a quieter server.");
            sb.AppendLine("🔌 **KMH API transport is now on by default and public-ready** - auth, caps, throttling and limits " +
                          $"are required by default; forward TCP {Features.Transport.TransportConfig.Current.KmhApiPort} for remote players and run `kmh transport-test`.");
            sb.Append("Please review `KMH-Data/Config/` after this update.");

            DiscordBridge.PostEmbedToChannel(cfg.AdminChannelId,
                KmhEmbedBuilder.Base(cfg, "⚠️ KMH v1.2.0 update notice", sb.ToString()),
                DiscordIcons.Warning);
        }
    }
}
