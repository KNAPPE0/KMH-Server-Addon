using System;
using KMHServerAddon.Diagnostics;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.AdminCommands
{
    // Shared `kmh ...` admin commands for both in-game chat and the server console; output goes through a reply
    // delegate. isAdmin gates mutating subcommands (the console operator is always admin).
    internal static class KmhServerCommands
    {
        public static void Dispatch(string[] args, bool isAdmin, string actorName, Action<string> reply)
        {
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "status":         Status(reply);                          break;
                case "diag":           Diag(reply);                            break;
                case "transport":      Transport(reply);                       break;
                case "verify":         Admin(isAdmin, "verify",  reply, () => Verify(reply));            break;
                case "audit":          Admin(isAdmin, "audit",   reply, () => Maintenance.KmhAudit.Run(reply)); break;
                case "backup":         Admin(isAdmin, "backup",  reply, () => Backup(args, reply));      break;
                case "backups":        Admin(isAdmin, "backups", reply, () => Backups(reply));           break;
                case "restore":        Admin(isAdmin, "restore", reply, () => Restore(args, reply));      break;
                case "snapshot-player": Admin(isAdmin, "snapshot-player", reply, () => SnapshotPlayerCmd(args, reply)); break;
                case "snapshot-server": Admin(isAdmin, "snapshot-server", reply, () => SnapshotServerCmd(args, reply)); break;
                case "snapshot-all":    Admin(isAdmin, "snapshot-all",    reply, () => SnapshotAllCmd(args, reply));    break;
                case "snapshot-verify": Admin(isAdmin, "snapshot-verify", reply, () => SnapshotVerifyCmd(args, reply)); break;
                case "restore-preview-player": Admin(isAdmin, "restore-preview-player", reply, () => Persistence.KmhSnapshot.PreviewPlayer(args.Length > 1 ? args[1] : "", reply)); break;
                case "save":           Admin(isAdmin, "save",    reply, () => SaveAll(reply));           break;
                case "export":         Admin(isAdmin, "export",  reply, () => Export(reply));            break;
                case "inspect":        Admin(isAdmin, "inspect", reply, () => Inspect(args, reply));      break;
                case "cancel":         Admin(isAdmin, "cancel",  reply, () => CancelCmd(args, reply));    break;
                case "recover":        Admin(isAdmin, "recover", reply, () => KmhRecoveryCommands.Run(args, reply));   break;
                case "validate":       Admin(isAdmin, "validate", reply, () => KmhValidateCommands.Run(args, reply)); break;
                case "ledger":         Admin(isAdmin, "ledger",  reply, () => Ledger(args, reply));       break;
                case "history":        Admin(isAdmin, "history", reply, () => History(args, reply));      break;
                case "smoketest":      Admin(isAdmin, "smoketest", reply, () => Maintenance.KmhSmokeTest.Run(reply)); break;
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
                case "wipe-player":    Admin(isAdmin, "wipe-player", reply, () => Maintenance.KmhPlayerCleanup.WipePlayer(Arg1(args), IsConfirm(args), reply)); break;
                case "reset-cleanup":  Admin(isAdmin, "reset-cleanup", reply, () => Maintenance.KmhPlayerCleanup.ResetCleanup(Arg1(args), IsConfirm(args), reply)); break;
                case "remove-player-guilds": Admin(isAdmin, "remove-player-guilds", reply, () => Maintenance.KmhPlayerCleanup.RemoveGuilds(Arg1(args), IsConfirm(args), reply)); break;
                case "unlink-player":  Admin(isAdmin, "unlink-player", reply, () => Maintenance.KmhPlayerCleanup.Unlink(Arg1(args), IsConfirm(args), reply)); break;
                case "rebuild-player": Admin(isAdmin, "rebuild-player", reply, () => Maintenance.KmhPlayerCleanup.RebuildPlayer(Arg1(args), reply)); break;
                case "extensions":     Extensions(reply);                      break;
                case "give-silver":    Admin(isAdmin, "give-silver",    reply, () => GiveSilver(args, actorName, reply));  break;
                case "treasury-reset": Admin(isAdmin, "treasury-reset", reply, () => TreasuryReset(args, actorName, reply)); break;
                case "reload-discord": Admin(isAdmin, "reload-discord", reply, () => ReloadDiscord(actorName, reply));     break;
                case "reload-economy": Admin(isAdmin, "reload-economy", reply, () => ReloadEconomy(reply));                break;
                case "reload-world":   Admin(isAdmin, "reload-world",   reply, () => ReloadWorld(reply));                  break;
                case "reload-features": Admin(isAdmin, "reload-features", reply, () => ReloadFeatures(reply));             break;
                case "drain-house":    Admin(isAdmin, "drain-house",    reply, () => DrainHouse(args, reply));             break;
                case "enforce":        Enforce(args, isAdmin, reply);          break;
                case "event":          Admin(isAdmin, "event",          reply, () => EventCmd(args, actorName, reply));    break;
                case "worldquest":
                case "wq":             Admin(isAdmin, "worldquest",     reply, () => WorldQuestCmd(args, actorName, reply)); break;
                case "season":         Admin(isAdmin, "season",         reply, () => SeasonCmd(args, actorName, reply));    break;
                default:               Help(reply);                            break;
            }
        }

        private static void Admin(bool isAdmin, string name, Action<string> reply, Action run)
        {
            if (!isAdmin) { reply($"'{name}' requires admin."); return; }
            run();
        }

        // kmh season [status|roll|reset] - roll archives leaders + advances; reset also wipes the economy for a fresh season.
        private static void SeasonCmd(string[] args, string actorName, Action<string> reply)
        {
            string action = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "status";
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
                // Destructive: require an explicit confirm token so it can't be fat-fingered.
                bool confirmed = args.Length > 2 && string.Equals(args[2], "confirm", StringComparison.OrdinalIgnoreCase);
                if (!confirmed)
                {
                    reply("kmh season reset WIPES the live economy (treasury, marketplace, auctions, wants, sites, quests, reputation, mail, standings) and starts a new season.");
                    reply("Guilds, Discord links, and the season archive are KEPT. It backs up first (reversible via 'kmh restore'). Confirm with:  kmh season reset confirm");
                    return;
                }
                if (Maintenance.KmhSeasonReset.Run(actorName, out string summary)) reply(summary);
                else reply($"Season reset {summary}");
            }
            else
            {
                reply($"Current season: {Features.Seasons.SeasonStore.CurrentSeason}. 'kmh season roll' archives + advances; 'kmh season reset' also wipes the economy.");
            }
        }

        // kmh diag - prove the data pipeline end to end: live in-memory counts + on-disk JSON sizes, so an owner can
        // see colony reports are collected and persisted. If "with colony report" stays 0 while players are connected
        // and have opened Standings, the client isn't sending (check their RimWorld log).
        private static void Diag(Action<string> reply)
        {
            reply("=== KMH data pipeline ===");
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

        // kmh transport - KMH API transport status (on by default since 1.2.0; chat stays as fallback).
        private static void Transport(Action<string> reply)
        {
            Features.Transport.TransportConfig c = Features.Transport.TransportConfig.Current;
            reply("=== KMH transport ===");
            if (!c.EnableKmhApiTransport) { reply("API transport: OFF - clients use the RWT chat path. Enable in Config/Transport.json."); return; }
            reply($"API transport: {(Features.Transport.KmhApiServer.Running ? "listening" : "ENABLED but not bound (see boot log)")} on {c.BindAddress}:{c.KmhApiPort}");
            reply($"Auth: {(c.RequireKmhApiAuth ? "required" : "OFF")} · chat fallback: {(c.AllowChatTransportFallback ? "on" : "off")} · connected: {Features.Transport.KmhApiServer.ConnectedCount}");
        }

        // kmh verify - dry, read-only integrity scan over every KMH JSON. Modifies nothing; safe to run any time.
        private static void Verify(Action<string> reply)
        {
            Persistence.KmhDataIntegrity.ScanResult r = Persistence.KmhDataIntegrity.Scan();
            reply($"=== KMH data integrity === {r.Summary}");
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

        // kmh backup [reason] - flush, then snapshot all of KMH-Data into KMH-Data-Backups/, then prune to retention.
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

        // kmh backups - list existing snapshots, newest first, with the restore recipe.
        private static void Backups(Action<string> reply)
        {
            System.Collections.Generic.List<Persistence.KmhDataBackup.BackupInfo> list = Persistence.KmhDataBackup.List();
            reply($"=== KMH backups ({list.Count}) === {Persistence.KmhDataPaths.BackupRoot}");
            if (list.Count == 0) { reply("  (none yet - made on boot when enabled, or via 'kmh backup')"); return; }
            foreach (Persistence.KmhDataBackup.BackupInfo b in list)
                reply($"  {b.Name}  ({b.Files} file(s), {b.Bytes} bytes)");
            reply("Restore: 'kmh restore <name|latest|before:<time>>' then restart (safety-backs up first). Or copy a backup's contents into KMH-Data by hand while stopped.");
        }

        // kmh snapshot-player <user> [YYYY-MM-DD_HH-MM] - versioned per-player KMH snapshot (recovery + audit).
        private static void SnapshotPlayerCmd(string[] args, Action<string> reply)
        {
            if (args == null || args.Length < 2) { reply("Usage: kmh snapshot-player <username> [YYYY-MM-DD_HH-MM]"); return; }
            string ts = args.Length > 2 ? args[2] : null;
            if (Persistence.KmhSnapshot.SnapshotPlayer(args[1], ts, null, out string dir, out string err))
                reply($"Player snapshot: {dir}");
            else reply($"Snapshot failed: {err}");
        }

        // kmh snapshot-server [YYYY-MM-DD_HH-MM] - versioned full-server KMH snapshot (all shared state).
        private static void SnapshotServerCmd(string[] args, Action<string> reply)
        {
            string ts = args != null && args.Length > 1 ? args[1] : null;
            if (Persistence.KmhSnapshot.SnapshotServer(ts, null, out string dir, out string err))
                reply($"Server snapshot: {dir}");
            else reply($"Snapshot failed: {err}");
        }

        // kmh snapshot-all [YYYY-MM-DD_HH-MM] - server snapshot + a player snapshot for everyone with standings.
        private static void SnapshotAllCmd(string[] args, Action<string> reply)
        {
            string ts = args != null && args.Length > 1 ? args[1] : null;
            System.Collections.Generic.List<string> fail = new System.Collections.Generic.List<string>();
            int n = Persistence.KmhSnapshot.SnapshotAll(ts, out _, fail);
            reply($"Wrote {n} snapshot(s) under {Persistence.KmhDataPaths.SnapshotsRoot}." + (fail.Count > 0 ? $" {fail.Count} issue(s):" : ""));
            foreach (string f in fail.GetRange(0, Math.Min(5, fail.Count))) reply($"  {f}");
        }

        // kmh snapshot-verify <folder> - checksum + JSON-validity check of a snapshot folder against its manifest.
        private static void SnapshotVerifyCmd(string[] args, Action<string> reply)
        {
            if (args == null || args.Length < 2) { reply("Usage: kmh snapshot-verify <snapshot-folder>"); return; }
            reply(Persistence.KmhSnapshot.Verify(args[1], out string detail) ? $"Snapshot OK: {detail}" : $"Snapshot INVALID: {detail}");
        }

        // kmh restore <name|latest|before:<time>> - queue a KMH-Data rollback; applied on the next restart (a safety
        // backup of current data is taken automatically). Use to line KMH up with an external RWT rollback.
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
                reply($"Restore queued: '{resolved}'. RESTART KMH to apply - current data is safety-backed-up automatically.");
                ServerLog.Warn($"kmh restore queued by admin: {resolved}");
            }
            else reply($"Restore not queued: {err}");
        }

        // kmh save - force every store to flush to disk now (saves are already per-mutation; this is a manual flush).
        private static void SaveAll(Action<string> reply)
        {
            int n = Maintenance.KmhDataFlush.FlushAll(reply);
            reply($"Flushed {n} KMH store(s) to {Persistence.KmhDataPaths.Folder}.");
            reply("NOTE: this saves KMH-Data ONLY, not RWT player colonies. KMH can't reach an RWT save-all API - save colonies via RWT/each client before restart.");
            ServerLog.Info($"kmh save: flushed {n} store(s)");
        }

        // kmh export - write the machine-readable status snapshot now (KMH-Data/status.json) for external tooling.
        private static void Export(Action<string> reply)
        {
            if (Maintenance.KmhStatusExport.WriteToDisk())
                reply($"Status exported to {Persistence.KmhDataPaths.StatusFile} (also auto-written on boot + every ~60s).");
            else
                reply("Status export failed - see server log.");
        }

        // kmh inspect <subsystem> - read-only dump of live state, including the IDs needed by `kmh cancel`.
        private static void Inspect(string[] args, Action<string> reply)
        {
            string what = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
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
                        reply($"  #{w.Id} {w.QtyFilled}/{w.QtyWanted}x {w.ItemDefName} by {w.BuyerUsername} @ {Util.SilverFmt.Format(w.UnitPriceSilver)}/ea, " +
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
                    reply("Cancel a stuck one with: kmh cancel quest <id>  (refunds the poster)");
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
                case "sites":
                {
                    var sites = Features.Sites.SiteStore.BuildSnapshotFor("")?.Sites;
                    string user = args != null && args.Length > 2 && string.Equals(args[1], "inspect", StringComparison.OrdinalIgnoreCase) ? args[2] : null;
                    int n = sites?.Count ?? 0;
                    reply(user == null ? $"=== Sites ({n}) ===  ('kmh sites inspect <user>' for one player's detail)" : $"=== Sites involving '{user}' ===");
                    if (sites != null)
                        foreach (var s in sites)
                        {
                            bool owns  = user != null && string.Equals(s.OwnerUsername, user, StringComparison.OrdinalIgnoreCase);
                            bool works = user != null && s.Workers != null && s.Workers.Contains(user, StringComparer.OrdinalIgnoreCase);
                            if (user != null && !owns && !works) continue;
                            reply($"  tile {s.Tile} {s.ItemDefName} x{s.BaseAmountPerCycle}/cycle by {s.OwnerUsername}");
                            if (user == null || s.WorkerProgress == null) continue;
                            foreach (var kv in s.WorkerProgress)
                            {
                                var wp = kv.Value; if (wp == null) continue;
                                string st = wp.Legacy ? "LEGACY (disabled)" : wp.PawnLoadId <= 0 ? "MISSING PAWN DATA (not producing)"
                                          : !string.IsNullOrEmpty(wp.BlockedReason) ? $"BLOCKED ({wp.BlockedReason})" : $"active (pawn '{wp.PawnName}')";
                                reply($"    worker {kv.Key}: {st}");
                            }
                        }
                    break;
                }
                case "guilds":
                case "guild":
                {
                    string sub = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (sub == "repair")
                    {
                        int fixes = Features.Guilds.GuildStore.RepairMembership(out System.Collections.Generic.List<string> gnotes);
                        reply(fixes == 0 ? "Guild membership is consistent - nothing to repair."
                                         : $"Guild membership repaired: {fixes} entry(ies) reconciled.");
                        foreach (string n in gnotes) reply($"  {n}");
                    }
                    else if (sub == "inspect")
                    {
                        string gname = args != null && args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "";
                        if (Features.Guilds.GuildStore.InspectGuild(gname, out System.Collections.Generic.List<string> glines))
                            foreach (string l in glines) reply(l);
                        else reply(string.IsNullOrEmpty(gname) ? "Usage: kmh guild inspect <guild name>" : $"No guild named '{gname}'.");
                    }
                    else reply($"=== Guilds === {Features.Guilds.GuildStore.ListGuilds().Count} guild(s). 'kmh guild inspect <name>' shows detail; 'kmh guild repair' reconciles membership. Manage in-game (Guild Hall) or Discord.");
                    break;
                }
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

        // kmh cancel <auction|want|quest> <id> - admin recovery for a stuck entry (full refund/undo).
        private static void CancelCmd(string[] args, Action<string> reply)
        {
            string kind = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
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

        private static string Arg1(string[] a) => a != null && a.Length > 1 ? a[1] : "";
        private static bool IsConfirm(string[] a)
        {
            if (a != null) foreach (string s in a) if (string.Equals(s, "confirm", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void PurgeRouter(string[] args, Action<string> reply)
        {
            switch (args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "")
            {
                case "marketplace": KmhCleanupCommands.PurgeMarketplaceSeller(args, reply); break;
                case "sites":       Maintenance.KmhPlayerCleanup.PurgeSites(args.Length > 3 ? args[3] : "", IsConfirm(args), reply); break;
                default:            reply("Usage: kmh purge marketplace seller <user> <dry|confirm>  |  kmh purge sites owner <user> <dry|confirm>"); break;
            }
        }

        // kmh rebuild-standings - re-read the player/colonist stores from disk and re-push to every client. Use if a
        // standings board looks stale or wrong; the snapshot is always rebuilt fresh from the store, so this just
        // reloads the source of truth and rebroadcasts (non-destructive).
        private static void RebuildStandings(Action<string> reply)
        {
            Features.PlayerStats.PlayerStatsStore.LoadFromDisk();
            Features.PlayerStats.PlayerStatsStore.LoadColonistsFromDisk();
            int n = Features.PlayerStats.PlayerStatsStore.BuildSnapshot().Entries.Count;
            Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot();
            reply($"Standings rebuilt from disk ({n} player(s)) and re-pushed to all clients.");
            ServerLog.Info($"kmh rebuild-standings: reloaded {n} player(s) and rebroadcast");
        }

        // kmh ledger [count] | kmh ledger <user> [count] - newest economy audit-trail entries, for disputes.
        private static void Ledger(string[] args, Action<string> reply)
        {
            string user = null;
            int count = 25;
            if (args != null && args.Length > 1)
            {
                // arg can be a count or a username; a username may be followed by a count.
                if (int.TryParse(args[1], out int c1)) count = c1;
                else { user = args[1]; if (args.Length > 2 && int.TryParse(args[2], out int c2)) count = c2; }
            }
            count = Math.Max(1, Math.Min(count, 200));
            System.Collections.Generic.List<string> lines = Persistence.TransactionLedger.ReadRecent(count, user);
            reply($"=== Economy ledger ({lines.Count} newest{(user != null ? $" for {user}" : "")}) ===");
            if (lines.Count == 0) reply("  (no entries yet - the ledger records every silver/item movement as it happens)");
            else foreach (string l in lines) reply("  " + l);
        }

        // kmh history <domain> [contains] [count] - newest state-change records (quests/guilds/sites/market/...).
        private static void History(string[] args, Action<string> reply)
        {
            string domain = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
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

        // Human "time left" for an EndsUtcTicks value.
        private static string Remain(long endsUtcTicks)
        {
            if (endsUtcTicks <= 0) return "n/a";
            TimeSpan left = new DateTime(endsUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow;
            return left.Ticks <= 0 ? "ended" : FormatDuration(left);
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

            long housePool  = Features.Marketplace.MarketplaceStore.HousePoolBalance();
            long wealthIdx  = Features.PlayerStats.PlayerStatsStore.TotalReportedWealth();
            bool dynPrice   = Features.Economy.EconomyConfig.Current.DynamicDemandPricingEnabled;
            bool minting    = Features.World.WorldConfig.Current.AllowMintedRewards;

            reply($"=== {Constants.DisplayName} v{typeof(Main_).Assembly.GetName().Version} ===");
            reply($"Uptime:           {uptime}");
            reply($"Verified clients: {clients}");
            reply($"PlayerStats:      {playerStats}");
            reply($"Marketplace:      {marketplace} public listing(s)");
            reply($"Quests:           {quests} public quest(s)");
            reply($"Guilds:           {guilds}");
            reply($"LinkedAccounts:   {links}");
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

        private static void ReloadWorld(Action<string> reply)
        {
            Features.World.WorldConfig.Reload();
            var cfg = Features.World.WorldConfig.Current;
            reply($"World config reloaded: events {(cfg.EventsEnabled ? "on" : "off")}, " +
                  $"auto-roll {(cfg.AutoRollEvents ? $"every {cfg.EventRollEveryMinutes}m" : "off")}, " +
                  $"auto-quests {(cfg.AutoGenerateQuests ? $"every {cfg.QuestGenEveryMinutes}m" : "off")}.");
        }

        // Re-read Features.json and refresh connected clients so a toggle applies without a restart.
        private static void ReloadFeatures(Action<string> reply)
        {
            Features.FeaturesConfig.Reload();
            System.Collections.Generic.List<string> disabled = Features.FeaturesConfig.Current.DisabledList();

            int refreshed = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                SubProtocol.KmhHandshakeHandler.SendHelloTo(c);
                refreshed++;
            }

            string state = disabled.Count == 0 ? "all systems enabled" : "disabled: " + string.Join(", ", disabled);
            reply($"Features reloaded ({state}). Refreshed {refreshed} client(s).");
            ServerLog.Info($"Features reloaded by admin ({state})");
        }

        // event <type> [hours] [magnitude] [target] | event end <type> | event list
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
            // Fire: sub is the type; optional duration (minutes), magnitude, target follow.
            int minutes = args.Length > 2 ? System.Math.Max(0, ParseDurationMinutes(args[2])) : 0;
            double mag = args.Length > 3 && double.TryParse(args[3], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double m) ? m : 0;
            string target = args.Length > 4 ? args[4] : "";
            var (fired, why) = Features.World.WorldEngine.FireEvent(sub, mag, target, minutes, actorName);
            reply(why);
        }

        // worldquest <coop|comp> <hunt|build> <defName> <goal> <reward> [minutes] [title...] | worldquest list | worldquest end <id>
        private static void WorldQuestCmd(string[] args, string actorName, Action<string> reply)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "" || sub == "help")
            {
                reply("Usage: kmh worldquest <coop|comp> <hunt|build|deliver> <defName> <goal> <reward> [minutes] [title...]");
                reply("  duration in minutes (2h / 1d also work); e.g. kmh worldquest coop hunt Muffalo 40 500 30 Thin the Herds");
                reply("  kmh worldquest list      - show active global quests");
                reply("  kmh worldquest end <id>  - cancel a quest (refunds its reward to the house pool)");
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

            // create: sub = kind; args[2]=objective, [3]=defName, [4]=goal, [5]=reward, [6]=hours?, rest=title
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

        // kmh treasury-reset <user|all> - clears personal treasury (and a solo guild's vault, which would otherwise
        // shelter silver) so a player can't farm by depositing starting resources, resetting their save, repeating.
        // Backs up KMH-Data first and logs what was cleared. Multi-member guild vaults are left alone.
        private static void TreasuryReset(string[] args, string actorName, Action<string> reply)
        {
            string target = args != null && args.Length > 1 ? args[1].Trim() : "";
            if (target.Length == 0)
            {
                reply("Usage: kmh treasury-reset <user|all>. Clears personal treasury (+ solo guild vault). Backs up first.");
                return;
            }

            if (!Persistence.KmhDataBackup.TryCreate("pre-treasury-reset", out string dir, out string err))
            {
                reply($"Reset aborted - backup failed: {err}");
                return;
            }
            string backup = System.IO.Path.GetFileName(dir);

            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                int n = Features.Treasury.TreasuryStore.ResetAllPersonal();
                ServerLog.Warn($"treasury-reset ALL by {actorName}: cleared {n} personal vault(s) (backup {backup})");
                reply($"Backup {backup}. Reset {n} personal treasury vault(s); guild vaults untouched.");
                foreach (ServerClient c in Network.ServerClients.Keys)
                {
                    if (c?.IsVerified != true) continue;
                    string u = c.GetData<UserFile>()?.Username;
                    if (!string.IsNullOrEmpty(u))
                        SubProtocol.KmhRouter.SendTo(c, SubProtocol.KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(u));
                }
                return;
            }

            long   had       = Features.Treasury.TreasuryStore.ResetPersonal(target);
            string soloGuild = Features.Guilds.GuildStore.SoloGuildOf(target);
            long   guildHad  = soloGuild != null ? Features.Treasury.TreasuryStore.ClearGuildVault(soloGuild) : -1;
            if (had < 0 && guildHad < 0) { reply($"No personal treasury found for '{target}'."); return; }
            // Purge escrowed value too (marketplace/auction/want), which sits outside the treasury and would shelter it.
            int purged = Features.Marketplace.MarketplaceStore.PurgeSeller(target);
            var (aRemoved, aRetracted) = Features.Auctions.AuctionStore.PurgeUser(target);
            purged += aRemoved + aRetracted + Features.WantBoard.WantStore.PurgeBuyer(target);
            long personalCleared = had < 0 ? 0 : had;
            string guildNote = (soloGuild != null && guildHad >= 0) ? $" + solo guild '{soloGuild}' ({Util.SilverFmt.Format(guildHad)})" : "";
            string escrowNote = purged > 0 ? $" + {purged} escrow(s) purged" : "";
            ServerLog.Warn($"treasury-reset for {target} by {actorName}: cleared personal {personalCleared}s{guildNote}{escrowNote} (backup {backup})");
            reply($"Backup {backup}. Reset {target}'s personal treasury (held {Util.SilverFmt.Format(personalCleared)}){guildNote}{escrowNote}.");
            ServerClient sc = SubProtocol.KmhRouter.ResolveClient(target);
            if (sc != null)
                SubProtocol.KmhRouter.SendTo(sc, SubProtocol.KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(target));
            if (purged > 0)
            {
                Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
                Features.Auctions.AuctionHandler.BroadcastSnapshot();
                Features.WantBoard.WantHandler.BroadcastSnapshot();
            }
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
            reply("  diag                         data-pipeline check: live standings counts + on-disk JSON sizes");
            reply("  transport                    KMH API transport status (on by default; chat fallback)");
            reply("  verify                       (admin) dry, read-only integrity scan of every KMH JSON file");
            reply("  audit                        (admin) advisory anti-cheat scan: economy outliers + modified-client signals");
            reply("  backup [reason]              (admin) snapshot KMH-Data into KMH-Data-Backups/");
            reply("  backups                      (admin) list snapshots + how to restore one");
            reply("  restore <name|latest|before:<time>>  (admin) roll KMH-Data back to a backup (applied on restart)");
            reply("  snapshot-player <user> [ts] | snapshot-server [ts] | snapshot-all [ts]  (admin) versioned KMH snapshots");
            reply("  snapshot-verify <folder>     (admin) checksum + JSON-validity check of a snapshot");
            reply("  restore-preview-player <folder>  (admin) read-only: classify what a player restore would change");
            reply("  save                         (admin) force-flush every store to disk now");
            reply("  export                       (admin) write KMH-Data/status.json for external dashboards/monitoring");
            reply("  inspect <subsystem>          (admin) dump live auctions/wants/quests/world/... with ids");
            reply("  cancel <auction|want|quest|marketplace> <id>  (admin) refund + remove a stuck entry");
            reply("  audit-player <user>          (admin) read-only summary of everything KMH knows about a player");
            reply("  reload-marketplace           (admin) re-read Marketplace.json into the LIVE store + re-push (edits ignored until this or a restart)");
            reply("  repush [marketplace|all]     (admin) re-push fresh snapshot(s) to clients (non-destructive)");
            reply("  purge marketplace seller <user> <dry|confirm>  (admin) remove a seller's listings, escrow refunded/recovered; backs up first");
            reply("  purge sites owner <user> <dry|confirm>  (admin) remove a player's sites (client pawns NOT deleted)");
            reply("  wipe-economy <user> <dry|confirm>  (admin) clear treasury/pending/marketplace/auctions/wants/escrow/recovery for a player ('reset-player-economy' = alias)");
            reply("  reset-preview <user>         (admin) read-only: exactly what a save-reset would clear vs keep for a player");
            reply("  sites inspect <user>         (admin) sites a player owns/works + per-worker pawn status (active/legacy/missing)");
            reply("  wipe-player <user> <dry|confirm>   (admin) wipe-economy + guild/sites/standings/reputation/Discord/mail");
            reply("  reset-cleanup <user> <dry|confirm> (admin) rollback/reset-abuse one-shot: backup+audit+wipe-economy+rebuild+verify+repush");
            reply("  remove-player-guilds <user> <dry|confirm> · unlink-player <user> <dry|confirm> · rebuild-player <user>  (admin)");
            reply("  recover list|player <p>|retry <id>|refund <id>|drop <id> <player>  (admin) release value that couldn't reach its owner");
            reply("  validate [configs|items|treasury|sites|recovery|colonists|guilds|all]  (admin) pre-flight configs, pools, colonist + guild health");
            reply("  NOTE: KMH stores live in memory - editing KMH-Data/*.json while running does NOTHING until a restart or 'reload-marketplace'.");
            reply("  NOTE: RWT 'Reset Player' does NOT clear KMH data (Marketplace/Treasury/etc.) - use the wipe/purge commands. 'kmh save' flushes KMH data ONLY, not RWT colonies.");
            reply("  v1.2.0: Marketplace category filtering, guild donate/perks/leave (in Guild Hall), and Sites real-pawn assignment are live. Suspicious colonist-skill flags are review-only (no auto-punish). Transport: 'kmh transport'.");
            reply("  ledger [user] [count]        (admin) recent economy audit-trail entries (disputes)");
            reply("  smoketest                    (admin) non-mutating self-check of every KMH subsystem");
            reply("  transport-test               (admin) security self-check of the API transport DoS guards + config");
            reply("  rebuild-standings            (admin) reload standings from disk + re-push to clients");
            reply("  extensions                   list loaded extensions");
            reply("  give-silver <user> <amount>  (admin) grant silver to a player");
            reply("  treasury-reset <user|all>    (admin) clear personal treasury (anti save-reset farming); backs up first");
            reply("  reload-discord               (admin) re-read Config/Discord/DiscordConfig.json");
            reply("  reload-economy               (admin) re-read Config/Economy.json + Sites.json");
            reply("  reload-world                 (admin) re-read Config/World.json");
            reply("  reload-features              (admin) re-read Config/Features.json + refresh clients");
            reply("  event <type> [minutes] [mag] (admin) fire a world event (see: kmh event help)");
            reply("  worldquest ... | wq ...      (admin) create/list/end a global quest (see: kmh worldquest help)");
            reply("  season roll|reset            (admin) archive leaders + advance; reset also WIPES the economy (backs up first)");
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
            Description   = "KMH server admin: kmh status / diag / extensions / reload-economy / reload-world / event / worldquest / reload-discord / drain-house / give-silver.";
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
