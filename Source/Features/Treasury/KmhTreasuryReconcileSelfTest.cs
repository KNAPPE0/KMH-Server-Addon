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
            int cap = Economy.EconomyConfig.Current.Treasury.MaxUnconfirmedPerPlayer;
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
                r.Add(Diagnostics.KmhSelfTestResult.Skipped("Pending: the backlog gate runs before any goods are removed", "no source tree beside this build"));
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

        // Without ledger scope a reload keeps a fresh deposit it lost and undoes deposits from another colony.
        private static void LedgerScopedRollback(List<(string, bool, string)> r)
        {
            string L = "a1b2c3d4e5f60718293a4b5c6d7e8f90", M = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
            var have = new HashSet<string>(StringComparer.Ordinal) { "C" };
            var unsaved = new HashSet<string>(StringComparer.Ordinal) { "U" };
            r.Add(("Rollback undo (ledger): that colony's commit after the loaded save is undone, with no time grace",
                   TreasuryStore.ShouldUndoInLedger("X", L, 2, L, 1, have, unsaved), ""));
            r.Add(("Rollback undo (ledger): another colony's commit is LEFT ALONE",
                   !TreasuryStore.ShouldUndoInLedger("X", M, 9, L, 1, have, unsaved), ""));
            r.Add(("Rollback undo (ledger): a commit at or before the loaded generation is LEFT ALONE",
                   !TreasuryStore.ShouldUndoInLedger("X", L, 1, L, 1, have, unsaved), ""));
            r.Add(("Rollback undo (ledger): a commit the save still lists, or unsaved goods, is LEFT ALONE",
                   !TreasuryStore.ShouldUndoInLedger("C", L, 2, L, 1, have, unsaved) && !TreasuryStore.ShouldUndoInLedger("U", L, 2, L, 1, have, unsaved), ""));
            r.Add(("Rollback undo (ledger): no ledger or no txn id never acts",
                   !TreasuryStore.ShouldUndoInLedger("X", "", 2, "", 1, have, unsaved) && !TreasuryStore.ShouldUndoInLedger("", L, 2, L, 1, have, unsaved), ""));
            r.Add(("Ledger id: only the client's 32-hex form is taken",
                   TreasuryStore.LedgerIdOrEmpty(L) == L && TreasuryStore.LedgerIdOrEmpty(null) == "" && TreasuryStore.LedgerIdOrEmpty(L.ToUpperInvariant()) == ""
                   && TreasuryStore.LedgerIdOrEmpty(L + "0") == "" && TreasuryStore.LedgerIdOrEmpty("g" + L.Substring(1)) == "", ""));

            string who = "kmh-selftest-ledger-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                bool Deposit(string txn, int silver, string ledger, long gen)
                    => TreasuryStore.BeginPendingDeposit(who, new Dto.PendingDeposit { TxnId = txn, Kind = Dto.PendingDeposit.KindSilver, Silver = silver })
                       && TreasuryStore.ConfirmDeposits(who, new[] { txn }, ledger, gen) == 1;
                string elsewhere = Guid.NewGuid().ToString("N"), older = Guid.NewGuid().ToString("N"), fresh = Guid.NewGuid().ToString("N");
                bool made = Deposit(elsewhere, 40, M, 1) && Deposit(older, 7, L, 1) && Deposit(fresh, 99, L, 2);
                TreasuryStore.NoteSaveGeneration(who, 1, M);
                TreasuryStore.NoteSaveGeneration(who, 2, L);
                long seen = TreasuryStore.NoteSaveGeneration(who, 1, L);
                (int undone, int flagged) = TreasuryStore.ReverseRolledBackCommits(who, new[] { older }, null, 120, L, 1);
                long silver = TreasuryStore.GetPersonalSilver(who);
                r.Add(("Rollback undo (ledger): a reload undoes only that colony's later deposit, however fresh, and nothing elsewhere",
                       made && TreasuryStore.SaveGenerationWentBackwards(seen, 1) && undone == 1 && flagged == 0 && silver == 47,
                       $"made={made} seen={seen} undone={undone} silver={silver} (want 47)"));
                r.Add(("Rollback undo (ledger): a colony never seen before is no rollback",
                       TreasuryStore.NoteSaveGeneration(who, 1, "ffffffffffffffffffffffffffffffff") == 0, ""));
                r.Add(("Rollback undo (ledger): a colony-scoped report leaves the player-wide watermark alone",
                       TreasuryStore.LastSaveGenerationOf(who) == 0, $"player-wide={TreasuryStore.LastSaveGenerationOf(who)}"));
                for (int i = 0; i < TreasuryStore.MaxLedgersPerVault + 8; i++) TreasuryStore.NoteSaveGeneration(who, 100 + i, Guid.NewGuid().ToString("N"));
                r.Add(("Rollback undo (ledger): colonies tracked per player are bounded",
                       TreasuryStore.LedgerCountForTest(who) == TreasuryStore.MaxLedgersPerVault, $"{TreasuryStore.LedgerCountForTest(who)}"));
            }
            finally { TreasuryStore.PurgeOwnerForTest(who); }

            // A colony argument the handler drops silently falls back to the player-wide rule.
            string src = Maintenance.KmhSourceProbe.Read(System.IO.Path.Combine("Features", "Treasury", "TreasuryHandler.cs"));
            if (src == null)
                r.Add(Diagnostics.KmhSelfTestResult.Skipped("Rollback undo (ledger): the handler passes the colony to every step", "no source tree beside this build"));
            else
                r.Add(("Rollback undo (ledger): the handler passes the colony to every step",
                       src.Contains("ConfirmDeposits(username, ids, LedgerOf(env)") && src.Contains("ReconcileDeposits(username, ids, unsaved, ReconcileGraceSeconds, ledger, gen)")
                       && src.Contains("ReverseRolledBackCommits(username, ids, unsaved, ReconcileGraceSeconds, ledger, gen)")
                       && src.Contains("TreasuryStore.NoteSaveGeneration(username, gen, ledger)"), ""));
        }

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            PendingBacklog(r);
            LedgerScopedRollback(r);
            long now    = DateTime.UtcNow.Ticks;
            long cutoff = now - TimeSpan.FromSeconds(120).Ticks;
            long old    = now - TimeSpan.FromSeconds(600).Ticks;
            long young  = now;
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
