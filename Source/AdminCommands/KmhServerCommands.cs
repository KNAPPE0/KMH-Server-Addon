using System;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.AdminCommands
{
    // Shared implementation of the kmh admin commands, used by BOTH the in-game chat command (/kmh ...) and the
    // server console command (kmh ...). Output goes through a reply delegate so each caller routes it to the right
    // place - chat to the player, console to Printer
    //
    // args[0] is the subcommand; args[1..] its parameters. isAdmin gates the mutating subcommands (the console
    // operator is always treated as admin)
    internal static class KmhServerCommands
    {
        public static void Dispatch(string[] args, bool isAdmin, string actorName, Action<string> reply)
        {
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "status":         Status(reply);                          break;
                case "extensions":     Extensions(reply);                      break;
                case "give-silver":    Admin(isAdmin, "give-silver",    reply, () => GiveSilver(args, actorName, reply));  break;
                case "reload-discord": Admin(isAdmin, "reload-discord", reply, () => ReloadDiscord(actorName, reply));     break;
                case "reload-economy": Admin(isAdmin, "reload-economy", reply, () => ReloadEconomy(reply));                break;
                case "drain-house":    Admin(isAdmin, "drain-house",    reply, () => DrainHouse(args, reply));             break;
                case "enforce":        Enforce(args, isAdmin, reply);          break;
                default:               Help(reply);                            break;
            }
        }

        private static void Admin(bool isAdmin, string name, Action<string> reply, Action run)
        {
            if (!isAdmin) { reply($"'{name}' requires admin."); return; }
            run();
        }

        private static void Status(Action<string> reply)
        {
            DateTime boot   = Main_.BootstrapUtc;
            string   uptime = boot == DateTime.MinValue ? "(unknown)" : FormatDuration(DateTime.UtcNow - boot);

            int clients = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true) clients++;

            int playerStats = Features.PlayerStats.PlayerStatsStore.BuildSnapshot().Entries.Count;
            int marketplace = Features.Marketplace.MarketplaceStore.BuildSnapshot(null).Listings.Count;
            int quests      = Features.Quests.QuestStore.BuildSnapshot(null).Quests.Count;
            int guilds      = Features.Guilds.GuildStore.ListGuilds().Count;
            int links       = Features.LinkedAccounts.LinkedAccountsStore.BuildSnapshot().Links.Count;

            reply($"=== {Constants.DisplayName} v{typeof(Main_).Assembly.GetName().Version} ===");
            reply($"Uptime:           {uptime}");
            reply($"Verified clients: {clients}");
            reply($"PlayerStats:      {playerStats}");
            reply($"Marketplace:      {marketplace} public listing(s)");
            reply($"Quests:           {quests} public quest(s)");
            reply($"Guilds:           {guilds}");
            reply($"LinkedAccounts:   {links}");
            reply($"Discord bridge:   {Features.Discord.DiscordBridge.DescribeStatus()}");
            reply($"Data folder:      {Persistence.KmhDataPaths.Folder}");
            reply($"Extensions:       {Extensibility.ExtensionLoader.Loaded.Count} loaded");
        }

        private static void Extensions(Action<string> reply)
        {
            var loaded = Extensibility.ExtensionLoader.Loaded;
            if (loaded.Count == 0)
            {
                reply("No extensions loaded. Drop *.dll files into kmh-extensions/ next to KMHServerAddon.exe.");
                return;
            }
            reply($"Loaded extensions ({loaded.Count}):");
            foreach (var ext in loaded) reply($"  - {ext.Name} v{ext.Version}  ({ext.SourceDll})");
        }

        private static void ReloadEconomy(Action<string> reply)
        {
            Features.Economy.EconomyConfig.Reload();
            Features.Sites.SitesConfig.Reload();
            var cfg = Features.Economy.EconomyConfig.Current;
            reply($"Economy config reloaded: tax {cfg.MarketplaceTaxPercent}%, " +
                  $"price {Util.SilverFmt.Format(cfg.MarketplaceMinUnitPrice)}-{Util.SilverFmt.Format(cfg.MarketplaceMaxUnitPrice)}, " +
                  $"max {cfg.MarketplaceMaxOpenListingsPerUser} listings/user, lifetime {cfg.MarketplaceListingLifetimeHours}h.");
        }

        private static void ReloadDiscord(string actorName, Action<string> reply)
        {
            ServerLog.Info($"kmh reload-discord requested by {actorName}");
            reply("Reloading Config/Discord/DiscordConfig.json and restarting the Discord bridge...");
            Features.Discord.DiscordBridge.Reload();
            reply($"Discord bridge: {Features.Discord.DiscordBridge.DescribeStatus()}");
        }

        private static void DrainHouse(string[] args, Action<string> reply)
        {
            if (args.Length < 2)
            {
                reply("Usage: kmh drain-house <username>");
                return;
            }
            string target  = args[1];
            long   drained = Features.Marketplace.MarketplaceStore.DrainHousePool(target);
            if (drained > 0)
            {
                reply($"Drained {Util.SilverFmt.Format(drained)} from the house pool to {target}.");
                ServerLog.Info($"kmh drain-house: {drained}s -> {target}");
            }
            else reply("House pool is empty.");
        }

        private static void GiveSilver(string[] args, string actorName, Action<string> reply)
        {
            if (args.Length < 3)
            {
                reply("Usage: kmh give-silver <username> <amount>");
                return;
            }
            string target = args[1];
            if (!int.TryParse(args[2], System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int amount) || amount <= 0)
            {
                reply($"Amount must be a positive integer. Got: '{args[2]}'.");
                return;
            }
            if (Features.Treasury.TreasuryStore.DepositSilver(target, amount, $"admin grant by {actorName}"))
            {
                reply($"Granted {Util.SilverFmt.Format(amount)} to {target}.");
                ServerLog.Info($"kmh give-silver: {actorName} -> {target} +{amount}s");
            }
            else reply($"Deposit failed for '{target}'. Check the username and server log.");
        }

        // enforce status | on | off | safe add|remove|list <mod>
        private static void Enforce(string[] args, bool isAdmin, Action<string> reply)
        {
            Features.Enforcement.EnforcementConfig cfg = Features.Enforcement.EnforcementConfig.Current;
            string sub = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "status";

            switch (sub)
            {
                case "on":
                case "enable":
                    if (!isAdmin) { reply("'enforce on' requires admin."); return; }
                    cfg.Enabled = true; cfg.Save();
                    // Broadcast the snapshot - clients whose local hash differs pull the profile themselves (so we
                    // don't re-stream it to clients that already have it)
                    Features.Enforcement.EnforcementHandler.BroadcastSnapshot();
                    if (Features.Enforcement.EnforcementProfile.HasProfile)
                        reply($"Config enforcement ENABLED. Connected clients will pull the profile ({Features.Enforcement.EnforcementProfile.FileCount} file(s)) if they don't already have it.");
                    else
                        reply("Config enforcement ENABLED (soft lock only - no profile to push). An admin should Publish their configs in-game (KMH tab -> Config Enforcement) for hard enforcement.");
                    return;

                case "off":
                case "disable":
                    if (!isAdmin) { reply("'enforce off' requires admin."); return; }
                    cfg.Enabled = false; cfg.Save();
                    Features.Enforcement.EnforcementHandler.BroadcastSnapshot();
                    Features.Enforcement.EnforcementHandler.SendRestoreToAll();
                    reply("Config enforcement DISABLED. Clients restore their personal configs.");
                    return;

                case "publish":
                    // Publishing is now done in-game by an admin (KMH tab -> Config Enforcement -> Publish), which
                    // uploads a zip of their Config. This console command just re-reads the persisted Profile.zip
                    // and re-broadcasts so connected clients pull it
                    if (!isAdmin) { reply("'enforce publish' requires admin."); return; }
                    int n = Features.Enforcement.EnforcementProfile.Reload();
                    if (n > 0)
                    {
                        reply($"Re-read profile: {n} file(s), hash {Features.Enforcement.EnforcementProfile.Hash}.");
                        if (cfg.Enabled)
                        {
                            Features.Enforcement.EnforcementHandler.BroadcastSnapshot();
                            reply("Connected clients will pull the updated profile.");
                        }
                    }
                    else reply("No profile published yet - an admin should Publish their configs in-game (KMH tab -> Config Enforcement).");
                    return;

                case "safe":
                    EnforceSafe(args, isAdmin, cfg, reply);
                    return;

                default: // status
                    reply($"Config enforcement: {(cfg.Enabled ? "ON" : "OFF")}  (admin bypass: {(cfg.AdminBypass ? "on" : "off")})");
                    reply($"Profile: {(Features.Enforcement.EnforcementProfile.HasProfile ? $"{Features.Enforcement.EnforcementProfile.FileCount} file(s), hash {Features.Enforcement.EnforcementProfile.Hash}" : "(none) - an admin should Publish configs in-game (KMH tab -> Config Enforcement)")}");
                    string[] safe = cfg.SafeMods ?? System.Array.Empty<string>();
                    reply($"Safe mods ({safe.Length}): {(safe.Length == 0 ? "(none)" : string.Join(", ", safe))}");
                    reply("Use: enforce on|off|publish  ·  enforce safe add|remove|list <mod>");
                    return;
            }
        }

        private static void EnforceSafe(string[] args, bool isAdmin,
            Features.Enforcement.EnforcementConfig cfg, Action<string> reply)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "list";

            if (op == "list")
            {
                string[] safe = cfg.SafeMods ?? System.Array.Empty<string>();
                reply($"Safe mods ({safe.Length}):");
                if (safe.Length == 0) reply("  (none) - add one with: enforce safe add <mod>");
                else foreach (string m in safe) reply($"  - {m}");
                return;
            }

            if (!isAdmin) { reply("'enforce safe' changes require admin."); return; }

            // A mod can be referenced by display name (with spaces), so join the tail.
            string mod = args.Length > 3 ? string.Join(" ", args[3..]) : "";
            if (string.IsNullOrWhiteSpace(mod))
            {
                reply("Usage: enforce safe add|remove <mod packageId or display name>");
                return;
            }

            if (op == "add")
            {
                if (cfg.AddSafe(mod)) { Features.Enforcement.EnforcementHandler.BroadcastSnapshot(); reply($"Added '{mod}' to the safe-mods list."); }
                else reply($"'{mod}' is already on the safe-mods list.");
            }
            else if (op == "remove" || op == "rm")
            {
                if (cfg.RemoveSafe(mod)) { Features.Enforcement.EnforcementHandler.BroadcastSnapshot(); reply($"Removed '{mod}' from the safe-mods list."); }
                else reply($"'{mod}' was not on the safe-mods list.");
            }
            else reply("Usage: enforce safe add|remove|list <mod>");
        }

        private static void Help(Action<string> reply)
        {
            reply("KMH server commands:");
            reply("  status                       health report");
            reply("  extensions                   list loaded extensions");
            reply("  give-silver <user> <amount>  (admin) grant silver to a player");
            reply("  reload-discord               (admin) re-read Config/Discord/DiscordConfig.json");
            reply("  reload-economy               (admin) re-read Config/Economy.json + Sites.json");
            reply("  drain-house <user>           (admin) move the marketplace house pool to a player");
            reply("  enforce on|off|status        (admin) lock players' Mod Options to the server");
            reply("  enforce publish              (admin) re-read + push Enforcement/Profile/ configs");
            reply("  enforce safe add|remove|list (admin) manage the mods players may still edit");
            reply("  help                         show this list");
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays    >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
            if (span.TotalHours   >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
            return $"{(int)span.TotalSeconds}s";
        }
    }

    // Server-console counterpart to the in-game /kmh chat command. The operator types "kmh <subcommand> ..." in the
    // GameServer console and is always treated as admin. Registered into CMD_Base.Commands at boot
    internal sealed class KmhServerConsoleCommand : CMD_Base
    {
        public KmhServerConsoleCommand()
        {
            Prefix        = "kmh";
            Description   = "KMH server admin: kmh status / extensions / reload-economy / reload-discord / drain-house / give-silver.";
            IsChatCommand = false;
            ParameterCount = -1; // accept any number of args
        }

        public override void Action()
        {
            try
            {
                KmhServerCommands.Dispatch(CommandParameters ?? Array.Empty<string>(),
                    isAdmin: true, actorName: "console",
                    line => Printer.Message($"[KMH] {line}"));
            }
            catch (Exception ex) { Printer.Error($"[KMH] kmh console command threw: {ex.Message}"); }
        }
    }
}
