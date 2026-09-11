using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Read-only: every fact comes from work already done earlier in boot.
    internal static class KmhUpgradeSummary
    {
        public static void Print(int fieldsBackfilled)
        {
            ServerLog.Info($"=== KMH v{KmhVersion.Build} startup summary ===");
            ServerLog.Info(KmhDataMeta.IsFreshInstall
                ? "  install: fresh - configs generated with this build's Balanced-safe defaults"
                : $"  install: upgraded from existing data (defaults revision {KmhDataMeta.AppliedDefaultsRevision})");

            if (fieldsBackfilled > 0)
                ServerLog.Info($"  configs: {fieldsBackfilled} missing field(s) backfilled with safe defaults (owner values kept)");
            if (KmhDefaultsUpgrade.RanThisBoot)
                ServerLog.Info($"  defaults upgrade: {KmhDefaultsUpgrade.Changed.Count} value(s) moved to KMH's defaults, {KmhDefaultsUpgrade.Preserved.Count} kept as owner-set");

            ServerLog.Info($"  economy profile: {Features.Economy.EconomyConfig.Current.EconomyMode ?? "Standard"}");

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
