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

        // Assembly-qualified so only RWT's own assembly binds, not something same-named. Newest first.
        private static readonly string[] ServerEntryTypeNames =
        {
            "RTServer.Core.Program, RTServer",
            "GameServer.Core.Program, GameServer",
        };

        public static int RunAndStartServer(string[] args)
        {
            // Before anything binds a port: this instance may be the replacement a `kmh restart` just launched.
            Maintenance.KmhRestart.WaitIfRelaunched();

            // Runs before anything writes: an empty RWT config kills RWT inside its own logger, naming no file.
            Diagnostics.RwtPreflight.Run();
            RunKmhBoot();
            if (!Features.Enforcement.EnforcementAdmission.Ready)
            {
                System.Console.Error.WriteLine("Colony Nexus admission compatibility failed; refusing to start an unguarded RWT server.");
                return 1;
            }
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
                // The stack alone sends owners hunting the wrong thing - lead with the cause when it is recognised.
                string why = Diagnostics.RwtPreflight.ExplainStartupFailure(ex);
                if (why != null)
                {
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} RWT server failed to start - {why}");
                    System.Console.Error.WriteLine($"{Constants.LogPrefix} This repeats on every restart until that "
                        + "file is fixed. Colony Nexus keeps a last-good copy of each RWT config in KMH-Data/RwtConfigBackup.");
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
                // Before anything can open a connection, so a self-test run sees every request that heads for Global.
                Features.Global.KmhGlobalIsolationMonitor.StartIfSelfTest();
                System.Console.WriteLine($"{Constants.LogPrefix} Colony Nexus server {KmhVersion.Display} ({KmhVersion.BuildTag}) starting…");

                HarmonyInstance = new Harmony(HarmonyId);
                HarmonyInstance.PatchAll(typeof(Main_).Assembly);
                Features.Enforcement.EnforcementAdmission.Install(HarmonyInstance);
                Features.Identity.AccountNameGuard.Install(HarmonyInstance);
                Features.Global.RwtBrowserEndpoint.Install(HarmonyInstance);
                Features.Simulation.SimulatedColonyGate.Install(HarmonyInstance);

                KmhHandshakeHandler.Register();
                LinkedAccountsHandler.Register();
                PlayerStatsHandler.Register();
                TreasuryHandler.Register();
                Features.Delivery.DeliveryHandler.Register();
                Features.Notifications.NotificationHandler.Register();
                MarketplaceHandler.Register();
                QuestHandler.Register();
                GuildHandler.Register();
                Features.Leaderboards.LeaderboardHandler.Register();
                Features.ItemLabels.ItemLabelsHandler.Register();
                Features.Sites.SiteMetadataHandler.Register();
                Diagnostics.KmhClientDebugLog.Register();
                Features.Reports.ReportHandler.Register();
                Features.Sites.SiteHandler.Register();
                Features.Roadworks.RoadworksHandler.Register();
                Features.Frontier.FrontierHandler.Register();
                Features.Reputation.ReputationHandler.Register();
                Features.Enforcement.EnforcementHandler.Register();
                Features.World.WorldHandler.Register();
                Features.Auctions.AuctionHandler.Register();
                Features.WantBoard.WantHandler.Register();
                Features.Mail.MailHandler.Register();
                Features.Global.Treasury.ConnectedTreasuryHandler.Register();
                Features.Global.Guilds.ConnectedGuildHandler.Register();
                Features.Global.Federation.FederationHandler.Register();
                Features.Chat.ChatHandler.Register();
                Features.Admin.ConfigEditorHandler.Register();
                Features.Global.KmhJoinTicketHandler.Register();
                Features.Chat.ChatRosterHandler.Register();
                Features.Media.KmhMediaHandler.Register();
                Features.Chat.ChatMediaRefresh.Register();
                Features.Media.KmhVideoHandler.Register();
                Features.Seasons.SeasonHandler.Register();

                Persistence.KmhDataPaths.EnsureFolder();

                // Otherwise a dependency that resolves and then fails on first use reads as "KMH-Data is not writable".
                bool serializationOk = Diagnostics.DependencyPreflight.Check(out string depDetail);
                if (!serializationOk) Diagnostics.ServerLog.Error("Missing dependency: " + depDetail);

                // A read-only KMH-Data otherwise makes every save fail silently and read as a data-collection bug.
                if (Persistence.JsonFileStore.SelfTest(out string storageDetail))
                    Diagnostics.ServerLog.Diag($"Persistence self-test: OK - {storageDetail}");
                else
                    Diagnostics.ServerLog.Error($"Persistence SELF-TEST FAILED - Colony Nexus data will NOT save! ({storageDetail}) "
                        + (serializationOk
                            ? "Check folder permissions / free disk space for KMH-Data."
                            : "The missing dependency above is the cause - fix that first; the folder itself may be fine."));

                // Before anything reads KMH-Data, or a restore that died mid-swap reads as a fresh install.
                Persistence.KmhDataBackup.FinishInterruptedRestore();

                // Before backup, scan and load, so the whole boot sees the restored data rather than half of it.
                Persistence.KmhDataRestore.ApplyPendingRestore();

                // All of this runs BEFORE any store loads, so it sees the pristine on-disk state.
                Maintenance.MaintenanceConfig.EnsureGenerated();
                Maintenance.MaintenanceConfig maint = Maintenance.MaintenanceConfig.Current;

                // Early, so boot traces respect the debug toggle.
                Features.Transport.TransportConfig.EnsureGenerated();
                ServerLog.DebugEnabled = Features.Transport.TransportConfig.Current.Advanced.DebugLogging;

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
                Features.Global.Federation.FederationConfig.EnsureGenerated();
                Features.Mods.ModPolicyConfig.EnsureGenerated();
                Features.Frontier.FrontierConfig.EnsureGenerated();
                Features.Identity.StaffConfig.EnsureGenerated();
                Features.Global.GlobalConfig.EnsureGenerated();
                Features.FeaturesConfig.EnsureGenerated();

                Features.Discord.DiscordIconAssets.Provision();

                ServerLog.Info(Persistence.KmhDataMeta.ReconcileOnBoot());
                ServerLog.Info($"Colony Nexus server identity: '{KmhServerIdentity.Name}' (id {KmhServerIdentity.Id})");
                // Scanned even when the report is off: the backup step below must know whether the data is trustworthy.
                bool dataDamaged = false;
                if (maint.IntegrityScanOnBoot || maint.BackupOnBoot)
                {
                    Persistence.KmhDataIntegrity.ScanResult scan = Persistence.KmhDataIntegrity.Scan();
                    if (maint.IntegrityScanOnBoot) Persistence.KmhDataIntegrity.LogScan(scan);
                    dataDamaged = scan.CriticalDamage;
                }
                // Reported by an earlier boot's load; the file now looks healthy only because it was recreated empty.
                if (Persistence.JsonFileStore.DamageReported(out _)) dataDamaged = true;
                // Before any prune: the journal names a pending migration's rollback backup, and the boot backup prunes at once.
                Persistence.KmhDataBackup.Pin(Maintenance.KmhMigrationJournal.Read()?.BackupDirName);

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
                        int pruned = Persistence.KmhDataBackup.Prune(maint.BackupRetention);
                        ServerLog.Info($"Boot backup: {System.IO.Path.GetFileName(backupDir)}" +
                                       (pruned > 0 ? $" ({pruned} old backup(s) pruned)" : ""));
                    }
                    else ServerLog.Verbose($"Boot backup skipped: {backupErr}");
                }

                RegisterShutdownFlush();

                Features.Media.KmhVideoCache.Start();
                Features.Media.KmhMediaCache.ResetOnBoot();   // converted copies never survive a restart

                // Move a legacy (pre-mode) Economy.json to the recommended Balanced BEFORE backfill would lock in Standard.
                bool economyModeMigrated = Features.Economy.EconomyConfig.MigrateLegacyModeToBalanced();
                // One canonical list, or a config reaches the validator but not the backfill and its new fields stay hidden.
                int fieldsBackfilled = 0;
                foreach ((string _, string cfgPath, object cfgDefaults) in Maintenance.KmhConfigValidation.ConfigTargets())
                    fieldsBackfilled += Persistence.JsonFileStore.BackfillMissingFields(cfgPath, cfgDefaults);
                Features.Chat.ChatConfig.MergeShippedImageHosts();

                // Reloaded AFTER every config write, or the banner and access checks run on a pre-rewrite cache.
                Features.Economy.EconomyConfig.Reload();
                if (economyModeMigrated)
                    ServerLog.Warn("Economy: migrated legacy (pre-v1.2.0) config to the recommended Balanced mode - remote-convenient with light caps/cooldowns/fees. Set EconomyMode in Config/Economy.json to change it.");

                Features.Enforcement.EnforcementProfile.Reload();
                // A publish staged by the previous process cannot be resumed, so it is cleared rather than left.
                Features.Enforcement.EnforcementPublish.SweepStagingAtBoot();
                Features.Mods.ModProfile.LoadFromDisk();

                Features.LinkedAccounts.LinkedAccountsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
                Maintenance.KmhReadiness.Recovering();
                Features.Economy.EconomyResetStore.LoadFromDisk();
                Features.Economy.KmhEconomyReset.LoadFromDisk();   // holds the barrier before any store is reachable
                // Advance past the restored generation, or ids issued against the replaced data hit different rows. The
                // restored rows match the restored vaults, so recovery finishes them rather than closing them unpaid.
                if (!string.IsNullOrEmpty(Persistence.KmhDataBackup.RestoredThisBoot)
                    && Features.Economy.KmhEconomyReset.BumpDataGeneration($"restored from {Persistence.KmhDataBackup.RestoredThisBoot}", staleRows: false))
                    Persistence.KmhDataBackup.ClearRestoredThisBoot();
                Features.Treasury.TreasuryStore.LoadFromDisk();
                // Drop vault rows that were created but never written to, so a lookup alone never leaves one behind.
                Features.Treasury.TreasuryStore.PruneUnusedVaults();
                Features.Roadworks.RoadworksStore.LoadFromDisk();
                Features.Frontier.KmhWorldDirector.LoadFromDisk();
                Features.Marketplace.MarketplaceStore.LoadFromDisk();
                Features.Quests.QuestStore.LoadFromDisk();
                Features.Guilds.GuildStore.LoadFromDisk();
                Features.Guilds.Contributions.KmhGuildContributionLedger.LoadFromDisk();
                // Every store loads before recovery: a purge or hold on an unloaded store is overwritten when it loads.
                Features.Auctions.AuctionStore.LoadFromDisk();
                Features.Delivery.DeliveryStore.LoadFromDisk();
                Features.Reputation.ReputationStore.LoadFromDisk();
                Features.Sites.SiteCatalogStore.LoadFromDisk();   // before sites: reclassification reads it
                Features.Sites.SiteStore.LoadFromDisk();
                Features.World.WorldStore.LoadFromDisk();
                Features.WantBoard.WantStore.LoadFromDisk();
                Features.Mail.MailStore.LoadFromDisk();
                Features.Chat.ChatModerationStore.LoadFromDisk();
                Features.Chat.ChatStore.LoadFromDisk();
                Features.Seasons.SeasonStore.LoadFromDisk();
                Features.Notifications.NotificationStore.LoadFromDisk();
                Features.Recovery.RecoveryStore.LoadFromDisk();
                Features.Global.Treasury.ConnectedTreasuryStore.LoadFromDisk();
                Features.Global.Guilds.FederatedGuildStore.LoadFromDisk();
                Features.Global.Federation.FederationOutbox.LoadFromDisk();
                Features.ItemLabels.ItemLabelCache.LoadFromDisk();
                Features.ItemLabels.WeatherDefCache.LoadFromDisk();
                Features.Discord.DiscordUserState.LoadFromDisk();
                Policy.KmhPolicyStore.LoadFromDisk();
                Transactions.KmhTransactionRepository.LoadFromDisk();

                // Not over a store a damaged file emptied: restoring it is the fix, and recovery would have nothing to replay.
                if (Maintenance.KmhBootRecovery.ShouldRun())
                {
                    // First: a wipe the last run started must finish before anything settles into the stores it empties.
                    Maintenance.KmhSeasonReset.RecoverOnBoot();
                    Transactions.KmhTransactionRepository.RecoverOnBoot();
                    // Needs the rows loaded above: a pending take is owned by the vault only while no row names it.
                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    // Same rule for a listing: units that left one are the ledger's only while a row answers for them.
                    Features.Marketplace.MarketplaceStore.RecoverPendingReserves();
                    // A fee a vault commit owed the house pool, cut off before the pool's save held it, is credited once.
                    Features.Treasury.TreasuryStore.SettleOwedFees();
                    // Same rule: a closed message's attachment is owed to whoever the record beside it names.
                    Features.Mail.MailStore.RecoverPendingPayouts();
                    // A quest delivery handed over but not completed rolls forward; a close's owed payout is paid.
                    Features.Quests.QuestStore.RecoverOnBoot();
                    // Site payouts and road work written down with their cycle, and roadworks refunds, likewise.
                    Features.Roadworks.RoadworksStore.RecoverOnBoot();
                    Features.Sites.SiteStore.RecoverOnBoot();
                    // A guild perk charged and then interrupted is applied or refunded.
                    Features.Guilds.GuildStore.RecoverPendingPerks();
                    // And for a want closed, or a purged auction's bid, with its silver still to refund.
                    Features.WantBoard.WantStore.RecoverPendingRefunds();
                    Features.Auctions.AuctionStore.SettlePendingRefunds();
                    // A completion recorded before the crash still owes its rewards.
                    Features.World.WorldEngine.ResumePendingPayouts();
                    // Last, and still behind the readiness barrier: an unfinished reset must not be raced.
                    Features.Economy.KmhEconomyReset.RecoverOnBoot();
                }

                // Sites and World are the truth; the director only holds references and is saved at its own moments.
                Features.Frontier.KmhWorldDirector.Reconcile();

                // After the stores load, because its steps operate on loaded state rather than raw config files.
                int cfgSchemaFrom = Persistence.KmhDataMeta.AppliedConfigSchema;   // capture before the stamp is raised
                Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.Migration);
                try
                {
                    // A journal left behind means the previous migration never committed, so roll back before retrying.
                    Maintenance.KmhMigrationRecovery.RecoverIfIncomplete();

                    Maintenance.KmhConfigMigrationPlan plan = Maintenance.KmhConfigMigration.Prepare();
                    if (!plan.Needed)
                    {
                        Maintenance.KmhConfigValidation.GateOnBoot();
                        Maintenance.KmhMigrationJournal.Clear();
                    }
                    else if (plan.Blocked != null)
                    {
                        Maintenance.KmhReadiness.Failed($"config migration blocked - {plan.Blocked}");
                    }
                    else
                    {
                        // Pinned for the whole migration: the next backup prunes on creation, and this is the only old copy.
                        Persistence.KmhDataBackup.Pin(plan.BackupName);
                        try { RunConfigMigration(plan); }
                        finally { Persistence.KmhDataBackup.Unpin(plan.BackupName); }
                    }
                }
                finally { Maintenance.KmhMaintenanceGate.Release(); }

                // A migration rewrites Economy.json after the reload above, so re-read or it lands only next boot.
                if (Maintenance.KmhConfigMigration.RanThisBoot) Features.Economy.EconomyConfig.Reload();

                // Applied only once every config rewrite is done, or a cache holds the pre-rewrite copy all run.
                Features.Comms.CommsPush.Wire();
                Maintenance.KmhInvalidation.Wire();
                Features.Comms.CommsStartup.ApplyAtBoot();
                Maintenance.KmhConfigRegeneration.Announce();
                // Every boot, or an upgraded server keeps the wording of whichever build first wrote it.
                Maintenance.KmhConfigRegeneration.RefreshReadme();

                Maintenance.KmhMigrationReport.Write(new Maintenance.KmhMigrationReport.Inputs
                {
                    TimestampUtc      = System.DateTime.UtcNow.ToString("o"),
                    Build             = KmhVersion.Build,
                    FreshInstall      = Persistence.KmhDataMeta.IsFreshInstall,
                    ConfigSchemaFrom  = cfgSchemaFrom,
                    ConfigSchemaTo    = KmhVersion.ConfigSchema,
                    DataSchema        = KmhVersion.DataSchema,
                    FieldsBackfilled  = fieldsBackfilled,
                    Regenerated       = Maintenance.KmhConfigRegeneration.Regenerated.Count,
                    Kept              = Maintenance.KmhConfigRegeneration.Preserved.Count,
                    Steps             = Maintenance.KmhConfigMigration.Report,
                });

                string ecoMode = Features.Economy.EconomyConfig.Current.EconomyMode ?? "Standard";
                bool wealth = Features.FeaturesConfig.Current.Wealth;
                if (string.Equals(ecoMode, "Standard", StringComparison.OrdinalIgnoreCase))
                    ServerLog.Warn("Economy: Treasury is in Remote/Standard mode - allows off-map wealth storage and may reduce raid pressure. " +
                        "Balanced (recommended), Localized or Hardcore are safer for public servers. Set EconomyMode in Config/Economy.json.");
                else
                    ServerLog.Info($"Economy: {ecoMode} - {Features.Economy.EconomyAccess.Describe()}; Colony Nexus wealth "
                                   + (wealth ? "counts toward raids" : "is NOT counted toward raids (a safe haven)") + ".");
                Features.Economy.EconomyPolicy pol = Features.Economy.EconomyAccess.Policy;
                if (pol.DepositFeePct > 0 || pol.WithdrawFeePct > 0)
                    ServerLog.Diag($"Economy: treasury silver fees {pol.DepositFeePct:0.#}% deposit / {pol.WithdrawFeePct:0.#}% withdraw -> the house pool (same sink as marketplace tax). Item deposits/withdraws pay no fee.");
                ServerLog.Diag("Economy: passive income (site rewards, marketplace/auction/want payouts) accrues into the treasury under every access mode; a restrictive mode only gates withdrawal, so value is never stranded silently ('kmh audit-player').");
                ServerLog.Diag(wealth
                    ? "Economy: Features.Wealth is ON - Colony Nexus off-map value (treasury + escrow) counts toward RimWorld raid/threat scaling on v1.3.0+ clients, so parking wealth in Colony Nexus does not dodge raids. Set Features.json Wealth=false to make Colony Nexus a safe haven."
                    : "Economy: Features.Wealth is OFF - Colony Nexus off-map value is NOT counted in RimWorld raid/threat scaling; Colony Nexus is a safe haven from the storyteller.");
                if (Features.Economy.EconomyConfig.Current.AnyGuildHallRule)
                    ServerLog.Warn("Guilds: physical Guild Hall restrictions are ENABLED - some guild actions require a hall / proximity. See Config/Economy.json.");
                if (Features.Sites.SiteStore.LegacyBlockedSiteCount > 0)
                    ServerLog.Warn($"Sites: {Features.Sites.SiteStore.LegacyBlockedSiteCount} existing site(s) are PAUSED because their output is now blocked by current rules - review with 'kmh audit sites' and either allow the output or remove the site.");
                if (Features.Sites.SiteStore.LegacyWorkerCount > 0)
                    ServerLog.Warn($"Sites: {Features.Sites.SiteStore.LegacyWorkerCount} legacy account-worker(s) are DISABLED - sites now need real pawns (caravan -> assign). Old workers were kept, not deleted; reassign to resume production. See 'kmh validate sites'.");
                if (!Features.Sites.SitesConfig.Current.Output.UseTiers && Features.Sites.SitesConfig.Current.Output.AllowUnsafeLegacy)
                    ServerLog.Warn("Sites: UNSAFE legacy mode is ON (Sites.json Output.UseTiers=false + Output.AllowUnsafeLegacy=true) - Sites can produce ANY def. This is admin/dev-only; standard servers should leave tiers on.");
                if (Features.Recovery.RecoveryStore.HeldCount > 0)
                    ServerLog.Warn($"Recovery: {Features.Recovery.RecoveryStore.HeldCount} item/silver record(s) couldn't reach their owner and are HELD - triage with 'kmh recover list'.");

                Maintenance.KmhUpgradeSummary.Print(fieldsBackfilled);

                EnsureFile(Persistence.KmhDataPaths.GlobalSetupFile,      Features.Global.GlobalSetupState.EnsureCreated);
                EnsureFile(Persistence.KmhDataPaths.LinkedAccountsFile,   () => Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.PlayerStatsFile,      () => Features.PlayerStats.PlayerStatsStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ColonistsFile,        () => Features.PlayerStats.PlayerStatsStore.SaveColonistsToDisk());
                EnsureFile(Persistence.KmhDataPaths.TreasuryFile,         () => Features.Treasury.TreasuryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.MarketplaceFile,      () => Features.Marketplace.MarketplaceStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.QuestsFile,           () => Features.Quests.QuestStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.GuildsFile,           () => Features.Guilds.GuildStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ReputationFile,       Features.Reputation.ReputationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.SitesFile,            () => Features.Sites.SiteStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.WorldFile,            () => Features.World.WorldStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.AuctionsFile,         () => Features.Auctions.AuctionStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.WantsFile,            () => Features.WantBoard.WantStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.RecoveryFile,         () => Features.Recovery.RecoveryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ConnectedTreasuryFile, () => Features.Global.Treasury.ConnectedTreasuryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.FederatedGuildsFile, () => Features.Global.Guilds.FederatedGuildStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.FederationOutboxFile, () => Features.Global.Federation.FederationOutbox.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.NotificationsFile,    () => Features.Notifications.NotificationStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ItemLabelsFile,       Features.ItemLabels.ItemLabelCache.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.DiscordUserStateFile, Features.Discord.DiscordUserState.SaveToDisk);
                // Materialized so the next boot's scan finds a canonical empty file instead of reporting it missing.
                EnsureFile(Persistence.KmhDataPaths.RoadworksFile,        () => Features.Roadworks.RoadworksStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.FrontierDirectorFile, () => Features.Frontier.KmhWorldDirector.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.MailFile,             () => Features.Mail.MailStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.ChatFile,             Features.Chat.ChatStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ChatModerationFile,   Features.Chat.ChatModerationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.SeasonsFile,          () => Features.Seasons.SeasonStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.DeliveryFile,         () => Features.Delivery.DeliveryStore.SaveToDisk());
                EnsureFile(Persistence.KmhDataPaths.GuildContributionsFile, () => Features.Guilds.Contributions.KmhGuildContributionLedger.SaveToDisk());
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
                                                  Features.Frontier.KmhWorldDirector.Tick,
                                                  Maintenance.KmhJobKind.Mutating, TimeSpan.FromMinutes(2));

                DiscordBridge.Start();
                Features.Discord.DiscordLive.Start();
                Features.Discord.DiscordEventPublisher.Start();
                Features.Discord.DiscordGuildRoleSync.Start();
                Features.Discord.DiscordConsoleFeed.Start();
                Features.Discord.KmhWorldChatDiscordBridge.Start();

                Maintenance.KmhOwnerNotice.ScheduleIfNeeded();

                Extensibility.ExtensionLoader.DiscoverAndLoad();

                // Last: value moves must not act on half-loaded state, nor on a store an unreadable file left empty.
                if (Persistence.JsonFileStore.DamageReported(out string damage)) Maintenance.KmhReadiness.Failed(damage);
                Maintenance.KmhReadiness.Ready();

                // After Ready, or the first beat describes a half-loaded server; skipping it drops a restart from the browser.
                Features.Global.KmhGlobalService.Start();

                int patchCount = HarmonyInstance.GetPatchedMethods().Count();
                ServerLog.Info($"Bootstrap complete - {patchCount} method(s) patched, Colony Nexus handlers registered, {Extensibility.ExtensionLoader.Loaded.Count} extension(s) loaded");
                AdminCommands.KmhDoctor.LogBootSummary();

                string prevBuild = Persistence.KmhDataMeta.PreviousBuildVersion;
                string curBuild  = typeof(Persistence.KmhDataMeta).Assembly.GetName().Version?.ToString() ?? "";
                if (!string.IsNullOrEmpty(prevBuild) && prevBuild != curBuild)
                    Maintenance.KmhWhatsNewBanner.Print(prevBuild);
            }
            catch (System.Exception ex)
            {
                // A KMH boot failure must not abort RWT, so nothing here throws - but a half-loaded KMH must not serve players.
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

        // Every exit but one leaves the journal in place: an uncommitted journal is what makes the next boot roll back.
        private static void RunConfigMigration(Maintenance.KmhConfigMigrationPlan plan)
        {
            Maintenance.KmhMigrationJournal journal =
                Maintenance.KmhMigrationJournal.Begin(plan.BackupName, out string journalErr);
            string failure = journal == null
                ? $"its journal could not be written ({journalErr}), and a migration without one cannot be rolled back"
                : Maintenance.KmhConfigMigration.Apply(plan);

            if (failure != null)
            {
                Maintenance.KmhReadiness.Failed(
                    $"config migration v{plan.From} -> v{plan.To} did not complete - {failure}. The journal was left "
                    + $"in place, so the next start rolls back to '{plan.BackupName}'");
                return;
            }

            Maintenance.KmhConfigValidation.GateOnBoot();
            if (journal.Commit(out string commitErr)) Maintenance.KmhMigrationJournal.Clear();
            else Maintenance.KmhReadiness.Failed($"the migration finished but could not be committed ({commitErr})");
        }

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
