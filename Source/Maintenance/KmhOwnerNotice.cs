using System;
using System.Text;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Discord;

namespace KMHServerAddon.Maintenance
{
    // The Discord half of the regeneration notice; the console half is KmhConfigRegeneration.Announce.
    internal static class KmhOwnerNotice
    {
        public static void ScheduleIfNeeded()
        {
            if (KmhConfigRegeneration.Regenerated.Count == 0) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await TrySendDiscord().ConfigureAwait(false);
                }
                catch (Exception ex) { ServerLog.Verbose($"Owner notice failed: {ex.Message}"); }
            });
        }

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
            sb.AppendLine("⚠️ **Colony Nexus regenerated your server configuration**");
            sb.AppendLine("**Colony Nexus v1.3.1 rebuilt this server's configuration from the current defaults.**");
            sb.AppendLine("Settings were *not* upgraded field by field - the files below were replaced outright, so any "
                        + "value you had tuned is back at its default.");
            sb.AppendLine();
            sb.AppendLine($"🔁 **Regenerated** - {string.Join(", ", KmhConfigRegeneration.Regenerated.ToArray())}");
            sb.AppendLine($"🔒 **Kept untouched** (identity and credentials) - {string.Join(", ", KmhConfigRegeneration.Preserved.ToArray())}");
            sb.AppendLine();
            sb.AppendLine("📦 Your previous files are in `KMH-Data-Backups` under a `premigration-config` folder.");
            sb.AppendLine("📖 What each file does is written to `KMH-Data/Config/README.txt`; `kmh config` lists every "
                        + "setting and its current value.");
            sb.Append("⚖️ The economy runs on `EconomyMode=Balanced` unless you change it.");

            await DiscordBridge.PostToChannelAsync(cfg.AdminChannelId, sb.ToString()).ConfigureAwait(false);
        }
    }
}
