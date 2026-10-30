using System;
using KMHServerAddon.Diagnostics;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.AdminCommands
{
    internal static class KmhServerCommands
    {
        // The fingerprint guard can't tell a modified client from a changed modpack, so reset is the owner's override.
        private static void SiteCatalogCmd(string[] args, string actor, Action<string> reply)
        {
            string sub = LowerArg1(args);
            if (sub == "reset")
            {
                Features.Sites.SiteCatalogStore.Reset();
                Features.Sites.SiteCatalogStore.SaveToDisk();
                Diagnostics.ServerLog.Warn($"Site catalog: reset by {actor}.");
                reply("Site catalog cleared. The next client to connect establishes the new one.");
                return;
            }

            if (sub.Length > 0 && sub != "reset")
            {
                string want = args[1];
                Features.Sites.Dto.SiteOutputMetadata m = Features.Sites.SiteCatalogStore.Lookup(want);
                if (m == null)
                {
                    reply($"No catalog entry for '{want}'. Names are case-sensitive defNames (e.g. Plasteel, ComponentIndustrial).");
                    return;
                }
                reply($"=== {m.DefName} ===");
                reply($"  family     : {Features.Sites.SiteOutputFamilies.DisplayName(m.Family)}  ({m.Family})");
                reply($"  decided by : {m.Source}");
                reply($"  skill      : {(string.IsNullOrEmpty(m.Skill) ? "-" : m.Skill)}");
                reply($"  categories : {Join(m.Categories)}");
                reply($"  stuff      : {Join(m.StuffCategories)}");
                reply($"  tags       : {Join(m.Tags)}");
                reply($"  flags      : animal={m.IsAnimalProduct} harvested={m.IsHarvestedFromPlant} "
                    + $"tree={m.IsTreeHarvest} wild={m.IsWildHarvest} "
                    + $"mineable={m.IsMineable} crafted={m.IsCraftedProduct} ingestible={m.IsIngestible}");
                if (!string.IsNullOrEmpty(m.FoodType)) reply($"  foodType   : {m.FoodType}");
                reply("  Accepted by: " + AcceptingArchetypes(m));
                reply("  Override with Config/Sites.json -> Output.FamilyOverrides (\"defName=family\").");
                return;
            }

            reply("=== Site output catalog ===");
            reply("  ('kmh site-catalog <defName>' explains one item; 'kmh site-catalog reset' clears it)");
            if (!Features.Sites.SiteCatalogStore.HasCatalog)
            {
                reply("  no catalog yet - archetype filtering is off until a client pushes one");
                reply("  (any player on Colony Nexus v1.3.0+ pushes it automatically on connect)");
                return;
            }
            reply($"  {Features.Sites.SiteCatalogStore.Count} classified item(s)");
            reply($"  fingerprint: {Features.Sites.SiteCatalogStore.Fingerprint}");
            reply($"  established by: {Features.Sites.SiteCatalogStore.EstablishedBy}");
            int rejected = Features.Sites.SiteCatalogStore.RejectedPushes;
            reply(rejected == 0
                ? "  refused pushes: none"
                : $"  refused pushes: {rejected} - a client's catalog did not match. Run 'kmh site-catalog reset' if the modpack really changed.");

            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (string fam in Features.Sites.SiteOutputFamilies.All) counts[fam] = 0;
            foreach (string fam in Features.Sites.SiteOutputFamilies.All)
            {
                // Counted via the economy's own lookup, so it never reports a classification the game would not apply.
                int n = 0;
                foreach (var e in Features.ItemLabels.ItemLabelCache.AllForCatalog())
                    if (Features.Sites.SiteCatalogStore.FamilyOf(e.DefName) == fam) n++;
                counts[fam] = n;
            }
            foreach (var kv in counts)
                if (kv.Value > 0) reply($"    {Features.Sites.SiteOutputFamilies.DisplayName(kv.Key)}: {kv.Value}");
            if (counts[Features.Sites.SiteOutputFamilies.Unknown] > 0)
                reply("  Unclassified items are hidden from preset site types and shown disabled under Custom. "
                    + "Set Config/Sites.json -> Output.FamilyOverrides (\"defName=family\") to place them.");
        }

        private static string Join(System.Collections.Generic.List<string> v)
            => v == null || v.Count == 0 ? "-" : string.Join(", ", v);

        private static string AcceptingArchetypes(Features.Sites.Dto.SiteOutputMetadata m)
        {
            var ids = new System.Collections.Generic.List<string>();
            foreach (string id in Features.Sites.SiteArchetypes.All)
                if (Features.Sites.SiteArchetypeRegistry.AcceptsOutput(id, m))
                    ids.Add(Features.Sites.SiteArchetypeRegistry.Get(id).DisplayName);
            return ids.Count == 0 ? "nothing - it cannot be produced" : string.Join(", ", ids);
        }

        // Nothing in KMH lowers a site's condition on its own, so this is the only thing that ever damages one.
        private static void SiteCondition(string[] args, string actorName, Action<string> reply)
        {
            string verb = LowerArg1(args);
            int tile = args != null && args.Length > 2 && int.TryParse(args[2], out int t) ? t : -1;
            int pts  = args != null && args.Length > 3 && int.TryParse(args[3], out int p) ? Math.Abs(p) : 25;
            if (verb != "damage" && verb != "repair")
            { reply("Usage: kmh site-condition damage <tile> [points] | kmh site-condition repair <tile> [points]"); return; }

            var (ok, reason) = Features.Sites.SiteStore.AdjustStability(
                tile, verb == "damage" ? -pts : pts, $"admin {verb} by {actorName}");
            reply(reason);
            if (ok) Features.Sites.SiteHandler.BroadcastSnapshot();
        }

        // Never reassigned automatically: a hand-on is explicit, logged with its admin, and counted so a reset waits.
        private static void OrphansCmd(string[] args, string actorName, Action<string> reply)
        {
            string sub = LowerArg1(args);
            using (Maintenance.KmhValueOps.Scope op = sub == "" ? null : Maintenance.KmhValueOps.Begin(Maintenance.KmhIngress.Admin, out _))
            {
                if (sub == "rebind-site")
                {
                    if (args.Length < 4 || !int.TryParse(args[2], out int tile)) { reply("Usage: kmh orphans rebind-site <tile> <guild name>"); return; }
                    bool ok = Features.Sites.SiteStore.RebindOrphanedSite(tile, string.Join(" ", args, 3, args.Length - 3), actorName, out string msg);
                    reply(msg);
                    if (ok) Features.Sites.SiteHandler.BroadcastSnapshot();
                    return;
                }
                if (sub == "rebind-legacy")
                {
                    bool confirm = args.Length > 2 && string.Equals(args[2], "confirm", StringComparison.OrdinalIgnoreCase);
                    var listed = Features.Sites.SiteStore.RebindUnprovenLegacy(confirm, actorName, out string msg);
                    foreach (var l in listed) reply($"  tile {l.Tile} -> guild '{l.Guild}'");
                    reply(msg + (confirm ? $" (by {actorName})" : " Nothing changed - 'kmh orphans rebind-legacy confirm' applies it."));
                    if (confirm) { ServerLog.Info($"kmh orphans rebind-legacy: {actorName} - {msg}"); Features.Sites.SiteHandler.BroadcastSnapshot(); }
                    return;
                }
                if (sub == "release-vault")
                {
                    if (args.Length < 4) { reply("Usage: kmh orphans release-vault <vault> <player>"); return; }
                    Features.Treasury.TreasuryStore.ReleaseOrphanVault(args[2], args[3], actorName, out string msg);
                    reply(msg);
                    return;
                }
                if (sub != "") { reply("Usage: kmh orphans [rebind-site <tile> <guild> | rebind-legacy [confirm] | release-vault <vault> <player>]"); return; }
            }
            var sites = Features.Sites.SiteStore.OrphanedGuildSites();
            var vaults = Features.Treasury.TreasuryStore.OrphanVaults();
            reply($"=== Orphans === {sites.Count} guild site(s), {vaults.Count} vault(s) no live guild holds.");
            int owedQ = Features.Quests.QuestStore.OwedCount, owedS = Features.Sites.SiteStore.OwedCount, owedR = Features.Roadworks.RoadworksStore.OwedCount;
            if (owedQ + owedS + owedR > 0)
                reply($"  payouts written down but not yet credited: quests {owedQ}, sites {owedS}, roadworks {owedR} - retried every sweep and at start.");
            foreach (var s in sites)
                reply($"  site tile {s.Tile} '{s.Name}' - was guild '{s.Guild}', paused, {s.StoredUnits} unit(s) stored");
            foreach (var v in vaults)
                reply($"  vault '{v.Key}' - {Util.SilverFmt.Format(v.Silver)}, {v.ItemKinds} item kind(s), {v.Payloads} payload(s)");
            if (sites.Count + vaults.Count > 0)
                reply("Hand one on explicitly: 'kmh orphans rebind-site <tile> <guild>' or 'kmh orphans release-vault <vault> <player>'.");
        }

        // fromConsole comes from the caller, never actorName: a player can register an account called "console".
        // Admin value moves are never refused (they end freezes) but are counted, so a backup or reset waits for one.
        private static readonly System.Collections.Generic.HashSet<string> CountedAdminCommands = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "give-silver", "drain-house", "treasury-reset", "cancel", "quest", "recover", "purge", "wipe-economy",
            "reset-player-economy", "wipe-player", "reset-cleanup", "remove-player-guilds", "freeze",
        };

        public static void Dispatch(string[] args, bool isAdmin, string actorName, Action<string> reply,
                                    bool fromConsole = false)
        {
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            using (CountedAdminCommands.Contains(sub) ? Maintenance.KmhValueOps.Begin(Maintenance.KmhIngress.Admin, out _) : null)
                DispatchCommand(sub, args, isAdmin, actorName, reply, fromConsole);
        }

        private static void DispatchCommand(string sub, string[] args, bool isAdmin, string actorName, Action<string> reply, bool fromConsole)
        {
            switch (sub)
            {
                case "status":         Status(reply, isAdmin);                 break;
                case "diag":           Admin(isAdmin, "diag", reply, () => Diag(reply)); break;
                case "doctor":         Admin(isAdmin, "doctor", reply, () => KmhDoctor.Run(reply)); break;
                // `kmh config reload <area>` is the form `kmh help` advertises, so "reload" must not be read as an area.
                case "config" when args.Length > 1 && string.Equals(args[1], "reload", StringComparison.OrdinalIgnoreCase):
                                       Admin(isAdmin, "reload", reply, () => ReloadCmd(ShiftArgs(args), actorName, reply)); break;
                case "config":         Admin(isAdmin, "config", reply, () => KmhConfigInspect.Run(args, reply)); break;
                case "config-editor":  ConfigEditorCmd(args, actorName, reply, fromConsole); break;
                case "setup":          Admin(isAdmin, "setup", reply, () => KmhSetupCommands.Run(args, actorName, reply, fromConsole)); break;
                case "transport":      Admin(isAdmin, "transport", reply, () => Transport(reply)); break;
                case "verify":         Admin(isAdmin, "verify",  reply, () => Verify(reply));            break;
                case "audit":          Admin(isAdmin, "audit",   reply, () => Maintenance.KmhAudit.Run(reply)); break;
                case "backup":         Admin(isAdmin, "backup",  reply, () => Backup(args, reply));      break;
                case "site-condition": Admin(isAdmin, "site-condition", reply, () => SiteCondition(args, actorName, reply)); break;
                case "site-catalog":   Admin(isAdmin, "site-catalog", reply, () => SiteCatalogCmd(args, actorName, reply)); break;
                // Listing is for any admin; handing a site or vault on gives value away, so it is the owner's.
                case "orphans":        Admin(isAdmin, "orphans", reply, () => { if (LowerArg1(args) == "" || OwnerOrConsole(actorName, fromConsole, "Handing on an orphaned site or vault", reply)) OrphansCmd(args, actorName, reply); }); break;
                case "backups":        Admin(isAdmin, "backups", reply, () => Backups(reply));           break;
                case "restore":        Admin(isAdmin, "restore", reply, () => { if (OwnerOrConsole(actorName, fromConsole, "Restoring a backup", reply)) Restore(args, reply); }); break;
                case "snapshot-player": Admin(isAdmin, "snapshot-player", reply, () => SnapshotPlayerCmd(args, reply)); break;
                case "snapshot-server": Admin(isAdmin, "snapshot-server", reply, () => SnapshotServerCmd(args, reply)); break;
                case "snapshot-all":    Admin(isAdmin, "snapshot-all",    reply, () => SnapshotAllCmd(args, reply));    break;
                case "snapshot-verify": Admin(isAdmin, "snapshot-verify", reply, () => SnapshotVerifyCmd(args, reply)); break;
                case "restore-preview-player": Admin(isAdmin, "restore-preview-player", reply, () => Persistence.KmhSnapshot.PreviewPlayer(args.Length > 1 ? args[1] : "", reply)); break;
                case "save":           Admin(isAdmin, "save",    reply, () => SaveAll(reply));           break;
                case "export":         Admin(isAdmin, "export",  reply, () => Export(reply));            break;
                case "inspect":        Admin(isAdmin, "inspect", reply, () => Inspect(args, reply));      break;
                // Advertised by the guilds summary, `kmh validate` and the guild integrity notes.
                case "guild":          Admin(isAdmin, "guild",   reply, () => KmhGuildCommands.Run(args, reply)); break;
                case "sites":          Admin(isAdmin, "sites",   reply, () => KmhSitesCommands.Run(args, reply)); break;
                case "cancel":         Admin(isAdmin, "cancel",  reply, () => CancelCmd(args, reply));    break;
                // Approving pays a poster's escrow to the claimer, who may be the admin's own alt; rejecting and refunding return it.
                case "quest":          Admin(isAdmin, "quest",   reply, () => { if (LowerArg1(args) != "approve" || OwnerOrConsole(actorName, fromConsole, "Approving another player's bounty", reply)) QuestSettleCmd(args, actorName, reply); }); break;
                case "recover":        Admin(isAdmin, "recover", reply, () => { if (LowerArg1(args) != "drop" || OwnerOrConsole(actorName, fromConsole, "Giving held value to another player", reply)) KmhRecoveryCommands.Run(args, reply); }); break;
                case "validate":       Admin(isAdmin, "validate", reply, () => KmhValidateCommands.Run(args, reply)); break;
                case "support-bundle": Admin(isAdmin, "support-bundle", reply, () => Maintenance.KmhSupportBundle.Create(reply)); break;
                case "reports":        Admin(isAdmin, "reports", reply, () => KmhReportCommands.Run(args, reply)); break;
                case "perf":           Admin(isAdmin, "perf",    reply, () => KmhPerfCommand.Run(reply)); break;
                case "ledger":         Admin(isAdmin, "ledger",  reply, () => Ledger(args, reply));       break;
                case "leaderboard":    Admin(isAdmin, "leaderboard", reply, () => KmhLeaderboardCommand.Run(args, reply)); break;
                case "sim":            Admin(isAdmin, "sim", reply, () => Features.Simulation.SimulatedColonies.Command(args, reply)); break;
                case "joins":          Admin(isAdmin, "joins", reply, () => SubProtocol.Patches.JoinDiagnostics.Command(args, reply)); break;
                case "history":        Admin(isAdmin, "history", reply, () => History(args, reply));      break;
                case "smoketest":      Admin(isAdmin, "smoketest", reply, () => Maintenance.KmhSmokeTest.Run(reply)); break;
                case "selftest":       Admin(isAdmin, "selftest",  reply, () => Maintenance.KmhSmokeTest.RunRegression(reply)); break;
                case "policy":         Admin(isAdmin, "policy", reply, () => PolicyCmd(args, reply)); break;
                case "migration-report":
                case "migrationreport": Admin(isAdmin, "migration-report", reply, () => { foreach (string l in Maintenance.KmhMigrationReport.ReadLatest()) reply(l); }); break;
                case "maintenance":    Admin(isAdmin, "maintenance", reply, () => MaintenanceCmd(args, reply)); break;
                case "contributions":  Admin(isAdmin, "contributions", reply, () => ContributionsCmd(args, reply)); break;
                case "catalog":        Admin(isAdmin, "catalog", reply, () => CatalogCmd(args, reply)); break;
                case "transport-test":
                case "transporttest":  Admin(isAdmin, "transport-test", reply, () => Maintenance.KmhTransportSecurityTest.Run(reply)); break;
                case "rebuild-standings": Admin(isAdmin, "rebuild-standings", reply, () => RebuildStandings(reply)); break;
                case "audit-player":   Admin(isAdmin, "audit-player", reply, () => KmhCleanupCommands.AuditPlayer(args, reply)); break;
                case "reload-marketplace": Admin(isAdmin, "reload-marketplace", reply, () => KmhCleanupCommands.ReloadMarketplace(reply)); break;
                case "repush":         Admin(isAdmin, "repush", reply, () => KmhCleanupCommands.Repush(args, reply)); break;
                case "purge":          Admin(isAdmin, "purge", reply, () => PurgeRouter(args, reply)); break;
                case "wipe-economy":
                case "reset-player-economy": Admin(isAdmin, "wipe-economy", reply, () => Maintenance.KmhPlayerCleanup.WipeEconomy(Arg1(args), IsConfirm(args), reply)); break;
                case "reset-preview":  Admin(isAdmin, "reset-preview", reply, () => KmhCleanupCommands.ResetPreview(Arg1(args), reply)); break;
                case "freeze":         Admin(isAdmin, "freeze", reply, () => Features.Moderation.KmhEconomyGuardCommands.Freeze(args, actorName, reply)); break;
                case "unfreeze":       Admin(isAdmin, "unfreeze", reply, () => Features.Moderation.KmhEconomyGuardCommands.Unfreeze(args, actorName, reply)); break;
                case "frozen":         Admin(isAdmin, "frozen", reply, () => Features.Moderation.KmhEconomyGuardCommands.List(reply)); break;
                case "deposit-limit":  Admin(isAdmin, "deposit-limit", reply, () => Features.Moderation.KmhEconomyGuardCommands.DepositLimit(args, actorName, reply)); break;
                case "wipe-player":    Admin(isAdmin, "wipe-player", reply, () => Maintenance.KmhPlayerCleanup.WipePlayer(Arg1(args), IsConfirm(args), reply)); break;
                case "reset-cleanup":  Admin(isAdmin, "reset-cleanup", reply, () => Maintenance.KmhPlayerCleanup.ResetCleanup(Arg1(args), IsConfirm(args), reply)); break;
                case "remove-player-guilds": Admin(isAdmin, "remove-player-guilds", reply, () => Maintenance.KmhPlayerCleanup.RemoveGuilds(Arg1(args), IsConfirm(args), reply)); break;
                case "unlink-player":  Admin(isAdmin, "unlink-player", reply, () => Maintenance.KmhPlayerCleanup.Unlink(Arg1(args), IsConfirm(args), reply)); break;
                case "rebuild-player": Admin(isAdmin, "rebuild-player", reply, () => Maintenance.KmhPlayerCleanup.RebuildPlayer(Arg1(args), reply)); break;
                case "reload":         Admin(isAdmin, "reload", reply, () => ReloadCmd(args, actorName, reply)); break;
                case "restart":        RestartCmd(args, actorName, reply, fromConsole); break;
                case "extensions":     Extensions(reply);                      break;
                // Creates value out of nothing, which leaderboards and federation then count: the owner's call only.
                case "give-silver":    Admin(isAdmin, "give-silver",    reply, () => { if (OwnerOrConsole(actorName, fromConsole, "Granting silver", reply)) GiveSilver(args, actorName, reply); });  break;
                case "treasury-reset": Admin(isAdmin, "treasury-reset", reply, () => TreasuryReset(args, actorName, fromConsole, reply)); break;
                case "reload-discord": Admin(isAdmin, "reload-discord", reply, () => ReloadDiscord(actorName, reply));     break;
                case "reload-economy": Admin(isAdmin, "reload-economy", reply, () => ReloadEconomy(reply));                break;
                case "reload-world":   Admin(isAdmin, "reload-world",   reply, () => ReloadWorld(reply));                  break;
                case "reload-features": Admin(isAdmin, "reload-features", reply, () => ReloadFeatures(reply));             break;
                case "drain-house":    Admin(isAdmin, "drain-house",    reply, () => { if (OwnerOrConsole(actorName, fromConsole, "Draining the house pool", reply)) DrainHouse(args, reply); }); break;
                case "enforce":        Enforce(args, isAdmin, reply);          break;
                case "event":          Admin(isAdmin, "event",          reply, () => EventCmd(args, actorName, reply));    break;
                case "worldquest":
                // A new quest's reward is minted past the house pool, and a credited delivery pays for goods never handed in.
                case "wq":             Admin(isAdmin, "worldquest",     reply, () => { string s = LowerArg1(args); if (s == "" || s == "help" || s == "list" || s == "end" || OwnerOrConsole(actorName, fromConsole, "Creating or crediting a world quest", reply)) WorldQuestCmd(args, actorName, reply); }); break;
                case "season":         Admin(isAdmin, "season",         reply, () => SeasonCmd(args, actorName, fromConsole, reply)); break;
                case "frontier":       Admin(isAdmin, "frontier",       reply, () => FrontierCmd(args, actorName, reply));  break;
                case "roadworks":      Admin(isAdmin, "roadworks",      reply, () => RoadworksCmd(args, actorName, reply)); break;
                case "global":         Admin(isAdmin, "global",         reply, () => KmhGlobalCommands.Run(args, actorName, reply, fromConsole)); break;
                case "federation":     Admin(isAdmin, "federation",     reply, () => KmhFederationCommands.Run(args, reply)); break;
                case "help":           Help(args, reply, isAdmin);              break;
                default:               Help(args, reply, isAdmin);              break;
            }
        }

        // Says it is console-only rather than feigning absence, so an owner in game knows where to go.
        private static void ConfigEditorCmd(string[] args, string actorName, Action<string> reply, bool fromConsole)
        {
            if (!fromConsole)
            {
                reply("'config-editor' can only be run at the server console. In-game settings access is deliberately");
                reply("not something that can be opened from in game, from Discord, or by anyone holding a badge.");
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string action = LowerArg1(args, "status");
            string who    = args != null && args.Length > 2 ? args[2] : "";

            switch (action)
            {
                case "allow":
                {
                    if (who.Length == 0) { reply("Usage: kmh config-editor allow <player> [minutes]"); return; }
                    if (!Features.Identity.StaffRegistry.IsOwner(who))
                    {
                        reply($"'{who}' is not listed under Owners in Staff.json, so access was NOT opened.");
                        reply("Add them there first; console access alone does not make someone the owner.");
                        return;
                    }
                    TimeSpan window = Features.Admin.ConfigEditorGrant.DefaultWindow;
                    if (args.Length > 3 && int.TryParse(args[3], out int mins) && mins > 0)
                        window = TimeSpan.FromMinutes(mins);
                    DateTimeOffset until = Features.Admin.ConfigEditorGrant.Open(who, window, actorName, now);
                    reply($"In-game settings access opened for '{who}' until {until:u} (UTC).");
                    reply("It closes on its own, and a server restart closes it immediately.");
                    return;
                }
                case "revoke":
                {
                    if (who.Length == 0) { reply("Usage: kmh config-editor revoke <player>"); return; }
                    reply(Features.Admin.ConfigEditorGrant.Close(who)
                        ? $"Closed in-game settings access for '{who}'."
                        : $"'{who}' did not have access.");
                    return;
                }
                case "revoke-all":
                    Features.Admin.ConfigEditorGrant.CloseAll();
                    reply("Closed in-game settings access for everyone.");
                    return;
                default:
                {
                    List<string> open = Features.Admin.ConfigEditorGrant.Describe(now);
                    if (open.Count == 0) { reply("Nobody has in-game settings access."); return; }
                    reply($"In-game settings access ({open.Count}):");
                    foreach (string line in open) reply("  " + line);
                    return;
                }
            }
        }

        private static void Admin(bool isAdmin, string name, Action<string> reply, Action run)
        {
            if (!isAdmin) { reply($"'{name}' requires admin."); return; }
            run();
        }

        // Server-wide and destructive, so an admin badge is not enough: the owner, or this machine's console.
        private static bool OwnerOrConsole(string actorName, bool fromConsole, string what, Action<string> reply)
        {
            if (fromConsole || Features.Identity.StaffRegistry.IsOwner(actorName)) return true;
            reply($"{what} is owner-only. '{actorName}' is not listed under Owners in Staff.json - run it at the server console.");
            return false;
        }

        private static void SeasonCmd(string[] args, string actorName, bool fromConsole, Action<string> reply)
        {
            string action = LowerArg1(args, "status");
            if (action == "roll")
            {
                (int season, int count) = Features.Seasons.SeasonStore.RollSeason();
                Features.Seasons.SeasonHandler.Broadcast();
                int now = Features.Seasons.SeasonStore.CurrentSeason;
                Extensibility.KmhEventBus.Instance.RaiseSeasonRolled(new KMH.Sdk.Server.Events.SeasonRolledEvent
                { RolledSeason = season, NewSeason = now, RecordCount = count, Actor = actorName, EconomyWiped = false });
                ServerLog.Info($"Season {season} rolled by {actorName} - {count} record(s) archived");
                reply($"Season {season} archived ({count} record(s)). Now in season {now}.");
            }
            else if (action == "reset")
            {
                bool confirmed = args.Length > 2 && string.Equals(args[2], "confirm", StringComparison.OrdinalIgnoreCase);
                if (!confirmed)
                {
                    reply("kmh season reset WIPES the live economy (treasury, marketplace, auctions, wants, sites, quests, reputation, mail, standings) and starts a new season.");
                    reply("Guilds, Discord links, and the season archive are KEPT. It backs up first (reversible via 'kmh restore'). Confirm with:  kmh season reset confirm");
                    return;
                }
                if (!OwnerOrConsole(actorName, fromConsole, "A season reset", reply)) return;
                if (Maintenance.KmhSeasonReset.Run(actorName, out string summary)) reply(summary);
                else reply($"Season reset {summary}");
            }
            else
            {
                reply($"Current season: {Features.Seasons.SeasonStore.CurrentSeason}. 'kmh season roll' archives + advances; 'kmh season reset' also wipes the economy.");
            }
        }

        private static void Diag(Action<string> reply)
        {
            reply("=== Colony Nexus data pipeline ===");
            reply($"Standings (live): {Features.PlayerStats.PlayerStatsStore.DiagLine()}");
            reply($"World (live):     {Features.World.WorldStore.ActiveEvents().Count} active event(s), " +
                  $"{Features.World.WorldStore.ActiveQuests().Count} active global quest(s)");
            reply("On-disk KMH-Data files (bytes; '(not created yet)' is normal until first write):");
            foreach (Persistence.KmhDataPaths.DataFile f in Persistence.KmhDataPaths.KnownDataFiles)
            {
                try
                {
                    reply(System.IO.File.Exists(f.Path)
                        ? $"  {f.Label}: {new System.IO.FileInfo(f.Path).Length} bytes"
                        : $"  {f.Label}: (not created yet)");
                }
                catch (Exception ex) { reply($"  {f.Label}: (error: {ex.Message})"); }
            }
        }

        private static void Transport(Action<string> reply)
        {
            Features.Transport.TransportConfig c = Features.Transport.TransportConfig.Current;
            reply("=== Colony Nexus transport ===");
            if (!c.EnableKmhApiTransport) { reply("API transport: OFF - clients use the RWT chat path. Enable in Config/Transport.json."); return; }
            // Bound once up, configured when not: an owner chasing a connection needs the number clients are given.
            bool up = Features.Transport.KmhApiServer.Running;
            string refused = Features.Transport.KmhApiServer.StartRefusal;
            reply($"API transport: {(up ? "listening" : refused != null ? "REFUSED to start" : "ENABLED but not bound (see boot log)")} on {c.BindAddress}:{(up ? Features.Transport.KmhApiServer.Port : c.KmhApiPort)}");
            if (refused != null) reply("  " + refused);
            if (up) reply($"Clients are told to dial: {(string.IsNullOrEmpty(c.PublicApiHost) ? "<the address they reached RWT on>" : c.PublicApiHost)}:{Features.Transport.KmhApiServer.Port}");
            reply($"Auth: {(c.RequireKmhApiAuth ? "required" : "OFF")} · chat fallback: {(c.AllowChatTransportFallback ? "on" : "off")} · connected: {Features.Transport.KmhApiServer.ConnectedCount}");
        }

        private static void Verify(Action<string> reply)
        {
            Persistence.KmhDataIntegrity.ScanResult r = Persistence.KmhDataIntegrity.Scan();
            reply($"=== Colony Nexus data integrity === {r.Summary}");
            bool anyProblem = false;
            foreach (Persistence.KmhDataIntegrity.FileStatus fs in r.Files)
            {
                if (fs.State == Persistence.KmhDataIntegrity.State.Ok || fs.State == Persistence.KmhDataIntegrity.State.Missing) continue;
                anyProblem = true;
                reply($"  [{fs.State}] {fs.Label} - {fs.Detail}" + (fs.Regenerable ? " (regenerates)" : " (IRREPLACEABLE)"));
            }
            foreach (string p in r.StrayCorruptFiles) reply($"  salvaged copy present: {System.IO.Path.GetFileName(p)}");
            foreach (string p in r.StrayTmpFiles)     reply($"  stray .tmp (safe to delete): {System.IO.Path.GetFileName(p)}");
            if (!anyProblem && r.StrayCorruptFiles.Count == 0 && r.StrayTmpFiles.Count == 0)
                reply("  All present files parse cleanly. No action needed.");
            if (r.CriticalDamage)
                reply("  ACTION: restore the affected file(s) from KMH-Data-Backups before players reconnect.");
        }

        private static void Backup(string[] args, Action<string> reply)
        {
            Maintenance.KmhDataFlush.FlushAll();
            string reason = args != null && args.Length > 1 ? string.Join("-", args[1..]) : "manual";
            if (Persistence.KmhDataBackup.TryCreate(reason, out string dir, out string err))
            {
                int pruned = Persistence.KmhDataBackup.Prune(Maintenance.MaintenanceConfig.Current.BackupRetention);
                reply($"Backup created: {System.IO.Path.GetFileName(dir)}" + (pruned > 0 ? $" ({pruned} old pruned)" : ""));
                reply($"Location: {Persistence.KmhDataPaths.BackupRoot}");
                ServerLog.Info($"kmh backup: {dir}");
            }
            else reply($"Backup failed: {err}");
        }

        private static void Backups(Action<string> reply)
        {
            System.Collections.Generic.List<Persistence.KmhDataBackup.BackupInfo> list = Persistence.KmhDataBackup.List();
            reply($"=== Colony Nexus backups ({list.Count}) === {Persistence.KmhDataPaths.BackupRoot}");
            if (list.Count == 0) { reply("  (none yet - made on boot when enabled, or via 'kmh backup')"); return; }
            foreach (Persistence.KmhDataBackup.BackupInfo b in list)
                reply($"  {b.Name}  ({b.Files} file(s), {b.Bytes} bytes)");
            reply("Restore: 'kmh restore <name|latest|before:<time>>' then restart (safety-backs up first). Or copy a backup's contents into KMH-Data by hand while stopped.");
        }

        private static void SnapshotPlayerCmd(string[] args, Action<string> reply)
        {
            if (args == null || args.Length < 2) { reply("Usage: kmh snapshot-player <username> [YYYY-MM-DD_HH-MM]"); return; }
            string ts = args.Length > 2 ? args[2] : null;
            if (Persistence.KmhSnapshot.SnapshotPlayer(args[1], ts, null, out string dir, out string err))
                reply($"Player snapshot: {dir}");
            else reply($"Snapshot failed: {err}");
        }

        private static void SnapshotServerCmd(string[] args, Action<string> reply)
        {
            string ts = args != null && args.Length > 1 ? args[1] : null;
            if (Persistence.KmhSnapshot.SnapshotServer(ts, null, out string dir, out string err))
                reply($"Server snapshot: {dir}");
            else reply($"Snapshot failed: {err}");
        }

        private static void SnapshotAllCmd(string[] args, Action<string> reply)
        {
            string ts = args != null && args.Length > 1 ? args[1] : null;
            System.Collections.Generic.List<string> fail = new System.Collections.Generic.List<string>();
            int n = Persistence.KmhSnapshot.SnapshotAll(ts, out _, fail);
            reply($"Wrote {n} snapshot(s) under {Persistence.KmhDataPaths.SnapshotsRoot}." + (fail.Count > 0 ? $" {fail.Count} issue(s):" : ""));
            foreach (string f in fail.GetRange(0, Math.Min(5, fail.Count))) reply($"  {f}");
        }

        private static void SnapshotVerifyCmd(string[] args, Action<string> reply)
        {
            if (args == null || args.Length < 2) { reply("Usage: kmh snapshot-verify <snapshot-folder>"); return; }
            reply(Persistence.KmhSnapshot.Verify(args[1], out string detail) ? $"Snapshot OK: {detail}" : $"Snapshot INVALID: {detail}");
        }

        private static void Restore(string[] args, Action<string> reply)
        {
            string spec = args != null && args.Length > 1 ? string.Join(" ", args[1..]).Trim() : "";
            if (spec.Length == 0)
            {
                reply("Usage: kmh restore <backup-name|latest|before:<iso or yyyyMMdd-HHmmss>>   (names: kmh backups)");
                return;
            }
            if (Persistence.KmhDataRestore.Queue(spec, out string resolved, out string err))
            {
                reply($"Restore queued: '{resolved}'. RESTART the server to apply - current data is safety-backed-up automatically.");
                ServerLog.Warn($"kmh restore queued by admin: {resolved}");
            }
            else reply($"Restore not queued: {err}");
        }

        private static void SaveAll(Action<string> reply)
        {
            int n = Maintenance.KmhDataFlush.FlushAll(reply);
            reply($"Flushed {n} Colony Nexus store(s) to {Persistence.KmhDataPaths.Folder}.");
            reply("NOTE: this saves KMH-Data ONLY, not RWT player colonies. Colony Nexus can't reach an RWT save-all API - save colonies via RWT/each client before restart.");
            ServerLog.Info($"kmh save: flushed {n} store(s)");
        }

        private static void Export(Action<string> reply)
        {
            if (Maintenance.KmhStatusExport.WriteToDisk())
                reply($"Status exported to {Persistence.KmhDataPaths.StatusFile} (also auto-written on boot + every ~60s).");
            else
                reply("Status export failed - see server log.");
        }

        internal static string[] ShiftArgs(string[] args)
        {
            if (args == null || args.Length < 2) return new[] { "" };
            var outp = new string[args.Length - 1];
            Array.Copy(args, 1, outp, 0, outp.Length);
            return outp;
        }

        private static void Inspect(string[] args, Action<string> reply)
        {
            string what = LowerArg1(args);
            switch (what)
            {
                case "auctions":
                {
                    System.Collections.Generic.List<Features.Auctions.Dto.AuctionDto> all = Features.Auctions.AuctionStore.AllForAdmin();
                    reply($"=== Auctions ({all.Count}) ===");
                    foreach (Features.Auctions.Dto.AuctionDto a in all)
                        reply($"  #{a.Id} {a.Qty}x {a.ItemDefName} by {a.SellerUsername} - " +
                              (a.CurrentBid > 0 ? $"bid {Util.SilverFmt.Format(a.CurrentBid)} by {a.HighBidder}" : "no bids") +
                              $", ends in {Remain(a.EndsUtcTicks)}");
                    if (all.Count > 0) reply("Cancel a stuck one with: kmh cancel auction <id>");
                    break;
                }
                case "wants":
                {
                    System.Collections.Generic.List<Features.WantBoard.Dto.WantDto> all = Features.WantBoard.WantStore.AllForAdmin();
                    reply($"=== Wants ({all.Count}) ===");
                    foreach (Features.WantBoard.Dto.WantDto w in all)
                        reply($"  #{w.Id} {w.QtyFilled}/{w.QtyWanted}x {w.ItemDefName} by {w.BuyerUsername} @ {Features.WantBoard.WantStore.PriceText(w.EffectiveMilli)}/ea, " +
                              $"escrow {Util.SilverFmt.Format(w.EscrowRemaining)}, ends in {Remain(w.EndsUtcTicks)}");
                    if (all.Count > 0) reply("Cancel a stuck one with: kmh cancel want <id>");
                    break;
                }
                case "quests":
                {
                    var qs = Features.Quests.QuestStore.BuildSnapshot(null).Quests;
                    reply($"=== Quests ({qs.Count} public) ===");
                    foreach (var q in qs)
                        reply($"  #{q.Id} [{q.Kind}/{q.State}] {q.Title} by {q.PosterUsername}, bounty {Util.SilverFmt.Format(q.BountySilver)}");
                    reply("Cancel a stuck one with: kmh cancel quest <id>  (refunds the poster, review included)");
                    reply("Settle one awaiting review with: kmh quest approve|reject <id>");
                    break;
                }
                case "world":
                {
                    var ev = Features.World.WorldStore.ActiveEvents();
                    var wq = Features.World.WorldStore.ActiveQuests();
                    reply($"=== World === {ev.Count} event(s), {wq.Count} global quest(s)");
                    foreach (var e in ev) reply($"  event {e.Type} - {e.Title}");
                    foreach (var q in wq) reply($"  quest #{q.Id} [{q.Kind}] {q.Title} {q.ProgressQty}/{q.GoalQty} {q.TargetDefName}, reward {Util.SilverFmt.Format(q.RewardPool)}");
                    reply("End a global quest with: kmh worldquest end <id>");
                    break;
                }
                case "marketplace":
                {
                    var ls = Features.Marketplace.MarketplaceStore.BuildSnapshot(null).Listings;
                    reply($"=== Marketplace ({ls.Count} public listing(s)) ===");
                    foreach (var l in ls)
                        reply($"  #{l.Id} {l.RemainingQty}x {l.ItemDefName} by {l.SellerUsername} @ {Util.SilverFmt.Format(l.UnitPriceSilver)}/ea");
                    break;
                }
                // Shifted past the subsystem word, or `kmh inspect guild repair` never reaches repair or per-player detail.
                case "sites":
                    KmhSitesCommands.Run(ShiftArgs(args), reply);
                    break;
                case "guilds":
                case "guild":
                    KmhGuildCommands.Run(ShiftArgs(args), reply);
                    break;
                case "treasury":
                    reply($"=== Treasury === house pool {Util.SilverFmt.Format(Features.Marketplace.MarketplaceStore.HousePoolBalance())}, " +
                          $"server reported wealth {Util.SilverFmt.Format(Features.PlayerStats.PlayerStatsStore.TotalReportedWealth())}.");
                    break;
                case "standings":
                    reply($"=== Standings === {Features.PlayerStats.PlayerStatsStore.DiagLine()}");
                    break;
                default:
                    reply("Usage: kmh inspect <auctions|wants|quests|world|marketplace|sites|guilds|treasury|standings>");
                    break;
            }
        }

        private static void CancelCmd(string[] args, Action<string> reply)
        {
            string kind = LowerArg1(args);
            if (args == null || args.Length < 3 || !long.TryParse(args[2], out long id) || id <= 0)
            {
                reply("Usage: kmh cancel <auction|want|quest|marketplace> <id>   (see ids via: kmh inspect <auctions|wants|quests|marketplace>)");
                return;
            }
            switch (kind)
            {
                case "auction": reply(Features.Auctions.AuctionHandler.AdminVoid(id));  break;
                case "want":    reply(Features.WantBoard.WantHandler.AdminCancel(id));  break;
                case "quest":   reply(Features.Quests.QuestHandler.AdminCancel(id));    break;
                case "marketplace":
                    if (Features.Marketplace.MarketplaceStore.AdminCancelListing(id, out string seller, out int refunded))
                    {
                        Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
                        reply($"Cancelled listing #{id} ({seller}); {refunded} item(s) refunded (or held in recovery). Snapshot re-pushed.");
                    }
                    else reply($"No marketplace listing #{id}.");
                    break;
                default:        reply("Usage: kmh cancel <auction|want|quest|marketplace> <id>"); break;
            }
        }

        private static void QuestSettleCmd(string[] args, string actorName, Action<string> reply)
        {
            string action = LowerArg1(args);
            if (args == null || args.Length < 3 || !long.TryParse(args[2], out long id) || id <= 0
                || (action != "approve" && action != "reject" && action != "refund"))
            {
                reply("Usage: kmh quest <approve|reject|refund> <id>   (ids via: kmh inspect quests)");
                return;
            }
            reply(Features.Quests.QuestHandler.AdminSettle(id, action, string.IsNullOrEmpty(actorName) ? "console" : actorName));
        }

        private static string Arg1(string[] a) => a != null && a.Length > 1 ? a[1] : "";
        private static string LowerArg1(string[] a, string fallback = "") => a != null && a.Length > 1 ? a[1].ToLowerInvariant() : fallback;
        private static bool IsConfirm(string[] a)
        {
            if (a != null) foreach (string s in a) if (string.Equals(s, "confirm", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void PurgeRouter(string[] args, Action<string> reply)
        {
            switch (LowerArg1(args))
            {
                case "marketplace": KmhCleanupCommands.PurgeMarketplaceSeller(args, reply); break;
                case "sites":       Maintenance.KmhPlayerCleanup.PurgeSites(args.Length > 3 ? args[3] : "", IsConfirm(args), reply); break;
                default:            reply("Usage: kmh purge marketplace seller <user> <dry|confirm>  |  kmh purge sites owner <user> <dry|confirm>"); break;
            }
        }

        private static void RebuildStandings(Action<string> reply)
        {
            Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
            Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
            int n = Features.PlayerStats.PlayerStatsStore.PlayerCount;
            Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot();
            reply($"Standings rebuilt from disk ({n} player(s)) and re-pushed to all clients.");
            ServerLog.Info($"kmh rebuild-standings: reloaded {n} player(s) and rebroadcast");
        }

        private static void Ledger(string[] args, Action<string> reply)
        {
            string user = null;
            int count = 25;
            if (args != null && args.Length > 1)
            {
                if (int.TryParse(args[1], out int c1)) count = c1;
                else { user = args[1]; if (args.Length > 2 && int.TryParse(args[2], out int c2)) count = c2; }
            }
            count = Math.Max(1, Math.Min(count, 200));
            System.Collections.Generic.List<string> lines = Persistence.TransactionLedger.ReadRecent(count, user);
            reply($"=== Economy ledger ({lines.Count} newest{(user != null ? $" for {user}" : "")}) ===");
            if (lines.Count == 0) reply("  (no entries yet - the ledger records every silver/item movement as it happens)");
            else foreach (string l in lines) reply("  " + l);
        }

        private static void History(string[] args, Action<string> reply)
        {
            string domain = LowerArg1(args);
            if (string.IsNullOrEmpty(domain) || !System.Linq.Enumerable.Contains(Persistence.KmhHistory.Domains, domain))
            {
                reply($"Usage: kmh history <{string.Join("|", Persistence.KmhHistory.Domains)}> [contains] [count]");
                reply("  e.g. 'kmh history guilds Bob 20' - the last 20 guild changes mentioning Bob.");
                reply("  Silver/item movements live in 'kmh ledger'; per-player restore in 'kmh snapshot-player'/'restore'.");
                return;
            }
            string contains = null; int count = 25;
            if (args.Length > 2)
            {
                if (int.TryParse(args[2], out int c1)) count = c1;
                else { contains = args[2]; if (args.Length > 3 && int.TryParse(args[3], out int c2)) count = c2; }
            }
            count = Math.Max(1, Math.Min(count, 200));
            System.Collections.Generic.List<string> lines = Persistence.KmhHistory.ReadRecent(domain, contains, count);
            reply($"=== History: {domain} ({lines.Count} newest{(contains != null ? $" containing '{contains}'" : "")}) ===");
            if (lines.Count == 0) reply("  (no records yet - history records state changes as they happen)");
            else foreach (string l in lines) reply("  " + l);
        }

        private static string Remain(long endsUtcTicks)
        {
            if (endsUtcTicks <= 0) return "n/a";
            TimeSpan left = new DateTime(endsUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow;
            return left.Ticks <= 0 ? "ended" : FormatDuration(left);
        }

        private static void PolicyCmd(string[] args, Action<string> reply)
        {
            string sub = LowerArg1(args);

            if (sub == "set")
            {
                if (args.Length < 5) { reply("Usage: kmh policy set <system> <key> <value>"); return; }
                if (Policy.KmhPolicyStore.Set(args[2], args[3], args[4], out string err))
                {
                    Policy.KmhPolicyStore.SaveToDisk();
                    reply($"Stored {args[2]}.{args[3]} = {args[4]} - recorded only, not yet enforced. "
                        + "The live economy uses Config/Economy.json.");
                }
                else reply($"Could not set: {err}");
                return;
            }
            if (sub == "reset")
            {
                if (args.Length < 3) { reply("Usage: kmh policy reset <system> [key]"); return; }
                bool done = args.Length >= 4 ? Policy.KmhPolicyStore.Clear(args[2], args[3]) : Policy.KmhPolicyStore.ClearSystem(args[2]);
                if (done) { Policy.KmhPolicyStore.SaveToDisk(); reply("Override cleared."); }
                else reply("No matching override.");
                return;
            }

            string profile = sub.Length > 0 ? args[1] : Features.Economy.EconomyConfig.Current.EconomyMode;
            foreach (string line in Policy.KmhPolicyReport.Describe(profile)) reply(line);
        }

        private static void MaintenanceCmd(string[] args, Action<string> reply)
        {
            string sub = LowerArg1(args);
            if (sub == "on")  { Maintenance.KmhMaintenanceGate.EnterOwner(); reply("Maintenance ON - deposits, purchases and other value moves are paused. Run 'kmh maintenance off' when done."); return; }
            if (sub == "off")
            {
                reply(Maintenance.KmhMaintenanceGate.ReleaseOwner()
                    ? $"Maintenance released ({Maintenance.KmhMaintenanceGate.Describe()})."
                    : $"No 'maintenance on' to release ({Maintenance.KmhMaintenanceGate.Describe()}) - a reset, restore or backup holding it lets go when it finishes.");
                return;
            }
            reply(Maintenance.KmhMaintenanceGate.Describe());
        }

        private static void CatalogCmd(string[] args, Action<string> reply)
        {
            string def = Arg1(args);
            if (def.Length == 0) { reply("Usage: kmh catalog <def> [value|unpin]"); return; }

            if (args.Length >= 3)
            {
                if (string.Equals(args[2], "unpin", StringComparison.OrdinalIgnoreCase))
                { Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, 0); reply($"{def}: unpinned (reverts to client-vouched)."); return; }
                if (long.TryParse(args[2], out long v) && v > 0)
                { Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, v); reply($"{def}: pinned to {Util.SilverFmt.Format(v)} (client pushes ignored)."); return; }
                reply("Value must be a positive number, or 'unpin'."); return;
            }

            (long value, bool pinned) = Features.ItemLabels.ItemLabelCache.ValueInfo(def);
            reply(value > 0
                ? $"{def}: {Util.SilverFmt.Format(value)}{(pinned ? " (owner-pinned)" : " (client-vouched)")}"
                : $"{def}: no trusted value recorded yet.");
        }

        private static void ContributionsCmd(string[] args, Action<string> reply)
        {
            string guild = args != null && args.Length > 1 ? string.Join(" ", args[1..]).Trim() : "";
            if (guild.Length == 0) { reply("Usage: kmh contributions <guild>"); return; }
            var players = Features.Guilds.Contributions.KmhGuildContributionLedger.PlayersIn(guild);
            if (players.Count == 0) { reply($"No recorded contributions for '{guild}'."); return; }
            reply($"=== Contributions: {guild} ===");
            foreach (string p in players)
            {
                var s = Features.Guilds.Contributions.KmhGuildContributionLedger.SummaryFor(guild, p);
                reply($"  {p}: {Util.SilverFmt.Format(s.Silver)} silver + {Util.SilverFmt.Format(s.ItemValue)} in items " +
                      $"({s.Count} contribution(s){(s.Returned > 0 ? $", {Util.SilverFmt.Format(s.Returned)} returned" : "")})");
            }
        }

        // Players and public Discord get the counts; the host path and economy internals are for the operator.
        internal static void Status(Action<string> reply, bool isAdmin)
        {
            DateTime boot   = Main_.BootstrapUtc;
            string   uptime = boot == DateTime.MinValue ? "(unknown)" : FormatDuration(DateTime.UtcNow - boot);

            int clients = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true) clients++;

            int playerStats = Features.PlayerStats.PlayerStatsStore.PlayerCount;
            int marketplace = Features.Marketplace.MarketplaceStore.BuildSnapshot(null).Listings.Count;
            int quests      = Features.Quests.QuestStore.BuildSnapshot(null).Quests.Count;
            int guilds      = Features.Guilds.GuildStore.ListGuilds().Count;
            int links       = Features.LinkedAccounts.LinkedAccountsStore.BuildSnapshot().Links.Count;

            long housePool  = Features.Marketplace.MarketplaceStore.HousePoolBalance();
            long wealthIdx  = Features.PlayerStats.PlayerStatsStore.TotalReportedWealth();
            bool dynPrice   = Features.Economy.EconomyConfig.Current.Marketplace.DynamicDemandPricingEnabled;
            bool minting    = Features.World.WorldConfig.Current.WorldQuests.Rewards.AllowMinted;

            reply($"=== {Constants.DisplayName} v{typeof(Main_).Assembly.GetName().Version} ===");
            reply($"Uptime:           {uptime}");
            reply($"Verified clients: {clients}");
            reply($"PlayerStats:      {playerStats}");
            reply($"Marketplace:      {marketplace} public listing(s)");
            reply($"Quests:           {quests} public quest(s)");
            reply($"Guilds:           {guilds}");
            reply($"LinkedAccounts:   {links}");
            if (!isAdmin)
            {
                reply($"Economy:          dynamic-pricing {(dynPrice ? "on" : "off")}, quest-minting {(minting ? "on" : "off")}");
                return;
            }
            reply($"Economy:          house pool {Util.SilverFmt.Format(housePool)}, server wealth {Util.SilverFmt.Format(wealthIdx)}, " +
                  $"dynamic-pricing {(dynPrice ? "on" : "off")}, quest-minting {(minting ? "on" : "off")}");
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
            reply($"Loaded extensions ({loaded.Count}); server SDK contract {Extensibility.KmhExtensionCompat.Current}:");
            foreach (var ext in loaded) reply($"  - {ext.Name} v{ext.Version}  (SDK contract {ext.SdkContract}, {ext.SourceDll})");
            var (mp, au, wa, qu, wd, vis) = Extensibility.KmhHooks.Instance.Counts();
            if (mp + au + wa + qu + wd + vis > 0)
                reply($"  Active rule hooks: marketplace {mp}, auction {au}, want {wa}, quest {qu}, withdraw {wd} (extensions can veto these).");
            if (vis > 0)
                reply($"  Marketplace visibility hooks: {vis} - listings are filtered per viewer, so snapshot sharing is off (higher CPU per broadcast).");
        }

        // The legacy `reload-<area>` forms route here too, so the two spellings can never diverge.
        private static void ReloadCmd(string[] args, string actorName, Action<string> reply)
        {
            string area = LowerArg1(args);

            // These three do more than drop a cached object; everything else comes from the KmhConfigReload table.
            switch (area)
            {
                case "features":    ReloadFeatures(reply);           return;
                case "discord":     ReloadDiscord(actorName, reply); return;
                case "marketplace": KmhCleanupCommands.ReloadMarketplace(reply); return;
                case "all":
                {
                    // After a clean boot this must report no change; a named field means boot failed to apply it.
                    var before = Features.Comms.CommsPresentation.Fields();

                    // One hello for the whole reload, not one per area - see CommsStartup.BeginBatch.
                    Features.Comms.CommsStartup.BeginBatch();

                    // Features and Discord come from the table; listing them here too restarts the Discord bridge twice.
                    int n = 0;
                    foreach (KmhConfigReload.Entry e in KmhConfigReload.All())
                        if (e.Reload != null && KmhConfigReload.Run(e.Area, reply, out _)) n++;
                    KmhCleanupCommands.ReloadMarketplace(reply);
                    Features.Comms.CommsStartup.EndBatch();

                    System.Collections.Generic.List<string> changed =
                        Features.Comms.CommsPresentation.Diff(before, Features.Comms.CommsPresentation.Fields());
                    if (changed.Count == 0)
                    {
                        reply("Communications state unchanged - startup had already applied it.");
                    }
                    else
                    {
                        reply($"Communications state CHANGED in {changed.Count} field(s) - startup did not apply these:");
                        foreach (string c in changed) { reply("  " + c); ServerLog.Warn("reload all: " + c); }
                    }

                    reply($"Reloaded {n + 1} area(s). Restart-only: {RestartOnlyList()}");
                    return;
                }
            }

            if (KmhConfigReload.Run(area, reply, out string why)) return;

            if (!string.IsNullOrEmpty(area)) reply(why);
            reply("Usage: kmh reload <" + string.Join("|", KmhConfigReload.ReloadableAreas().ToArray())
                  + "|marketplace|all>");
            reply("Restart-only: " + RestartOnlyList());
        }

        // These gate themselves outside Features.json; without this an owner is told everything is on while one is off.
        private static string SubsystemSuffix()
        {
            var off = new System.Collections.Generic.List<string>();
            try { if (!Features.Frontier.FrontierConfig.Current.Enabled) off.Add("Frontier"); } catch { }
            try { if (!Features.Media.MediaConfig.Current.Images.ResolverEnabled) off.Add("media resolver"); } catch { }
            try { if (!Features.Chat.ChatConfig.Current.AllowImagePreviews) off.Add("chat image previews"); } catch { }
            try { if (!Features.Identity.StaffConfig.Current.ShowStaffBadges) off.Add("staff badges"); } catch { }
            return off.Count == 0 ? "" : "; subsystems OFF: " + string.Join(", ", off);
        }

        // Needs 'confirm': it disconnects every player, and it sits one keystroke from `reload`.
        private static void RestartCmd(string[] args, string actorName, Action<string> reply, bool fromConsole)
        {
            // The settings window's bar: an RWT admin moderates a session but may not restart somebody's server.
            // Discord's console role keeps it, as RWT's own console commands already stop the server from there.
            if (!fromConsole && !ConsoleExecutor.Remote && !OwnerWithSettingsAccess(actorName, reply, "Restarting")) return;

            if (!IsConfirm(args))
            {
                reply($"'kmh restart confirm' restarts this server now - {Maintenance.KmhRestart.Describe()}.");
                reply("  Every player is disconnected and reconnects on their own once it is back.");
                reply($"  Settings waiting on a restart: {RestartOnlyList()}");
                return;
            }
            Maintenance.KmhRestart.Request(actorName, reply);
        }

        // Both halves: the Owners list is a file, the grant is a console action meaning "I am at the machine".
        internal static bool OwnerWithSettingsAccess(string actorName, Action<string> reply, string what)
        {
            if (!Features.Identity.StaffRegistry.IsOwner(actorName))
            {
                reply($"{what} is owner-only. '{actorName}' is not listed under Owners in Staff.json.");
                return false;
            }
            if (!Features.Admin.ConfigEditorGrant.Allows(actorName, DateTimeOffset.UtcNow))
            {
                reply($"{what} needs settings access opened at the server console first: "
                    + $"kmh config-editor allow {actorName}");
                return false;
            }
            return true;
        }

        private static string RestartOnlyList()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (KmhConfigReload.Entry e in KmhConfigReload.All())
            {
                if (!string.IsNullOrEmpty(e.RestartOnly)) parts.Add($"{e.Area} ({e.RestartOnly})");
                else if (e.Reload == null) parts.Add($"{e.Area} (the whole area)");
            }
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
        }

        private static void ReloadEconomy(Action<string> reply)
        {
            Features.Economy.EconomyConfig.Reload();
            Features.Sites.SitesConfig.Reload();   // site pricing reads economy values; keep the pair consistent
            var cfg = Features.Economy.EconomyConfig.Current;
            reply($"Economy config reloaded: tax {cfg.Marketplace.TaxPercent}%, " +
                  $"price {cfg.Marketplace.MinUnitPrice:0.###}-{cfg.Marketplace.MaxUnitPrice:0.###}, " +
                  $"max {cfg.Marketplace.MaxOpenListingsPerUser} listings/user, lifetime {cfg.Marketplace.ListingLifetimeHours}h.");
        }

        private static void ReloadWorld(Action<string> reply)
        {
            Features.World.WorldConfig.Reload();
            var cfg = Features.World.WorldConfig.Current;
            reply($"World config reloaded: events {(cfg.Events.Enabled ? "on" : "off")}, " +
                  $"auto-roll {(cfg.Events.AutoRoll ? $"every {cfg.Events.RollEveryMinutes}m" : "off")}, " +
                  $"auto-quests {(cfg.WorldQuests.AutoGen.Enabled ? $"every {cfg.WorldQuests.AutoGen.EveryMinutes}m" : "off")}.");
        }

        private static void ReloadFeatures(Action<string> reply)
        {
            Features.Comms.CommsStartup.ApplyFeatures(push: true);
            System.Collections.Generic.List<string> disabled = Features.FeaturesConfig.Current.DisabledList();

            // Named as Features.json specifically, because it is only one set of gates and a plain "all enabled" would lie.
            string state = disabled.Count == 0
                ? "all Features.json gates enabled" + SubsystemSuffix()
                : "disabled: " + string.Join(", ", disabled) + SubsystemSuffix();
            reply($"Features reloaded ({state}). Connected clients refreshed.");
            ServerLog.Info($"Features reloaded by admin ({state})");
        }

        private static void EventCmd(string[] args, string actorName, Action<string> reply)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "" || sub == "help")
            {
                reply("Usage: kmh event <type> [minutes] [magnitude] [target]   (duration in minutes; 2h / 1d also accepted)");
                reply("  types: tax_holiday, market_boom, market_crash, double_worker_xp, house_stipend, resource_shortage, bounty_target");
                reply("  e.g. kmh event tax_holiday 30          - a 30-minute tax holiday");
                reply("  kmh event end <type>   - end an active event");
                reply("  kmh event list         - show active events");
                return;
            }
            if (sub == "list")
            {
                var active = Features.World.WorldStore.ActiveEvents();
                if (active.Count == 0) { reply("No active events."); return; }
                foreach (var e in active) reply($"  {e.Type} - {e.Title} ({e.Description})");
                return;
            }
            if (sub == "end")
            {
                string t = args.Length > 2 ? args[2] : "";
                var (ok, reason) = Features.World.WorldEngine.EndEvent(t);
                reply(reason);
                return;
            }
            int minutes = args.Length > 2 ? System.Math.Max(0, ParseDurationMinutes(args[2])) : 0;
            double mag = args.Length > 3 && double.TryParse(args[3], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double m) ? m : 0;
            string target = args.Length > 4 ? args[4] : "";
            var (fired, why) = Features.World.WorldEngine.FireEvent(sub, mag, target, minutes, actorName);
            reply(why);
        }

        private static void WorldQuestCmd(string[] args, string actorName, Action<string> reply)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "" || sub == "help")
            {
                reply("Usage: kmh worldquest <coop|comp> <hunt|build|deliver> <defName> <goal> <reward> [minutes] [title...]");
                reply("  duration in minutes (2h / 1d also work); e.g. kmh worldquest coop hunt Muffalo 40 500 30 Thin the Herds");
                reply("  kmh worldquest list      - show active global quests");
                reply("  kmh worldquest end <id>  - cancel a quest (refunds its reward to the house pool)");
                reply("  kmh worldquest deliver <id> <user> <qty>  - credit a delivery lost to a disconnect");
                return;
            }
            if (sub == "deliver")
            {
                if (args.Length < 5 || !long.TryParse(args[2], out long did) || !int.TryParse(args[4], out int dqty) || dqty <= 0)
                { reply("Usage: kmh worldquest deliver <id> <user> <qty>"); return; }
                var dq = Features.World.WorldStore.FindQuest(did);
                if (dq == null) { reply($"No quest #{did}."); return; }
                // The entry point a client's delivery uses, so payout and consequences match a real one.
                Features.World.WorldEngine.ApplyDelivery(args[3], did, dq.TargetDefName, dqty);
                ServerLog.Info($"kmh worldquest deliver #{did} {args[3]} x{dqty} by {actorName}");
                var after = Features.World.WorldStore.FindQuest(did);
                reply(after == null
                    ? $"Credited {dqty}x {dq.TargetDefName} to #{did}."
                    : $"Credited {dqty}x {dq.TargetDefName} to #{did} - now {after.ProgressQty}/{after.GoalQty}, state {after.State}.");
                return;
            }
            if (sub == "list")
            {
                var qs = Features.World.WorldStore.ActiveQuests();
                if (qs.Count == 0) { reply("No active global quests."); return; }
                foreach (var q in qs)
                    reply($"  #{q.Id} [{q.Kind}/{q.Objective}] {q.Title} - {q.ProgressQty}/{q.GoalQty} {q.TargetDefName}, " +
                          $"reward {Util.SilverFmt.Format(q.RewardPool)}");
                return;
            }
            if (sub == "end")
            {
                if (args.Length < 3 || !long.TryParse(args[2], out long id)) { reply("Usage: kmh worldquest end <id>"); return; }
                var (ok, reason) = Features.World.WorldEngine.EndWorldQuest(id, actorName);
                reply(reason);
                return;
            }

            if (args.Length < 6)
            {
                reply("Usage: kmh worldquest <coop|comp> <hunt|build|deliver> <defName> <goal> <reward> [minutes] [title...]");
                return;
            }
            string objective = args[2];
            string defName   = args[3];
            if (!int.TryParse(args[4], out int goal) || goal <= 0) { reply($"Goal must be a positive integer (got '{args[4]}')."); return; }
            if (!long.TryParse(args[5], out long reward) || reward < 0) { reply($"Reward must be a non-negative integer (got '{args[5]}')."); return; }
            int minutes = 0, titleStart = 6;
            if (args.Length > 6) { int parsed = ParseDurationMinutes(args[6]); if (parsed >= 0) { minutes = parsed; titleStart = 7; } }
            string title = args.Length > titleStart ? string.Join(" ", args[titleStart..]) : "";

            var (created, why) = Features.World.WorldEngine.CreateWorldQuest(sub, objective, defName, goal, reward, minutes, title, "", actorName);
            reply(why);
        }

        private static void FrontierCmd(string[] args, string actorName, Action<string> reply)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            var cfg = Features.Frontier.FrontierConfig.Current;

            if (sub == "help")
            {
                KmhCommandHelp.Topic("frontier", reply, isAdmin: true);
                reply("  kmh frontier claim <tile> <user> [guild]   Claim on a player's behalf");
                reply("  kmh frontier status verbose               Add raw timers and ids");
                return;
            }

            if (sub == "claim")
            {
                if (args.Length < 4 || !int.TryParse(args[2], out int ctile))
                { reply("Usage: kmh frontier claim <tile> <user> [guild]"); return; }
                bool forGuild = args.Length > 4 && string.Equals(args[4], "guild", StringComparison.OrdinalIgnoreCase);
                // No admin bypass: the same eligibility and window rules a player's own claim goes through.
                var (cok, creason) = Features.Sites.SiteStore.ClaimOutpost(args[3], ctile, forGuild);
                ServerLog.Info($"kmh frontier claim {ctile} for {args[3]} by {actorName}: {creason}");
                reply(creason);
                if (cok) Features.Sites.SiteHandler.BroadcastSnapshot();
                return;
            }

            if (sub == "status")
            {
                bool verbose = args.Length > 2 && string.Equals(args[2], "verbose", StringComparison.OrdinalIgnoreCase);
                Features.Frontier.KmhWorldDirector.StatusView v = Features.Frontier.KmhWorldDirector.Status();

                reply("Frontier");
                reply($"  State: {(v.Enabled ? v.Step : "disabled")}");

                if (!string.IsNullOrEmpty(v.Blocker))
                {
                    reply("  Automatic action: BLOCKED");
                    reply($"  Reason: {v.Blocker}");
                }
                else if (v.NextEligibleUtc > v.NowUtc)
                    reply($"  Next action: {FormatDuration(TimeSpan.FromTicks(v.NextEligibleUtc - v.NowUtc))}");
                else
                    reply("  Next action: due now");

                reply($"  Budget: {v.Budget} / {v.BudgetMax}");
                reply($"  Outposts: {v.Outposts} / {v.MaxOutposts}");
                reply($"  Operations: {v.Operations} / {v.MaxOperations}");
                reply(v.PlacementOpen
                    ? $"  Placement: open, {v.PlacementProposals} proposal(s), closes in {FormatDuration(TimeSpan.FromTicks(Math.Max(0, v.PlacementExpiresUtc - v.NowUtc)))}"
                    : "  Placement: none");
                if (v.LastAskedClients >= 0) reply($"  Last asked: {v.LastAskedClients} client(s)");
                if (v.UnfinishedResolutions > 0) reply($"  Resolutions mid-flight: {v.UnfinishedResolutions}");

                if (verbose)
                {
                    var raw = Features.Frontier.KmhWorldDirector.StateForReport();
                    reply($"  revision {raw.Revision}, next eligible {new DateTime(v.NextEligibleUtc, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss}Z");
                    reply($"  budget refilled {new DateTime(Math.Max(1, v.BudgetRefilledUtc), DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss}Z, window {cfg.BudgetRefillHours}h");
                    foreach (var rec in Features.Frontier.KmhWorldDirector.UnfinishedResolutions())
                        reply($"  operation #{rec.OperationId}: {rec.Phase} -> {rec.Consequence}");
                }
                return;
            }

            if (sub == "list")
            {
                var sites = Features.Sites.SiteStore.AllForApi();
                int n = 0;
                foreach (var s in sites)
                {
                    if (string.IsNullOrEmpty(s.OutpostTemplate)) continue;
                    n++;
                    reply($"  tile {s.Tile} {Features.Sites.SiteStore.NameOf(s)} [{s.OutpostTemplate}/{s.OutpostState}] condition {s.Stability}% held by {Features.Sites.SiteOwnership.ControllerLabel(s)}");
                }
                if (n == 0) reply("No outposts.");
                return;
            }

            if (sub == "place")
            {
                if (args.Length < 3 || !int.TryParse(args[2], out int tile))
                { reply("Usage: kmh frontier place <tile>"); return; }
                string result = Features.Frontier.KmhWorldDirector.TryPlaceAsOperator(tile);
                Diagnostics.ServerLog.Info($"kmh frontier place {tile} by {actorName}: {result}");
                reply(result);
                return;
            }

            if (sub == "tick") { Features.Frontier.KmhWorldDirector.Tick(); reply("Director ticked."); return; }

            reply($"Unknown subcommand '{sub}'. Try 'kmh frontier help'.");
        }

        private static void RoadworksCmd(string[] args, string actorName, Action<string> reply)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            if (sub == "help")
            {
                reply("Usage: kmh roadworks [status|list|cancel <id>]");
                reply("  cancel <id>  - stop a project and return its unbuilt escrow to the owner");
                return;
            }

            var all = Features.Roadworks.RoadworksStore.AllProjectsForApi();
            if (sub == "status")
            {
                int building = 0;
                foreach (var p in all) if (p.State == Features.Roadworks.Dto.RoadProject.StateBuilding) building++;
                reply($"=== Roadworks === {Features.Roadworks.RoadworksStore.SegmentCount} segment(s) built, "
                      + $"{building} active project(s) of {all.Count} total");
                reply($"  reserved escrow: {Util.SilverFmt.Format(Features.Roadworks.RoadworksStore.ReservedSilverTotal())}");
                return;
            }

            if (sub == "list")
            {
                if (all.Count == 0) { reply("No road projects."); return; }
                foreach (var p in all)
                    reply($"  #{p.Id} [{p.State}] {p.Tier} from tile {p.SiteTile} by {p.OwnerUsername} - "
                          + $"segment {p.CurrentSegment}/{Math.Max(0, (p.Route?.Count ?? 0) - 1)} "
                          + $"({p.CurrentProgress:P0}), escrow {Util.SilverFmt.Format(p.EscrowSilver - p.EscrowSilverSpent)} unspent");
                return;
            }

            if (sub == "cancel")
            {
                if (args.Length < 3 || !long.TryParse(args[2], out long id)) { reply("Usage: kmh roadworks cancel <id>"); return; }
                string owner = Features.Roadworks.RoadworksStore.OwnerOfProject(id);
                if (string.IsNullOrEmpty(owner)) { reply($"No such project #{id}."); return; }
                // Same path a player's own cancel takes, so the refund and escrow accounting cannot drift.
                bool ok = Features.Roadworks.RoadworksStore.CancelProject(owner, id, out int refunded, out string why);
                ServerLog.Info($"kmh roadworks cancel #{id} (owner {owner}) by {actorName}: {(ok ? "cancelled" : why)}");
                reply(ok ? $"Cancelled #{id}; {Util.SilverFmt.Format(refunded)} returned to {owner}. Built road stays." : why);
                if (ok) Features.Roadworks.RoadworksHandler.BroadcastSnapshot();
                return;
            }

            reply($"Unknown subcommand '{sub}'. Try 'kmh roadworks help'.");
        }

        private static void ReloadDiscord(string actorName, Action<string> reply)
        {
            if (!Features.Discord.DiscordBridge.TryClaimReload(out TimeSpan wait))
            { reply($"The Discord bridge was just reloaded - try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))}s."); return; }
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

        // One player goes through the journaled wipe-economy, since a vault cleared alone leaves escrow and payouts to recover.
        private static void TreasuryReset(string[] args, string actorName, bool fromConsole, Action<string> reply)
        {
            string target = args != null && args.Length > 1 ? args[1].Trim() : "";
            bool commit = IsConfirm(args);
            if (target.Length == 0)
            {
                reply("Usage: kmh treasury-reset <user|all> <dry|confirm>. One player: the same as wipe-economy. "
                    + "all: every personal vault and nothing else. Backs up first.");
                return;
            }
            if (!string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                Maintenance.KmhPlayerCleanup.WipeEconomy(target, commit, reply);
                return;
            }

            const string scope = "guild vaults, listings, auctions, wants, escrow and owed payouts are untouched - 'kmh season reset' wipes all of it";
            if (!commit)
            {
                reply($"dry-run: {Features.Treasury.TreasuryStore.PersonalVaultCount()} personal treasury vault(s) WOULD be cleared; {scope}. "
                    + "Run 'kmh treasury-reset all confirm' to do it.");
                return;
            }
            if (!OwnerOrConsole(actorName, fromConsole, "Clearing every treasury", reply)) return;
            if (!Persistence.KmhDataBackup.TryCreate("pre-treasury-reset", out string dir, out string err))
            {
                reply($"Reset aborted - backup failed: {err}");
                return;
            }
            string backup = System.IO.Path.GetFileName(dir);
            int n;
            bool saved;
            // Quiesced, or a deposit admitted a moment earlier lands in a vault after it was cleared.
            using (Maintenance.KmhValueOps.Barrier gate = Maintenance.KmhValueOps.Quiesce(
                       Maintenance.KmhMaintenanceReason.DataRepair, TimeSpan.FromSeconds(10), out string busy))
            {
                if (gate == null) { reply($"Reset aborted - {busy}. Nothing was cleared (backup {backup})."); return; }
                n = Features.Treasury.TreasuryStore.ResetAllPersonal();
                saved = n == 0 || Features.Treasury.TreasuryStore.SaveToDisk();
            }
            ServerLog.Warn($"treasury-reset ALL by {actorName}: cleared {n} personal vault(s) (backup {backup}){(saved ? "" : " - NOT written down")}");
            reply($"Backup {backup}. Reset {n} personal treasury vault(s); {scope}."
                + (saved ? "" : " WARNING: the treasury could not be written down - a restart brings the vaults back; fix the disk and run it again."));
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (!string.IsNullOrEmpty(u))
                    SubProtocol.KmhRouter.SendTo(c, SubProtocol.KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(u));
            }
        }

        private static void Enforce(string[] args, bool isAdmin, Action<string> reply)
        {
            Features.Enforcement.EnforcementConfig cfg = Features.Enforcement.EnforcementConfig.Current;
            string sub = LowerArg1(args, "status");

            switch (sub)
            {
                case "on":
                case "enable":
                    if (!isAdmin) { reply("'enforce on' requires admin."); return; }
                    cfg.Enabled = true; cfg.Save();
                    // Only the hash goes out; a client whose hash differs pulls the profile itself.
                    Features.Enforcement.EnforcementHandler.BroadcastSnapshot();
                    if (Features.Enforcement.EnforcementProfile.HasProfile)
                        reply($"Config enforcement ENABLED. Connected clients will pull the profile ({Features.Enforcement.EnforcementProfile.FileCount} file(s)) if they don't already have it.");
                    else
                        reply("Config enforcement ENABLED (soft lock only - no profile to push). An admin should Publish their configs in-game (Colony Nexus tab -> Config Enforcement) for hard enforcement.");
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
                    // Only re-reads the persisted zip; the profile itself is published in-game by an admin.
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
                    else reply("No profile published yet - an admin should Publish their configs in-game (Colony Nexus tab -> Config Enforcement).");
                    return;

                case "safe":
                    EnforceSafe(args, isAdmin, cfg, reply);
                    return;

                default: // status
                    reply($"Config enforcement: {(cfg.Enabled ? "ON" : "OFF")}  (admin bypass: {(cfg.AdminBypass ? "on" : "off")})");
                    reply($"Profile: {(Features.Enforcement.EnforcementProfile.HasProfile ? $"{Features.Enforcement.EnforcementProfile.FileCount} file(s), hash {Features.Enforcement.EnforcementProfile.Hash}" : "(none) - an admin should Publish configs in-game (Colony Nexus tab -> Config Enforcement)")}");
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

        // Split into topics because listing every command at once floods roughly fifty lines into chat.
        internal static void Help(string[] args, Action<string> reply, bool isAdmin = true)
        {
            string entered = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string topic   = LowerArg1(args, null);

            if (entered != "help" && entered != "")
            {
                reply($"Unknown Colony Nexus command: {entered}");
                reply("Try: kmh help");
                return;
            }

            if (topic == null) { KmhCommandHelp.Root(reply, isAdmin); return; }

            if (topic == "all")
            {
                foreach (string t in new[] { "server", "frontier", "world", "sites", "economy",
                                             "players", "config", "backup", "check", "fix", "advanced" })
                    KmhCommandHelp.Topic(t, reply, isAdmin);
                return;
            }

            KmhCommandHelp.Topic(topic, reply, isAdmin);
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays    >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
            if (span.TotalHours   >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
            return $"{(int)span.TotalSeconds}s";
        }
    }

    // The console operator is always treated as admin, unlike the in-game /kmh path.
    internal sealed class KmhServerConsoleCommand : CMD_Base
    {
        public KmhServerConsoleCommand()
        {
            Prefix        = "kmh";
            Description   = "Colony Nexus server admin. Type 'kmh help' for topics, 'kmh help <topic>' for a group's commands.";
            IsChatCommand = false;
            ParameterCount = -1; // accept any number of args
        }

        public override void Action()
        {
            try { Invoke(CommandParameters ?? Array.Empty<string>(), line => Printer.Message($"[Colony Nexus] {line}")); }
            catch (Exception ex) { Printer.Error($"[Colony Nexus] kmh console command threw: {ex.Message}"); }
        }

        internal static void Invoke(string[] args, Action<string> reply)
            => KmhServerCommands.Dispatch(args, isAdmin: true, actorName: "console", reply, fromConsole: !ConsoleExecutor.Remote);
    }
}
