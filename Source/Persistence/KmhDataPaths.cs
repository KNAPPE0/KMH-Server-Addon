using System.Collections.Generic;
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

        // One catalog of every KMH JSON file, shared by the integrity scan, the diag report, and the force-save flush
        // so they never drift out of sync. Regenerable = rebuilds from defaults/clients if lost (configs, caches);
        // the rest is irreplaceable runtime state worth shouting about if it goes corrupt
        public readonly struct DataFile
        {
            public readonly string Label;
            public readonly string Path;
            public readonly bool   Regenerable;
            public DataFile(string label, string path, bool regenerable) { Label = label; Path = path; Regenerable = regenerable; }
        }

        public static IReadOnlyList<DataFile> KnownDataFiles => new[]
        {
            // Irreplaceable runtime state
            new DataFile("Players/PlayerStats", PlayerStatsFile,    false),
            new DataFile("Players/Colonists",   ColonistsFile,      false),
            new DataFile("Treasury",            TreasuryFile,       false),
            new DataFile("Marketplace",         MarketplaceFile,    false),
            new DataFile("Quests",              QuestsFile,         false),
            new DataFile("Guilds",              GuildsFile,         false),
            new DataFile("Reputation",          ReputationFile,     false),
            new DataFile("Sites",               SitesFile,          false),
            new DataFile("World",               WorldFile,          false),
            new DataFile("Auctions",            AuctionsFile,       false),
            new DataFile("WantBoard",           WantsFile,          false),
            new DataFile("Notifications",       NotificationsFile,  false),
            new DataFile("Recovery",            RecoveryFile,       false),
            new DataFile("Seasons",             SeasonsFile,        false),
            new DataFile("Accounts",            LinkedAccountsFile, false),
            // Regenerable - rebuilt from clients/defaults on the next run if lost
            new DataFile("Catalog/ItemLabels",  ItemLabelsFile,           true),
            new DataFile("Catalog/WeatherDefs", WeatherDefsFile,          true),
            new DataFile("Players/SaveIds",     SaveIdsFile,              true),
            new DataFile("Discord/UserState",   DiscordUserStateFile,     true),
            new DataFile("Config/Economy",      EconomyConfigFile,        true),
            new DataFile("Config/Sites",        SitesConfigFile,          true),
            new DataFile("Config/Reputation",   ReputationConfigFile,     true),
            new DataFile("Config/Quests",       QuestsConfigFile,         true),
            new DataFile("Config/Enforcement",  EnforcementConfigFile,    true),
            new DataFile("Config/World",        WorldConfigFile,          true),
            new DataFile("Config/Maintenance",  MaintenanceConfigFile,    true),
            new DataFile("Config/Transport",    TransportConfigFile,      true),
            new DataFile("Config/Discord",      DiscordConfigFile,        true),
            new DataFile("Config/Features",     FeaturesConfigFile,       true),
        };

        // The folder containing KMHServerAddon.exe (binary-rooted, vs Folder which is data-rooted at the RWT cwd).
        // Extensions find their drop-folder here
        public static string AddonDir => System.AppContext.BaseDirectory;

        // Backups live in a SIBLING folder, never inside KMH-Data, so a backup pass never recurses into itself and a
        // wipe of KMH-Data leaves the backups standing
        public static string BackupRoot => Path.Combine(Master.MainPath ?? Directory.GetCurrentDirectory(), "KMH-Data-Backups");

        // Data-format stamp + last-run marker (dotfile so it sorts/hides out of the way). Drives the migration guard
        public static string MetaFile => Path.Combine(Folder, ".kmh-meta.json");

        // Coordinated-rollback marker. In the backups folder (sibling) so it survives a KMH-Data wipe and an external
        // rollback tool can drop it. One line: a backup folder name, "latest", or "before:<iso|yyyyMMdd-HHmmss>".
        public static string RestoreRequestFile => Path.Combine(BackupRoot, ".kmh-restore-request");

        // Machine-readable status snapshot for external tooling (dashboards, monitoring, rollback correlation).
        // Rewritten on a cadence, so its freshness also serves as a liveness heartbeat.
        public static string StatusFile => Path.Combine(Folder, "status.json");

        // Snapshots/<Season>/<PlayerId|_server>/<YYYY-MM-DD_HH-MM>/ - timestamp-matchable recovery snapshots.
        public static string SnapshotsRoot => Sub("Snapshots");

        // External snapshot-request marker, consumed on the sweep ("player <user> [ts]" | "server [ts]" | "all [ts]").
        public static string SnapshotRequestFile => Path.Combine(Folder, ".kmh-snapshot-request");

        // Append-only economy audit trail (daily JSONL files). Not in KnownDataFiles - JSONL isn't a single JSON doc,
        // so the integrity scan (which JToken-parses whole files) skips it by design
        public static string LedgerDir => Sub("Ledger");

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
        public static string WorldConfigFile        => Path.Combine(Sub("Config"), "World.json");
        public static string MaintenanceConfigFile  => Path.Combine(Sub("Config"), "Maintenance.json");
        public static string TransportConfigFile     => Path.Combine(Sub("Config"), "Transport.json");
        public static string FeaturesConfigFile      => Path.Combine(Sub("Config"), "Features.json");
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
        public static string WorldFile           => Path.Combine(Sub("World"),       "World.json");
        public static string AuctionsFile        => Path.Combine(Sub("Auctions"),    "Auctions.json");
        public static string NotificationsFile   => Path.Combine(Sub("Notifications"), "Notifications.json");
        public static string RecoveryFile        => Path.Combine(Sub("Recovery"),     "Recovery.json");
        public static string WantsFile           => Path.Combine(Sub("WantBoard"),   "Wants.json");
        public static string SeasonsFile         => Path.Combine(Sub("Seasons"),     "Seasons.json");
        public static string PlayerStatsFile     => Path.Combine(Sub("Players"),     "PlayerStats.json");
        public static string SaveIdsFile         => Path.Combine(Sub("Players"),     "SaveIds.json");
        public static string ColonistsFile       => Path.Combine(Sub("Players"),     "Colonists.json");
        public static string LinkedAccountsFile  => Path.Combine(Sub("Accounts"),    "LinkedAccounts.json");
        public static string ItemLabelsFile      => Path.Combine(Sub("Catalog"),     "ItemLabels.json");
        public static string WeatherDefsFile     => Path.Combine(Sub("Catalog"),     "WeatherDefs.json");
        public static string DiscordUserStateFile        => Path.Combine(Sub("Discord"), "UserState.json");
        public static string DiscordGuildRolesFile       => Path.Combine(Sub("Discord"), "GuildRoles.json");
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
            "Enforcement", Path.Combine("Enforcement", "Profile"), "Icons", "Notifications", "WantBoard", "Seasons",
            "Ledger", "Debug",
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
