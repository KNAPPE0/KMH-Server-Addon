using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Recovery;
using KMHServerAddon.Features.Recovery.Dto;

namespace KMHServerAddon.AdminCommands
{
    // Undeliverable value is held rather than destroyed, and this is the only thing that ever releases it.
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
                    ListStuckTransactions(reply);
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

        // A different store from the recovery queue, and the boot notice points owners here, so they must be visible.
        private static void ListStuckTransactions(Action<string> reply)
        {
            List<Transactions.KmhTransaction> pending;
            try { pending = Transactions.KmhTransactionRepository.Pending(); }
            catch (Exception ex) { reply($"  (could not read the transaction ledger: {ex.Message})"); return; }
            if (pending == null || pending.Count == 0) return;

            reply($"=== Transactions holding value ({pending.Count}) ===");
            foreach (Transactions.KmhTransaction t in pending)
            {
                string what = string.IsNullOrEmpty(t.Validated) ? t.Requested : t.Validated;
                string esc  = t.EscrowSilver > 0 ? $"{t.EscrowSilver}s" : "";
                if (t.EscrowItems != null && t.EscrowItems.Count > 0) esc += (esc.Length > 0 ? " + " : "") + $"{t.EscrowItems.Count} item kind(s)";
                if (t.EscrowPayloads != null && t.EscrowPayloads.Count > 0) esc += (esc.Length > 0 ? " + " : "") + $"{t.EscrowPayloads.Count} full-state item(s)";
                reply($"  {t.Id}  {t.State}  {t.Player}  {t.Source}->{t.Destination}  {what}" + (esc.Length > 0 ? $"  [holds {esc}]" : ""));
            }
            reply("  A Delivered row means the goods were sent but the player's client has not confirmed receiving them.");
            reply("  These settle themselves: the delivery is re-sent on their next join, and their confirmation closes the row.");
        }
    }
}
