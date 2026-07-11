using System.Linq;
using System.Reflection;
using HarmonyLib;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Discord;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.Features.LinkedAccounts;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.PlayerStats;
using KMHServerAddon.Features.Quests;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon
{
    // Installs KMH patches/handlers/state/services, then hands off to RWT Main.
    internal static class Main_
    {
        public const string HarmonyId = Constants.HarmonyId;

        public static Harmony  HarmonyInstance { get; private set; }
        public static System.DateTime BootstrapUtc { get; private set; } = System.DateTime.MinValue;

        // Patch + register KMH, then start RWT's own server in this process.
        public static int RunAndStartServer(string[] args)
        {
            RunKmhBoot();
            try
            {
                System.Type prog = System.Type.GetType("GameServer.Core.Program, GameServer", throwOnError: true);
                MethodInfo entry = prog.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
                                   ?? prog.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
                if (entry == null)
                {
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} Could not find GameServer.Core.Program.Main - RWT may have renamed it.");
                    return 1;
                }
                // RWT's Main may be Main() or Main(string[]) - pass args only if it takes them.
                object[] call = entry.GetParameters().Length == 1 ? new object[] { args } : null;
                entry.Invoke(null, call);
                return 0;
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine($"{Constants.LogPrefix} RWT server failed to start: {ex}");
                return 1;
            }
        }

        // Safe KMH boot: patches, handlers, and state load.
        public static void RunKmhBoot()
        {
            try
            {
                BootstrapUtc = System.DateTime.UtcNow;
                System.Console.WriteLine(
                    $"{Constants.LogPrefix} {Constants.DisplayName} v{typeof(Main_).Assembly.GetName().Version} bootstrapping…");

                // Registers all [HarmonyPatch] classes now; target patches fire later when RWT calls them.
                HarmonyInstance = new Harmony(HarmonyId);
                HarmonyInstance.PatchAll(typeof(Main_).Assembly);

                // Registers handshake/ping handlers plus each self-contained feature handler.
                KmhHandshakeHandler.Register();
                LinkedAccountsHandler.Register();
                PlayerStatsHandler.Register();
                TreasuryHandler.Register();
                MarketplaceHandler.Register();
                QuestHandler.Register();
                GuildHandler.Register();
                Features.ItemLabels.ItemLabelsHandler.Register();
                Diagnostics.KmhClientDebugLog.Register();
                Features.Sites.SiteHandler.Register();
                Features.Reputation.ReputationHandler.Register();
                Features.Enforcement.EnforcementHandler.Register();
                Features.World.WorldHandler.Register();
                Features.Auctions.AuctionHandler.Register();
                Features.WantBoard.WantHandler.Register();
                Features.Seasons.SeasonHandler.Register();

                // Loads persisted feature state from KMH-Data/, warning and using empty stores when files are missing or invalid.
                Persistence.KmhDataPaths.EnsureFolder();

                // Name a missing dependency plainly - a self-contained build shipped without Newtonsoft.Json.dll would
                // otherwise fail later with a confusing "storage failed" error.
                try { _ = Newtonsoft.Json.JsonConvert.SerializeObject(new { ok = true }); }
                catch (System.Exception depEx)
                {
                    Diagnostics.ServerLog.Error($"Missing dependency: KMH could not load Newtonsoft.Json ({depEx.GetType().Name}). " +
                        "For a self-contained/folder build, extract the whole package so Newtonsoft.Json.dll sits next to the exe.");
                }

                // Prove storage actually works before we rely on it: a read-only/locked KMH-Data or bad perms would
                // otherwise make every save fail silently and look like a data-collection bug. Loud if it fails.
                if (Persistence.JsonFileStore.SelfTest(out string storageDetail))
                    Diagnostics.ServerLog.Info($"Persistence self-test: OK - {storageDetail}");
                else
                    Diagnostics.ServerLog.Error($"Persistence SELF-TEST FAILED - KMH data will NOT save! ({storageDetail}) " +
                                                "Check folder permissions / free disk space for KMH-Data, or a missing dependency (see above).");

                // Coordinated rollback: apply any queued restore now - before backup/scan/load - so the whole boot
                // sees the restored KMH-Data. Safety-backs up current data first. No-op when nothing is queued.
                Persistence.KmhDataRestore.ApplyPendingRestore();

                // Release-safety pass, all BEFORE any store loads so it sees/preserves the pristine on-disk state:
                // (1) reconcile the data-format stamp (backs up + migrates only if a format change shipped),
                // (2) integrity-scan every KMH JSON and report (loud if irreplaceable data is damaged),
                // (3) take the once-per-boot safety backup and prune old copies.
                Maintenance.MaintenanceConfig.EnsureGenerated();
                Maintenance.MaintenanceConfig maint = Maintenance.MaintenanceConfig.Current;

                // Seed transport config + apply its debug toggle early so boot traces respect it.
                Features.Transport.TransportConfig.EnsureGenerated();
                ServerLog.DebugEnabled = Features.Transport.TransportConfig.Current.DebugLogging;

                ServerLog.Info(Persistence.KmhDataMeta.ReconcileOnBoot());
                ServerLog.Info($"KMH server identity: '{KmhServerIdentity.Name}' (id {KmhServerIdentity.Id})");
                if (maint.IntegrityScanOnBoot)
                    Persistence.KmhDataIntegrity.LogScan(Persistence.KmhDataIntegrity.Scan());
                if (maint.BackupOnBoot)
                {
                    if (Persistence.KmhDataBackup.TryCreate("boot", out string backupDir, out string backupErr))
                    {
                        int pruned = Persistence.KmhDataBackup.Prune(maint.BackupRetention);
                        ServerLog.Info($"Boot backup: {System.IO.Path.GetFileName(backupDir)}" +
                                       (pruned > 0 ? $" ({pruned} old backup(s) pruned)" : ""));
                    }
                    else ServerLog.Verbose($"Boot backup skipped: {backupErr}");
                }

                // Guaranteed final flush on shutdown. Saves are already per-mutation, so this only matters for the
                // rare in-flight change at exit - cheap insurance, registered once.
                RegisterShutdownFlush();

                // Seeds ready-to-edit config files on first boot; loaded values are clamped.
                Features.Economy.EconomyConfig.EnsureGenerated();
                Features.Sites.SitesConfig.EnsureGenerated();
                Features.Discord.DiscordConfig.EnsureGenerated();
                Features.Reputation.ReputationConfig.EnsureGenerated();
                Features.Quests.QuestsConfig.EnsureGenerated();
                Features.Enforcement.EnforcementConfig.EnsureGenerated();
                Features.World.WorldConfig.EnsureGenerated();
                Features.FeaturesConfig.EnsureGenerated();

                // Upgrade migration: detect a pre-v1.2.0 Sites.json (no tier fields) BEFORE backfill so we can alert the
                // owner about the curated-catalog behavior. Missing tier fields backfill to SAFE defaults (curated on, Tier 4
                // blocked) - KMH never silently reverts to the old all-item behavior.
                bool sitesMigratedToTiers = System.IO.File.Exists(Persistence.KmhDataPaths.SitesConfigFile)
                    && !Persistence.JsonFileStore.FileHasKey(Persistence.KmhDataPaths.SitesConfigFile, "UseSiteOutputTiers");
                // Move a legacy (pre-mode) Economy.json to the recommended Balanced BEFORE backfill would lock in Standard.
                bool economyModeMigrated = Features.Economy.EconomyConfig.MigrateLegacyModeToBalanced();
                int fieldsBackfilled = 0;
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.EconomyConfigFile,     new Features.Economy.EconomyConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.SitesConfigFile,       new Features.Sites.SitesConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.DiscordConfigFile,     new Features.Discord.DiscordConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.ReputationConfigFile,  new Features.Reputation.ReputationConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.QuestsConfigFile,      new Features.Quests.QuestsConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.EnforcementConfigFile, new Features.Enforcement.EnforcementConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.WorldConfigFile,       new Features.World.WorldConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.MaintenanceConfigFile, new Maintenance.MaintenanceConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.TransportConfigFile,   new Features.Transport.TransportConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.FeaturesConfigFile,    new Features.FeaturesConfig());

                // One-shot v1.2.0 recommended-defaults upgrade (marker-gated; preserves owner-edited values).
                Maintenance.KmhDefaultsUpgrade.ApplyIfNeeded();

                // Reload economy AFTER all config writes (migration + backfill + defaults-upgrade) so the boot banner
                // and every access check read the final on-disk value - not a stale cache (fixes Standard-then-Balanced).
                Features.Economy.EconomyConfig.Reload();
                if (economyModeMigrated)
                    ServerLog.Warn("Economy: migrated legacy (pre-v1.2.0) config to the recommended Balanced mode - remote-convenient with light caps/cooldowns/fees. Set EconomyMode in Config/Economy.json to change it.");

                Features.Enforcement.EnforcementProfile.Reload();

                // Loads each feature's saved state from KMH-Data/.
                Features.LinkedAccounts.LinkedAccountsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
                Features.Economy.EconomyResetStore.LoadFromDisk();
                Features.Treasury.TreasuryStore.LoadFromDisk();
                Features.Marketplace.MarketplaceStore.LoadFromDisk();
                Features.Quests.QuestStore.LoadFromDisk();
                Features.Guilds.GuildStore.LoadFromDisk();
                Features.Reputation.ReputationStore.LoadFromDisk();
                Features.Sites.SiteStore.LoadFromDisk();
                Features.World.WorldStore.LoadFromDisk();
                Features.Auctions.AuctionStore.LoadFromDisk();
                Features.WantBoard.WantStore.LoadFromDisk();
                Features.Seasons.SeasonStore.LoadFromDisk();
                Features.Notifications.NotificationStore.LoadFromDisk();
                Features.Recovery.RecoveryStore.LoadFromDisk();
                Features.ItemLabels.ItemLabelCache.LoadFromDisk();
                Features.ItemLabels.WeatherDefCache.LoadFromDisk();
                Features.Discord.DiscordUserState.LoadFromDisk();

                // Startup banner: name the economy profile so the owner knows how permissive the treasury is.
                string ecoMode = Features.Economy.EconomyConfig.Current.EconomyMode ?? "Standard";
                if (Features.Economy.EconomyAccess.Policy.IsStrict)
                    ServerLog.Warn($"Economy: STRICT treasury mode active - {Features.Economy.EconomyAccess.Describe()}");
                else if (string.Equals(ecoMode, "Standard", StringComparison.OrdinalIgnoreCase))
                    ServerLog.Warn("Economy: Treasury is in Remote/Standard mode - allows off-map wealth storage and may reduce raid pressure. " +
                        "Balanced (recommended), Localized or Hardcore are safer for public servers. Set EconomyMode in Config/Economy.json.");
                else
                    ServerLog.Info($"Economy: {ecoMode} profile active - {Features.Economy.EconomyAccess.Describe()}");
                Features.Economy.EconomyPolicy pol = Features.Economy.EconomyAccess.Policy;
                if (pol.DepositFeePct > 0 || pol.WithdrawFeePct > 0)
                    ServerLog.Info($"Economy: treasury silver fees {pol.DepositFeePct:0.#}% deposit / {pol.WithdrawFeePct:0.#}% withdraw -> the house pool (same sink as marketplace tax). Item deposits/withdraws pay no fee.");
                ServerLog.Info("Economy: passive income (site rewards, marketplace/auction/want payouts) accrues into the treasury under every access mode; a restrictive mode only gates withdrawal, so value is never stranded silently ('kmh audit-player').");
                // v1.2.0 does NOT feed KMH off-map value into RimWorld's raid/threat scaling - that patch is deferred. Say so loudly so owners don't assume it.
                ServerLog.Warn("Economy: KMH off-map value (treasury + escrow) is NOT yet counted in RimWorld raid/threat scaling. Off-map wealth pressure is planned for a later release - do not assume stored wealth raises raids yet.");
                if (Features.Economy.EconomyConfig.Current.AnyGuildHallRule)
                    ServerLog.Warn("Guilds: physical Guild Hall restrictions are ENABLED - some guild actions require a hall / proximity. See Config/Economy.json.");
                if (sitesMigratedToTiers)
                    ServerLog.Warn("Sites: your Sites.json was upgraded to v1.2.0 - outputs now use a CURATED, server-authoritative tier catalog. " +
                        "Sites can no longer produce weapons/apparel/tech/archotech/bionics/genes/relics/ammo; unknown items and Tier 4 are BLOCKED by default. " +
                        "Your existing values were kept; review Config/Sites.json (OutputTiers, AllowedDefNames/AllowExplicitComplexSiteOutputs) to allow more.");
                if (Features.Sites.SiteStore.LegacyBlockedSiteCount > 0)
                    ServerLog.Warn($"Sites: {Features.Sites.SiteStore.LegacyBlockedSiteCount} existing site(s) are PAUSED because their output is now blocked by current rules - review with 'kmh audit sites' and either allow the output or remove the site.");
                if (Features.Sites.SiteStore.LegacyWorkerCount > 0)
                    ServerLog.Warn($"Sites: {Features.Sites.SiteStore.LegacyWorkerCount} legacy account-worker(s) are DISABLED - sites now need real pawns (caravan -> assign). Old workers were kept, not deleted; reassign to resume production. See 'kmh validate sites'.");
                if (!Features.Sites.SitesConfig.Current.UseSiteOutputTiers && Features.Sites.SitesConfig.Current.AllowUnsafeLegacySiteOutputs)
                    ServerLog.Warn("Sites: UNSAFE legacy mode is ON (UseSiteOutputTiers=false + AllowUnsafeLegacySiteOutputs=true) - Sites can produce ANY def. This is admin/dev-only; standard servers should leave tiers on.");
                if (Features.Recovery.RecoveryStore.HeldCount > 0)
                    ServerLog.Warn($"Recovery: {Features.Recovery.RecoveryStore.HeldCount} item/silver record(s) couldn't reach their owner and are HELD - triage with 'kmh recover list'.");

                Maintenance.KmhUpgradeSummary.Print(fieldsBackfilled);

                // Writes empty default state files on first boot so KMH-Data folders are materialized before first use.
                EnsureFile(Persistence.KmhDataPaths.LinkedAccountsFile,   Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.PlayerStatsFile,      Features.PlayerStats.PlayerStatsStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ColonistsFile,        Features.PlayerStats.PlayerStatsStore.SaveColonistsToDisk);
                EnsureFile(Persistence.KmhDataPaths.TreasuryFile,         Features.Treasury.TreasuryStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.MarketplaceFile,      Features.Marketplace.MarketplaceStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.QuestsFile,           Features.Quests.QuestStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.GuildsFile,           Features.Guilds.GuildStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ReputationFile,       Features.Reputation.ReputationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.SitesFile,            Features.Sites.SiteStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.WorldFile,            Features.World.WorldStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.AuctionsFile,         Features.Auctions.AuctionStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.WantsFile,            Features.WantBoard.WantStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.RecoveryFile,         Features.Recovery.RecoveryStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.NotificationsFile,    Features.Notifications.NotificationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ItemLabelsFile,       Features.ItemLabels.ItemLabelCache.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.DiscordUserStateFile, Features.Discord.DiscordUserState.SaveToDisk);

                // One-time prime of the marketplace house pool on a brand-new server so global-quest rewards can be
                // funded before any tax revenue accrues. No-ops on every later boot (the seeded flag persists).
                Features.Marketplace.MarketplaceStore.SeedHousePoolOnce(Features.World.WorldConfig.Current.HousePoolSeed);

                // Durable economy audit trail: start the background flusher that writes queued ledger lines to disk.
                Persistence.TransactionLedger.Start();

                // Readable per-domain state-change history (quests/guilds/sites/market/...) for owner inspection.
                Persistence.KmhHistory.Start();

                // KMH API listener - no-ops unless EnableKmhApiTransport=true (off by default)
                Features.Transport.KmhApiServer.Start();

                // Periodically sweeps expired listings and quests with a cheap 60s background pass.
                Maintenance.ExpirySweeper.Start();

                // World Engine: expires ended events, auto-rolls new ones + auto-generates quests (on by default).
                Features.World.WorldEngine.Start();

                // Loads the optional Discord bridge config and starts the bot safely in the background when enabled.
                DiscordBridge.Start();
                // Periodically posts the top-N player leaderboard when the Discord bridge and channel are configured.
                DiscordLeaderboardPoster.Start();
                // Periodically refreshes user showcase posts when the Discord bridge/channel/interval are enabled.
                Features.Discord.DiscordShowcaseSweep.Start();
                // Subscribes to KMH events and posts enabled branded Discord embeds to their configured channels.
                Features.Discord.DiscordEventPublisher.Start();
                // Mirrors KMH guild membership to Discord roles on linked players (opt-in: Roles.SyncGuildRoles).
                Features.Discord.DiscordGuildRoleSync.Start();
                // Hooks RWT logging after startup and mirrors console output to the configured Discord Admin channel.
                Features.Discord.DiscordConsoleFeed.Start();

                // Must-read owner notice (~5s after boot, + Discord Admin channel) when the defaults upgrade ran.
                Maintenance.KmhOwnerNotice.ScheduleIfNeeded();

                // Scans kmh-extensions/ for IKmhServerExtension DLLs, registers valid ones, and skips failed loads safely.
                Extensibility.ExtensionLoader.DiscoverAndLoad();

                int patchCount = HarmonyInstance.GetPatchedMethods().Count();
                ServerLog.Info($"Bootstrap complete - {patchCount} method(s) patched, KMH handlers registered, {Extensibility.ExtensionLoader.Loaded.Count} extension(s) loaded");

                // Show the what's-new banner once when the build changed since the last run.
                string prevBuild = Persistence.KmhDataMeta.PreviousBuildVersion;
                string curBuild  = typeof(Persistence.KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "";
                if (!string.IsNullOrEmpty(prevBuild) && prevBuild != curBuild)
                    Maintenance.KmhWhatsNewBanner.Print(prevBuild);
            }
            catch (System.Exception ex)
            {
                // Recoverable startup hook: log KMH boot failures and let RWT continue instead of aborting GameServer.
                System.Console.Error.WriteLine($"{Constants.LogPrefix} Bootstrap failed: {ex}");
            }
        }

        // Writes the store's default state if missing so the KMH-Data folder is fully seeded on first boot.
        private static void EnsureFile(string path, System.Action save)
        {
            try { if (!System.IO.File.Exists(path)) save(); }
            catch (System.Exception ex) { ServerLog.Warn($"Could not materialize {System.IO.Path.GetFileName(path)}: {ex.Message}"); }
        }

        private static bool _shutdownFlushHooked;

        // Flush all stores when the process exits (Ctrl-C / SIGTERM / normal teardown). Idempotent and best-effort.
        private static void RegisterShutdownFlush()
        {
            if (_shutdownFlushHooked) return;
            _shutdownFlushHooked = true;
            try
            {
                System.AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                {
                    try { int n = Maintenance.KmhDataFlush.FlushAll(); ServerLog.Info($"Shutdown flush: {n} store(s) saved."); }
                    catch { /* shutting down anyway */ }
                };
            }
            catch (System.Exception ex) { ServerLog.Verbose($"Could not register shutdown flush: {ex.Message}"); }
        }
    }
}
