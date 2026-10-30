using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Roadworks.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Roadworks
{
    internal static class RoadworksStore
    {
        private static readonly object _lock = new object();
        // Cancel and finish refunds are owed in the same save as the change. Declared below _lock because it captures it.
        private static readonly Transactions.KmhOwedPayouts _owed = new Transactions.KmhOwedPayouts("Roadworks", _lock, SaveToDisk);
        private static readonly Dictionary<string, long> _appliedWork = new Dictionary<string, long>(StringComparer.Ordinal);
        // A cycle's record clears the moment its work is laid, so a week of markers covers any replay many times over.
        private static readonly long AppliedWorkTtl = TimeSpan.FromDays(7).Ticks;
        private static readonly Dictionary<string, RoadSegment> _segments =
            new Dictionary<string, RoadSegment>(StringComparer.Ordinal);
        private static readonly Dictionary<long, RoadProject> _projects = new Dictionary<long, RoadProject>();
        private static long _revision = 0;
        private static long _nextProjectId = 1;

        public static long Revision { get { lock (_lock) return _revision; } }
        public static int SegmentCount { get { lock (_lock) return _segments.Count; } }

        // Wealth, audit and save-reset all read this, so they cannot disagree about what is still reserved.
        internal static long UnspentEscrow(RoadProject p)
            => p == null || p.State != RoadProject.StateBuilding ? 0 : Math.Max(0, p.EscrowSilver - p.EscrowSilverSpent);

        // Off-map value the owner can still reclaim. A completed road is infrastructure, not stored value.
        public static long ReservedSilverFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long total = 0;
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (string.Equals(p.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                        total += UnspentEscrow(p);
            return total;
        }

        public static long ReservedSilverTotal()
        {
            long total = 0;
            lock (_lock) foreach (RoadProject p in _projects.Values) total += UnspentEscrow(p);
            return total;
        }

        // Segments go to everyone; projects and escrow are the caller's alone.
        public static RoadworksSnapshot BuildSnapshotFor(string username)
        {
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            var snap = new RoadworksSnapshot
            {
                MaxRouteSegments = cfg.Roadworks.MaxSegmentsPerProject,
                AllowRoadworks   = Sites.SitesConfig.Current.AllowRoadworks,
                WorkPerWorker     = Sites.SiteStore.RoadworkBaseWorkPerWorker,
                WorkPerSkillLevel = Sites.SiteStore.RoadworkWorkPerSkillLevel,
            };
            foreach (string tier in RoadTiers.All)
            {
                snap.SilverPerSegment[tier] = RoadTiers.SilverPerSegment(tier);
                snap.WorkPerSegment[tier]   = RoadTiers.WorkPerSegment(tier);
            }

            lock (_lock)
            {
                snap.Revision = _revision;
                snap.Segments = SharedSegmentsLocked(snap.AllowRoadworks);
                snap.Projects = ProjectsForSnapshot(_projects.Values, username);
            }
            // Summed from the very rows the player is shown, so the headline figure and the list cannot disagree.
            foreach (RoadProjectDto d in snap.Projects) snap.EscrowSilver += d.EscrowUnspent;
            return snap;
        }

        internal static List<RoadSegment> SegmentsForTest() { lock (_lock) return new List<RoadSegment>(_segments.Values); }

        // Costs a connected client one extra snapshot, which is what lets a suite prove the shared list is not stale.
        internal static void BumpRevisionForTest() { lock (_lock) _revision++; }
        internal static void InsertProjectForTest(RoadProject p) { lock (_lock) _projects[p.Id] = p; }
        internal static void InsertSegmentForTest(RoadSegment s) { lock (_lock) _segments[RoadKeys.For(s)] = s; }
        internal static RoadProject ProjectForTest(long id) { lock (_lock) return _projects.TryGetValue(id, out RoadProject p) ? p : null; }
        internal static void RemoveForTest(long projectId, IEnumerable<string> segmentKeys)
        {
            lock (_lock)
            {
                _projects.Remove(projectId);
                foreach (string k in segmentKeys) if (k != null) _segments.Remove(k);
            }
        }

        // The whole planet's roads are the same for every player, so a broadcast to fifty of them builds this once.
        private static List<RoadSegmentDto> _sharedSegments;
        private static long _sharedSegmentsRevision = -1;
        private static bool _sharedSegmentsAllow;

        // Held only under _lock, and handed out READ-ONLY: every snapshot in one broadcast shares the same list.
        private static List<RoadSegmentDto> SharedSegmentsLocked(bool allow)
        {
            if (_sharedSegments != null && _sharedSegmentsRevision == _revision && _sharedSegmentsAllow == allow)
                return _sharedSegments;

            _sharedSegments         = SegmentsForSnapshot(_segments.Values, allow);
            _sharedSegmentsRevision = _revision;
            _sharedSegmentsAllow    = allow;
            return _sharedSegments;
        }

        // Turned off sends none, so clients withdraw what they applied; the store keeps them, so it is reversible.
        internal static List<RoadSegmentDto> SegmentsForSnapshot(IEnumerable<RoadSegment> segments, bool allow)
        {
            var outp = new List<RoadSegmentDto>();
            if (segments == null || !allow) return outp;
            foreach (RoadSegment s in segments)
            {
                if (s?.A == null || s.B == null) continue;
                outp.Add(new RoadSegmentDto
                {
                    LayerA = s.A.LayerId, TileA = s.A.TileId,
                    LayerB = s.B.LayerId, TileB = s.B.TileId,
                    Tier = RoadTiers.Normalize(s.Tier),
                });
            }
            return outp;
        }

        // A project names what someone is spending and could reclaim, so it never reaches another player.
        internal static List<RoadProjectDto> ProjectsForSnapshot(IEnumerable<RoadProject> projects, string username)
        {
            var outp = new List<RoadProjectDto>();
            if (projects == null || string.IsNullOrEmpty(username)) return outp;
            foreach (RoadProject p in projects)
            {
                if (p == null || !string.Equals(p.OwnerUsername, username, StringComparison.OrdinalIgnoreCase)) continue;
                outp.Add(new RoadProjectDto
                {
                    Id = p.Id, SiteTile = p.SiteTile, Tier = RoadTiers.Normalize(p.Tier), State = p.State,
                    SegmentsTotal = Math.Max(0, (p.Route?.Count ?? 0) - 1),
                    SegmentsDone = p.CurrentSegment, CurrentProgress = p.CurrentProgress,
                    EscrowSilver = p.EscrowSilver, EscrowUnspent = UnspentEscrow(p),
                    RouteTiles  = (p.Route ?? new List<RoadTile>()).ConvertAll(t => t.TileId),
                    RouteLayers = (p.Route ?? new List<RoadTile>()).ConvertAll(t => t.LayerId),
                });
            }
            return outp;
        }

        public static List<RoadSegment> AllSegments()
        {
            lock (_lock) return new List<RoadSegment>(_segments.Values);
        }

        public static List<RoadProject> ProjectsFor(string username)
        {
            var outp = new List<RoadProject>();
            if (string.IsNullOrEmpty(username)) return outp;
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (string.Equals(p.OwnerUsername, username, StringComparison.OrdinalIgnoreCase)) outp.Add(p);
            return outp;
        }

        public static int ActiveProjectCountForSite(int siteTile)
        {
            int n = 0;
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (p.SiteTile == siteTile && p.State == RoadProject.StateBuilding) n++;
            return n;
        }

        public static int MaxActiveProjectsForTier(int tier) => tier <= 1 ? 1 : (tier == 2 ? 2 : 3);

        // The proposed route minus anything already built - an existing segment is not new construction.
        internal static List<(RoadTile a, RoadTile b)> BuildableSegments(List<RoadTile> route, HashSet<string> existing)
        {
            var keys  = new List<string>();
            var pairs = new Dictionary<string, (RoadTile, RoadTile)>(StringComparer.Ordinal);
            if (route != null)
                for (int i = 0; i + 1 < route.Count; i++)
                {
                    RoadTile a = route[i], b = route[i + 1];
                    if (!RoadKeys.IsWellFormed(a, b)) continue;
                    string k = RoadKeys.For(a, b);
                    keys.Add(k);
                    if (!pairs.ContainsKey(k)) pairs[k] = (a, b);
                }

            // Same rule the client quotes with, so the shown price is the charged price.
            var outp = new List<(RoadTile, RoadTile)>();
            foreach (string k in RoadKeys.NewSegmentKeys(keys, existing)) outp.Add(pairs[k]);
            return outp;
        }

        // Null means allowed; permission comes from the site's own ownership rule, never a Roadworks-specific one.
        internal static string SiteRefusalFor(bool found, bool canManage, string archetype, int siteTier,
                                              string username, string tier)
        {
            if (!found) return "No site on that tile.";
            if (string.IsNullOrEmpty(username) || !canManage) return "That isn't your site.";
            if (archetype != Sites.SiteArchetypes.Roadworks) return "Only a Roadworks site can build roads.";
            if (!RoadTiers.AllowedAtSiteTier(tier, siteTier))
                return $"A Tier {siteTier} Roadworks site cannot build {RoadTiers.DisplayName(tier)}.";
            return null;
        }

        // Never validates adjacency - that stays the client's assertion.
        public static bool TryStartProject(string username, int siteTile, string tier,
                                           List<RoadTile> route, out RoadProject project, out string reason)
        {
            project = null; reason = null;
            if (string.IsNullOrEmpty(username)) { reason = "No caller."; return false; }
            if (!Sites.SitesConfig.Current.AllowRoadworks)
            { reason = "Roadworks is turned off on this server."; return false; }
            if (route == null || route.Count < 2) { reason = "A route needs at least two tiles."; return false; }
            // Pricing skips a malformed step, so the build must never be handed one either.
            for (int i = 0; i + 1 < route.Count; i++)
                if (!RoadKeys.IsWellFormed(route[i], route[i + 1]))
                { reason = "That route has a step that isn't between two different valid tiles."; return false; }

            // Authority, archetype and tier come from the site, never from the caller.
            bool found = Sites.SiteStore.TryGetSiteFacts(siteTile, username, out bool canManage, out string archetype, out int siteTier);
            reason = SiteRefusalFor(found, canManage, archetype, siteTier, username, tier);
            if (reason != null) return false;

            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            if (route.Count - 1 > cfg.Roadworks.MaxSegmentsPerProject)
            { reason = $"That route is too long ({route.Count - 1} segments, max {cfg.Roadworks.MaxSegmentsPerProject})."; return false; }

            string t = RoadTiers.Normalize(tier);
            int maxActive = MaxActiveProjectsForTier(siteTier);
            if (ActiveProjectCountForSite(siteTile) >= maxActive)
            { reason = $"This Roadworks site already has {maxActive} active project(s)."; return false; }

            List<(RoadTile a, RoadTile b)> buildable;
            lock (_lock)
            {
                var existing = new HashSet<string>(_segments.Keys, StringComparer.Ordinal);
                buildable = BuildableSegments(route, existing);
            }
            if (buildable.Count == 0) { reason = "That route is already built."; return false; }

            int cost = RoadTiers.SilverPerSegment(t) * buildable.Count;

            // The charge rides a ledger row written before the debit, and the project is saved naming that row: a crash
            // before the save is refunded at boot, one after it keeps the project funded - never both.
            Transactions.KmhTransaction tx = null;
            if (cost > 0)
            {
                string summary = $"roadworks project ({buildable.Count} segment(s), {RoadTiers.DisplayName(t)})";
                tx = Transactions.KmhTransactionRepository.OpenTake(username, Transactions.KmhTxType.Purchase, summary, siteTile, cost,
                         null, null, destination: "roadworks", targetKind: Transactions.KmhTransactionRepository.TargetRoadStart);
                if (tx == null) { reason = "The server couldn't record that charge - nothing was taken. Try again shortly."; return false; }
                if (!Treasury.TreasuryStore.WithdrawBundleForTxn(username, cost, new List<KeyValuePair<string, int>>(), tx.TakeMarker, summary))
                {
                    Transactions.KmhTransactionRepository.Abort(tx);
                    reason = "Your treasury doesn't have enough silver for that route.";
                    return false;
                }
            }
            AtStartPhaseForTest?.Invoke("charged");

            // Everything checked before the charge could have moved while the treasury lock was held, so re-ask now.
            bool stillFound = Sites.SiteStore.TryGetSiteFacts(siteTile, username, out bool stillManage,
                                                              out string stillArch, out int stillTier);
            string commitRefusal = SiteRefusalFor(stillFound, stillManage, stillArch, stillTier, username, t);

            long now = DateTime.UtcNow.Ticks;
            int refundDelta = 0;
            string deltaMarker = null;
            bool saved = false;
            if (commitRefusal == null)
                lock (_lock)
                {
                    var existingNow = new HashSet<string>(_segments.Keys, StringComparer.Ordinal);
                    List<(RoadTile a, RoadTile b)> stillBuildable = BuildableSegments(route, existingNow);
                    if (stillBuildable.Count == 0) commitRefusal = "That route was just built.";
                    else if (ActiveProjectCountForSite(siteTile) >= MaxActiveProjectsForTier(stillTier))
                        commitRefusal = $"This Roadworks site already has {MaxActiveProjectsForTier(stillTier)} active project(s).";
                    else
                    {
                        // Part of the route may have been laid during the charge, so escrow only what is still left.
                        int finalCost = RoadTiers.SilverPerSegment(t) * stillBuildable.Count;
                        refundDelta = Math.Max(0, cost - finalCost);
                        project = new RoadProject
                        {
                            Id = _nextProjectId++, OwnerUsername = username, SiteTile = siteTile, Tier = t,
                            Route = new List<RoadTile>(route), CurrentSegment = 0, CurrentProgress = 0,
                            State = RoadProject.StateBuilding, CreatedUtcTicks = now, UpdatedUtcTicks = now,
                            EscrowSilver = finalCost, EscrowSilverSpent = 0, StartTxId = tx?.Id ?? "",
                        };
                        _projects[project.Id] = project;
                        // The part already built is owed back in the same save that funds the project.
                        if (refundDelta > 0)
                            deltaMarker = _owed.RecordLocked(new Transactions.KmhOwedPayout
                            {
                                Marker = $"road:{project.Id}:{now}:delta", User = username, Silver = refundDelta,
                                Note = $"roadworks project #{project.Id} - part of the route was already built",
                            });
                        _revision++;
                        saved = SaveToDisk();
                        if (!saved) { _projects.Remove(project.Id); _owed.ForgetLocked(deltaMarker); _revision++; }
                    }
                }

            if (commitRefusal != null || !saved)
            {
                RefundStart(tx);
                project = null;
                reason = commitRefusal != null
                    ? commitRefusal + (cost > 0 ? " Your silver was refunded." : "")
                    : "The server couldn't save that project - your silver was returned. Try again shortly.";
                return false;
            }
            AtStartPhaseForTest?.Invoke("saved");
            if (tx != null) Transactions.KmhTransactionRepository.Settle(tx);
            _owed.Settle(deltaMarker);
            RaiseSiteChanged(siteTile, username, "road_started");
            Diagnostics.ServerLog.Info($"Roadworks: {username} started project #{project.Id} - {RoadTiers.DisplayName(t)} escrowed at {project.EscrowSilver} silver"
                + (refundDelta > 0 ? $" ({refundDelta} returned - part of the route was already built)." : "."));
            return true;
        }

        internal static Action<string> AtStartPhaseForTest;

        // On the row's own refund marker, so this and boot recovery return the charge once between them.
        private static void RefundStart(Transactions.KmhTransaction tx)
        {
            if (tx != null && !Transactions.KmhTransactionRepository.Compensate(tx))
                Diagnostics.ServerLog.Error($"Roadworks: charge {tx.Id} could not be refunded yet - the row stays open and the next boot refunds it.");
        }

        // A project saved naming the row that funded it: boot settles that row instead of refunding it.
        internal static bool HoldsStart(string txId)
        {
            if (string.IsNullOrEmpty(txId)) return false;
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (p != null && string.Equals(p.StartTxId, txId, StringComparison.Ordinal)) return true;
            return false;
        }

        // Completed segments stay; unspent escrow returns, owed in the same save that cancels.
        public static bool CancelProject(string username, long projectId, out int refunded, out string reason)
        {
            refunded = 0; reason = null;
            RoadProject p;
            string marker;
            lock (_lock)
            {
                if (!_projects.TryGetValue(projectId, out p)) { reason = "No such project."; return false; }
                if (!string.Equals(p.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                { reason = "That isn't your project."; return false; }
                if (p.State != RoadProject.StateBuilding) { reason = "That project isn't active."; return false; }
                refunded = Math.Max(0, p.EscrowSilver - p.EscrowSilverSpent);
                p.EscrowSilverSpent = p.EscrowSilver;
                p.State = RoadProject.StateCancelled;
                p.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                _revision++;
                marker = _owed.RecordLocked(new Transactions.KmhOwedPayout
                {
                    Marker = $"road:{p.Id}:{p.CreatedUtcTicks}:cancel", User = username, Silver = refunded,
                    Note = $"roadworks project #{projectId} cancelled - unbuilt segments refunded",
                });
                // Durable with its refund owed, or the restart reopens the project with escrow the owner already has.
                if (!SaveToDisk())
                {
                    _owed.ForgetLocked(marker);
                    p.EscrowSilverSpent = p.EscrowSilver - refunded;
                    p.State = RoadProject.StateBuilding;
                    refunded = 0; _revision++;
                    reason = "The server couldn't save that - nothing changed. Try again shortly.";
                    return false;
                }
            }
            _owed.Settle(marker);
            RaiseSiteChanged(p.SiteTile, username, "road_cancelled");
            Diagnostics.ServerLog.Info($"Roadworks: project #{projectId} cancelled by {username}; {refunded} silver returned, completed segments kept.");
            return true;
        }

        // Read before the site lock is taken, or asking per site from inside it would invert the lock order.
        internal static HashSet<int> TilesWithActiveProjects()
        {
            var tiles = new HashSet<int>();
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (p != null && p.State == RoadProject.StateBuilding) tiles.Add(p.SiteTile);
            return tiles;
        }

        // Lowest id first, so a work cycle splits deterministically rather than however the dictionary yields.
        internal static List<long> ActiveProjectIdsForSite(int siteTile)
        {
            var ids = new List<long>();
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (p != null && p.SiteTile == siteTile && p.State == RoadProject.StateBuilding) ids.Add(p.Id);
            ids.Sort();
            return ids;
        }

        // Roadworks holds player silver, so an owner must be able to see and unstick it without a game client.
        internal static List<RoadProject> AllProjectsForApi()
        {
            var all = new List<RoadProject>();
            lock (_lock) foreach (RoadProject p in _projects.Values) if (p != null) all.Add(p.ShallowClone());
            all.Sort((x, y) => x.Id.CompareTo(y.Id));
            return all;
        }

        // Lets the operator path reuse the player cancel exactly, rather than growing a second one that could drift.
        internal static string OwnerOfProject(long projectId)
        {
            lock (_lock) return _projects.TryGetValue(projectId, out RoadProject p) && p != null ? p.OwnerUsername : null;
        }

        private static int AdvanceLocked(RoadProject p, double work, long now)
        {
            int completed = 0;
            double perSegment = RoadTiers.WorkPerSegment(p.Tier);
            double remaining = work;
            while (remaining > 0 && p.CurrentSegment + 1 < p.Route.Count)
            {
                double need = perSegment * (1.0 - p.CurrentProgress);
                if (remaining < need) { p.CurrentProgress += remaining / perSegment; remaining = 0; break; }

                remaining -= need;
                RoadTile a = p.Route[p.CurrentSegment], b = p.Route[p.CurrentSegment + 1];
                p.CurrentSegment++;
                p.CurrentProgress = 0;

                string k = RoadKeys.For(a, b);
                if (k != null && RoadKeys.IsWellFormed(a, b) && !_segments.ContainsKey(k))
                {
                    _segments[k] = new RoadSegment
                    {
                        A = a, B = b, Tier = p.Tier, OwnerUsername = p.OwnerUsername,
                        SourceSiteTile = p.SiteTile, ProjectId = p.Id, BuiltUtcTicks = now,
                    };
                    p.EscrowSilverSpent = Math.Min(p.EscrowSilver, p.EscrowSilverSpent + RoadTiers.SilverPerSegment(p.Tier));
                    completed++;
                }
            }
            if (p.CurrentSegment + 1 >= p.Route.Count) p.State = RoadProject.StateComplete;
            p.UpdatedUtcTicks = now;
            if (completed > 0) _revision++;
            return completed;
        }

        public static int AdvanceProject(long projectId, double work)
        {
            if (work <= 0) return 0;
            int completed;
            string owner;
            lock (_lock)
            {
                if (!_projects.TryGetValue(projectId, out RoadProject p)) return 0;
                if (p.State != RoadProject.StateBuilding) return 0;
                owner = p.OwnerUsername;
                completed = AdvanceLocked(p, work, DateTime.UtcNow.Ticks);
            }
            // Only sections that are on disk count toward a federation.
            if (completed > 0 && SaveToDisk())
                Global.Federation.FederationService.RecordGuildWork("road", owner, completed);
            RefundFinished();
            return completed;
        }

        // The marker is saved with the progress: a replay finds it and lays nothing, and a failed save undoes both.
        // True also when no active project is left to lay the work on.
        public static bool ApplyWorkOnce(string marker, int siteTile, double work)
        {
            if (string.IsNullOrEmpty(marker) || work <= 0) return true;
            var worked = new List<(string Owner, int Segments)>();
            lock (_lock)
            {
                if (_appliedWork.ContainsKey(marker)) return true;
                var active = new List<RoadProject>();
                foreach (RoadProject p in _projects.Values)
                    if (p != null && p.SiteTile == siteTile && p.State == RoadProject.StateBuilding) active.Add(p);
                if (active.Count == 0) return true;
                var before = active.ConvertAll(p => p.ShallowClone());
                var segmentsBefore = new HashSet<string>(_segments.Keys, StringComparer.Ordinal);
                long now = DateTime.UtcNow.Ticks;
                double each = work / active.Count;
                foreach (RoadProject p in active)
                {
                    int n = AdvanceLocked(p, each, now);
                    if (n > 0) worked.Add((p.OwnerUsername, n));
                }
                _appliedWork[marker] = now;
                if (!SaveToDisk())
                {
                    foreach (RoadProject b in before) _projects[b.Id] = b;
                    foreach (string k in new List<string>(_segments.Keys)) if (!segmentsBefore.Contains(k)) _segments.Remove(k);
                    _appliedWork.Remove(marker);
                    _revision++;
                    return false;
                }
            }
            foreach ((string owner, int n) in worked) Global.Federation.FederationService.RecordGuildWork("road", owner, n);
            RefundFinished();
            return true;
        }

        internal static bool WorkAppliedForTest(string marker) { lock (_lock) return marker != null && _appliedWork.ContainsKey(marker); }

        // A segment someone else built first, or a price change mid-project, leaves escrow the project never spent.
        // The refund is owed in the same save that marks it spent, so a restart neither drops nor repeats it.
        internal static long RefundFinished()
        {
            var markers = new List<string>();
            long total = 0;
            lock (_lock)
            {
                var due = new List<(RoadProject P, int SpentBefore, string Marker)>();
                foreach (RoadProject p in _projects.Values)
                    if (p != null && p.State == RoadProject.StateComplete && p.EscrowSilver > p.EscrowSilverSpent)
                    {
                        int silver = p.EscrowSilver - p.EscrowSilverSpent;
                        string m = _owed.RecordLocked(new Transactions.KmhOwedPayout
                        {
                            Marker = $"road:{p.Id}:{p.CreatedUtcTicks}:finished", User = p.OwnerUsername, Silver = silver,
                            Note = $"roadworks project #{p.Id} finished - {silver} silver for segments it never built refunded",
                        });
                        due.Add((p, p.EscrowSilverSpent, m));
                        p.EscrowSilverSpent = p.EscrowSilver;
                        total += silver;
                    }
                if (due.Count == 0) return 0;
                _revision++;
                if (!SaveToDisk())
                {
                    foreach (var d in due) { d.P.EscrowSilverSpent = d.SpentBefore; _owed.ForgetLocked(d.Marker); }
                    _revision++;
                    return 0;
                }
                foreach (var d in due)
                {
                    if (d.Marker != null) markers.Add(d.Marker);
                    Diagnostics.ServerLog.Info($"Roadworks: project #{d.P.Id} finished; {d.P.EscrowSilver - d.SpentBefore} unspent escrow owed back to {d.P.OwnerUsername}.");
                }
            }
            foreach (string m in markers) _owed.Settle(m);
            return total;
        }

        // Boot, and the sweep: refunds written down and then interrupted, or refused by the vault.
        public static void RecoverOnBoot()
        {
            (int paid, int left) = _owed.Resume();
            if (paid > 0 || left > 0) Diagnostics.ServerLog.Warn($"Roadworks: recovery paid {paid} interrupted refund(s), {left} still owed.");
        }

        public static int RetryOwed() => _owed.Count == 0 ? 0 : _owed.Resume(quiet: true).paid;
        internal static int OwedCount => _owed.Count;

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropOwedFor(string user) { lock (_lock) return _owed.DropForLocked(user); }

        internal static int OwedCountForTest => _owed.Count;
        internal static (long Silver, int Units) UnpaidOwedForTest(string user) => _owed.UnpaidTotals(user);
        internal static Action<string> AtPayoutPhaseForTest { set => _owed.AtPhaseForTest = value; }

        // Roadworks is a Site archetype, so extensions observe it through SiteChanged rather than a parallel event.
        private static void RaiseSiteChanged(int tile, string owner, string reason)
        {
            try
            {
                Extensibility.KmhEventBus.Instance.RaiseSiteChanged(new KMH.Sdk.Server.Events.SiteChangedEvent
                { Tile = tile, OwnerUsername = owner ?? "", Reason = reason });
            }
            catch { }
        }

        public static void ClearForNewSeason()
        {
            lock (_lock) { _segments.Clear(); _projects.Clear(); _owed.ClearLocked(); _appliedWork.Clear(); _revision++; _nextProjectId = 1; }
            SaveToDisk();
        }

        public static bool HasProjects(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            lock (_lock)
                foreach (RoadProject p in _projects.Values)
                    if (string.Equals(p.OwnerUsername, username, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Single source for both the dry run and the commit, or the preview understates what is lost.
        internal static (List<long> ids, long burned) PurgeSelection(IEnumerable<RoadProject> projects, string username)
        {
            var ids = new List<long>(); long burned = 0;
            if (projects == null || string.IsNullOrEmpty(username)) return (ids, burned);
            foreach (RoadProject p in projects)
            {
                if (p == null || !string.Equals(p.OwnerUsername, username, StringComparison.OrdinalIgnoreCase)) continue;
                burned += UnspentEscrow(p);
                ids.Add(p.Id);
            }
            return (ids, burned);
        }

        // Anti-shelter: a save-reset burns reserved escrow with the vault. Completed roads stay - infrastructure.
        public static (int projects, long silverBurned) PurgeOwner(string username, bool dryRun)
        {
            if (string.IsNullOrEmpty(username)) return (0, 0);
            List<long> ids; long burned;
            lock (_lock)
            {
                (ids, burned) = PurgeSelection(_projects.Values, username);
                if (ids.Count > 0 && !dryRun)
                {
                    foreach (long id in ids) _projects.Remove(id);
                    _revision++;
                }
            }
            if (ids.Count > 0 && !dryRun)
            {
                SaveToDisk();
                Diagnostics.ServerLog.Warn($"Roadworks: purged {ids.Count} project(s) for {username}; {burned} reserved silver burned (completed roads kept).");
            }
            return (ids.Count, burned);
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.RoadworksFile, out RoadNetworkState s) || s == null) return;
            lock (_lock)
            {
                _segments.Clear(); _projects.Clear();
                _owed.LoadLocked(s.Owed);
                _appliedWork.Clear();
                if (s.AppliedWork != null) foreach (KeyValuePair<string, long> kv in s.AppliedWork) _appliedWork[kv.Key] = kv.Value;
                if (s.Segments != null)
                    foreach (RoadSegment seg in s.Segments)
                    {
                        string k = RoadKeys.For(seg?.A, seg?.B);
                        if (k != null) _segments[k] = seg;
                    }
                if (s.Projects != null)
                    foreach (RoadProject p in s.Projects) if (p != null && p.Id > 0) _projects[p.Id] = p;
                // A loaded revision is not this run's counter, so the shared segment list cannot be trusted across it.
                _revision = s.Revision;
                _sharedSegmentsRevision = -1;
                _nextProjectId = Math.Max(1, s.NextProjectId);
                foreach (RoadProject p in _projects.Values) if (p.Id >= _nextProjectId) _nextProjectId = p.Id + 1;
            }
            Diagnostics.ServerLog.Diag($"Roadworks: loaded {_segments.Count} road segment(s), {_projects.Count} project(s).");
        }

        // False means memory-only: a project's escrowed silver is then refundable twice, so callers must roll back.
        public static bool SaveToDisk()
        {
            RoadNetworkState s = new RoadNetworkState();
            long seq;
            lock (_lock)
            {
                s.Segments.AddRange(KmhDeepCopy.OfList(_segments.Values));
                s.Projects.AddRange(KmhDeepCopy.OfList(_projects.Values));
                s.Revision = _revision;
                s.NextProjectId = _nextProjectId;
                s.Owed = _owed.SnapshotLocked();
                long cutoff = DateTime.UtcNow.Ticks - AppliedWorkTtl;
                foreach (string k in new List<string>(_appliedWork.Keys)) if (_appliedWork[k] < cutoff) _appliedWork.Remove(k);
                s.AppliedWork = new Dictionary<string, long>(_appliedWork, StringComparer.Ordinal);
                seq = JsonFileStore.NextSequence();
            }
            return JsonFileStore.Save(KmhDataPaths.RoadworksFile, s, seq);
        }
    }
}
