using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Frontier.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Frontier
{
    // Coordination only: it never holds silver, and never copies Site or quest state.
    internal static class KmhWorldDirector
    {
        private static readonly object _lock = new object();
        private static DirectorState _state = new DirectorState();

        public static long Revision { get { lock (_lock) return _state.Revision; } }

        public static int OutpostCount        { get { lock (_lock) return _state.OutpostTiles.Count; } }
        public static int ActiveOperationCount { get { lock (_lock) return _state.ActiveOperationIds.Count; } }

        // The order is the point: unfinished work always completes before anything new starts.
        internal enum Step { Idle, ResumeResolution, ResolvePlacement, OpenPlacement }

        internal static Step NextStep(DirectorState st, FrontierConfig cfg, long nowTicks)
        {
            if (st == null || cfg == null || !cfg.Enabled) return Step.Idle;

            // First, or a second operation could resolve on top of a half-applied one after a restart.
            foreach (ResolutionRecord rec in st.Resolutions)
                if (FrontierResolution.NeedsResume(rec.Phase)) return Step.ResumeResolution;

            PlacementToken pending = st.PendingPlacement;
            if (pending != null && !pending.Consumed)
            {
                // Only once the window closes, or whoever replied first would decide the tile.
                return FrontierPlacement.IsExpired(pending, nowTicks) ? Step.ResolvePlacement : Step.Idle;
            }

            return MayAct(st, cfg, nowTicks, out _) ? Step.OpenPlacement : Step.Idle;
        }

        public static bool MayAct(DirectorState st, FrontierConfig cfg, long nowTicks, out string why)
        {
            why = null;
            if (st == null || cfg == null) { why = "no director state"; return false; }
            if (!cfg.Enabled)                     { why = "frontier operations are turned off"; return false; }
            if (nowTicks < st.NextEligibleUtcTicks) { why = "global cooldown"; return false; }
            if (st.SpawnBudget <= 0)              { why = "no spawn budget left"; return false; }
            if (st.OutpostTiles.Count >= cfg.MaxOutposts)            { why = "outpost cap reached"; return false; }
            if (st.ActiveOperationIds.Count >= cfg.MaxActiveOperations) { why = "operation cap reached"; return false; }
            return true;
        }

        // Nothing else removes a cooldown, so without this the map grows for the life of the server.
        internal static int PruneExpiredCooldowns(Dictionary<string, long> map, long nowTicks)
        {
            if (map == null || map.Count == 0) return 0;
            List<string> expired = null;
            foreach (KeyValuePair<string, long> kv in map)
                if (kv.Value <= nowTicks) (expired = expired ?? new List<string>()).Add(kv.Key);
            if (expired == null) return 0;
            foreach (string k in expired) map.Remove(k);
            return expired.Count;
        }

        public static bool TargetIsCool(DirectorState st, int tile, long nowTicks)
        {
            if (st?.TargetCooldowns == null) return true;
            return !st.TargetCooldowns.TryGetValue(tile.ToString(), out long until) || nowTicks >= until;
        }

        // A step rather than a trickle, so a week offline yields one window's budget and not a week's.
        public static bool RefillDue(DirectorState st, FrontierConfig cfg, long nowTicks)
        {
            if (st == null || cfg == null) return false;
            long window = TimeSpan.FromHours(cfg.BudgetRefillHours).Ticks;
            if (window <= 0) return false;
            if (st.BudgetRefilledUtcTicks <= 0) return true;   // never refilled: this tick anchors the cycle
            return nowTicks - st.BudgetRefilledUtcTicks >= window;
        }

        // Advancing the stamp is load-bearing: without it every later tick reads as overdue and the cap means nothing.
        internal static bool ApplyRefill(DirectorState st, FrontierConfig cfg, long nowTicks)
        {
            if (!RefillDue(st, cfg, nowTicks)) return false;
            st.SpawnBudget = cfg.SpawnBudget;
            st.BudgetRefilledUtcTicks = nowTicks;
            return true;
        }

        // The refusal on an existing record is what makes resolution at-most-once across a restart.
        public static bool TryBeginResolution(long operationId, string consequence, int targetTile, out string why)
        {
            why = null;
            if (operationId <= 0) { why = "no operation id"; return false; }
            if (!FrontierOperations.IsDispatchable(consequence))
            { why = "that consequence cannot be applied"; return false; }

            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                foreach (ResolutionRecord r in _state.Resolutions)
                    if (r.OperationId == operationId)
                    { why = FrontierResolution.IsTerminal(r.Phase) ? "already resolved" : "already resolving"; return false; }

                _state.Resolutions.Add(new ResolutionRecord
                {
                    OperationId = operationId,
                    Phase = FrontierResolution.PhaseResolving,
                    Consequence = FrontierOperations.NormalizeConsequence(consequence),
                    TargetTile = targetTile,
                    StartedUtcTicks = now,
                    UpdatedUtcTicks = now,
                });
                _state.Revision++;
            }
            SaveToDisk();   // durable before the world changes, or a crash loses the evidence that work began
            return true;
        }

        public static bool TryAdvanceResolution(long operationId, string toPhase)
        {
            bool moved = false;
            lock (_lock)
            {
                foreach (ResolutionRecord r in _state.Resolutions)
                {
                    if (r.OperationId != operationId) continue;
                    if (!FrontierResolution.CanAdvance(r.Phase, toPhase)) return false;
                    r.Phase = toPhase;
                    r.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    moved = true;
                    break;
                }
                if (moved) _state.Revision++;
            }
            // A phase only counts as advanced once the disk agrees; otherwise the resume path must run again.
            return moved && SaveToDisk();
        }

        // Copies: only TryAdvanceResolution persists a phase, so writing the live record would not stick.
        public static List<ResolutionRecord> UnfinishedResolutions()
        {
            var outp = new List<ResolutionRecord>();
            lock (_lock)
                foreach (ResolutionRecord r in _state.Resolutions)
                    if (r != null && FrontierResolution.NeedsResume(r.Phase)) outp.Add(r.Clone());
            return outp;
        }

        // Only terminal records are dropped, since an unfinished one still has to be resumed.
        internal static List<ResolutionRecord> PruneFinished(List<ResolutionRecord> records, int maxTerminal)
        {
            var kept = new List<ResolutionRecord>();
            if (records == null) return kept;
            int terminal = 0;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                ResolutionRecord r = records[i];
                if (r == null) continue;
                if (!FrontierResolution.IsTerminal(r.Phase)) { kept.Insert(0, r); continue; }
                if (terminal >= maxTerminal) continue;
                terminal++;
                kept.Insert(0, r);
            }
            return kept;
        }

        // One question at a time, or a client could answer twice under two different token ids.
        public static PlacementToken OpenPlacement(string template, IEnumerable<int> excludeTiles)
        {
            long now = DateTime.UtcNow.Ticks;
            PlacementToken token;
            lock (_lock)
            {
                if (!FrontierPlacement.CanOpenNew(_state.PendingPlacement, now)) return null;

                token = new PlacementToken
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Nonce = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0),
                    Template = template ?? "",
                    IssuedUtcTicks = now,
                    ExpiresUtcTicks = now + TimeSpan.FromMinutes(FrontierConfig.Current.PlacementWindowMinutes).Ticks,
                };
                if (excludeTiles != null) foreach (int t in excludeTiles) token.ExcludeTiles.Add(t);
                _state.PendingPlacement = token;
                _state.Revision++;
            }
            SaveToDisk();
            return token;
        }

        public static bool TryRecordProposal(string user, string tokenId, int tile, int layer, string fingerprint,
                                             out string why)
        {
            why = null;
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                PlacementToken token = _state.PendingPlacement;
                if (token == null) { why = "no open placement question"; return false; }
                if (!FrontierPlacement.ProposalFits(token, user, tokenId, tile, fingerprint, now, out why)) return false;

                token.Proposals.Add(new PlacementProposal
                { Username = user, Tile = tile, Layer = layer, WorldFingerprint = fingerprint, ReceivedUtcTicks = now });
                _state.Revision++;
            }
            SaveToDisk();
            return true;
        }

        // Consumed whatever the outcome, so a failed answer does not leave the question open to retry.
        public static bool TryResolvePlacement(out int tile, out string why)
        {
            tile = -1; why = null;
            // Snapshotted, because TryAccept enumerates Proposals while an answering client adds to that list.
            PlacementToken token;
            lock (_lock) token = _state.PendingPlacement?.CloneForRead();
            if (token == null) { why = "no placement question"; return false; }

            bool ok = FrontierPlacement.TryAccept(token, FrontierConfig.Current.AllowSingleProposer, out tile, out why);
            lock (_lock)
            {
                if (_state.PendingPlacement != null) _state.PendingPlacement.Consumed = true;
                _state.Revision++;
            }
            // An unwritten consumption leaves the token pending, and one answer places a second outpost after a restart.
            if (!SaveToDisk())
            {
                lock (_lock)
                {
                    if (_state.PendingPlacement != null) _state.PendingPlacement.Consumed = false;
                    _state.Revision++;
                }
                tile = -1;
                why = "the placement answer could not be written down";
                return false;
            }
            return ok;
        }

        public static void NoteOutpost(int tile, bool present)
        {
            lock (_lock)
            {
                bool has = _state.OutpostTiles.Contains(tile);
                if (present == has) return;
                if (present) _state.OutpostTiles.Add(tile); else _state.OutpostTiles.Remove(tile);
                _state.Revision++;
            }
            SaveToDisk();
        }

        public static void NoteOperation(long id, bool active)
        {
            lock (_lock)
            {
                bool has = _state.ActiveOperationIds.Contains(id);
                if (active == has) return;
                if (active) _state.ActiveOperationIds.Add(id); else _state.ActiveOperationIds.Remove(id);
                _state.Revision++;
            }
            SaveToDisk();
        }

        internal static void Tick()
        {
            FrontierConfig cfg = FrontierConfig.Current;
            long now = DateTime.UtcNow.Ticks;

            // First, or the cap fills with locations nobody can claim and the director stops acting for good.
            foreach (int expired in Sites.SiteStore.ExpireClaimWindows(now))
            {
                NoteOutpost(expired, false);
                Diagnostics.ServerLog.Info($"Frontier: nobody claimed tile {expired} in time - it is dormant and no longer holds a slot.");
            }

            DirectorState snapshot;
            bool refilled;
            lock (_lock)
            {
                refilled = ApplyRefill(_state, cfg, now);
                if (refilled) _state.Revision++;
                // Copied, because the consequence dispatcher adds to the journal from another thread.
                snapshot = _state.CopyForRead();
            }
            // Persisted here, or a restart reads the old stamp and refills all over again.
            if (refilled) SaveToDisk();

            switch (NextStep(snapshot, cfg, now))
            {
                case Step.ResumeResolution:
                    foreach (ResolutionRecord rec in UnfinishedResolutions())
                        KmhOperationConsequence.Resume(rec);
                    return;

                case Step.ResolvePlacement:
                {
                    if (!TryResolvePlacement(out int tile, out string why))
                    { NoteBlocker($"placement abandoned - {why}", now); return; }
                    ClearBlocker();
                    if (!TryEstablishAt(tile, cfg, now, out string reason))
                        Diagnostics.ServerLog.Diag("frontier.establish", $"Frontier: outpost not established - {reason}");
                    return;
                }

                case Step.OpenPlacement:
                {
                    // A dormant tile is already on the map, so reusing one asks the clients nothing.
                    int dormant = Sites.SiteStore.FindReusableDormantTile(t => TargetIsCool(snapshot, t, now));
                    if (dormant >= 0)
                    {
                        if (TryEstablishAt(dormant, cfg, now, out string dwhy))
                        {
                            ClearBlocker();
                            Diagnostics.ServerLog.Info($"Frontier: woke the dormant location on tile {dormant} instead of claiming another.");
                        }
                        else
                        {
                            NoteBlocker($"dormant tile {dormant} not woken - {dwhy}", now);
                        }
                        return;
                    }

                    List<int> exclude = Sites.SiteStore.AllOccupiedTiles();
                    lock (_lock)
                        foreach (KeyValuePair<string, long> kv in _state.TargetCooldowns)
                            if (kv.Value > now && int.TryParse(kv.Key, out int t) && !exclude.Contains(t)) exclude.Add(t);

                    PlacementToken token = OpenPlacement(SiteOutpostTemplate, exclude);
                    if (token == null) return;
                    int asked = FrontierHandler.AskForPlacement(token);
                    _lastAskedClients = asked;
                    if (asked == 0) NoteBlocker("no connected client can answer a placement question", now);
                    else Diagnostics.ServerLog.Info($"Frontier: asked {asked} client(s) where a {SiteOutpostTemplate} outpost could go ({exclude.Count} tile(s) excluded).");
                    return;
                }
            }
        }

        private const string SiteOutpostTemplate = Sites.Dto.SiteEntry.TemplateRuins;

        private static readonly object _blockerLock = new object();
        private static string _blocker = "";
        private static long   _blockerSinceUtc;
        private static long   _blockerLoggedUtc;
        private static int    _lastAskedClients = -1;

        // The same blocker every minute is noise, so it is logged once and then only every quarter hour it persists.
        private static void NoteBlocker(string reason, long nowTicks)
        {
            bool say;
            lock (_blockerLock)
            {
                if (!string.Equals(_blocker, reason, StringComparison.Ordinal))
                {
                    _blocker = reason; _blockerSinceUtc = nowTicks; _blockerLoggedUtc = nowTicks;
                    say = true;
                }
                else
                {
                    say = nowTicks - _blockerLoggedUtc >= TimeSpan.FromMinutes(15).Ticks;
                    if (say) _blockerLoggedUtc = nowTicks;
                }
            }
            if (say) Diagnostics.ServerLog.Info($"Frontier: {reason}.");
        }

        private static void ClearBlocker()
        {
            lock (_blockerLock) { _blocker = ""; _blockerSinceUtc = 0; _blockerLoggedUtc = 0; }
        }

        internal sealed class StatusView
        {
            public bool   Enabled;
            public string Step = "";
            public string Blocker = "";
            public long   BlockerSinceUtc;
            public int    Budget, BudgetMax, Outposts, MaxOutposts, Operations, MaxOperations;
            public long   NextEligibleUtc, NowUtc, PlacementExpiresUtc, BudgetRefilledUtc;
            public bool   PlacementOpen;
            public int    PlacementProposals, LastAskedClients, UnfinishedResolutions;
        }

        public static StatusView Status()
        {
            FrontierConfig cfg = FrontierConfig.Current;
            long now = DateTime.UtcNow.Ticks;
            var v = new StatusView { Enabled = cfg.Enabled, NowUtc = now, BudgetMax = cfg.SpawnBudget,
                                     MaxOutposts = cfg.MaxOutposts, MaxOperations = cfg.MaxActiveOperations };
            lock (_lock)
            {
                v.Budget = _state.SpawnBudget;
                v.Outposts = _state.OutpostTiles.Count;
                v.Operations = _state.ActiveOperationIds.Count;
                v.NextEligibleUtc = _state.NextEligibleUtcTicks;
                v.BudgetRefilledUtc = _state.BudgetRefilledUtcTicks;
                PlacementToken p = _state.PendingPlacement;
                v.PlacementOpen = p != null && !p.Consumed;
                v.PlacementExpiresUtc = p?.ExpiresUtcTicks ?? 0;
                v.PlacementProposals = p?.Proposals?.Count ?? 0;
                foreach (ResolutionRecord rec in _state.Resolutions)
                    if (FrontierResolution.NeedsResume(rec.Phase)) v.UnfinishedResolutions++;
                v.Step = NextStep(_state, cfg, now).ToString();
                if (!MayAct(_state, cfg, now, out string why) && why != null) v.Blocker = why;
            }
            lock (_blockerLock)
            {
                if (!string.IsNullOrEmpty(_blocker)) { v.Blocker = _blocker; v.BlockerSinceUtc = _blockerSinceUtc; }
                v.LastAskedClients = _lastAskedClients;
            }
            return v;
        }

        // The only way an outpost comes into existence, so the director and an operator share one funding rule.
        internal static bool TryEstablishAt(int tile, FrontierConfig cfg, long now, out string why)
        {
            // Checked first, because a location with no operation behind it holds a slot it can never release.
            if (FrontierFunding.Affordable(FrontierScale.Reward(cfg, FrontierScale.Live(cfg)),
                                           Marketplace.MarketplaceStore.HousePoolBalance(),
                                           cfg.MinOperationReward) <= 0)
            { why = "the house pool cannot fund a reclaim operation"; return false; }

            var (ok, reason) = Sites.SiteStore.FindReusableDormantTile(t => t == tile) == tile
                ? Sites.SiteStore.ReviveDormantOutpost(tile)
                : Sites.SiteStore.EstablishOutpost(tile, SiteOutpostTemplate, "", 0);
            why = reason;
            if (!ok) return false;

            NoteOutpost(tile, true);
            if (!OpenReclaimFor(tile, cfg))
            {
                // The pool drained between the check and the debit, so the location must not keep a slot.
                if (Sites.SiteStore.TryAdvanceOutpost(tile, Sites.Dto.SiteEntry.OutpostDerelict,
                                                      Sites.Dto.SiteEntry.OutpostDormant, 0, out _))
                    NoteOutpost(tile, false);
                why = "its operation could not be funded - the location is dormant and holds no slot";
                return false;
            }
            lock (_lock)
            {
                _state.SpawnBudget = _state.SpawnBudget > 0 ? _state.SpawnBudget - 1 : 0;
                _state.NextEligibleUtcTicks = now + TimeSpan.FromMinutes(cfg.MinMinutesBetweenActions).Ticks;
                PruneExpiredCooldowns(_state.TargetCooldowns, now);
                _state.TargetCooldowns[tile.ToString()] = now + TimeSpan.FromMinutes(cfg.TargetCooldownMinutes).Ticks;
                _state.Revision++;
            }
            SaveToDisk();
            Sites.SiteHandler.BroadcastSnapshot();
            return true;
        }

        // An admin picks the tile, but the caps still apply so they cannot create state the director could not.
        public static string PlacementRefusalFor(DirectorState st, FrontierConfig cfg, int tile, long nowTicks)
        {
            if (cfg == null || !cfg.Enabled) return "Frontier operations are turned off.";
            if (tile < 0) return "Pick a valid tile.";
            if (st == null) return "No director state.";
            if (st.OutpostTiles.Count >= cfg.MaxOutposts) return "The outpost cap is already reached.";
            if (st.ActiveOperationIds.Count >= cfg.MaxActiveOperations) return "The active operation cap is already reached.";
            if (!TargetIsCool(st, tile, nowTicks)) return "That tile is still on cooldown.";
            return null;
        }

        public static string TryPlaceAsOperator(int tile)
        {
            FrontierConfig cfg = FrontierConfig.Current;
            long now = DateTime.UtcNow.Ticks;
            DirectorState st;
            lock (_lock) st = _state.CopyForRead();

            string refusal = PlacementRefusalFor(st, cfg, tile, now);
            if (refusal != null) return refusal;
            return TryEstablishAt(tile, cfg, now, out string why)
                ? $"Established {why} on tile {tile}."
                : why;
        }

        // Copied, not the live state: the caller reads it outside this lock while the tick is free to mutate.
        public static DirectorState StateForReport() { lock (_lock) return _state.CopyForRead(); }

        // Funded from the house pool before it is advertised, so no operation offers a reward nobody can pay.
        private static bool OpenReclaimFor(int tile, FrontierConfig cfg)
        {
            // One scale for both, read once: sizing the cost and the reward from separate counts would drift them apart.
            double scale = FrontierScale.Live(cfg);
            int    askQty = FrontierScale.Cost(cfg, scale);
            long reward = FrontierFunding.Affordable(FrontierScale.Reward(cfg, scale),
                                                     Marketplace.MarketplaceStore.HousePoolBalance(),
                                                     cfg.MinOperationReward);
            if (reward <= 0)
            { Diagnostics.ServerLog.Diag("frontier.fund", $"Frontier: no reclaim operation for tile {tile} - the house pool cannot fund one."); return false; }
            if (!Marketplace.MarketplaceStore.TryDebitHousePool(reward, $"frontier reclaim operation (tile {tile})"))
            { Diagnostics.ServerLog.Diag("frontier.fund", $"Frontier: reclaim funding failed for tile {tile}."); return false; }

            string name = Sites.SiteStore.DisplayNameOf(tile);
            World.Dto.ServerQuestDto q = World.WorldStore.CreateQuest(
                World.Dto.ServerQuestDto.KindCooperative,
                World.Dto.ServerQuestDto.ObjDeliver,
                cfg.ReclaimMaterialDefName,
                $"Reclaim {name}",
                $"Deliver {askQty}x {cfg.ReclaimMaterialDefName} to restore {name}. Once restored it can be claimed.",
                askQty,
                reward, cfg.ReclaimDurationMinutes,
                reservedFromPool: reward,
                operationType: World.Dto.ServerQuestDto.OpReclaim,
                operationSource: World.Dto.ServerQuestDto.SourceWorldDirector,
                targetSiteTile: tile,
                consequence: FrontierOperations.ConsequenceFor(World.Dto.ServerQuestDto.OpReclaim));

            NoteOperation(q.Id, true);
            World.WorldHandler.BroadcastSnapshot();
            Notifications.KmhNotify.ToEveryoneOnline("neutral",
                $"Frontier Operation available: reclaim {name}. Deliver {askQty}x {cfg.ReclaimMaterialDefName} to restore it.");
            Diagnostics.ServerLog.Info($"Frontier: reclaim operation #{q.Id} for {name} (tile {tile}), " +
                                       $"{askQty}x {cfg.ReclaimMaterialDefName} for {reward} at scale {scale:0.00} fully backed.");
            return true;
        }


        public static void ClearForNewSeason()
        {
            lock (_lock) { _state = new DirectorState(); _state.Revision++; }
            SaveToDisk();
        }

        // A crash can land between three stores' saves, so the two authoritative ones are re-read instead.
        internal static bool ReconcileLists(DirectorState st, List<int> actualTiles, List<long> actualOps,
                                            out int tilesFixed, out int opsFixed)
        {
            tilesFixed = 0; opsFixed = 0;
            if (st == null) return false;

            var tiles = new HashSet<int>(actualTiles ?? new List<int>());
            var ops   = new HashSet<long>(actualOps ?? new List<long>());

            foreach (int t in st.OutpostTiles) if (!tiles.Contains(t)) tilesFixed++;
            foreach (int t in tiles) if (!st.OutpostTiles.Contains(t)) tilesFixed++;
            foreach (long id in st.ActiveOperationIds) if (!ops.Contains(id)) opsFixed++;
            foreach (long id in ops) if (!st.ActiveOperationIds.Contains(id)) opsFixed++;

            if (tilesFixed == 0 && opsFixed == 0) return false;
            st.OutpostTiles = new List<int>(tiles);
            st.ActiveOperationIds = new List<long>(ops);
            return true;
        }

        public static void Reconcile()
        {
            List<int>  tiles = Sites.SiteStore.SlotHoldingOutpostTiles();
            List<long> ops   = World.WorldStore.ActiveOperationIds();

            bool changed;
            int tilesFixed, opsFixed;
            lock (_lock)
            {
                changed = ReconcileLists(_state, tiles, ops, out tilesFixed, out opsFixed);
                if (changed) _state.Revision++;
            }
            if (changed)
            {
                SaveToDisk();
                Diagnostics.ServerLog.Info($"Frontier: reconciled director state against Sites and World - "
                    + $"{tilesFixed} outpost tile(s) and {opsFixed} operation id(s) corrected.");
            }

            // The director refuses every other step while one of these exists.
            foreach (ResolutionRecord rec in UnfinishedResolutions())
            {
                Diagnostics.ServerLog.Info($"Frontier: resuming operation #{rec.OperationId} left at '{rec.Phase}' by an unclean shutdown.");
                KmhOperationConsequence.Resume(rec);
            }

            // Last, because a resume above can put a location back into a live state.
            ReleaseOrphanedLocations();
        }

        // A crash before the journal record exists leaves nothing to resume, and the quest is pruned soon after.
        private static void ReleaseOrphanedLocations()
        {
            System.Collections.Generic.HashSet<int> live = World.WorldStore.ActiveOperationTiles();
            foreach (int tile in Sites.SiteStore.OrphanedOutpostTiles(live))
            {
                if (!Sites.SiteStore.TryAdvanceOutpost(tile, Sites.Dto.SiteEntry.OutpostDerelict,
                                                      Sites.Dto.SiteEntry.OutpostDormant, 0, out string why))
                { Diagnostics.ServerLog.Info($"Frontier: orphaned tile {tile} not released - {why}"); continue; }
                NoteOutpost(tile, false);
                Diagnostics.ServerLog.Info($"Frontier: reconciled orphaned tile {tile} - no operation was left to advance it; it is dormant and holds no slot.");
            }
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.FrontierDirectorFile, out DirectorState st) || st == null) return;
            int dropped = PruneExpiredCooldowns(st.TargetCooldowns, DateTime.UtcNow.Ticks);
            lock (_lock) _state = st;
            if (dropped > 0) Diagnostics.ServerLog.Debug($"Frontier: dropped {dropped} expired target cooldown(s) on load.");
            int unfinished = UnfinishedResolutions().Count;
            Diagnostics.ServerLog.Info($"Frontier: director loaded - {st.OutpostTiles.Count} outpost(s), "
                + $"{st.ActiveOperationIds.Count} active operation(s), {unfinished} resolution(s) to resume.");
        }

        public static bool SaveToDisk()
        {
            DirectorState copy;
            long seq;
            lock (_lock)
            {
                _state.Resolutions = PruneFinished(_state.Resolutions, FrontierConfig.Current.ResolutionRetention);
                // Copied so the save is deterministic; JsonFileStore would otherwise retry a serialize race.
                copy = _state.CopyForRead();
                seq = JsonFileStore.NextSequence();
            }
            return JsonFileStore.Save(KmhDataPaths.FrontierDirectorFile, copy, seq);
        }
    }
}
