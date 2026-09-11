using System.Collections.Generic;
using System.IO;

namespace KMHServerAddon.Persistence
{
    // Every path resolves lazily: Master.MainPath is not set until RWT's Main() runs, so none of these can be a const.
    internal static class KmhDataPaths
    {
        public const string FolderName = "KMH-Data";

        public static string Folder => Path.Combine(Master.MainPath ?? Directory.GetCurrentDirectory(), FolderName);

        // The integrity scan, the diag report and the force-save flush all read this, so they cannot drift apart.
        public readonly struct DataFile
        {
            public readonly string Label;
            public readonly string Path;
            public readonly bool   Regenerable;
            // Absence is a finding only for a file a healthy server should already have.
            public readonly bool   AbsentIsNormal;
            public DataFile(string label, string path, bool regenerable, bool absentIsNormal = false)
            { Label = label; Path = path; Regenerable = regenerable; AbsentIsNormal = absentIsNormal; }
        }

        public static IReadOnlyList<DataFile> KnownDataFiles => new[]
        {
            new DataFile("Players/PlayerStats", PlayerStatsFile,    false),
            new DataFile("Players/Colonists",   ColonistsFile,      false),
            new DataFile("Treasury",            TreasuryFile,       false),
            new DataFile("Marketplace",         MarketplaceFile,    false),
            new DataFile("Quests",              QuestsFile,         false),
            new DataFile("Guilds",              GuildsFile,         false),
            new DataFile("Reputation",          ReputationFile,     false),
            new DataFile("Sites",               SitesFile,          false),
            new DataFile("Roadworks",           RoadworksFile,      false),
            new DataFile("FrontierDirector",    FrontierDirectorFile, false),
            new DataFile("World",               WorldFile,          false),
            new DataFile("Auctions",            AuctionsFile,       false),
            new DataFile("WantBoard",           WantsFile,          false),
            new DataFile("Mail",                MailFile,           false),
            new DataFile("ChatModeration",      ChatModerationFile, false),
            // Not regenerable despite being a log: losing it loses real player conversation.
            new DataFile("Chat",                ChatFile,           false),
            new DataFile("Notifications",       NotificationsFile,  false),
            new DataFile("Recovery",            RecoveryFile,       false),
            new DataFile("Delivery/Outbound",   DeliveryFile,       false),
            new DataFile("Seasons",             SeasonsFile,        false),
            new DataFile("Accounts",            LinkedAccountsFile, false),
            new DataFile("Guilds/Contributions", GuildContributionsFile, false),
            new DataFile("Transactions/Ledger", TransactionLedgerFile,  false),
            // Nothing generates this one, so absence is normal - but an existing one is owner intent, never rebuilt.
            new DataFile("Config/Policies",     PoliciesFile,       false, true),
            // Client-pushed, so a server no client has connected to yet legitimately has none of these.
            new DataFile("Catalog/ItemLabels",  ItemLabelsFile,           true, true),
            new DataFile("Catalog/WeatherDefs", WeatherDefsFile,          true, true),
            new DataFile("Players/SaveIds",     SaveIdsFile,              true, true),
            // Only exists while a reset is unfinished or after one has completed, so absence is normal.
            new DataFile("Players/EconomyReset", EconomyResetFile,        true, true),
            // Discord is optional, so none of its state exists on a server that never linked a bot.
            new DataFile("Discord/UserState",   DiscordUserStateFile,        true, true),
            new DataFile("Discord/GuildRoles",  DiscordGuildRolesFile,       true, true),
            new DataFile("Discord/Leaderboard", DiscordLeaderboardStateFile, true, true),
            // Absent MEANS no migration is in flight, so this must never be created to reach a zero missing count.
            new DataFile("Migrations/Journal",  MigrationJournalFile,        true, true),
            new DataFile("Config/Economy",      EconomyConfigFile,        true),
            new DataFile("Config/Sites",        SitesConfigFile,          true),
            new DataFile("Config/Frontier",     FrontierConfigFile,       true),
            new DataFile("Config/Reputation",   ReputationConfigFile,     true),
            new DataFile("Config/Quests",       QuestsConfigFile,         true),
            new DataFile("Config/Enforcement",  EnforcementConfigFile,    true),
            new DataFile("Config/World",        WorldConfigFile,          true),
            new DataFile("Config/Maintenance",  MaintenanceConfigFile,    true),
            new DataFile("Config/Transport",    TransportConfigFile,      true),
            new DataFile("Config/Chat",         ChatConfigFile,           true),
            new DataFile("Config/Mail",         MailConfigFile,           true),
            new DataFile("Config/Media",        MediaConfigFile,          true),
            new DataFile("Config/Staff",        StaffConfigFile,          true),
            new DataFile("Sites/Catalog",       SiteCatalogFile,          true, true),
            new DataFile("Config/Discord",      DiscordConfigFile,        true),
            new DataFile("Config/Features",     FeaturesConfigFile,       true),
        };

        // Binary-rooted, unlike Folder, which is data-rooted at the RWT cwd.
        public static string AddonDir => System.AppContext.BaseDirectory;

        // A sibling, or a backup pass recurses into itself and a wipe of KMH-Data takes the backups with it.
        public static string BackupRoot => Path.Combine(Master.MainPath ?? Directory.GetCurrentDirectory(), "KMH-Data-Backups");

        public static string MetaFile => Path.Combine(Folder, ".kmh-meta.json");

        // One line: a backup folder name, "latest", or "before:<iso|yyyyMMdd-HHmmss>".
        public static string RestoreRequestFile => Path.Combine(BackupRoot, ".kmh-restore-request");

        // Rewritten on a cadence, so its freshness doubles as a liveness heartbeat for external monitoring.
        public static string StatusFile => Path.Combine(Folder, "status.json");

        public static string SnapshotsRoot => Sub("Snapshots");

        public static string SnapshotRequestFile => Path.Combine(Folder, ".kmh-snapshot-request");

        // Not a DataFile: these are daily JSONL, and the integrity scan JToken-parses whole documents.
        public static string LedgerDir => Sub("Ledger");

        private static string Sub(params string[] parts)
        {
            string p = Folder;
            foreach (string s in parts) p = Path.Combine(p, s);
            return p;
        }

        public static string EconomyConfigFile    => Path.Combine(Sub("Config"), "Economy.json");
        public static string SitesConfigFile       => Path.Combine(Sub("Config"), "Sites.json");
        public static string ReputationConfigFile  => Path.Combine(Sub("Config"), "Reputation.json");
        public static string QuestsConfigFile       => Path.Combine(Sub("Config"), "Quests.json");
        public static string EnforcementConfigFile  => Path.Combine(Sub("Config"), "Enforcement.json");
        public static string WorldConfigFile        => Path.Combine(Sub("Config"), "World.json");
        public static string MaintenanceConfigFile  => Path.Combine(Sub("Config"), "Maintenance.json");
        public static string TransportConfigFile     => Path.Combine(Sub("Config"), "Transport.json");
        public static string ChatConfigFile          => Path.Combine(Sub("Config"), "Chat.json");
        public static string MailConfigFile          => Path.Combine(Sub("Config"), "Mail.json");
        public static string MediaConfigFile         => Path.Combine(Sub("Config"), "Media.json");
        // Deliberately not a DataFile: converted copies of other people's media, expiring and never worth restoring.
        public static string MediaCacheDir           => Sub("MediaCache");
        public static string DebugDir                => Sub("Debug");
        public static string StaffConfigFile         => Path.Combine(Sub("Config"), "Staff.json");
        public static string SiteCatalogFile         => Path.Combine(Sub("Sites"), "Catalog.json");
        public static string FeaturesConfigFile      => Path.Combine(Sub("Config"), "Features.json");
        public static string PoliciesFile            => Path.Combine(Sub("Config"), "Policies.json");
        public static string MigrationsDir           => Sub("Migrations");
        public static string MigrationReportLatest   => Path.Combine(MigrationsDir, "latest.txt");
        public static string MigrationJournalFile    => Path.Combine(MigrationsDir, "journal.json");
        public static string DiscordConfigFile      => Path.Combine(Sub("Config", "Discord"), "DiscordConfig.json");

        // The owner drops the exact mod-config .xml files to enforce here; the server pushes them to clients.
        public static string EnforcementProfileDir  => Sub("Enforcement", "Profile");

        public static string TreasuryFile        => Path.Combine(Sub("Treasury"),    "Treasury.json");
        public static string MarketplaceFile     => Path.Combine(Sub("Marketplace"), "Marketplace.json");
        public static string QuestsFile          => Path.Combine(Sub("Quests"),      "Quests.json");
        public static string GuildsFile          => Path.Combine(Sub("Guilds"),      "Guilds.json");
        public static string GuildContributionsFile => Path.Combine(Sub("Guilds"), "Contributions.json");
        public static string TransactionLedgerFile   => Path.Combine(Sub("Transactions"), "Ledger.json");
        public static string ReputationFile      => Path.Combine(Sub("Reputation"),  "Reputation.json");
        public static string SitesFile           => Path.Combine(Sub("Sites"),       "Sites.json");
        public static string RoadworksFile       => Path.Combine(Sub("Roads"),       "Roads.json");
        public static string FrontierDirectorFile => Path.Combine(Sub("Frontier"),   "Director.json");
        public static string FrontierConfigFile  => Path.Combine(Sub("Config"),      "Frontier.json");
        public static string WorldFile           => Path.Combine(Sub("World"),       "World.json");
        public static string AuctionsFile        => Path.Combine(Sub("Auctions"),    "Auctions.json");
        public static string NotificationsFile   => Path.Combine(Sub("Notifications"), "Notifications.json");
        public static string RecoveryFile        => Path.Combine(Sub("Recovery"),     "Recovery.json");
        public static string DeliveryFile        => Path.Combine(Sub("Delivery"),     "Outbound.json");
        public static string WantsFile           => Path.Combine(Sub("WantBoard"),   "Wants.json");
        public static string MailFile            => Path.Combine(Sub("Mail"),        "Mail.json");
        public static string ChatModerationFile  => Path.Combine(Sub("ChatModeration"), "Blocks.json");
        public static string ChatFile            => Path.Combine(Sub("Chat"),         "Chat.json");
        public static string SeasonsFile         => Path.Combine(Sub("Seasons"),     "Seasons.json");
        public static string PlayerStatsFile     => Path.Combine(Sub("Players"),     "PlayerStats.json");
        public static string SaveIdsFile         => Path.Combine(Sub("Players"),     "SaveIds.json");
        public static string EconomyResetFile    => Path.Combine(Sub("Players"),     "EconomyReset.json");
        public static string ColonistsFile       => Path.Combine(Sub("Players"),     "Colonists.json");
        public static string LinkedAccountsFile  => Path.Combine(Sub("Accounts"),    "LinkedAccounts.json");
        public static string ItemLabelsFile      => Path.Combine(Sub("Catalog"),     "ItemLabels.json");
        public static string WeatherDefsFile     => Path.Combine(Sub("Catalog"),     "WeatherDefs.json");
        public static string DiscordUserStateFile        => Path.Combine(Sub("Discord"), "UserState.json");
        public static string DiscordGuildRolesFile       => Path.Combine(Sub("Discord"), "GuildRoles.json");
        public static string DiscordLeaderboardStateFile => Path.Combine(Sub("Discord"), "LeaderboardState.json");

        public static string IconsDir => Sub("Icons");

        // Directories nothing in KnownDataFiles lives in, so they cannot be derived from it.
        private static readonly string[] ExtraDomains =
        {
            Path.Combine("Enforcement", "Profile"), "Icons", "Ledger", "Debug", "MediaCache", "Snapshots",
        };

        // Derived, never hand-listed: a hand-kept copy drifts, and a data file whose folder is missing cannot be written.
        public static IEnumerable<string> RequiredDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataFile f in KnownDataFiles)
            {
                string dir = Path.GetDirectoryName(f.Path);
                if (string.IsNullOrEmpty(dir) || seen.Add(dir)) { if (!string.IsNullOrEmpty(dir)) yield return dir; }
            }
            foreach (string d in ExtraDomains)
            {
                string full = Path.Combine(Folder, d);
                if (seen.Add(full)) yield return full;
            }
        }

        public static void EnsureFolder()
        {
            try { Directory.CreateDirectory(Folder); } catch { /* logged at write time */ }
            foreach (string d in RequiredDirectories())
                try { Directory.CreateDirectory(d); } catch { }

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
