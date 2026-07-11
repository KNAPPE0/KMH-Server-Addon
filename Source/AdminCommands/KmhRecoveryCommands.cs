using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Recovery;
using KMHServerAddon.Features.Recovery.Dto;

namespace KMHServerAddon.AdminCommands
{
    // `kmh recover ...` - release value the economy couldn't deliver to its owner (invalid/deleted account, disbanded
    // guild). Held instead of destroyed; this is where an admin retries/refunds/hands it off.
    internal static class KmhRecoveryCommands
    {
        public static void Run(string[] args, Action<string> reply)
        {
            switch (args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "list")
            {
                case "list":
                {
                    List<RecoveryRecord> held = RecoveryStore.Snapshot(includeResolved: false);
                    reply($"=== Recovery queue ({held.Count} held) ===");
                    if (held.Count == 0) reply("  (empty - no items/silver are stuck)");
                    else { foreach (RecoveryRecord r in held) reply("  " + RecoveryStore.Describe(r));
                           reply("  Resolve: kmh recover retry <id> | refund <id> | drop <id> <player>"); }
                    break;
                }
                case "player":
                {
                    string who = Arg(args, 2);
                    List<RecoveryRecord> rs = RecoveryStore.ForUser(who);
                    reply($"=== Recovery for '{who}' ({rs.Count} held) ===");
                    if (rs.Count == 0) reply("  (none)");
                    else foreach (RecoveryRecord r in rs) reply("  " + RecoveryStore.Describe(r));
                    break;
                }
                case "retry":
                    if (!long.TryParse(Arg(args, 2), out long rid)) { reply("Usage: kmh recover retry <id>"); return; }
                    RecoveryStore.Retry(rid, out string rmsg); reply(rmsg);
                    break;
                case "refund":
                    if (!long.TryParse(Arg(args, 2), out long fid)) { reply("Usage: kmh recover refund <id>"); return; }
                    RecoveryStore.RefundAsSilver(fid, out string fmsg); reply(fmsg);
                    break;
                case "drop":
                    if (!long.TryParse(Arg(args, 2), out long did) || string.IsNullOrEmpty(Arg(args, 3)))
                    { reply("Usage: kmh recover drop <id> <player>"); return; }
                    RecoveryStore.DropToPlayer(did, Arg(args, 3), out string dmsg); reply(dmsg);
                    break;
                default:
                    reply("Usage: kmh recover list | player <p> | retry <id> | refund <id> | drop <id> <player>");
                    break;
            }
        }

        private static string Arg(string[] a, int i) => a != null && a.Length > i ? a[i] : "";
    }
}
