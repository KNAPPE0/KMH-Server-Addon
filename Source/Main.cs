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
    internal static class Main_
    {
        public const string HarmonyId = Constants.HarmonyId;

        public static Harmony  HarmonyInstance { get; private set; }
        public static System.DateTime BootstrapUtc { get; private set; } = System.DateTime.MinValue;

        // Assembly-qualified so we bind RWT's own assembly, not something same-named. Newest first.
        private static readonly string[] ServerEntryTypeNames =
        {
            "RTServer.Core.Program, RTServer",
            "GameServer.Core.Program, GameServer",
        };

        public static int RunAndStartServer(string[] args)
        {
            // Runs before anything writes: an empty RWT config kills RWT inside its own logger, naming no file.
            Diagnostics.RwtPreflight.Run();
            RunKmhBoot();
            try
            {
                // Try both identities rather than hard-coding one; RWT renamed the assembly mid-life.
                System.Type prog = null;
                foreach (string tn in ServerEntryTypeNames)
                {
                    prog = System.Type.GetType(tn, throwOnError: false);
                    if (prog != null) break;
                }
                if (prog == null)
                {
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} Could not find RWT's Program type - tried: "
                        + string.Join(" / ", ServerEntryTypeNames));
                    return 1;
                }
                MethodInfo entry = prog.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
                                   ?? prog.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
                if (entry == null)
                {
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} Could not find {prog.FullName}.Main - RWT may have renamed it.");
                    return 1;
                }
                // RWT's Main may be Main() or Main(string[]) - pass args only if it takes them.
                object[] call = entry.GetParameters().Length == 1 ? new object[] { args } : null;
                entry.Invoke(null, call);
                return 0;
            }
            catch (System.Exception ex)
            {
                // The stack alone sends owners hunting the wrong thing - lead with the cause when we recognise it.
                string why = Diagnostics.RwtPreflight.ExplainStartupFailure(ex);
                if (why != null)
                {
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} RWT server failed to start - {why}");
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} This repeats on every restart until that "
                        + "file is fixed. KMH keeps a last-good copy of each RWT config in KMH-Data/RwtConfigBackup.");
                }
                System.Console.Error.WriteLine($"{Constants.LogPrefix} RWT server failed to start: {ex}");
                return 1;
            }
        }

        public static void RunKmhBoot()
        {
            try
            {
                BootstrapUtc = System.DateTime.UtcNow;
                // First thing of all: a boot that dies during patching is exactly the one worth having a file for.
                Diagnostics.KmhLogSink.Start();
                System.Console.WriteLine(
                    $"{Constants.LogPrefix} {Constants.DisplayName} v{typeof(Main_).Assembly.GetName().Version} bootstrapping…");

                HarmonyInstance = new Harmony(HarmonyId);
                HarmonyInstance.PatchAll(typeof(Main_).Assembly);

                KmhHandshakeHandler.Register();
                LinkedAccountsHandler.Register();
                PlayerStatsHandler.Register();
                TreasuryHandler.Register();
                Features.Delivery.DeliveryHandler.Register();
                MarketplaceHandler.Register();
                QuestHandler.Register();
                GuildHandler.Register();
                Features.ItemLabels.ItemLabelsHandler.Register();
                Features.Sites.SiteMetadataHandler.Register();
                Diagnostics.KmhClientDebugLog.Register();
                Features.Sites.SiteHandler.Register();
                Features.Roadworks.RoadworksHandler.Register();
                Features.Frontier.FrontierHandler.Register();
                Features.Reputation.ReputationHandler.Register();
                Features.Enforcement.EnforcementHandler.Register();
                Features.World.WorldHandler.Register();
                Features.Auctions.AuctionHandler.Register();
                Features.WantBoard.WantHandler.Register();
                Features.Mail.MailHandler.Register();
                Features.Chat.ChatHandler.Register();
                Features.Chat.ChatRosterHandler.Register();
                Features.Media.KmhMediaHandler.Register();
                Features.Chat.ChatMediaRefresh.Register();
                Features.Media.KmhVideoHandler.Register();
                Features.Seasons.SeasonHandler.Register();

                Persistence.KmhDataPaths.EnsureFolder();

                // A missing Newtonsoft otherwise surfaces later as a confusing "storage failed".
                try { _ = Newtonsoft.Json.JsonConvert.SerializeObject(new { ok = true }); }
                catch (System.Exception depEx)
                {
                    Diagnostics.ServerLog.Error($"Missing dependency: KMH could not load Newtonsoft.Json ({depEx.GetType().Name}). " +
                        "For a self-contained/folder build, extract the whole package so Newtonsoft.Json.dll sits next to the exe.");
                }

                // A read-only KMH-Data otherwise makes every save fail silently and read as a data-collection bug.
                if (Persistence.JsonFileStore.SelfTest(out string storageDetail))
                    Diagnostics.ServerLog.Info($"Persistence self-test: OK - {storageDetail}");
                else
                    Diagnostics.ServerLog.Error($"Persistence SELF-TEST FAILED - KMH data will NOT save! ({storageDetail}) " +
                                                "Check folder permissions / free disk space for KMH-Data, or a missing dependency (see above).");

                // Before anything reads KMH-Data, or a restore that died mid-swap reads as a fresh install.
                Persistence.KmhDataBackup.FinishInterruptedRestore();

                // Before backup, scan and load, so the whole boot sees the restored data rather than half of it.
                Persistence.KmhDataRestore.ApplyPendingRestore();

                // All of this runs BEFORE any store loads, so it sees the pristine on-disk state.
                Maintenance.MaintenanceConfig.EnsureGenerated();
                Maintenance.MaintenanceConfig maint = Maintenance.MaintenanceConfig.Current;

                // Seed transport config + apply its debug toggle early so boot traces respect it.
                Features.Transport.TransportConfig.EnsureGenerated();
                ServerLog.DebugEnabled = Features.Transport.TransportConfig.Current.DebugLogging;

                // Seeded BEFORE the scan below, or a first boot reports configs it is about to write as missing.
                Features.Economy.EconomyConfig.EnsureGenerated();
                Features.Sites.SitesConfig.EnsureGenerated();
                Features.Discord.DiscordConfig.EnsureGenerated();
                Features.Reputation.ReputationConfig.EnsureGenerated();
                Features.Quests.QuestsConfig.EnsureGenerated();
                Features.Enforcement.EnforcementConfig.EnsureGenerated();
                Features.World.WorldConfig.EnsureGenerated();
                Features.Chat.ChatConfig.EnsureGenerated();
                Features.Media.MediaConfig.EnsureGenerated();
                Features.Mail.MailConfig.EnsureGenerated();
                Features.Frontier.FrontierConfig.EnsureGenerated();
                Features.Identity.StaffConfig.EnsureGenerated();
                Features.FeaturesConfig.EnsureGenerated();

                ServerLog.Info(Persistence.KmhDataMeta.ReconcileOnBoot());
                ServerLog.Info($"KMH server identity: '{KmhServerIdentity.Name}' (id {KmhServerIdentity.Id})");
                // Scanned even when the report is off: the backup step below must know whether the data is trustworthy.
                bool dataDamaged = false;
                if (maint.IntegrityScanOnBoot || maint.BackupOnBoot)
                {
                    Persistence.KmhDataIntegrity.ScanResult scan = Persistence.KmhDataIntegrity.Scan();
                    if (maint.IntegrityScanOnBoot) Persistence.KmhDataIntegrity.LogScan(scan);
                    dataDamaged = scan.CriticalDamage;
                }
                string bootBackupName = "";
                if (dataDamaged)
                {
                    // A crash loop would otherwise spend the whole retention on copies of broken data in about a minute.
                    ServerLog.Error("Boot backup and pruning SKIPPED - KMH-Data is damaged, so the existing backups "
                        + "may be the only intact copies. Restore one from KMH-Data-Backups before players reconnect.");
                }
                else if (maint.BackupOnBoot)
                {
                    if (Persistence.KmhDataBackup.TryCreate("boot", out string backupDir, out string backupErr))
                    {
                        bootBackupName = System.IO.Path.GetFileName(backupDir);
                        int pruned = Persistence.KmhDataBackup.Prune(maint.BackupRetention);
                        ServerLog.Info($"Boot backup: {bootBackupName}" +
                                       (pruned > 0 ? $" ({pruned} old backup(s) pruned)" : ""));
                    }
                    else ServerLog.Verbose($"Boot backup skipped: {backupErr}");
                }

                RegisterShutdownFlush();

                Features.Media.KmhVideoCache.Start();
                Features.Media.KmhMediaCache.ResetOnBoot();   // converted copies never survive a restart

                // Detected BEFORE backfill, because backfill writes the safe defaults that erase the evidence.
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
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.MailConfigFile,        new Features.Mail.MailConfig());
                fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(Persistence.KmhDataPaths.FrontierConfigFile,    new Features.Frontier.FrontierConfig());

                Maintenance.KmhDefaultsUpgrade.ApplyIfNeeded();

                // Reloaded AFTER every config write, or the banner and access checks run on a pre-rewrite cache.
                Features.Economy.EconomyConfig.Reload();
                if (economyModeMigrated)
                    ServerLog.Warn("Economy: migrated legacy (pre-v1.2.0) config to the recommended Balanced mode - remote-convenient with light caps/cooldowns/fees. Set EconomyMode in Config/Economy.json to change it.");

                Features.Enforcement.EnforcementProfile.Reload();

                Features.LinkedAccounts.LinkedAccountsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
                Maintenance.KmhReadiness.Recovering();
                Features.Economy.EconomyResetStore.LoadFromDisk();
                Features.Economy.KmhEconomyReset.LoadFromDisk();   // holds the barrier before any store is reachable
                // Advance past the restored generation, or ids issued against the replaced data hit different rows.
                if (!string.IsNullOrEmpty(Persistence.KmhDataBackup.RestoredThisBoot))
                {
                    Features.Economy.KmhEconomyReset.BumpDataGeneration(
                        $"restored from {Persistence.KmhDataBackup.RestoredThisBoot}");
                    Persistence.KmhDataBackup.ClearRestoredThisBoot();
                }
                Features.Treasury.TreasuryStore.LoadFromDisk();
                // Drop vault rows that were created but never written to, so a lookup alone never leaves one behind.
                Features.Treasury.TreasuryStore.PruneUnusedVaults();
                Features.Roadworks.RoadworksStore.LoadFromDisk();
                Features.Frontier.KmhWorldDirector.LoadFromDisk();
                Features.Marketplace.MarketplaceStore.LoadFromDisk();
                Features.Quests.QuestStore.LoadFromDisk();
                Features.Guilds.GuildStore.LoadFromDisk();
                Features.Guilds.Contributions.KmhGuildContributionLedger.LoadFromDisk();
                Transactions.KmhTransactionRepository.LoadFromDisk();
                Transactions.KmhTransactionRepository.RecoverOnBoot();   // reconcile transactions left by a crash
                // Needs the rows loaded above: a pending take is owned by the vault only while no row names it.
                Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                // After the ledger, before the stores it purges are used: an unfinished reset must not be raced.
                Features.Economy.KmhEconomyReset.RecoverOnBoot();
                Features.Reputation.ReputationStore.LoadFromDisk();
                Features.Sites.SiteCatalogStore.LoadFromDisk();   // before sites: reclassification reads it
                Features.Sites.SiteStore.LoadFromDisk();
                Features.World.WorldStore.LoadFromDisk();
                Features.Auctions.AuctionStore.LoadFromDisk();
                Features.WantBoard.WantStore.LoadFromDisk();
                Features.Mail.MailStore.LoadFromDisk();
                Features.Chat.ChatModerationStore.LoadFromDisk();
                Features.Chat.ChatStore.LoadFromDisk();
                Features.Seasons.SeasonStore.LoadFromDisk();
                Features.Notifications.NotificationStore.LoadFromDisk();
                Features.Recovery.RecoveryStore.LoadFromDisk();
                Features.Delivery.DeliveryStore.LoadFromDisk();
                Features.ItemLabels.ItemLabelCache.LoadFromDisk();
                Features.ItemLabels.WeatherDefCache.LoadFromDisk();
                Features.Discord.DiscordUserState.LoadFromDisk();
                Policy.KmhPolicyStore.LoadFromDisk();

                // Sites and World are the truth; the director only holds references and is saved at its own moments.
                Features.Frontier.KmhWorldDirector.Reconcile();

                // After the stores load, because its steps operate on loaded state rather than raw config files.
                int cfgSchemaFrom = Persistence.KmhDataMeta.AppliedConfigSchema;   // capture before the stamp is raised
                Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.Migration);
                try
                {
                    // A journal left behind means the previous migration never committed, so roll back before retrying.
                    Maintenance.KmhMigrationRecovery.RecoverIfIncomplete();

                    Maintenance.KmhMigrationJournal journal = Maintenance.KmhMigrationJournal.Begin(bootBackupName);
                    Maintenance.KmhConfigMigration.ApplyIfNeeded();
                    Maintenance.KmhConfigValidation.GateOnBoot();   // load-test configs; recover from backup on failure
                    journal.Commit();
                    Maintenance.KmhMigrationJournal.Clear();        // clean commit: no incomplete journal remains
                }
                finally { Maintenance.KmhMaintenanceGate.Release(); }

                // A migration rewrites Economy.json after the reload above, so re-read or it lands only next boot.
                Features.Economy.EconomyConfig.Reload();

                // Applied only once every config rewrite is done, or a cache holds the pre-rewrite copy all run.
                Features.Comms.CommsPush.Wire();          // how a later reload reaches connected clients
                Maintenance.KmhInvalidation.Wire();       // a vault changed by Discord/SDK/admin still reaches its owner
                Features.Comms.CommsStartup.ApplyAtBoot();

                Maintenance.KmhMigrationReport.Write(new Maintenance.KmhMigrationReport.Inputs
                {
                    TimestampUtc      = System.DateTime.UtcNow.ToString("o"),
                    Build             = KmhVersion.Build,
                    FreshInstall      = Persistence.KmhDataMeta.IsFreshInstall,
                    ConfigSchemaFrom  = cfgSchemaFrom,
                    ConfigSchemaTo    = KmhVersion.ConfigSchema,
                    DataSchema        = KmhVersion.DataSchema,
                    FieldsBackfilled  = fieldsBackfilled,
                    DefaultsChanged   = Maintenance.KmhDefaultsUpgrade.Changed.Count,
                    DefaultsPreserved = Maintenance.KmhDefaultsUpgrade.Preserved.Count,
                    Steps             = Maintenance.KmhConfigMigration.Report,
                });

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
                // Both branches log, because an owner has to know which way this server actually behaves.
                if (Features.FeaturesConfig.Current.Wealth)
                    ServerLog.Warn("Economy: Features.Wealth is ON - KMH off-map value (treasury + escrow) counts toward RimWorld raid/threat scaling on v1.3.0+ clients, so parking wealth in KMH does not dodge raids. Set Features.json Wealth=false to make KMH a safe haven.");
                else
                    ServerLog.Info("Economy: Features.Wealth is OFF - KMH off-map value is NOT counted in RimWorld raid/threat scaling; KMH is a safe haven from the storyteller.");
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

                EnsureFile(Persistence.KmhDataPaths.LinkedAccountsFile,   () => Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.PlayerStatsFile,      Features.PlayerStats.PlayerStatsStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ColonistsFile,        Features.PlayerStats.PlayerStatsStore.SaveColonistsToDisk);
                EnsureFile(Persistence.KmhDataPaths.TreasuryFile,         () => Features.Treasury.TreasuryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.MarketplaceFile,      () => Features.Marketplace.MarketplaceStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.QuestsFile,           () => Features.Quests.QuestStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.GuildsFile,           () => Features.Guilds.GuildStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ReputationFile,       Features.Reputation.ReputationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.SitesFile,            () => Features.Sites.SiteStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.WorldFile,            Features.World.WorldStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.AuctionsFile,         () => Features.Auctions.AuctionStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.WantsFile,            () => Features.WantBoard.WantStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.RecoveryFile,         () => Features.Recovery.RecoveryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.NotificationsFile,    () => Features.Notifications.NotificationStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ItemLabelsFile,       Features.ItemLabels.ItemLabelCache.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.DiscordUserStateFile, Features.Discord.DiscordUserState.SaveToDisk);
                // Materialized so the next boot's scan finds a canonical empty file instead of reporting it missing.
                EnsureFile(Persistence.KmhDataPaths.RoadworksFile,        () => Features.Roadworks.RoadworksStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.FrontierDirectorFile, () => Features.Frontier.KmhWorldDirector.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.MailFile,             () => Features.Mail.MailStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ChatFile,             Features.Chat.ChatStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ChatModerationFile,   Features.Chat.ChatModerationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.SeasonsFile,          Features.Seasons.SeasonStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.DeliveryFile,         () => Features.Delivery.DeliveryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.GuildContributionsFile, Features.Guilds.Contributions.KmhGuildContributionLedger.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.TransactionLedgerFile, () => Transactions.KmhTransactionRepository.SaveToDisk());

                // Primes the house pool once, so global-quest rewards are fundable before any tax revenue exists.
                Features.Marketplace.MarketplaceStore.SeedHousePoolOnce(Features.World.WorldConfig.Current.HousePoolSeed);

                Persistence.TransactionLedger.Start();

                Persistence.KmhHistory.Start();

                Features.Transport.KmhApiServer.Start();

                // After the API, whose port the relay shares rather than opening a second one to forward.
                Features.Media.KmhVideoRelay.Start();

                Maintenance.ExpirySweeper.Start();

                Features.World.WorldEngine.Start();
                Maintenance.KmhScheduler.Register("frontier-director", TimeSpan.FromMinutes(1),
                                                  Features.Frontier.KmhWorldDirector.Tick, TimeSpan.FromMinutes(2));

                DiscordBridge.Start();
                DiscordLeaderboardPoster.Start();
                Features.Discord.DiscordShowcaseSweep.Start();
                Features.Discord.DiscordEventPublisher.Start();
                Features.Discord.DiscordGuildRoleSync.Start();
                Features.Discord.DiscordConsoleFeed.Start();

                Maintenance.KmhOwnerNotice.ScheduleIfNeeded();

                Extensibility.ExtensionLoader.DiscoverAndLoad();

                // Last: value moves must not act on a half-loaded copy of the state.
                Maintenance.KmhReadiness.Ready();

                int patchCount = HarmonyInstance.GetPatchedMethods().Count();
                ServerLog.Info($"Bootstrap complete - {patchCount} method(s) patched, KMH handlers registered, {Extensibility.ExtensionLoader.Loaded.Count} extension(s) loaded");

                string prevBuild = Persistence.KmhDataMeta.PreviousBuildVersion;
                string curBuild  = typeof(Persistence.KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "";
                if (!string.IsNullOrEmpty(prevBuild) && prevBuild != curBuild)
                    Maintenance.KmhWhatsNewBanner.Print(prevBuild);
            }
            catch (System.Exception ex)
            {
                // A KMH boot failure must not abort RWT, so nothing in here may throw - but a half-loaded KMH must not serve players either.
                try { Maintenance.KmhReadiness.Failed(ex.GetType().Name + ": " + ex.Message); } catch { }
                try { System.Console.Error.WriteLine($"{Constants.LogPrefix} Bootstrap failed: {ex}"); } catch { }
            }
        }

        private static void EnsureFile(string path, System.Action save)
        {
            try { if (!System.IO.File.Exists(path)) save(); }
            catch (System.Exception ex) { ServerLog.Warn($"Could not materialize {System.IO.Path.GetFileName(path)}: {ex.Message}"); }
        }

        private static bool _shutdownFlushHooked;

        private static void RegisterShutdownFlush()
        {
            if (_shutdownFlushHooked) return;
            _shutdownFlushHooked = true;
            try
            {
                System.AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                {
                    try { Maintenance.KmhShutdown.Begin(); }
                    catch { /* shutting down anyway */ }
                };
            }
            catch (System.Exception ex) { ServerLog.Verbose($"Could not register shutdown flush: {ex.Message}"); }
        }
    }
}
