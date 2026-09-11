using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Economy
{
    internal enum KmhResetPhase { None = 0, Preparing = 1, Applying = 2, Finalizing = 3, Complete = 4 }

    // Economy is the new-save reset; Player is an admin wipe, adding the identity and history a new save deliberately keeps.
    internal enum KmhResetScope { Economy = 0, Player = 1 }

    // Written down before anything is destroyed, so a crash resumes the same reset rather than stranding half-cleared stores.
    internal sealed class KmhResetOperation
    {
        public string        Username    { get; set; } = "";
        public string        SaveId      { get; set; } = "";
        public KmhResetScope Scope       { get; set; } = KmhResetScope.Economy;
        public KmhResetPhase Phase       { get; set; } = KmhResetPhase.None;
        public long          StartedUtc  { get; set; }
        public long          Generation  { get; set; }
        public List<string>  DoneSteps   { get; set; } = new List<string>();
    }

    internal static class KmhEconomyReset
    {
        private static readonly object _lock = new object();
        private static KmhResetOperation _active;
        private static long _generation;
        private static bool _gateHeld;

        // Each step is a whole-store purge, so re-running one is harmless; the checkpoints only skip work after a restart.
        private const string StepTransactions = "transactions";
        private const string StepTreasury     = "treasury";
        private const string StepGuildVault   = "solo-guild";
        private const string StepMarketplace  = "marketplace";
        private const string StepAuctions     = "auctions";
        private const string StepWants        = "wants";
        private const string StepEscrow       = "escrow";
        private const string StepSites        = "sites";
        private const string StepRecovery     = "recovery";
        private const string StepDeliveries   = "deliveries";
        private const string StepMail         = "mail";
        private const string StepGuild        = "guild";
        private const string StepIdentity     = "identity";

        private static Dictionary<string, long> _userGeneration
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private sealed class State
        {
            public KmhResetOperation Active     { get; set; }
            public long              Generation { get; set; }
            public Dictionary<string, long> UserGeneration { get; set; }
                = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        // Bumped once per completed reset, so an operation holding a pre-reset value is refused rather than applied.
        public static long Generation { get { lock (_lock) return _generation; } }

        // Per player: a global counter would refuse everyone else's in-flight work every time one person started a new colony.
        public static long GenerationOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            lock (_lock) { _userGeneration.TryGetValue(username, out long g); return g; }
        }

        // Object ids restart at 1 after a season roll, so an action naming an old id must be refused, not applied to whatever holds that number now.
        public static void BumpDataGeneration(string reason)
        {
            long now;
            lock (_lock) { _generation++; now = _generation; SaveLocked(); }
            ServerLog.Warn($"Data generation is now {now} ({reason}) - actions issued before this are refused.");
        }

        public static bool InProgress { get { lock (_lock) return _active != null && _active.Phase != KmhResetPhase.Complete; } }

        public static string ActiveUser { get { lock (_lock) return _active?.Username ?? ""; } }

        public static KmhResetPhase ActivePhase { get { lock (_lock) return _active?.Phase ?? KmhResetPhase.None; } }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.EconomyResetFile, out State s) || s == null) return;
            lock (_lock)
            {
                _generation = Math.Max(0, s.Generation);
                if (s.UserGeneration != null)
                    _userGeneration = new Dictionary<string, long>(s.UserGeneration, StringComparer.OrdinalIgnoreCase);
                _active = s.Active != null && s.Active.Phase != KmhResetPhase.Complete ? s.Active : null;
            }
        }

        private static bool SaveLocked()
        {
            State s = new State { Active = _active, Generation = _generation };
            foreach (KeyValuePair<string, long> kv in _userGeneration) s.UserGeneration[kv.Key] = kv.Value;
            return JsonFileStore.Save(KmhDataPaths.EconomyResetFile, s);
        }

        // An unfinished reset holds the barrier from the moment boot notices it, before any store is reachable.
        public static void RecoverOnBoot()
        {
            KmhResetOperation op;
            lock (_lock) op = _active;
            if (op == null || op.Phase == KmhResetPhase.Complete) return;
            ServerLog.Warn($"Economy reset: resuming an unfinished reset for '{op.Username}' (phase {op.Phase}, " +
                           $"{op.DoneSteps?.Count ?? 0} step(s) already applied).");
            HoldBarrier();
            Apply(op);
        }

        // False means nothing was written down, so nothing is destroyed - the caller must not start purging.
        public static bool Begin(string username, string saveId, out string reason)
            => Begin(username, saveId, KmhResetScope.Economy, out reason);

        public static bool Begin(string username, string saveId, KmhResetScope scope, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(username)) { reason = "no username"; return false; }
            lock (_lock)
            {
                if (_active != null && _active.Phase != KmhResetPhase.Complete)
                { reason = $"a reset for '{_active.Username}' is already running"; return false; }
                _active = new KmhResetOperation
                {
                    Username   = username,
                    SaveId     = saveId ?? "",
                    Scope      = scope,
                    Phase      = KmhResetPhase.Preparing,
                    StartedUtc = DateTime.UtcNow.Ticks,
                    Generation = _generation + 1,
                };
                if (!SaveLocked()) { _active = null; reason = "the reset could not be written down"; return false; }
            }
            HoldBarrier();
            return true;
        }

        // For a caller that did the destruction itself; it opened the operation first, so a crash before this leaves RecoverOnBoot to finish.
        public static void Finish(string username)
        {
            KmhResetOperation op;
            lock (_lock) op = _active;
            if (op == null || !string.Equals(op.Username, username, StringComparison.OrdinalIgnoreCase)) return;
            lock (_lock) { op.Phase = KmhResetPhase.Applying; SaveLocked(); }
            Apply(op);
        }

        public static void Run(string username, string saveId)
        {
            KmhResetOperation op;
            lock (_lock) op = _active;
            if (op == null || !string.Equals(op.Username, username, StringComparison.OrdinalIgnoreCase)) return;
            Apply(op);
        }

        private static void HoldBarrier()
        {
            lock (_lock)
            {
                if (_gateHeld) return;
                _gateHeld = true;
            }
            Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.DataRepair);
        }

        private static void ReleaseBarrier()
        {
            lock (_lock)
            {
                if (!_gateHeld) return;
                _gateHeld = false;
            }
            Maintenance.KmhMaintenanceGate.Release();
        }

        private static bool Done(KmhResetOperation op, string step) => op.DoneSteps != null && op.DoneSteps.Contains(step);

        // The work commits before the checkpoint: recording it first would let a restart skip a purge that never ran.
        private static bool Step(KmhResetOperation op, string step, Action work)
        {
            if (Done(op, step)) return true;
            work();
            lock (_lock)
            {
                (op.DoneSteps ?? (op.DoneSteps = new List<string>())).Add(step);
                if (SaveLocked()) return true;
                op.DoneSteps.Remove(step);
            }
            ServerLog.Error($"Economy reset: could not record step '{step}' for '{op.Username}' - the reset stays open " +
                            "and resumes on the next attempt.");
            return false;
        }

        private static void Advance(KmhResetOperation op, KmhResetPhase phase)
        {
            lock (_lock) { op.Phase = phase; SaveLocked(); }
        }

        private static void Apply(KmhResetOperation op)
        {
            string user = op.Username;
            try
            {
                if (op.Phase == KmhResetPhase.Preparing) Advance(op, KmhResetPhase.Applying);

                // Aborted, not compensated: a refund would hand back exactly the value the reset is destroying on purpose.
                if (!Step(op, StepTransactions, () => Transactions.KmhTransactionRepository.AbortAllFor(user))) return;
                if (!Step(op, StepTreasury,     () => Treasury.TreasuryStore.ResetPersonal(user))) return;
                if (!Step(op, StepGuildVault,   () =>
                {
                    string solo = Guilds.GuildStore.SoloGuildOf(user);
                    if (solo != null) Treasury.TreasuryStore.ClearGuildVault(solo);
                })) return;
                if (!Step(op, StepMarketplace,  () => Marketplace.MarketplaceStore.PurgeSeller(user))) return;
                if (!Step(op, StepAuctions,     () => Auctions.AuctionStore.PurgeUser(user))) return;
                if (!Step(op, StepWants,        () => WantBoard.WantStore.PurgeBuyer(user))) return;
                if (!Step(op, StepEscrow,       () => KmhEscrowPurge.BurnAll(user))) return;
                if (!Step(op, StepSites,        () =>
                {
                    Sites.SiteStore.PurgeOwner(user, dryRun: false);
                    Sites.SiteStore.RemoveWorkerEverywhere(user);
                })) return;
                if (!Step(op, StepRecovery,     () => Recovery.RecoveryStore.ClearUser(user))) return;
                // An unacked delivery would hand the destroyed value straight back on the next join.
                if (!Step(op, StepDeliveries,   () => Delivery.DeliveryStore.PurgeUser(user))) return;

                if (op.Scope == KmhResetScope.Player)
                {
                    if (!Step(op, StepMail,     () => Mail.MailStore.PurgeUser(user))) return;
                    if (!Step(op, StepGuild,    () => Guilds.GuildStore.Leave(user, out _))) return;
                    if (!Step(op, StepIdentity, () =>
                    {
                        PlayerStats.PlayerStatsStore.RemoveUser(user);
                        Reputation.ReputationStore.RemoveUser(user);
                        Notifications.NotificationStore.ClearUser(user);
                        LinkedAccounts.LinkedAccountsStore.Unlink(user);
                    })) return;
                }

                Advance(op, KmhResetPhase.Finalizing);

                // The save id advances only here, so until it does a restart resumes this reset instead of calling the new save handled.
                if (op.Scope == KmhResetScope.Player) EconomyResetStore.Forget(user);
                else                                  EconomyResetStore.ConfirmReset(user, op.SaveId);
                lock (_lock)
                {
                    _generation = Math.Max(_generation, op.Generation);
                    _userGeneration.TryGetValue(user, out long mine);
                    _userGeneration[user] = mine + 1;
                    op.Phase = KmhResetPhase.Complete;
                    SaveLocked();
                    _active = null;
                }
                ServerLog.Warn($"Economy reset complete for '{user}' - their data generation is now {GenerationOf(user)}.");
            }
            finally
            {
                if (!InProgress) ReleaseBarrier();
            }
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _active = null; _generation = 0; _userGeneration.Clear(); }
            ReleaseBarrier();
        }
    }
}
