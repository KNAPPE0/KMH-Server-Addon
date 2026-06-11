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
    // installs Harmony patches, registers KMH handlers, loads persisted state, starts the Discord bridge +
    // sweepers, then runs RWT's own Main in-process
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

        // Installs patches + registers handlers + loads state. Never throws.
        public static void RunKmhBoot()
        {
            try
            {
                BootstrapUtc = System.DateTime.UtcNow;
                System.Console.WriteLine(
                    $"{Constants.LogPrefix} {Constants.DisplayName} v{typeof(Main_).Assembly.GetName().Version} bootstrapping…");

                // Install all [HarmonyPatch] classes in this assembly. Harmony.PatchAll only REGISTERS - patches
                // fire when their targets are first called (well after RWT's Main starts)
                HarmonyInstance = new Harmony(HarmonyId);
                HarmonyInstance.PatchAll(typeof(Main_).Assembly);

                // Register KMH handshake / ping / pong handlers + per-feature handlers. Each feature is
                // self-contained - registering it is one line; removing it is deleting its folder + this line
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

                // Load every feature's persisted state from KMH-Data/. Each call is no-op + log-warn if the file is
                // missing or malformed - first-run / fresh server starts with empty stores
                Persistence.KmhDataPaths.EnsureFolder();

                // Configs: generate a ready-to-edit file with defaults on first boot so owners tune a real config
                // we give them (no example to hunt for). Values are clamped on load
                Features.Economy.EconomyConfig.EnsureGenerated();
                Features.Sites.SitesConfig.EnsureGenerated();
                Features.Discord.DiscordConfig.EnsureGenerated();
                Features.Reputation.ReputationConfig.EnsureGenerated();
                Features.Quests.QuestsConfig.EnsureGenerated();
                Features.Enforcement.EnforcementConfig.EnsureGenerated();
                Features.Enforcement.EnforcementProfile.Reload(); // read Enforcement/Profile/ at boot

                // Load every feature's persisted state from KMH-Data/.
                Features.LinkedAccounts.LinkedAccountsStore.LoadFromDisk();
                Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
                Features.Treasury.TreasuryStore.LoadFromDisk();
                Features.Marketplace.MarketplaceStore.LoadFromDisk();
                Features.Quests.QuestStore.LoadFromDisk();
                Features.Guilds.GuildStore.LoadFromDisk();
                Features.Reputation.ReputationStore.LoadFromDisk();
                Features.Sites.SiteStore.LoadFromDisk();
                Features.ItemLabels.ItemLabelCache.LoadFromDisk();
                Features.Discord.DiscordUserState.LoadFromDisk();

                // Materialize each state file on first boot so every KMH-Data domain folder ships its file (empty
                // defaults) instead of sitting empty until the first write
                EnsureFile(Persistence.KmhDataPaths.LinkedAccountsFile,   Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.PlayerStatsFile,      Features.PlayerStats.PlayerStatsStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.TreasuryFile,         Features.Treasury.TreasuryStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.MarketplaceFile,      Features.Marketplace.MarketplaceStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.QuestsFile,           Features.Quests.QuestStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.GuildsFile,           Features.Guilds.GuildStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ReputationFile,       Features.Reputation.ReputationStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.SitesFile,            Features.Sites.SiteStore.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.ItemLabelsFile,       Features.ItemLabels.ItemLabelCache.SaveToDisk);
                EnsureFile(Persistence.KmhDataPaths.DiscordUserStateFile, Features.Discord.DiscordUserState.SaveToDisk);

                // Periodic background sweeper for expired listings + quests. Cheap (60s interval, microsecond pass
                // over small stores)
                Maintenance.ExpirySweeper.Start();

                // Optional Discord bridge. Reads Config/Discord/DiscordConfig.json and silently skips startup when
                // the file is missing or the bot token is empty. With a token configured, the bot logs in on a
                // background task - its failure mode is isolated, so the addon stays up even if Discord is
                // unreachable
                DiscordBridge.Start();

                // Leaderboard auto-poster. Self-disables when the bridge is off or leaderboard_channel_id is 0;
                // otherwise posts a top-N player embed on the configured cadence
                DiscordLeaderboardPoster.Start();

                // Showcase sweep - refreshes every active per-user showcase post on the configured cadence so
                // listings stay current with treasury moves. Self-disables when the bridge is off / showcase
                // channel is unconfigured / interval < 1
                Features.Discord.DiscordShowcaseSweep.Start();

                // Event publisher - subscribes to the KMH event bus and posts branded embeds (new listing, item
                // sold, site built, quest completed, guild created) to their configured channels. Each post
                // self-gates on the Embeds.Post* toggles + channel ids
                Features.Discord.DiscordEventPublisher.Start();

                // Extension loader - scans kmh-extensions/ for *.dll containing types that implement
                // IKmhServerExtension, instantiates each, calls Register(host). Failed loads are logged + skipped;
                // KMH bootstrap continues. Creates the folder with a tiny README on first boot if missing
                Extensibility.ExtensionLoader.DiscoverAndLoad();

                int patchCount = HarmonyInstance.GetPatchedMethods().Count();
                ServerLog.Info($"Bootstrap complete - {patchCount} method(s) patched, KMH handlers registered, {Extensibility.ExtensionLoader.Loaded.Count} extension(s) loaded");
            }
            catch (System.Exception ex)
            {
                // Recoverable - if KMH boot fails, RWT's own Main still runs right after this hook, so the stock
                // server comes up unpatched rather than crashing. NEVER rethrow: a startup-hook exception would
                // abort the whole GameServer process
                System.Console.Error.WriteLine($"{Constants.LogPrefix} Bootstrap failed: {ex}");
            }
        }

        // Write a store's current (usually empty) state if its file doesn't exist yet, so the KMH-Data domain
        // folder ships its file on first boot
        private static void EnsureFile(string path, System.Action save)
        {
            try { if (!System.IO.File.Exists(path)) save(); }
            catch (System.Exception ex) { ServerLog.Warn($"Could not materialize {System.IO.Path.GetFileName(path)}: {ex.Message}"); }
        }
    }
}
