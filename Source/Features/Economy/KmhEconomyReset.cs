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
        private const string StepPendingPay   = "pending-payouts";
        private const string StepMail         = "mail";
        private const string StepGuild        = "guild";
        private const string StepIdentity     = "identity";

        private static Dictionary<string, long> _userGeneration
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // A season reset or restore replaces every player's data at once; a floor raises all generations, loaded or not.
        private static long _playerFloor;

        private sealed class State
        {
            public KmhResetOperation Active      { get; set; }
            public long              Generation  { get; set; }
            public long              PlayerFloor { get; set; }
            public Dictionary<string, long> UserGeneration { get; set; }
                = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        // Bumped once per completed reset, so an operation holding a pre-reset value is refused rather than applied.
        public static long Generation { get { lock (_lock) return _generation; } }

        // Per player: a global counter would refuse everyone's in-flight work whenever one player started a new colony.
        public static long GenerationOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            lock (_lock)
            {
                _userGeneration.TryGetValue(username, out long g);
                return Math.Max(g, _playerFloor);
            }
        }

        // Shortened only by the self-test, which holds a move open on purpose.
        internal static TimeSpan QuiesceWait = TimeSpan.FromSeconds(10);

        // Object ids restart at 1 after a season roll, so an action naming an old id must be refused.
        // False when the bump is in memory only: a restart would bring the old generation back.
        public static bool BumpDataGeneration(string reason, bool staleRows = true)
        {
            long now;
            bool saved;
            // The floor moves too: in-flight rows check the PER-PLAYER generation, so bumping only the global
            // counter (which stamps Discord buttons) lets pre-wipe rows settle into vaults the wipe just emptied.
            // Past every player's own generation, or one reset earlier keeps theirs and their old rows still settle.
            lock (_lock)
            {
                _generation++;
                if (staleRows)
                {
                    long top = _playerFloor;
                    foreach (long g in _userGeneration.Values) top = Math.Max(top, g);
                    _playerFloor = top + 1;
                }
                now = _generation;
                saved = SaveLocked();
            }
            ServerLog.Warn($"Data generation is now {now} ({reason}) - actions issued before this are refused.");
            if (!saved) ServerLog.Error($"Data generation {now} could not be written down - a restart would bring the old one back.");
            return saved;
        }

        public static bool InProgress { get { lock (_lock) return _active != null && _active.Phase != KmhResetPhase.Complete; } }

        // What is owed to this user belongs to the save being destroyed: the reset purges it, so nothing may pay it first.
        public static bool Resetting(string user)
        {
            if (string.IsNullOrEmpty(user)) return false;
            lock (_lock)
                return _active != null && _active.Phase != KmhResetPhase.Complete
                    && string.Equals(_active.Username, user, StringComparison.OrdinalIgnoreCase);
        }

        private static bool _applying, _stalled;

        // Without this retry a stalled reset's barrier outlives the disk fault; a Preparing reset belongs to its caller.
        public static bool ResumeStalled()
        {
            KmhResetOperation op;
            lock (_lock)
            {
                op = _active;
                if (op == null || !_stalled || _applying) return false;
                if (op.Phase != KmhResetPhase.Applying && op.Phase != KmhResetPhase.Finalizing) return false;
            }
            ServerLog.Warn($"Economy reset: retrying the unfinished reset for '{op.Username}'.");
            HoldBarrier();
            Apply(op);
            return true;
        }

        internal static bool StalledForTest { get { lock (_lock) return _stalled; } }

        // Nothing marks a Preparing reset stalled; this lets the phase guard be tested anyway.
        internal static void MarkStalledForTest() { lock (_lock) _stalled = true; }

        internal static List<string> DoneStepsForTest()
        {
            lock (_lock) return _active?.DoneSteps != null ? new List<string>(_active.DoneSteps) : new List<string>();
        }

        public static string ActiveUser { get { lock (_lock) return _active?.Username ?? ""; } }

        public static KmhResetPhase ActivePhase { get { lock (_lock) return _active?.Phase ?? KmhResetPhase.None; } }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.EconomyResetFile, out State s) || s == null) return;
            lock (_lock)
            {
                _generation  = Math.Max(0, s.Generation);
                _playerFloor = Math.Max(0, s.PlayerFloor);
                if (s.UserGeneration != null)
                    _userGeneration = new Dictionary<string, long>(s.UserGeneration, StringComparer.OrdinalIgnoreCase);
                _active = s.Active != null && s.Active.Phase != KmhResetPhase.Complete ? s.Active : null;
            }
        }

        private static bool SaveLocked()
        {
            State s = new State { Active = _active, Generation = _generation, PlayerFloor = _playerFloor };
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

        // For a caller that did the destruction itself; it opened the operation first, so RecoverOnBoot finishes a crash.
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
        // saved names the stores the work changed; a purge that is only in memory is not a step done.
        private static bool Step(KmhResetOperation op, string step, Action work, params Func<bool>[] saved)
        {
            if (Done(op, step)) return true;
            work();
            foreach (Func<bool> save in saved)
                if (!save())
                {
                    lock (_lock) _stalled = true;
                    ServerLog.Error($"Economy reset: step '{step}' for '{op.Username}' could not be written down - the reset "
                                  + "stays open and is retried automatically.");
                    return false;
                }
            lock (_lock)
            {
                (op.DoneSteps ?? (op.DoneSteps = new List<string>())).Add(step);
                if (SaveLocked()) return true;
                op.DoneSteps.Remove(step);
                _stalled = true;
            }
            ServerLog.Error($"Economy reset: could not record step '{step}' for '{op.Username}' - the reset stays open " +
                            "and is retried automatically.");
            return false;
        }

        private static void Advance(KmhResetOperation op, KmhResetPhase phase)
        {
            lock (_lock) { op.Phase = phase; SaveLocked(); }
        }

        private static void Apply(KmhResetOperation op)
        {
            string user = op.Username;
            lock (_lock)
            {
                if (_applying) return;   // the sweeper's retry and the caller's own run must not purge side by side
                _applying = true;
                _stalled = false;
            }
            try
            {
                if (op.Phase == KmhResetPhase.Preparing) Advance(op, KmhResetPhase.Applying);
                // The barrier stops new moves; one admitted before it closed would write into the stores this empties.
                // Nothing is purged until they finish, and the stalled reset is retried with the barrier still held.
                if (!Maintenance.KmhValueOps.WaitForIdle(QuiesceWait))
                {
                    lock (_lock) _stalled = true;
                    ServerLog.Warn($"Economy reset for '{user}': value moves were still running after {QuiesceWait.TotalSeconds:0.#}s - "
                                 + "nothing was purged yet, and the reset is retried automatically.");
                    return;
                }

                // Aborted, not compensated: a refund would hand back exactly the value the reset is destroying on purpose.
                // A trade with someone else is ended first, and one that cannot end yet holds the reset open.
                if (!Step(op, StepTransactions, () => Transactions.KmhTransactionRepository.AbortAllFor(user),
                          Transactions.KmhTransactionRepository.SaveToDisk,
                          () => Transactions.KmhTransactionRepository.PendingCountFor(user) == 0)) return;
                if (!Step(op, StepTreasury,     () => Treasury.TreasuryStore.ResetPersonal(user),
                          Treasury.TreasuryStore.SaveToDisk)) return;
                if (!Step(op, StepGuildVault,   () =>
                {
                    string solo = Guilds.GuildStore.SoloGuildOf(user);
                    if (solo != null) Treasury.TreasuryStore.ClearGuildVault(solo);
                }, Treasury.TreasuryStore.SaveToDisk)) return;
                if (!Step(op, StepMarketplace,  () => Marketplace.MarketplaceStore.PurgeSeller(user),
                          Marketplace.MarketplaceStore.SaveToDisk)) return;
                if (!Step(op, StepAuctions,     () => Auctions.AuctionStore.PurgeUser(user),
                          Auctions.AuctionStore.SaveToDisk)) return;
                if (!Step(op, StepWants,        () => WantBoard.WantStore.PurgeBuyer(user),
                          WantBoard.WantStore.SaveToDisk)) return;
                if (!Step(op, StepEscrow,       () => KmhEscrowPurge.BurnAll(user),
                          Quests.QuestStore.SaveToDisk, Mail.MailStore.SaveToDisk, Roadworks.RoadworksStore.SaveToDisk)) return;
                if (!Step(op, StepSites,        () =>
                {
                    Sites.SiteStore.PurgeOwner(user, dryRun: false);
                    Sites.SiteStore.RemoveWorkerEverywhere(user);
                }, Sites.SiteStore.SaveToDisk)) return;
                if (!Step(op, StepRecovery,     () => Recovery.RecoveryStore.ClearUser(user),
                          Recovery.RecoveryStore.SaveToDisk)) return;
                // An unacked delivery would hand the destroyed value straight back on the next join.
                if (!Step(op, StepDeliveries,   () => Delivery.DeliveryStore.PurgeUser(user),
                          Delivery.DeliveryStore.SaveToDisk)) return;
                // Written down but not yet paid, and paid at the next boot into the new save's treasury.
                if (!Step(op, StepPendingPay,   () =>
                {
                    Mail.MailStore.DropPendingPayoutsFor(user);
                    World.WorldStore.DropPendingPayoutsFor(user);
                    Marketplace.MarketplaceStore.DropPendingReservesFor(user);
                    WantBoard.WantStore.DropPendingRefundsFor(user);
                    Auctions.AuctionStore.DropPendingRefundsFor(user);
                    Quests.QuestStore.DropOwedFor(user);
                    Sites.SiteStore.DropOwedFor(user);
                    Roadworks.RoadworksStore.DropOwedFor(user);
                }, Mail.MailStore.SaveToDisk, World.WorldStore.SaveToDisk, Marketplace.MarketplaceStore.SaveToDisk,
                   WantBoard.WantStore.SaveToDisk, Auctions.AuctionStore.SaveToDisk, Quests.QuestStore.SaveToDisk,
                   Sites.SiteStore.SaveToDisk, Roadworks.RoadworksStore.SaveToDisk)) return;

                if (op.Scope == KmhResetScope.Player)
                {
                    if (!Step(op, StepMail,     () => Mail.MailStore.PurgeUser(user), Mail.MailStore.SaveToDisk)) return;
                    if (!Step(op, StepGuild,    () => Guilds.GuildStore.LeaveForReset(user, out _, out _), Guilds.GuildStore.SaveToDisk,
                              () => string.IsNullOrEmpty(Guilds.GuildStore.CurrentGuildOf(user)))) return;
                    if (!Step(op, StepIdentity, () =>
                    {
                        PlayerStats.PlayerStatsStore.RemoveUser(user);
                        Reputation.ReputationStore.RemoveUser(user);
                        Notifications.NotificationStore.ClearUser(user);
                        LinkedAccounts.LinkedAccountsStore.Unlink(user);
                    }, Notifications.NotificationStore.SaveToDisk, LinkedAccounts.LinkedAccountsStore.SaveToDisk)) return;
                }

                Advance(op, KmhResetPhase.Finalizing);

                // The save id advances only here, so until then a restart resumes this reset rather than skipping it.
                // Unwritten, a restart would read the old id and wipe the new save's progress a second time.
                bool idSaved = op.Scope == KmhResetScope.Player ? EconomyResetStore.Forget(user)
                                                                : EconomyResetStore.ConfirmReset(user, op.SaveId);
                bool closed = false;
                if (idSaved)
                    lock (_lock)
                    {
                        long was = _generation;
                        bool had = _userGeneration.TryGetValue(user, out long mine);
                        _generation = Math.Max(_generation, op.Generation);
                        _userGeneration[user] = Math.Max(mine, _playerFloor) + 1;
                        op.Phase = KmhResetPhase.Complete;
                        closed = SaveLocked();
                        if (closed) _active = null;
                        else
                        {
                            _generation = was;
                            if (had) _userGeneration[user] = mine; else _userGeneration.Remove(user);
                            op.Phase = KmhResetPhase.Finalizing;
                        }
                    }
                if (!closed)
                {
                    lock (_lock) _stalled = true;
                    ServerLog.Error($"Economy reset for '{user}': every step is done but the close could not be written down - "
                                  + "the economy stays paused and the close is retried automatically.");
                    return;
                }
                ServerLog.Warn($"Economy reset complete for '{user}' - their data generation is now {GenerationOf(user)}.");
            }
            finally
            {
                lock (_lock) _applying = false;
                if (!InProgress) ReleaseBarrier();
            }
        }

        internal static void ResetForTest()
        {
            lock (_lock) { _active = null; _generation = 0; _playerFloor = 0; _userGeneration.Clear(); _applying = false; _stalled = false; }
            ReleaseBarrier();
        }
    }
}
