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

                // Release-safety pass, all BEFORE any store loads so it sees/preserves the pristine on-disk state:
                // (1) reconcile the data-format stamp (backs up + migrates only if a format change shipped),
                // (2) integrity-scan every KMH JSON and report (loud if irreplaceable data is damaged),
                // (3) take the once-per-boot safety backup and prune old copies.
                Maintenance.MaintenanceConfig.EnsureGenerated();
                Maintenance.MaintenanceConfig maint = Maintenance.MaintenanceConfig.Current;

                // Seed transport config + apply its debug toggle early so boot traces respect it. API transport off by default.
                Features.Transport.TransportConfig.EnsureGenerated();
                ServerLog.DebugEnabled = Features.Transport.TransportConfig.Current.DebugLogging;

                ServerLog.Info(Persistence.KmhDataMeta.ReconcileOnBoot());
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
                Features.Enforcement.EnforcementProfile.Reload();

                // Loads each feature's saved state from KMH-Data/.
                Features.LinkedAccounts.LinkedAccountsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
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
                Features.ItemLabels.ItemLabelCache.LoadFromDisk();
                Features.Discord.DiscordUserState.LoadFromDisk();

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
                EnsureFile(Persistence.KmhDataPaths.NotificationsFile,    Features.Notifications.NotificationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ItemLabelsFile,       Features.ItemLabels.ItemLabelCache.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.DiscordUserStateFile, Features.Discord.DiscordUserState.SaveToDisk);

                // One-time prime of the marketplace house pool on a brand-new server so global-quest rewards can be
                // funded before any tax revenue accrues. No-ops on every later boot (the seeded flag persists).
                Features.Marketplace.MarketplaceStore.SeedHousePoolOnce(Features.World.WorldConfig.Current.HousePoolSeed);

                // Durable economy audit trail: start the background flusher that writes queued ledger lines to disk.
                Persistence.TransactionLedger.Start();

                // KMH API listener - no-ops unless EnableKmhApiTransport=true (off by default)
                Features.Transport.KmhApiServer.Start();

                // Periodically sweeps expired listings and quests with a cheap 60s background pass.
                Maintenance.ExpirySweeper.Start();

                // World Engine: expires ended events, optionally auto-rolls new ones (off by default in World.json).
                Features.World.WorldEngine.Start();

                // Loads the optional Discord bridge config and starts the bot safely in the background when enabled.
                DiscordBridge.Start();
                // Periodically posts the top-N player leaderboard when the Discord bridge and channel are configured.
                DiscordLeaderboardPoster.Start();
                // Periodically refreshes user showcase posts when the Discord bridge/channel/interval are enabled.
                Features.Discord.DiscordShowcaseSweep.Start();
                // Subscribes to KMH events and posts enabled branded Discord embeds to their configured channels.
                Features.Discord.DiscordEventPublisher.Start();
                // Hooks RWT logging after startup and mirrors console output to the configured Discord Admin channel.
                Features.Discord.DiscordConsoleFeed.Start();

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
