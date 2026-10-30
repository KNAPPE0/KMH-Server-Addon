using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Written before anything is destroyed, so boot finishes a crashed wipe instead of leaving half a reset.
    internal sealed class KmhSeasonResetJournal
    {
        [Newtonsoft.Json.JsonProperty("backup")]            public string Backup           { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("actor")]             public string Actor            { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("started")]           public string StartedUtc       { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("generation_bumped")] public bool   GenerationBumped { get; set; }
        [Newtonsoft.Json.JsonProperty("season_rolled")]     public bool   SeasonRolled     { get; set; }
        [Newtonsoft.Json.JsonProperty("rolled_season")]     public int    RolledSeason     { get; set; }
        [Newtonsoft.Json.JsonProperty("record_count")]      public int    RecordCount      { get; set; }
        [Newtonsoft.Json.JsonProperty("completed")]         public bool   Completed        { get; set; }
    }

    // Backs up first, so a season reset stays reversible through 'kmh restore'.
    internal static class KmhSeasonReset
    {
        private static int _running;

        // Returning false stops before the backup and wipe, so a test can hold the latch without destroying anything.
        internal static Func<bool> BeforeWipeForTest;

        // Stand-ins for the destructive steps, so the crash and resume path can be driven without emptying a server.
        internal static Func<(int Rolled, int Records)> RollForTest;
        internal static Func<IEnumerable<Action>> ClearersForTest;
        internal static Func<int> ClearVaultsForTest;
        internal static Action AfterRollForTest;

        private static string JournalFile => Persistence.KmhDataPaths.SeasonResetJournalFile;

        internal static KmhSeasonResetJournal PendingJournal()
            => Persistence.JsonFileStore.TryLoad(JournalFile, out KmhSeasonResetJournal j) ? j : null;

        private static bool SaveJournal(KmhSeasonResetJournal j)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(JournalFile);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            }
            catch { }
            return Persistence.JsonFileStore.Save(JournalFile, j);
        }

        private static void DeleteJournal()
        {
            try { if (System.IO.File.Exists(JournalFile)) System.IO.File.Delete(JournalFile); }
            catch (Exception ex) { ServerLog.Warn($"Season reset: its finished journal could not be removed ({ex.Message}); it is marked complete, so nothing re-runs."); }
        }

        // An open journal means half-wiped stores: the economy stays paused until the reset finishes, or players would
        // build on data the next pass wipes again. One held gate at most, released only by a finished reset.
        private static readonly object _heldLock = new object();
        private static bool _held;

        internal static bool HeldForTest { get { lock (_heldLock) return _held; } }
        internal static void ReleaseHeldForTest() { lock (_heldLock) if (_held) { _held = false; KmhMaintenanceGate.Release(); } }

        private static void KeepOrRelease(bool keepPaused)
        {
            lock (_heldLock)
            {
                if (keepPaused && !_held) { _held = true; return; }
                KmhMaintenanceGate.Release();
                if (!keepPaused && _held) { _held = false; KmhMaintenanceGate.Release(); }
            }
        }

        private static bool OpenJournal(out KmhSeasonResetJournal j)
        {
            j = PendingJournal();
            if (j != null && j.Completed) { DeleteJournal(); j = null; }
            return j != null;
        }

        // Runs after the stores load and before any other recovery, holding the barrier the reset would have held.
        public static void RecoverOnBoot()
        {
            if (!OpenJournal(out KmhSeasonResetJournal j)) return;
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                ServerLog.Warn($"Season reset: finishing the reset {j.Actor} started at {j.StartedUtc} "
                             + $"(restore point {j.Backup}) - a crash stopped it part way.");
                KmhMaintenanceGate.Enter(KmhMaintenanceReason.OwnerMaintenance);
                bool done = false;
                try { done = Wipe(j, out string summary); ServerLog.Warn($"Season reset: {summary}"); }
                finally { KeepOrRelease(!done); }
            }
            finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
        }

        // The sweeper's retry for a reset left open by a failed write, so the pause ends once the disk recovers.
        public static bool ResumePending()
        {
            lock (_heldLock) if (!_held) return false;
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
            try
            {
                bool done = !OpenJournal(out KmhSeasonResetJournal j) || Wipe(j, out _);
                if (done) lock (_heldLock) if (_held) { _held = false; KmhMaintenanceGate.Release(); }
                return done;
            }
            finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
        }

        public static bool Run(string actorName, out string summary)
        {
            summary = "";

            // Two concurrent confirmations would take two backups and roll the season twice; the second is refused.
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            { summary = "aborted - a season reset is already running"; return false; }
            try
            {
                // A half-applied per-player reset would resume its checkpoints against data this wipe is about to empty.
                if (Features.Economy.KmhEconomyReset.InProgress)
                { summary = $"aborted - a reset for '{Features.Economy.KmhEconomyReset.ActiveUser}' is still running"; return false; }

                // Held across the whole destructive window, or purchases, settlements and refunds hit emptying stores.
                KmhMaintenanceGate.Enter(KmhMaintenanceReason.OwnerMaintenance);
                bool done = false;
                try
                {
                    if (BeforeWipeForTest != null && !BeforeWipeForTest())
                    { summary = "aborted - selftest"; return false; }
                    // The gate stops new moves; one already running (request, job, Discord purchase) would write into the wipe.
                    if (!KmhValueOps.WaitForIdle(TimeSpan.FromSeconds(10)))
                    { summary = "aborted - value moves were still running after 10s; nothing was wiped, try again"; return false; }
                    done = RunLocked(actorName, out summary);
                    return done;
                }
                finally { KeepOrRelease(!done && OpenJournal(out _)); }
            }
            finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
        }

        private static bool RunLocked(string actorName, out string summary)
        {
            summary = "";

            // A new journal would overwrite an unfinished one, and a second backup would capture its half-wiped stores.
            if (OpenJournal(out KmhSeasonResetJournal open)) return Wipe(open, out summary);

            // Safety net first - never wipe without a backup.
            if (!Persistence.KmhDataBackup.TryCreate("pre-season-reset", out string dir, out string err))
            { summary = $"aborted - backup failed: {err}"; return false; }
            string backup = System.IO.Path.GetFileName(dir);
            // Named before the wipe, because a partial failure never produces the summary that would name it.
            ServerLog.Warn($"Season reset: restore point is backup {backup} (restore with: kmh restore {backup}).");

            var journal = new KmhSeasonResetJournal { Backup = backup, Actor = actorName ?? "", StartedUtc = DateTime.UtcNow.ToString("o") };
            if (!SaveJournal(journal))
            { summary = "aborted - the reset could not be written down before starting; nothing was wiped"; return false; }
            return Wipe(journal, out summary);
        }

        // Every step is either recorded in the journal or safe to run twice, which is what lets boot finish it.
        private static bool Wipe(KmhSeasonResetJournal j, out string summary)
        {
            // Bump first: ids restart at 1, so a pre-wipe row must already be stale if a crash stops it half way.
            if (!j.GenerationBumped)
            {
                Features.Economy.KmhEconomyReset.BumpDataGeneration("season reset");
                j.GenerationBumped = true;
                if (!SaveJournal(j)) { summary = "stopped - the journal could not be updated; the economy stays paused and the reset is retried"; return false; }
            }

            // Roll before wiping the stats it reads; recorded, since rolling twice archives an empty season over the real one.
            if (!j.SeasonRolled)
            {
                (int rolled, int records) = RollForTest != null ? RollForTest() : Features.Seasons.SeasonStore.RollSeason();
                j.SeasonRolled = true; j.RolledSeason = rolled; j.RecordCount = records;
                if (!SaveJournal(j)) { summary = "stopped - the journal could not be updated; the economy stays paused and the reset is retried"; return false; }
            }
            AfterRollForTest?.Invoke();

            // Treasury last, so a concurrent sweeper settlement cannot re-credit an already-wiped vault.
            foreach (Action clear in ClearersForTest != null ? ClearersForTest() : SeasonClearers()) clear();
            // In-memory only, but a fresh season must not inherit the old cooldown windows.
            Features.Economy.EconomyAccess.ClearRuntimeState();
            int vaults = ClearVaultsForTest != null ? ClearVaultsForTest() : Features.Treasury.TreasuryStore.ClearAllVaults();

            // The journal closes only once every cleared store is on disk, or a crash revives old data in the new season.
            int unwritten = 0;
            KmhDataFlush.FlushAll(e => { unwritten++; ServerLog.Warn($"Season reset: {e}"); });
            if (unwritten > 0)
            {
                summary = $"{unwritten} store(s) could not be written after the wipe - the economy stays paused and the reset is retried until they are.";
                ServerLog.Error($"Season reset: {summary}");
                return false;
            }
            // Marked complete before the file goes: a journal that cannot be removed must not wipe the new season at boot.
            j.Completed = true;
            if (!SaveJournal(j))
            {
                j.Completed = false;
                summary = "the wipe is on disk but the journal could not be closed - the economy stays paused and the close is retried.";
                ServerLog.Error($"Season reset: {summary}");
                return false;
            }
            DeleteJournal();
            Rebroadcast();

            int newSeason = Features.Seasons.SeasonStore.CurrentSeason;
            summary = $"Season {j.RolledSeason} archived ({j.RecordCount} record(s)); economy wiped ({vaults} vault(s) + " +
                      $"listings/auctions/wants/sites/quests/reputation/mail/world/standings cleared). " +
                      $"Now in season {newSeason}. Backup {j.Backup} (restore with: kmh restore {j.Backup}).";
            ServerLog.Warn($"SEASON RESET by {j.Actor}: {summary}");
            Extensibility.KmhEventBus.Instance.RaiseSeasonRolled(new KMH.Sdk.Server.Events.SeasonRolledEvent
            { RolledSeason = j.RolledSeason, NewSeason = newSeason, RecordCount = j.RecordCount, Actor = j.Actor, EconomyWiped = true });
            try { KmhStatusExport.WriteToDisk(); } catch { }
            return true;
        }

        // Enumerated rather than inlined so the coverage test can reflect over it and prove none was forgotten.
        internal static IEnumerable<Action> SeasonClearers()
        {
            yield return Features.Marketplace.MarketplaceStore.ClearForNewSeason;
            yield return Features.Auctions.AuctionStore.ClearForNewSeason;
            yield return Features.WantBoard.WantStore.ClearForNewSeason;
            yield return Features.Mail.MailStore.ClearForNewSeason;
            yield return Features.Chat.ChatStore.ClearForNewSeason;
            yield return Features.Sites.SiteStore.ClearForNewSeason;
            yield return Features.Roadworks.RoadworksStore.ClearForNewSeason;
            yield return Features.Frontier.KmhWorldDirector.ClearForNewSeason;
            yield return Features.Quests.QuestStore.ClearForNewSeason;
            yield return Features.Reputation.ReputationStore.ClearForNewSeason;
            yield return Features.Notifications.NotificationStore.ClearForNewSeason;
            yield return Features.Recovery.RecoveryStore.ClearForNewSeason;
            yield return Features.World.WorldStore.ClearForNewSeason;
            yield return Features.PlayerStats.PlayerStatsStore.ClearForNewSeason;
        }

        // A stale client view is cosmetic, so one failing broadcast never aborts the reset.
        private static void Rebroadcast()
        {
            Safe(() => Features.Marketplace.MarketplaceHandler.BroadcastSnapshot());
            Safe(() => Features.Quests.QuestHandler.BroadcastSnapshot());
            Safe(() => Features.Auctions.AuctionHandler.BroadcastSnapshot());
            Safe(() => Features.WantBoard.WantHandler.BroadcastSnapshot());
            Safe(() => Features.Sites.SiteHandler.BroadcastSnapshot());
            Safe(() => Features.Roadworks.RoadworksHandler.BroadcastSnapshot());
            Safe(() => Features.Reputation.ReputationHandler.BroadcastSnapshot());
            Safe(() => Features.World.WorldHandler.BroadcastSnapshot());
            Safe(() => Features.PlayerStats.PlayerStatsHandler.BroadcastSnapshot());
            Safe(() => Features.Seasons.SeasonHandler.Broadcast());
            Safe(() =>
            {
                foreach (ServerClient c in Network.ServerClients.Keys)
                {
                    if (c?.IsVerified != true) continue;
                    string u = c.GetData<UserFile>()?.Username;
                    if (string.IsNullOrEmpty(u)) continue;
                    KmhRouter.SendTo(c, KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(u));
                }
            });
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception ex) { ServerLog.Warn($"Season reset rebroadcast step failed: {ex.Message}"); }
        }
    }
}
