using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Read-only: every fact comes from work already done earlier in boot.
    internal static class KmhUpgradeSummary
    {
        public static void Print(int fieldsBackfilled)
        {
            ServerLog.Info($"=== Colony Nexus {KmhVersion.Build} ({KmhVersion.BuildTag}) startup summary ===");
            ServerLog.Info($"  server: {KmhServerIdentity.Name}");
            Features.Mods.ModPolicyConfig mods = Features.Mods.ModPolicyConfig.Current;
            string stated = Features.Mods.ModPolicyConfig.GameVersionOf(mods.RimWorldVersion);
            ServerLog.Info(stated == mods.GameVersion
                ? $"  players: RimWorld {mods.GameVersion} (Config/Mods.json RimWorldVersion); other versions are refused at join"
                : $"  players: RimWorld {mods.GameVersion} - Config/Mods.json says {stated}, but RimWorld Together "
                + $"{Features.Mods.ModPolicyConfig.RunningRwtVersion()} admits only RimWorld {mods.GameVersion}; other versions are refused at join");
            ServerLog.Info(KmhDataMeta.IsFreshInstall
                ? "  install: new server - current Balanced defaults written to KMH-Data/Config (each file explained in Config/README.txt)"
                : $"  install: existing data, config schema v{KmhDataMeta.AppliedConfigSchema}");

            if (fieldsBackfilled > 0)
                ServerLog.Info($"  configs: {fieldsBackfilled} missing field(s) backfilled with safe defaults (owner values kept)");
            if (KmhConfigRegeneration.Regenerated.Count > 0)
                ServerLog.Info($"  configs: {KmhConfigRegeneration.Regenerated.Count} regenerated from v1.3.1 defaults, "
                             + $"{KmhConfigRegeneration.Preserved.Count} kept (identity and credentials)");

            ServerLog.Info($"  economy profile: {Features.Economy.EconomyConfig.Current.EconomyMode ?? "Standard"}");

            Features.Global.GlobalConfig g = Features.Global.GlobalConfig.Current;
            ServerLog.Info("  Colony Nexus Global: " + (!g.Enabled
                ? "off (Config/Global.json Enabled lists this server on the website and in the browser)"
                : "on" + (string.IsNullOrEmpty(g.RegisteredServerId) ? ", registering on first contact" : ", registered")));
            Features.Discord.DiscordConfig d = Features.Discord.DiscordConfig.LoadOrDefault();
            ServerLog.Info("  Discord: " + (d.IsEnabled ? "on" : d.Enabled ? "waiting for a bot token (Config/Discord/DiscordConfig.json)" : "off"));
            Features.Chat.ChatConfig c = Features.Chat.ChatConfig.Current;
            ServerLog.Info($"  chat images: previews {(c.AllowImagePreviews ? "on" : "off")}, typed links {(c.AllowTypedImageUrls ? "on" : "off")}, "
                         + $"{c.ImageHostAllowList?.Length ?? 0} trusted host(s)");

            (int _, int __, int legacy) = Features.Treasury.TreasuryStore.PayloadHealth();
            if (legacy > 0)
                ServerLog.Info($"  legacy payloads: {legacy} treasury item(s) predate the payload format - state unproven but kept (not deleted)");

            if (Features.Sites.SiteStore.LegacyBlockedSiteCount > 0)
                ServerLog.Info($"  sites paused: {Features.Sites.SiteStore.LegacyBlockedSiteCount} output(s) blocked by current rules - review 'kmh audit sites'");
            if (Features.Recovery.RecoveryStore.HeldCount > 0)
                ServerLog.Info($"  recovery queue: {Features.Recovery.RecoveryStore.HeldCount} held record(s) - 'kmh recover list'");

            ServerLog.Info("  full check anytime: 'kmh validate all'");
        }
    }
}
