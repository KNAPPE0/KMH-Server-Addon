using System.IO;
using GameServer.Core;

namespace KMHServerAddon.Persistence
{
    // Canonical locations for KMH state. Everything lives under <RWT cwd>/KMH-Data/ (PascalCase, never inside RWT's
    // own folders), resolved lazily so it's safe to read any time after RWT's Main() sets Master.MainPath
    internal static class KmhDataPaths
    {
        public const string FolderName = "KMH-Data";

        public static string Folder => Path.Combine(Master.MainPath ?? Directory.GetCurrentDirectory(), FolderName);

        // The folder containing KMHServerAddon.exe (binary-rooted, vs Folder which is data-rooted at the RWT cwd).
        // Extensions find their drop-folder here
        public static string AddonDir => System.AppContext.BaseDirectory;

        private static string Sub(params string[] parts)
        {
            string p = Folder;
            foreach (string s in parts) p = Path.Combine(p, s);
            return p;
        }

        // Config/ - everything an owner edits by hand. Discord gets its own subfolder so its (larger) config
        // doesn't crowd the rest
        public static string EconomyConfigFile    => Path.Combine(Sub("Config"), "Economy.json");
        public static string SitesConfigFile       => Path.Combine(Sub("Config"), "Sites.json");
        public static string ReputationConfigFile  => Path.Combine(Sub("Config"), "Reputation.json");
        public static string QuestsConfigFile       => Path.Combine(Sub("Config"), "Quests.json");
        public static string EnforcementConfigFile  => Path.Combine(Sub("Config"), "Enforcement.json");
        public static string DiscordConfigFile      => Path.Combine(Sub("Config", "Discord"), "DiscordConfig.json");

        // Hard enforcement: the owner drops the exact mod-config (.xml) files to enforce into
        // EnforcementProfileDir; the server pushes them to clients
        public static string EnforcementProfileDir  => Sub("Enforcement", "Profile");

        // Per-domain runtime state.
        public static string TreasuryFile        => Path.Combine(Sub("Treasury"),    "Treasury.json");
        public static string MarketplaceFile     => Path.Combine(Sub("Marketplace"), "Marketplace.json");
        public static string QuestsFile          => Path.Combine(Sub("Quests"),      "Quests.json");
        public static string GuildsFile          => Path.Combine(Sub("Guilds"),      "Guilds.json");
        public static string ReputationFile      => Path.Combine(Sub("Reputation"),  "Reputation.json");
        public static string SitesFile           => Path.Combine(Sub("Sites"),       "Sites.json");
        public static string PlayerStatsFile     => Path.Combine(Sub("Players"),     "PlayerStats.json");
        public static string LinkedAccountsFile  => Path.Combine(Sub("Accounts"),    "LinkedAccounts.json");
        public static string ItemLabelsFile      => Path.Combine(Sub("Catalog"),     "ItemLabels.json");
        public static string DiscordUserStateFile        => Path.Combine(Sub("Discord"), "UserState.json");
        public static string DiscordLeaderboardStateFile => Path.Combine(Sub("Discord"), "LeaderboardState.json");

        // Discord embed icons. Auto-created so the owner just drops icons here - no Assets/Icons folder to set up
        // next to the exe by hand
        public static string IconsDir => Sub("Icons");

        // The full subfolder set. Used by EnsureFolder so the layout appears on first run even before anything is
        // saved
        private static readonly string[] Domains =
        {
            "Config", Path.Combine("Config", "Discord"),
            "Treasury", "Marketplace", "Quests", "Guilds",
            "Reputation", "Sites", "Players", "Accounts", "Catalog", "Discord",
            "Enforcement", Path.Combine("Enforcement", "Profile"), "Icons",
        };

        // Idempotent - safe to call repeatedly. Creates KMH-Data/ and every domain subfolder, and drops the Icons
        // readme so owners know what to add
        public static void EnsureFolder()
        {
            foreach (string d in Domains)
                try { Directory.CreateDirectory(Path.Combine(Folder, d)); } catch { /* logged at write time */ }

            try
            {
                string readme = Path.Combine(IconsDir, "README.txt");
                if (!File.Exists(readme)) File.WriteAllText(readme, IconsReadme);
            }
            catch { }
        }

        private const string IconsReadme =
@"KMH Discord embed icons
=======================
Drop icons here and the bot attaches them to its embeds. Names are
extension-less - for each name below use any format Discord can show
(.png / .webp / .gif / .jpg). A missing icon just posts without a thumbnail;
kmh_logo is the fallback. (Discord can't display .dds - raster only.)

Names: kmh_logo, marketplace, leaderboard, site_created, site_destroyed,
quest, guild, treasury, warning, error.

Turn the whole feature on/off with UseBundledIcons in
Config/Discord/DiscordConfig.json.
";
    }
}
