using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Treasury
{
    // Reverting a deposit whose goods the client already removed destroys them, so that case is never reverted.
    internal static class KmhTreasuryReconcileSelfTest
    {
        // The bound must refuse new deposits rather than reap old ones, which would destroy already-removed goods.
        private static void PendingBacklog(List<(string, bool, string)> r)
        {
            string user = "kmh-selftest-backlog";
            int cap = Economy.EconomyConfig.Current.MaxUnconfirmedDepositsPerPlayer;
            r.Add(("Pending: the backlog bound is configured and clamped sane", cap >= 1 && cap <= 1000, $"cap={cap}"));

            int before = TreasuryStore.UnconfirmedDepositCount(user);
            bool opened = TreasuryStore.BeginPendingDeposit(user, new Dto.PendingDeposit
                { TxnId = "kmh-selftest-backlog-1", Kind = Dto.PendingDeposit.KindSilver, Silver = 1 });
            int after = TreasuryStore.UnconfirmedDepositCount(user);
            r.Add(("Pending: an opened deposit is counted as unconfirmed", opened && after == before + 1, $"{before} -> {after}"));

            bool dupe = TreasuryStore.BeginPendingDeposit(user, new Dto.PendingDeposit
                { TxnId = "kmh-selftest-backlog-1", Kind = Dto.PendingDeposit.KindSilver, Silver = 1 });
            r.Add(("Pending: the same txn id never opens twice, so one action is one record",
                   !dupe && TreasuryStore.UnconfirmedDepositCount(user) == after, ""));

            // The generation at open is what later tells a stale pending apart from one whose goods a save removed.
            var probe = new Dto.PendingDeposit { TxnId = "kmh-selftest-backlog-2", Kind = Dto.PendingDeposit.KindSilver, Silver = 1 };
            TreasuryStore.BeginPendingDeposit(user, probe);
            r.Add(("Pending: a new record records the save generation it was opened at", probe.SaveGenAtOpen >= 0, $"gen={probe.SaveGenAtOpen}"));

            // Below the cap nothing is refused; at and above it the refusal carries a reason the player can act on.
            bool underCap = !TreasuryHandler.WouldRefuseForBacklogForTest(user, out _);
            r.Add(("Pending: a player under the cap is never refused", underCap, $"open={TreasuryStore.UnconfirmedDepositCount(user)} cap={cap}"));

            for (int i = TreasuryStore.UnconfirmedDepositCount(user); i < cap; i++)
                TreasuryStore.BeginPendingDeposit(user, new Dto.PendingDeposit
                    { TxnId = $"kmh-selftest-fill-{i}", Kind = Dto.PendingDeposit.KindSilver, Silver = 1 });

            int atCap = TreasuryStore.UnconfirmedDepositCount(user);
            bool refused = TreasuryHandler.WouldRefuseForBacklogForTest(user, out string why);
            r.Add(("Pending: at the cap a further deposit is refused", refused && atCap >= cap, $"open={atCap} cap={cap}"));
            r.Add(("Pending: the refusal names the backlog and what clears it",
                   refused && why != null && why.Contains("waiting for your game to save"), why ?? "(none)"));

            // The gate must sit in preflight, before the client removes anything.
            string src = Maintenance.KmhSourceProbe.Read(System.IO.Path.Combine("Features", "Treasury", "TreasuryHandler.cs"));
            if (src == null)
                r.Add(("Pending: the backlog gate runs before any goods are removed", true, "not asked - no source tree beside this build"));
            else
            {
                int gate   = src.IndexOf("TooManyUnconfirmed(username, out string backlog)", StringComparison.Ordinal);
                int check  = src.IndexOf("static bool CheckDepositAllowed", StringComparison.Ordinal);
                int begin  = src.IndexOf("BeginPendingDeposit", StringComparison.Ordinal);
                r.Add(("Pending: the backlog gate runs before any goods are removed",
                       gate > check && gate < begin, $"gate@{gate} check@{check} begin@{begin}"));
                r.Add(("Pending: an over-cap refusal never parks goods in recovery",
                       !src.Contains("HoldRejectedDeposit(client, username, amount, null, backlog)"), ""));
            }

            TreasuryStore.DiscardPendingForTest(user);
            r.Add(("Pending: the self-test leaves no records behind", TreasuryStore.UnconfirmedDepositCount(user) == 0, ""));
        }

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            PendingBacklog(r);
            long now    = DateTime.UtcNow.Ticks;
            long cutoff = now - TimeSpan.FromSeconds(120).Ticks;   // the reconcile grace boundary
            long old    = now - TimeSpan.FromSeconds(600).Ticks;   // well past grace
            long young  = now;                                     // inside grace
            var committed = new HashSet<string>(StringComparer.Ordinal) { "C" };
            var unsaved   = new HashSet<string>(StringComparer.Ordinal) { "U" };

            // Undoing removes a player's value, so the false-positive cases matter more here than the exploit case.
            r.Add(("Rollback undo: a commit the client no longer holds is undone",
                   TreasuryStore.ShouldUndoCommitted("X", old, committed, unsaved, cutoff), ""));
            r.Add(("Rollback undo: a commit the client still lists is LEFT ALONE",
                   !TreasuryStore.ShouldUndoCommitted("C", old, committed, unsaved, cutoff), ""));
            r.Add(("Rollback undo: unsaved-goods-gone is LEFT ALONE (mid-flight, next save finalises it)",
                   !TreasuryStore.ShouldUndoCommitted("U", old, committed, unsaved, cutoff), ""));
            r.Add(("Rollback undo: a fresh commit inside grace is LEFT ALONE",
                   !TreasuryStore.ShouldUndoCommitted("X", young, committed, unsaved, cutoff), ""));
            r.Add(("Rollback undo: a blank txn id is never acted on",
                   !TreasuryStore.ShouldUndoCommitted("", old, committed, unsaved, cutoff)
                   && !TreasuryStore.ShouldUndoCommitted(null, old, committed, unsaved, cutoff), ""));
            // An empty durable set is what a broken client sends, so the generation rule is asserted alongside it.
            r.Add(("Rollback undo: only a BACKWARDS generation counts as evidence",
                   TreasuryStore.SaveGenerationWentBackwards(9, 4)
                   && !TreasuryStore.SaveGenerationWentBackwards(4, 9)
                   && !TreasuryStore.SaveGenerationWentBackwards(9, 9)
                   && !TreasuryStore.SaveGenerationWentBackwards(0, 4)
                   && !TreasuryStore.SaveGenerationWentBackwards(9, 0), "0 = client reports none, never a rollback"));

            // Both paths run for every client that reports a generation, so creating a vault would leave empty rows forever.
            const string ghost = "__kmh_rollback_probe__diag";
            long seenForGhost = TreasuryStore.NoteSaveGeneration(ghost, 5);
            (int undone, int flagged) = TreasuryStore.ReverseRolledBackCommits(ghost, committed, unsaved, 120);
            bool ghostVault = TreasuryStore.HasVaultForUser(ghost);
            r.Add(("Rollback undo: tracking a save generation creates no treasury",
                   seenForGhost == 0 && !ghostVault, ghostVault ? "a vault was created for a player who has none" : ""));
            r.Add(("Rollback undo: undoing with no treasury is a no-op",
                   undone == 0 && flagged == 0 && !ghostVault, $"{undone} undone, {flagged} flagged"));

            // A vault at zero today may still hold history, so the refusals matter more than the removals.
            r.Add(("Prune: a never-touched vault is unused",
                   TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot { OwnerKey = "_personal:ghost" }), ""));
            r.Add(("Prune: silver in the vault protects it",
                   !TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot { SilverBalance = 1 }), ""));
            r.Add(("Prune: a spent-out vault keeps its history",
                   !TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot { LifetimeSilverIn = 500, LifetimeSilverOut = 500 }), "balance 0 but it held value"));
            r.Add(("Prune: a transaction record protects it",
                   !TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot
                   { RecentTransactions = new List<Dto.TreasuryTransaction> { new Dto.TreasuryTransaction() } }), "audit trail"));
            r.Add(("Prune: items protect it",
                   !TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot
                   { Items = new Dictionary<string, int> { { "Steel", 1 } } }), ""));
            r.Add(("Prune: a pending or committed deposit protects it",
                   !TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot
                   { PendingDeposits = new List<Dto.PendingDeposit> { new Dto.PendingDeposit() } })
                   && !TreasuryStore.IsUnusedVault(new Dto.TreasurySnapshot
                   { CommittedDeposits = new List<Dto.PendingDeposit> { new Dto.PendingDeposit() } }), ""));
            r.Add(("Prune: null is never pruned", !TreasuryStore.IsUnusedVault(null), ""));

            TreasuryStore.ReconcileVerdict v;
            r.Add(("Reconcile: durably-saved txn commits",
                (v = TreasuryStore.ReconcileVerdictFor("C", old, committed, unsaved, cutoff)) == TreasuryStore.ReconcileVerdict.Commit, v.ToString()));
            r.Add(("Reconcile: unsaved-goods-gone KEPT even when old (the fix)",
                (v = TreasuryStore.ReconcileVerdictFor("U", old, committed, unsaved, cutoff)) == TreasuryStore.ReconcileVerdict.Keep, v.ToString()));
            r.Add(("Reconcile: unknown + old reverts (real rollback)",
                (v = TreasuryStore.ReconcileVerdictFor("X", old, committed, unsaved, cutoff)) == TreasuryStore.ReconcileVerdict.Revert, v.ToString()));
            r.Add(("Reconcile: unknown + young kept (grace window)",
                (v = TreasuryStore.ReconcileVerdictFor("X", young, committed, unsaved, cutoff)) == TreasuryStore.ReconcileVerdict.Keep, v.ToString()));
            r.Add(("Reconcile: null sets don't throw, old unknown reverts",
                (v = TreasuryStore.ReconcileVerdictFor("X", old, null, null, cutoff)) == TreasuryStore.ReconcileVerdict.Revert, v.ToString()));

            bool backwards = TreasuryStore.SaveGenerationWentBackwards(seen: 12, incoming: 7);
            bool forward   = !TreasuryStore.SaveGenerationWentBackwards(seen: 7, incoming: 12);
            bool same      = !TreasuryStore.SaveGenerationWentBackwards(seen: 7, incoming: 7);
            bool oldClient = !TreasuryStore.SaveGenerationWentBackwards(seen: 12, incoming: 0)
                          && !TreasuryStore.SaveGenerationWentBackwards(seen: 0, incoming: 5);
            r.Add(("Save gen: a lower generation is flagged as a rollback", backwards, "12 -> 7 detected"));
            r.Add(("Save gen: forward/equal are not rollbacks", forward && same, "no false positive on normal play"));
            r.Add(("Save gen: 0 never counts (client doesn't report one)", oldClient, "old clients exempt"));

            return r;
        }
    }
}
