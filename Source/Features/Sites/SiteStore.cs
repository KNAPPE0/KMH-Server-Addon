using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Sites
{
    // Server-authoritative custom-site ledger keyed by world tile.
    internal static class SiteStore
    {
        public const double XpPerCycle = 250.0;

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, SiteEntry> _byTile = new Dictionary<int, SiteEntry>();
        private static readonly Dictionary<string, long> _lastBuildUtc =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // owner -> last build ticks (cooldown)

        private static int CountOwnedLocked(string owner)
        {
            int n = 0;
            foreach (SiteEntry s in _byTile.Values)
                if (SiteOwnership.IsOwnedBy(s, owner)) n++;
            return n;
        }
        // Counts by the owner's CURRENT guild, so an ex-member's sites stop consuming the quota they left behind.
        private static int CountGuildOwnedLocked(string guild)
        {
            int n = 0;
            foreach (SiteEntry s in _byTile.Values)
                if (string.Equals(OwnerCurrentGuild(s), guild, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
        private static int CountWorkedLocked(string user)
        {
            int n = 0;
            foreach (SiteEntry s in _byTile.Values)
                if (s.Workers.Contains(user, StringComparer.OrdinalIgnoreCase)) n++;
            return n;
        }

        // Legacy and blocked account-workers do not count, so a site holding only those stays paused.
        private static int WorkerCount(SiteEntry s)
        {
            if (s.Workers == null || s.WorkerProgress == null) return 0;
            int n = 0;
            foreach (string w in s.Workers)
                if (s.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) && wp != null && wp.IsActivePawnWorker) n++;
            return n;
        }
        // A system or neutral location has nobody to pay, so it must never enter the reward cycle at all.
        private static bool Producing(SiteEntry s)
            => s != null && SiteOwnership.IsPlayerControlled(s) && WorkerCount(s) > 0;

        // A worker count -> raw speed factor (more workers = faster cycles). 0 workers = paused.
        private static double RawSpeed(int workerCount)
        {
            if (workerCount <= 0) return 0;
            if (workerCount == 1) return 1.0;
            return 1.0 + 0.5 * Math.Log(workerCount);
        }

        // EarnedLevel, not CurrentLevel: this drives real output, so it may only count XP the server awarded.
        private static double AvgSkillLevel(SiteEntry s)
        {
            if (s.WorkerProgress == null) return 0;
            double total = 0; int n = 0;
            foreach (WorkerProgressDto wp in s.WorkerProgress.Values)
                if (wp != null && wp.IsActivePawnWorker) { total += wp.EarnedLevel; n++; }
            return n == 0 ? 0 : total / n;
        }

        // Workers drive speed and skill drives output, each capped separately, so neither compounds the other.
        private static double SpeedFactor(SiteEntry s)
        {
            double raw = RawSpeed(WorkerCount(s));
            if (raw <= 0) return 0;
            double cap = s.TierMaxSpeedMultiplier > 0 ? s.TierMaxSpeedMultiplier : 3.0;
            return Math.Min(cap, Math.Max(1.0, raw));
        }

        internal static double OutputFactor(SiteEntry s)
        {
            if (!Producing(s)) return 0;
            double cap = s.TierMaxOutputMultiplier > 0 ? s.TierMaxOutputMultiplier : 2.0;
            double raw = 1.0 + AvgSkillLevel(s) / 20.0;   // +0..+100% over skill 0..20
            double skill = Math.Min(cap, Math.Max(1.0, raw));
            // Outside the tier cap on purpose, so damage and buildings both still change the result at any tier.
            return skill * (1.0 + SiteBuildings.ProductionBonus(s)) * SiteStability.OutputFactor(s.Stability);
        }

        private static double EffectiveCycleMs(SiteEntry s)
        {
            double sp = SpeedFactor(s);
            return sp <= 0 ? double.PositiveInfinity : s.BaseCycleTimeMs / sp;   // display path sanitizes
        }

        private static bool IsFinitePositive(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0 && v < 1e12;

        // Paced by client value and by amount (which the client can't fake).
        public static double CycleTimeMsForValue(float marketValue, int amount)
        {
            double perUnit = SitesConfig.Current.CycleMinutesPerRewardUnit;
            double minutes = 30.0 + marketValue / 5.0 + Math.Max(0, amount) * perUnit;
            minutes = Math.Min(240.0, Math.Max(30.0, minutes));
            return minutes * 60.0 * 1000.0;
        }

        public static int BuildCost(string ownerUsername, float marketValue, int amount, double tierCostMult = 1.0)
        {
            double mult = SitesConfig.Current.CustomSitePriceMultiplier;
            // Saturating, because an overflowing double->int cast would price a huge order at the floor.
            int raw = ToCost(marketValue * (double)amount * mult);
            // A client can under-report value but not amount, so amount sets a floor of its own.
            int throughputFloor = ToCost(Math.Max(0, amount) * (double)SitesConfig.Current.MinBuildCostPerUnit);
            int cost = Math.Max(500, Math.Max(raw, throughputFloor));
            if (tierCostMult > 0) cost = ToCost(cost * tierCostMult);
            string guild = Guilds.GuildStore.CurrentGuildOf(ownerUsername);
            double discount = Guilds.GuildStore.CustomSiteCostMultiplierFor(guild);
            return Math.Max(1, ToCost(cost * discount));
        }

        // Saturating double -> silver. NaN and negatives collapse to 0 so they can never read as "cheap".
        private static int ToCost(double v)
        {
            if (double.IsNaN(v) || v <= 0) return 0;
            double c = Math.Ceiling(v);
            return c >= int.MaxValue ? int.MaxValue : (int)c;
        }

        private static int MaxWorkersFor(string ownerUsername)
        {
            string guild = Guilds.GuildStore.CurrentGuildOf(ownerUsername);
            return 5 + Guilds.GuildStore.SiteMaxWorkersBonusFor(guild);
        }

        // Damage only ever arrives from an admin command or an extension, so the "when" stays the server's decision.
        public static (bool ok, string reason) AdjustStability(int tile, int delta, string why)
        {
            int now;
            string owner;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                now = SiteStability.Clamp(s.Stability + delta);
                if (now == s.Stability) return (false, $"Stability already {now}.");
                s.Stability = now;
                owner = s.OwnerUsername;
            }
            SaveToDisk();
            RaiseSite(tile, owner, "stability_changed");
            Diagnostics.ServerLog.Info($"Sites: tile {tile} stability -> {now} ({SiteStability.Describe(now)}){(string.IsNullOrEmpty(why) ? "" : " - " + why)}.");
            return (true, $"Tile {tile} stability is now {now} ({SiteStability.Describe(now)}).");
        }

        // Charged outside the lock, then re-checked inside it so two repairs cannot both pay for the same damage.
        public static (bool ok, string reason) RepairSite(string user, int tile)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");
            int before;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                if (!SiteOwnership.CanManage(s, user)) return (false, "That isn't your site.");
                before = SiteStability.Clamp(s.Stability);
            }
            if (before >= SiteStability.Healthy) return (false, "That site is already in full repair.");

            int cost = SiteStability.RepairCost(before, SitesConfig.Current.SiteRepairCostPerPoint);
            if (cost > 0 && !Treasury.TreasuryStore.WithdrawSilver(user, cost, note: $"site repair (tile {tile})"))
                return (false, $"Repairs cost {Util.SilverFmt.Format(cost)} - your treasury is short.");

            bool repaired = false;
            lock (_lock)
            {
                if (_byTile.TryGetValue(tile, out SiteEntry s) && s != null
                    && SiteOwnership.CanManage(s, user)
                    && SiteStability.Clamp(s.Stability) == before)
                {
                    s.Stability = SiteStability.Healthy;
                    repaired = true;
                }
            }
            if (!repaired)
            {
                if (cost > 0) Items.KmhPayloadEscrow.DeliverSilver(user, cost, $"site repair refunded - condition changed (tile {tile})", "site repair refund could not be credited");
                return (false, "The site's condition changed - nothing was charged.");
            }

            // The charge has already left the treasury, so a repair the disk never took is refunded, not kept.
            if (!SaveToDisk())
            {
                lock (_lock)
                {
                    if (_byTile.TryGetValue(tile, out SiteEntry s2) && s2 != null) s2.Stability = before;
                }
                if (cost > 0) Items.KmhPayloadEscrow.DeliverSilver(user, cost, "site repair could not be saved", "site repair refund could not be credited");
                return (false, "The server couldn't save that repair - your silver was returned. Try again shortly.");
            }
            RaiseSite(tile, user, "stability_changed");
            return (true, $"Site repaired to full for {Util.SilverFmt.Format(cost)}.");
        }

        // Starts with no stored goods, so capturing one moves no player value.
        public static (bool ok, string reason) EstablishOutpost(int tile, string template, string faction, long operationId)
        {
            if (tile < 0) return (false, "Pick a valid tile.");
            string t = SiteOutposts.NormalizeTemplate(template);
            if (!SiteOutposts.IsSpawnable(t)) return (false, "That outpost template cannot be established yet.");

            string entry = SiteOutposts.EntryStateFor(t);
            var site = new SiteEntry
            {
                Tile              = tile,
                OwnerKind         = SiteEntry.OwnerNeutral,
                ControllerFaction = faction ?? "",
                SiteName          = SiteOutposts.GenerateName(tile, t),
                OutpostTemplate   = t,
                OutpostState      = entry,
                EstablishedUtcTicks = DateTime.UtcNow.Ticks,
                OriginOperationId = operationId,
                Archetype         = SiteArchetypes.Custom,
                Stability         = 25,                       // derelict, but standing
                AccessMode        = SiteEntry.AccessPublic,
                ItemDefName       = "",
                BaseAmountPerCycle = 0,
                MaxWorkers        = 0,
                OwnerRewardDestination = SiteEntry.DestTreasury,
            };
            if (!SiteOutposts.IsConsistent(site, out string why)) return (false, why);

            lock (_lock)
            {
                if (_byTile.ContainsKey(tile)) return (false, "A site already exists on that tile.");
                _byTile[tile] = site;
            }
            SaveToDisk();
            RaiseSite(tile, "", "outpost_spawned");
            Diagnostics.ServerLog.Info($"Frontier: established {NameOf(site)} ({t}) on tile {tile}.");
            return (true, NameOf(site));
        }

        // Idempotent, so a resumed resolution that finds the target state already set replays safely.
        public static bool TryAdvanceOutpost(int tile, string expectedFrom, string to, long operationId, out string why)
        {
            why = null;
            long now = DateTime.UtcNow.Ticks;
            string display;

            // Resolved before our lock, since WorldStore holds its own.
            string winner = "";
            if (SiteOutposts.NormalizeState(to) == SiteEntry.OutpostClaimable && operationId > 0
                && World.WorldStore.TryGetContributions(operationId, out var wq, out var wf))
                winner = Frontier.FrontierCapture.ResolveEligible(wq, wf);

            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) { why = "no site on that tile"; return false; }
                string current = SiteOutposts.NormalizeState(s.OutpostState);
                switch (SiteOutposts.VerdictFor(current, expectedFrom, to))
                {
                    case SiteOutposts.Move.AlreadyThere: return true;   // a replay, not a failure
                    case SiteOutposts.Move.WrongFrom:
                        why = $"expected '{expectedFrom}' but the outpost is '{current}'"; return false;
                    case SiteOutposts.Move.Illegal:
                        why = $"'{current}' cannot become '{to}'"; return false;
                }

                s.OutpostState = SiteOutposts.NormalizeState(to);
                // An operation-less transition must not erase which operation created the place.
                if (operationId > 0) s.OriginOperationId = operationId;
                if (s.OutpostState == SiteEntry.OutpostClaimable)
                {
                    s.ClaimWindowEndsUtcTicks = now + TimeSpan.FromMinutes(SitesConfig.Current.OutpostClaimWindowMinutes).Ticks;
                    // Only a real answer overwrites: a resumed resolution must not erase the winner already recorded.
                    if (!string.IsNullOrEmpty(winner)) s.ClaimEligibleUsername = winner;
                }
                else if (s.OutpostState == SiteEntry.OutpostDormant)
                {
                    s.ClaimWindowEndsUtcTicks = 0;
                    s.ClaimEligibleUsername = "";
                }
                display = NameOf(s);
            }
            SaveToDisk();
            RaiseSite(tile, "", "outpost_" + SiteOutposts.NormalizeState(to));
            Diagnostics.ServerLog.Info($"Frontier: {display} on tile {tile} is now {SiteOutposts.NormalizeState(to)}.");
            return true;
        }

        // Tiles still holding a director slot: captured belongs to a player and dormant is asleep, so neither counts.
        public static List<int> SlotHoldingOutpostTiles()
        {
            var outp = new List<int>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                    if (s != null && SiteOutposts.IsOutpost(s) && SiteOutposts.IsContested(s.OutpostState)) outp.Add(s.Tile);
            return outp;
        }

        // Contested locations whose operation is already gone, which nothing else would ever advance again.
        public static List<int> OrphanedOutpostTiles(HashSet<int> tilesWithLiveOperations)
        {
            var outp = new List<int>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s == null || !SiteOutposts.IsOutpost(s)) continue;
                    // Derelict only, since Claimable expires on its own.
                    if (SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostDerelict) continue;
                    if (tilesWithLiveOperations != null && tilesWithLiveOperations.Contains(s.Tile)) continue;
                    outp.Add(s.Tile);
                }
            return outp;
        }

        // Oldest first, so the same tile is not woken twice while other sleeping ones wait.
        public static int FindReusableDormantTile(Func<int, bool> tileIsCool)
        {
            int best = -1; long bestAge = long.MaxValue;
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s == null || !SiteOutposts.IsOutpost(s)) continue;
                    if (SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostDormant) continue;
                    if (tileIsCool != null && !tileIsCool(s.Tile)) continue;
                    if (s.EstablishedUtcTicks < bestAge) { bestAge = s.EstablishedUtcTicks; best = s.Tile; }
                }
            return best;
        }

        // The last cycle's decisions are cleared; the tile, name and archetype stay, so the map keeps its history.
        public static (bool ok, string reason) ReviveDormantOutpost(int tile)
        {
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "no site on that tile");
                if (!SiteOutposts.IsOutpost(s)) return (false, "that site is not an outpost");
                if (SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostDormant) return (false, "that location is not dormant");
                s.OriginOperationId = 0;
                s.ClaimEligibleUsername = "";
                s.ClaimWindowEndsUtcTicks = 0;
                s.CapturedBy = "";
                s.Stability = 25;
            }
            if (!TryAdvanceOutpost(tile, SiteEntry.OutpostDormant, SiteEntry.OutpostDerelict, 0, out string why))
                return (false, why);
            return (true, "revived");
        }

        // Nothing else moves an outpost out of Claimable, so an unclaimed one would hold its director slot forever.
        public static List<int> ExpireClaimWindows(long nowTicks)
        {
            var due = new List<int>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s == null || SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostClaimable) continue;
                    if (s.ClaimWindowEndsUtcTicks > 0 && nowTicks >= s.ClaimWindowEndsUtcTicks) due.Add(s.Tile);
                }
            var moved = new List<int>();
            foreach (int tile in due)
                if (TryAdvanceOutpost(tile, SiteEntry.OutpostClaimable, SiteEntry.OutpostDormant, 0, out _))
                    moved.Add(tile);
            return moved;
        }

        // Stamped when it became claimable; the operation lookup is only a pre-1.3.0 fallback that dies on pruning.
        public static string EligibleClaimantFor(SiteEntry s)
        {
            if (s == null || SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostClaimable) return "";
            if (!string.IsNullOrEmpty(s.ClaimEligibleUsername)) return s.ClaimEligibleUsername;
            if (s.OriginOperationId <= 0) return "";
            if (!World.WorldStore.TryGetContributions(s.OriginOperationId, out var qty, out var firstUtc)) return "";
            return Frontier.FrontierCapture.ResolveEligible(qty, firstUtc);
        }

        public static (bool ok, string reason) ClaimOutpost(string user, int tile, bool forGuild)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");

            // Eligibility first, outside our lock.
            SiteEntry probe;
            lock (_lock) { if (!_byTile.TryGetValue(tile, out probe) || probe == null) return (false, "No site on that tile."); }
            string eligible = EligibleClaimantFor(probe);
            long now = DateTime.UtcNow.Ticks;
            string refusal = Frontier.FrontierCapture.RefusalFor(
                SiteOutposts.NormalizeState(probe.OutpostState), probe.ClaimWindowEndsUtcTicks, now, eligible, user);
            if (refusal != null) return (false, refusal);

            // Counted as an ordinary Site from here on, so it obeys the same cap the player is already held to.
            SitesConfig ccfg = SitesConfig.Current;
            if (!forGuild)
            {
                int owned;
                lock (_lock) owned = CountOwnedLocked(user);
                string capWhy = Frontier.FrontierCapture.CapRefusalFor(owned, ccfg.MaxSitesPerPlayer, false);
                if (capWhy != null) return (false, capWhy);
            }

            // Guild claims are validated against CURRENT membership and rank; the client never names the guild.
            string guild = "";
            if (forGuild)
            {
                guild = Guilds.GuildStore.CurrentGuildOf(user) ?? "";
                if (string.IsNullOrEmpty(guild)) return (false, "You are not in a guild.");
                string rank = Guilds.GuildStore.RankOf(user) ?? "";
                if (rank != Guilds.Dto.GuildMemberDto.RankOfficer && rank != Guilds.Dto.GuildMemberDto.RankOwner)
                    return (false, "Only a guild officer or owner can claim for the guild.");
                int gOwned;
                lock (_lock) gOwned = CountGuildOwnedLocked(guild);
                string gCapWhy = Frontier.FrontierCapture.CapRefusalFor(gOwned, ccfg.MaxSitesPerGuild, true);
                if (gCapWhy != null) return (false, gCapWhy);
            }

            string previous, display;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                // Re-check under the lock: the window may have closed or someone may have claimed it meanwhile.
                if (SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostClaimable)
                    return (false, "That location was just claimed.");

                previous = SiteOwnership.ControllerLabel(s);
                display  = NameOf(s);

                // Kept so a failed consistency check below can undo the whole transfer rather than half of it.
                string wasState = s.OutpostState, wasKind = s.OwnerKind, wasOwner = s.OwnerUsername,
                       wasGuild = s.ControllingGuild, wasFaction = s.ControllerFaction, wasCapturedBy = s.CapturedBy;
                long wasWindow = s.ClaimWindowEndsUtcTicks;

                s.OutpostState = SiteEntry.OutpostCaptured;
                s.ClaimWindowEndsUtcTicks = 0;
                s.CapturedBy = forGuild ? guild : user;

                if (forGuild) { s.OwnerKind = SiteEntry.OwnerGuildKind; s.ControllingGuild = guild; s.OwnerUsername = ""; }
                else          { s.OwnerKind = SiteEntry.OwnerPlayer;    s.OwnerUsername = user;     s.ControllingGuild = ""; }

                // System-only authority must not survive onto a player-owned site.
                s.ControllerFaction = "";
                // OwnerGuild and MaxWorkers stay unwritten: freezing them here would stop a later perk reaching this site.
                s.AccessMode        = NormalizeAccess(s.AccessMode);
                s.OwnerRewardDestination = NormalizeDest(s.OwnerRewardDestination);

                if (!SiteOutposts.IsConsistent(s, out string why))
                {
                    s.OutpostState = wasState; s.OwnerKind = wasKind; s.OwnerUsername = wasOwner;
                    s.ControllingGuild = wasGuild; s.ControllerFaction = wasFaction; s.CapturedBy = wasCapturedBy;
                    s.ClaimWindowEndsUtcTicks = wasWindow;
                    return (false, why);
                }
            }

            SaveToDisk();
            Extensibility.KmhEventBus.Instance.RaiseSiteChanged(new KMH.Sdk.Server.Events.SiteChangedEvent
            {
                Tile = tile, OwnerUsername = forGuild ? guild : user, Reason = "site_captured",
                PreviousController = previous, OperationId = probe.OriginOperationId,
            });
            // Credited to the player even for a guild claim, since a guild cannot earn a capture on its own.
            PlayerStats.PlayerStatsStore.BumpFrontierCaptures(user);
            Diagnostics.ServerLog.Info($"Frontier: {display} on tile {tile} captured by {(forGuild ? "guild " + guild : user)} (was {previous}).");
            return (true, $"{display} is now yours. It keeps its buildings, condition and history.");
        }

        // Charged outside the lock, then re-checked inside it so a slot cannot be taken twice.
        public static (bool ok, string reason) AddBuilding(string user, int tile, string kind)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");
            string k = (kind ?? "").Trim().ToLowerInvariant();

            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry pre) || pre == null) return (false, "No site on that tile.");
                if (!SiteOwnership.CanManage(pre, user)) return (false, "That isn't your site.");
                if (!SiteBuildings.CanAdd(pre, k, pre.OutputTier, out string why)) return (false, why);
            }

            int cost = Math.Max(0, SitesConfig.Current.SiteBuildingCostSilver);
            if (cost > 0 && !Treasury.TreasuryStore.WithdrawSilver(user, cost, note: $"site building ({k}, tile {tile})"))
                return (false, $"That building costs {Util.SilverFmt.Format(cost)} - your treasury is short.");

            bool added = false;
            lock (_lock)
            {
                if (_byTile.TryGetValue(tile, out SiteEntry s) && s != null
                    && SiteOwnership.CanManage(s, user)
                    && SiteBuildings.CanAdd(s, k, s.OutputTier, out _))
                {
                    s.Buildings.Add(new SiteBuilding { Kind = k, Level = 1, State = SiteBuilding.StateOperational });
                    added = true;
                }
            }
            if (!added)
            {
                // Lost the race, so hand the silver straight back rather than keeping it for nothing.
                if (cost > 0) Items.KmhPayloadEscrow.DeliverSilver(user, cost, $"site building refunded - slot was taken (tile {tile})", "site building refund could not be credited");
                return (false, "That slot was just taken - nothing was charged.");
            }

            // The charge has already left the treasury, so a building the disk never took is refunded, not kept.
            if (!SaveToDisk())
            {
                lock (_lock)
                {
                    if (_byTile.TryGetValue(tile, out SiteEntry s3) && s3?.Buildings != null && s3.Buildings.Count > 0)
                        s3.Buildings.RemoveAt(s3.Buildings.Count - 1);
                }
                if (cost > 0) Items.KmhPayloadEscrow.DeliverSilver(user, cost, "site building could not be saved", "site building refund could not be credited");
                return (false, "The server couldn't save that building - your silver was returned. Try again shortly.");
            }
            RaiseSite(tile, user, "building_added");
            return (true, $"Built a {k} building for {Util.SilverFmt.Format(cost)}.");
        }

        // A slot index alone is unsafe: a co-owner can reorder the list while the confirmation prompt is open.
        public static (bool ok, string reason) RemoveBuilding(string user, int tile, int index,
                                                             string expectedKind = null, string expectedState = null)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                if (!SiteOwnership.CanManage(s, user)) return (false, "That isn't your site.");
                if (s.Buildings == null || index < 0 || index >= s.Buildings.Count) return (false, "No such building.");

                SiteBuilding target = s.Buildings[index];
                if (!MatchesExpected(target, expectedKind, expectedState))
                    return (false, "That building slot changed while you were confirming - reopen the site and try again.");

                s.Buildings.RemoveAt(index);
            }
            SaveToDisk();
            RaiseSite(tile, user, "building_removed");
            return (true, "Building demolished. Nothing is refunded.");
        }

        // An older client sends no expectation and keeps the previous behaviour; anything it does name must match.
        internal static bool MatchesExpected(SiteBuilding b, string expectedKind, string expectedState)
        {
            if (b == null) return false;
            if (!string.IsNullOrEmpty(expectedKind)
                && !string.Equals(b.Kind, expectedKind, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(expectedState)
                && !string.Equals(b.State, expectedState, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // Order is fixed: a preset's skill, then the classified family's, then the legacy guess for an uncatalogued server.
        internal static string SkillFor(string archetype, string family, string legacySkill)
        {
            string preset = SiteArchetypes.DefaultSkillFor(SiteArchetypes.Normalize(archetype));
            if (!string.IsNullOrEmpty(preset)) return preset;
            string byFamily = SiteOutputFamilies.SkillFor(family);
            if (!string.IsNullOrEmpty(byFamily)) return byFamily;
            return legacySkill;
        }

        internal static int MaxWorkersLive(SiteEntry s)
            => s == null ? 0 : MaxWorkersFor(SiteOwnership.PayoutAccount(s)) + SiteBuildings.HousingBonus(s);

        // Perk lookups need the guild now; SiteEntry.OwnerGuild stays the creation-time guild for access and quota.
        internal static string OwnerCurrentGuild(SiteEntry s)
            => SiteOwnership.OwningGuildLive(s);

        public static (bool ok, string reason) Build(string owner, int tile, string itemDefName,
            int amountPerCycle, float marketValuePerUnit, string accessMode, int ownerTaxPercent,
            string ownerDestination, int marketplaceUnitPrice, string archetype)
        {
            SitesConfig cfg = SitesConfig.Current;
            if (string.IsNullOrEmpty(owner)) return (false, "No owner.");
            if (!cfg.AllowCustomSites) return (false, "Custom sites are disabled on this server.");
            if (tile < 0) return (false, "Pick a valid tile.");
            if (string.IsNullOrEmpty(itemDefName)) return (false, "Pick an item to produce.");
            if (amountPerCycle <= 0) return (false, "Amount per cycle must be > 0.");

            if (!PlanOutput(owner, itemDefName, amountPerCycle, marketValuePerUnit, archetype, tile, out OutputPlan plan, out string planWhy))
                return (false, planWhy);
            SiteOutputClass cls = plan.Cls;
            string archId       = plan.ArchId;
            string family       = plan.Family;
            double archCostMult = plan.ArchCostMultiplier;
            marketValuePerUnit  = plan.MarketValuePerUnit;
            ownerTaxPercent     = Math.Max(0, Math.Min(50, ownerTaxPercent));

            string ownerGuild = Guilds.GuildStore.CurrentGuildOf(owner) ?? "";
            lock (_lock)
            {
                if (_byTile.ContainsKey(tile)) return (false, "A site already exists on that tile.");
                if (cfg.MaxSitesServerWide > 0 && _byTile.Count >= cfg.MaxSitesServerWide)
                    return (false, "The server has reached its site limit.");
                if (cfg.MaxSitesPerPlayer > 0 && CountOwnedLocked(owner) >= cfg.MaxSitesPerPlayer)
                    return (false, $"You already own the maximum of {cfg.MaxSitesPerPlayer} site(s).");
                if (cfg.MaxSitesPerGuild > 0 && !string.IsNullOrEmpty(ownerGuild) && CountGuildOwnedLocked(ownerGuild) >= cfg.MaxSitesPerGuild)
                    return (false, $"Your guild has reached its {cfg.MaxSitesPerGuild}-site limit.");
                if (cfg.BuildCooldownMinutes > 0 && _lastBuildUtc.TryGetValue(owner, out long last)
                    && DateTime.UtcNow.Ticks - last < TimeSpan.FromMinutes(cfg.BuildCooldownMinutes).Ticks)
                    return (false, $"Build cooldown - wait a bit before building another site (every {cfg.BuildCooldownMinutes} min).");
            }
            return BuildValidated(owner, tile, itemDefName, amountPerCycle, marketValuePerUnit, accessMode,
                                  ownerTaxPercent, ownerDestination, marketplaceUnitPrice, plan, ownerGuild);
        }

        // Prices through the same plan and BuildCost call the real build makes, so the quote cannot drift from it.
        public static Dto.SiteBuildQuote QuoteBuild(string user, string itemDefName, int amountPerCycle,
                                                    float marketValuePerUnit, string archetype, int tile)
        {
            var q = new Dto.SiteBuildQuote { ItemDefName = itemDefName ?? "", Amount = amountPerCycle, Archetype = archetype ?? "" };
            if (string.IsNullOrEmpty(user))         { q.Reason = "No caller."; return q; }
            if (string.IsNullOrWhiteSpace(itemDefName)) { q.Reason = "Pick an item to produce."; return q; }
            if (amountPerCycle <= 0)                { q.Reason = "Amount per cycle must be > 0."; return q; }

            if (!PlanOutput(user, itemDefName, amountPerCycle, marketValuePerUnit, archetype, tile,
                            out OutputPlan plan, out string why, quiet: true))
            { q.Reason = why ?? "That output can't be produced here."; return q; }

            // Archetype stays verbatim because the client matches replies to requests on it.
            q.Ok           = true;
            q.Family       = plan.Family;
            q.MarketValuePerUnit = plan.MarketValuePerUnit;
            q.CostMultiplier     = plan.Cls.CostMultiplier * plan.ArchCostMultiplier;
            q.Cost         = BuildCost(user, plan.MarketValuePerUnit, amountPerCycle, q.CostMultiplier);
            q.CycleMinutes = (int)Math.Round(CycleTimeMsForValue(plan.MarketValuePerUnit, amountPerCycle)
                                             * plan.Cls.CycleMultiplier / 60000.0);
            q.MaxAmount    = Math.Max(1, Math.Min(SitesConfig.Current.CustomSiteMaxRewardAmount, plan.Cls.MaxAmount));
            q.Balance      = Treasury.TreasuryStore.GetPersonalSilver(user);
            q.Affordable   = q.Balance >= q.Cost;
            return q;
        }

        // Shared so building a site and configuring a captured one cannot drift apart.
        internal sealed class OutputPlan
        {
            public SiteOutputClass Cls;
            public string ArchId = SiteArchetypes.Custom;
            public string Family = SiteOutputFamilies.Unknown;
            public double ArchCostMultiplier = 1.0;
            public float  MarketValuePerUnit;
        }

        // quiet suppresses refusal logging, because a quote runs this while the player is still typing.
        private static bool PlanOutput(string owner, string itemDefName, int amountPerCycle, float marketValuePerUnit,
                                       string archetype, int tile, out OutputPlan plan, out string why,
                                       bool quiet = false)
        {
            plan = null; why = null;
            SitesConfig cfg = SitesConfig.Current;

            // Unknown, rare and tech items land in a disabled tier, so a site cannot print gear.
            SiteOutputClass cls;
            int tierMaxAmount;
            if (cfg.UseSiteOutputTiers)
            {
                // Resolve the human label server-side so owners can allow/block by exact label, not just raw defName.
                string label = Features.ItemLabels.ItemLabelCache.LabelFor(itemDefName);
                cls = SiteOutputRules.Classify(itemDefName, label);
                if (!cls.IsAllowed)
                {
                    if (!quiet) Diagnostics.ServerLog.Info($"Sites: {owner} output '{itemDefName}' ('{label}') rejected - {cls.BlockReason} (tier {cls.TierNumber} {cls.TierName}).");
                    why = cls.BlockReason; return false;
                }
                tierMaxAmount = Math.Min(cfg.CustomSiteMaxRewardAmount, cls.MaxAmount);
            }
            else
            {
                // Turning tiers off must not silently reopen all-def output, so the safety gate stays on by default.
                if (!cfg.AllowUnsafeLegacySiteOutputs
                    && !Items.KmhItemSafety.IsAllowedSiteOutput(itemDefName, out string outReason))
                { why = outReason; return false; }
                cls = new SiteOutputClass
                {
                    IsAllowed = true, TierNumber = 1, TierName = "Basic", RelevantSkill = DetermineRelevantSkill(itemDefName),
                    MaxAmount = cfg.CustomSiteMaxRewardAmount, CostMultiplier = 1.0, CycleMultiplier = 1.0,
                    MaxSpeedMultiplier = 3.0, MaxOutputMultiplier = 2.0,
                };
                tierMaxAmount = cfg.CustomSiteMaxRewardAmount;
            }

            // Rejected rather than clamped, so a typed value beating the UI cap cannot quietly succeed at a lower amount.
            if (amountPerCycle > tierMaxAmount)
            {
                if (!quiet) Diagnostics.ServerLog.Warn($"Sites: {owner} REJECTED - amount {amountPerCycle} > max {tierMaxAmount} for '{itemDefName}' (tier {cls.TierNumber} {cls.TierName}, tile {tile}).");
                why = $"Site amount exceeds max for this item tier - {cls.TierName} allows up to {tierMaxAmount} per cycle.";
                return false;
            }

            // Uses the same call the picker previewed with, and is skipped entirely until a catalog exists.
            string archId = SiteArchetypes.Normalize(archetype);
            string family = SiteOutputFamilies.Unknown;
            double archCostMult = 1.0;
            if (cfg.ArchetypesEnabled && SiteCatalogStore.HasCatalog)
            {
                if (archId == SiteArchetypes.Custom && !cfg.AllowCustomArchetype)
                { why = "Custom sites are disabled on this server - pick one of the site types."; return false; }

                Dto.SiteOutputMetadata meta = SiteCatalogStore.Lookup(itemDefName);
                family = meta?.Family ?? SiteOutputFamilies.Unknown;
                if (!SiteArchetypeRegistry.AcceptsOutput(archId, meta))
                {
                    SiteArchetypeDef def = SiteArchetypeRegistry.Get(archId);
                    why = family == SiteOutputFamilies.Unknown
                        ? $"KMH has not classified '{itemDefName}' yet, so no site type can be built on it."
                        : $"A {def.DisplayName} cannot produce {SiteOutputFamilies.DisplayName(family).ToLowerInvariant()} items.";
                    if (!quiet) Diagnostics.ServerLog.Info($"Sites: {owner} output '{itemDefName}' rejected - archetype '{archId}' does not accept family '{family}'.");
                    return false;
                }
                archCostMult = SiteArchetypeRegistry.Get(archId).CostMultiplier;
            }

            // Refused outright without a trusted value: the client's own figure under-reported bought a cheaper, faster, richer site.
            marketValuePerUnit = Items.KmhItemSafety.TrustedMarketValueOrZero(itemDefName, marketValuePerUnit, out bool valueSuspicious);
            if (valueSuspicious && !quiet)
                Diagnostics.ServerLog.Warn($"Sites: {owner} reported an off market value for {itemDefName} (tile {tile}) - using the trusted catalog value.");
            if (marketValuePerUnit <= 0f)
            {
                why = $"KMH has no trusted market value for '{itemDefName}' yet, so it cannot price a site on it. "
                    + "An admin can set one with 'kmh catalog <def> <value>', or it appears once the item catalog has been received.";
                if (!quiet) Diagnostics.ServerLog.Info($"Sites: {owner} build on '{itemDefName}' (tile {tile}) refused - no trusted market value.");
                return false;
            }

            plan = new OutputPlan
            {
                Cls = cls, ArchId = archId, Family = family,
                ArchCostMultiplier = archCostMult, MarketValuePerUnit = marketValuePerUnit,
            };
            return true;
        }

        // Caller already holds _lock, so guild membership arrives from the precheck rather than re-reading GuildStore.
        private static string CommitRefusalLocked(string owner, int tile, SitesConfig cfg, string ownerGuild)
        {
            if (_byTile.ContainsKey(tile)) return "Another site was just built on that tile";
            if (cfg.MaxSitesServerWide > 0 && _byTile.Count >= cfg.MaxSitesServerWide)
                return "The server reached its site limit";
            if (cfg.MaxSitesPerPlayer > 0 && CountOwnedLocked(owner) >= cfg.MaxSitesPerPlayer)
                return $"You already own the maximum of {cfg.MaxSitesPerPlayer} site(s)";
            if (cfg.MaxSitesPerGuild > 0 && !string.IsNullOrEmpty(ownerGuild)
                && CountGuildOwnedLocked(ownerGuild) >= cfg.MaxSitesPerGuild)
                return $"Your guild reached its {cfg.MaxSitesPerGuild}-site limit";
            if (cfg.BuildCooldownMinutes > 0 && _lastBuildUtc.TryGetValue(owner, out long last)
                && DateTime.UtcNow.Ticks - last < TimeSpan.FromMinutes(cfg.BuildCooldownMinutes).Ticks)
                return "Build cooldown";
            return null;
        }

        // One-time setup for something captured rather than designed: the same output rules as Build, no build cost.
        public static (bool ok, string reason) ConfigureCaptured(string user, int tile, string itemDefName,
            int amountPerCycle, float marketValuePerUnit, string accessMode, int ownerTaxPercent,
            string ownerDestination, int marketplaceUnitPrice, string archetype)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");
            if (string.IsNullOrEmpty(itemDefName)) return (false, "Pick an item to produce.");
            if (amountPerCycle <= 0) return (false, "Amount per cycle must be > 0.");

            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry pre) || pre == null) return (false, "No site on that tile.");
                if (!SiteOutposts.IsOutpost(pre)) return (false, "That site is not a Frontier outpost.");
                if (SiteOutposts.NormalizeState(pre.OutpostState) != SiteEntry.OutpostCaptured)
                    return (false, "Only a captured outpost can be set up.");
                if (!SiteOwnership.CanManage(pre, user)) return (false, "That isn't your site.");
                // One-time: after this it is an ordinary Site, changed through the ordinary Site controls.
                if (!string.IsNullOrEmpty(pre.ItemDefName))
                    return (false, "That outpost is already set up.");
            }

            if (!PlanOutput(user, itemDefName, amountPerCycle, marketValuePerUnit, archetype, tile, out OutputPlan plan, out string why))
                return (false, why);

            // Re-asked because classification above released the lock, and membership can change in that window.
            if (!CanManageTile(tile, user)) return (false, "That isn't your site.");

            string display;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                // Re-checked under the lock: the two reads are separated by the classification above.
                if (SiteOutposts.NormalizeState(s.OutpostState) != SiteEntry.OutpostCaptured
                    || !string.IsNullOrEmpty(s.ItemDefName)) return (false, "That outpost is already set up.");

                s.ItemDefName        = itemDefName;
                s.BaseAmountPerCycle = amountPerCycle;
                s.MarketValuePerUnit = plan.MarketValuePerUnit;
                s.BaseCycleTimeMs    = CycleTimeMsForValue(plan.MarketValuePerUnit, amountPerCycle) * plan.Cls.CycleMultiplier;
                s.AccessMode         = NormalizeAccess(accessMode);
                s.OwnerTaxPercent    = Math.Max(0, Math.Min(50, ownerTaxPercent));
                // MaxWorkers is derived live: freezing it here means a perk bought later never reaches this site.
                s.OwnerRewardDestination = NormalizeDest(ownerDestination);
                s.MarketplaceUnitPrice   = Math.Max(1, marketplaceUnitPrice);
                s.Archetype          = plan.ArchId;
                s.RelevantSkillDef   = SkillFor(plan.ArchId, plan.Family, plan.Cls.RelevantSkill);
                s.OutputTier             = plan.Cls.TierNumber;
                s.TierMaxSpeedMultiplier = plan.Cls.MaxSpeedMultiplier;
                s.TierMaxOutputMultiplier= plan.Cls.MaxOutputMultiplier;
                s.LastRewardUtcTicks = DateTime.UtcNow.Ticks;
                display = NameOf(s);
            }
            SaveToDisk();
            RaiseSite(tile, user, "outpost_configured");
            Diagnostics.ServerLog.Info($"Frontier: {user} set up captured outpost {display} (tile {tile}) to produce {amountPerCycle}x {itemDefName}.");
            return (true, $"{display} is now producing {amountPerCycle}x {itemDefName} per cycle.");
        }

        // The half of Build that commits, once every rule has already passed.
        private static (bool ok, string reason) BuildValidated(string owner, int tile, string itemDefName,
            int amountPerCycle, float marketValuePerUnit, string accessMode, int ownerTaxPercent,
            string ownerDestination, int marketplaceUnitPrice, OutputPlan plan, string ownerGuild)
        {
            SitesConfig cfg = SitesConfig.Current;
            SiteOutputClass cls = plan.Cls;
            string archId = plan.ArchId, family = plan.Family;
            double archCostMult = plan.ArchCostMultiplier;

            // Folded into the tier multiplier so BuildCost's floor and rounding see the real number.
            int cost = BuildCost(owner, marketValuePerUnit, amountPerCycle, cls.CostMultiplier * archCostMult);
            if (!Treasury.TreasuryStore.WithdrawSilver(owner, cost, note: $"custom site build (tile {tile})"))
                return (false, $"Build costs {Util.SilverFmt.Format(cost)} - your treasury is short.");

            SiteEntry site = new SiteEntry
            {
                Tile               = tile,
                OwnerUsername      = owner,
                OwnerGuild         = ownerGuild,
                ItemDefName        = itemDefName,
                BaseAmountPerCycle = amountPerCycle,
                MarketValuePerUnit = marketValuePerUnit,
                BaseCycleTimeMs    = CycleTimeMsForValue(marketValuePerUnit, amountPerCycle) * cls.CycleMultiplier,
                AccessMode         = NormalizeAccess(accessMode),
                OwnerTaxPercent    = ownerTaxPercent,
                MaxWorkers         = MaxWorkersFor(owner),
                OwnerRewardDestination = NormalizeDest(ownerDestination),
                MarketplaceUnitPrice   = Math.Max(1, marketplaceUnitPrice),
                Archetype          = archId,
                RelevantSkillDef   = SkillFor(archId, family, cls.RelevantSkill),
                OutputTier             = cls.TierNumber,
                TierMaxSpeedMultiplier = cls.MaxSpeedMultiplier,
                TierMaxOutputMultiplier= cls.MaxOutputMultiplier,
                LastRewardUtcTicks = DateTime.UtcNow.Ticks,
            };
            if (cfg.AutoAddOwnerAsSiteWorker)
            {
                site.Workers.Add(owner);
                site.WorkerProgress[owner] = new WorkerProgressDto { JoinedUtcTicks = DateTime.UtcNow.Ticks, Destination = SiteEntry.DestTreasury };
            }
            // Re-checked in the same lock that inserts, or two builds on different tiles could both pass one cap.
            string refusal;
            lock (_lock)
            {
                refusal = CommitRefusalLocked(owner, tile, cfg, ownerGuild);
                if (refusal == null) { _byTile[tile] = site; _lastBuildUtc[owner] = DateTime.UtcNow.Ticks; }
            }
            if (refusal != null)
            {
                Items.KmhPayloadEscrow.DeliverSilver(owner, cost, $"custom site build refund (tile {tile}: {refusal})", "site build refund could not be credited");
                return (false, refusal + " - your build cost was refunded.");
            }
            // The build cost has already left the treasury, so a site the disk never took is refunded, not kept.
            if (!SaveToDisk())
            {
                lock (_lock) { _byTile.Remove(tile); _lastBuildUtc.Remove(owner); }
                Items.KmhPayloadEscrow.DeliverSilver(owner, cost, "site build could not be saved", "site build refund could not be credited");
                return (false, "The server couldn't save that site - your build cost was refunded. Try again shortly.");
            }
            RaiseSite(tile, owner, "built");
            PlayerStats.PlayerStatsStore.BumpSitesBuilt(owner);
            return (true, $"Built a {itemDefName} site (cost {Util.SilverFmt.Format(cost)}).");
        }

        public const string BlockedPawnAway = "pawn away from site";

        // present=false keeps the worker assigned but stops production; changed=false is a silent re-validation.
        public static (bool ok, string reason, bool changed) JoinWorker(string username, int tile, int baseSkillLevel,
            string pawnName = "", int pawnLoadId = -1, bool present = true)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.", false);
            // A headless server cannot verify the reported pawn, so skill and name stay advisory.
            if (SitesConfig.Current.RequirePawnSiteWorkers && pawnLoadId <= 0)
                return (false, "This site needs a real colonist. Send a caravan to the site tile, then assign a pawn.", false);
            baseSkillLevel = Items.KmhItemSafety.ValidateClientSkill(baseSkillLevel, out _);
            pawnName = Util.KmhSafe.Cap(pawnName ?? "", 48);
            int workerCap = SitesConfig.Current.MaxWorkerSitesPerPlayer;
            string owner; bool refreshed = false, changed = true;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.", false);
                bool isOwner = SiteOwnership.CanManage(s, username);
                if (!isOwner && !CanAccess(s, username)) return (false, "This site isn't open to you.", false);

                if (s.WorkerProgress.TryGetValue(username, out WorkerProgressDto existing) && existing != null)
                {
                    string newBlocked = present ? "" : BlockedPawnAway;
                    changed = existing.PawnLoadId != pawnLoadId
                              || !string.Equals(existing.BlockedReason ?? "", newBlocked, StringComparison.Ordinal)
                              || existing.Legacy;
                    // Tracks the current pawn rather than keeping a dead one's peak; server XP still drives progression.
                    existing.BaseSkillLevel   = baseSkillLevel;
                    existing.PawnName         = pawnName;
                    existing.PawnLoadId       = pawnLoadId;
                    existing.LastValidatedUtc = DateTime.UtcNow.Ticks;
                    existing.Legacy           = false;   // a real pawn now works it
                    existing.BlockedReason    = newBlocked;
                    refreshed = true;
                    owner = s.OwnerUsername;
                }
                else
                {
                    int maxWorkers = MaxWorkersLive(s);
                if (WorkerCount(s) >= maxWorkers) return (false, $"This site is full ({maxWorkers} workers).", false);
                    if (workerCap > 0 && CountWorkedLocked(username) >= workerCap) return (false, $"You're at your {workerCap}-site work limit.", false);
                    s.Workers.Add(username);
                    s.WorkerProgress[username] = new WorkerProgressDto
                    {
                        JoinedUtcTicks   = DateTime.UtcNow.Ticks,
                        BaseSkillLevel   = baseSkillLevel,
                        Destination      = SiteEntry.DestTreasury,
                        PawnName         = pawnName,
                        PawnLoadId       = pawnLoadId,
                        LastValidatedUtc = DateTime.UtcNow.Ticks,
                        BlockedReason    = present ? "" : BlockedPawnAway,
                    };
                    owner = s.OwnerUsername;
                }
            }
            if (changed)
            {
                SaveToDisk();
                RaiseSite(tile, owner, refreshed ? "worker_refreshed" : "worker_joined");
            }
            string msg = refreshed
                ? (present
                    ? (string.IsNullOrEmpty(pawnName) ? "Refreshed your site worker." : $"Assigned {pawnName} to the site.")
                    : $"{(string.IsNullOrEmpty(pawnName) ? "Your worker" : pawnName)} is away - site work paused until a pawn is back at the site.")
                : (string.IsNullOrEmpty(pawnName) ? "Joined the site as a worker." : $"{pawnName} joined the site as a worker.");
            return (true, msg, changed);
        }

        public static (bool ok, string reason) LeaveWorker(string username, int tile)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.");
            string owner;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                int removed = s.Workers.RemoveAll(w => string.Equals(w, username, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return (false, "You don't work here.");
                s.WorkerProgress.Remove(username);
                owner = s.OwnerUsername;
            }
            SaveToDisk();
            RaiseSite(tile, owner, "worker_left");
            return (true, "Left the site.");
        }

        public const string ScopeMine = "mine";   // the caller's own worker share
        public const string ScopeSite = "site";   // the site's own output

        internal enum DestTarget { None, WorkerShare, SiteOutput }

        // Kept pure so the precedence can be argued with a test.
        internal static DestTarget ResolveDestTarget(string scope, bool isWorker, bool owns, bool guildOwned,
                                                     bool mayManage, string dest, out string refusal)
        {
            refusal = null;
            scope = (scope ?? "").Trim().ToLowerInvariant();

            if (scope == ScopeMine)
            {
                if (!isWorker) { refusal = "You aren't working this site."; return DestTarget.None; }
                return DestTarget.WorkerShare;
            }

            if (scope != ScopeSite && isWorker) return DestTarget.WorkerShare;

            if (owns) return DestTarget.SiteOutput;
            if (guildOwned && mayManage)
            {
                // Guild-owned output may only rest somewhere the guild owns.
                if (dest != SiteEntry.DestStorage && dest != SiteEntry.DestTreasury)
                { refusal = "A guild site's own output can go to its storage or the guild vault."; return DestTarget.None; }
                return DestTarget.SiteOutput;
            }
            refusal = "You can't direct this site's own output.";
            return DestTarget.None;
        }

        // An explicit scope is what lets an owner who also works the site reach the site's own output.
        public static (bool ok, string reason) SetDestination(string username, int tile, string destination,
                                                             string scope = null)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.");
            string dest = NormalizeDest(destination);
            scope = (scope ?? "").Trim().ToLowerInvariant();
            // Resolved before our lock: CanManage reads GuildStore, which holds its own.
            bool mayManage = CanManageTile(tile, username);
            string what;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                bool isWorker = s.WorkerProgress.TryGetValue(username, out WorkerProgressDto wp) && wp != null;

                DestTarget target = ResolveDestTarget(scope, isWorker, SiteOwnership.IsOwnedBy(s, username),
                                                      SiteOwnership.IsGuildOwned(s), mayManage, dest, out string refusal);
                if (target == DestTarget.None) return (false, refusal);

                if (target == DestTarget.WorkerShare) { wp.Destination = dest; what = "Your share"; }
                else                                  { s.OwnerRewardDestination = dest; what = "This site's output"; }
            }
            SaveToDisk();
            return (true, $"{what} now goes to {dest}.");
        }

        public static (bool ok, string reason) Cancel(string owner, int tile)
        {
            if (string.IsNullOrEmpty(owner)) return (false, "No user.");
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                if (!SiteOwnership.CanManage(s, owner)) return (false, "You can't manage this site.");
                _byTile.Remove(tile);
            }
            SaveToDisk();
            RaiseSite(tile, owner, "removed");
            return (true, "Site removed.");
        }

        private static void RaiseSite(int tile, string owner, string reason)
            => Extensibility.KmhEventBus.Instance.RaiseSiteChanged(
                new KMH.Sdk.Server.Events.SiteChangedEvent { Tile = tile, OwnerUsername = owner ?? "", Reason = reason });

        // Road progress is labour, so it deliberately avoids OutputFactor's tier and building multipliers.
        internal const double RoadworkBaseWorkPerWorker = 1.0;
        internal const double RoadworkWorkPerSkillLevel = 0.25;

        // The project test is load-bearing: with no project the diverted work is discarded downstream.
        internal static bool DivertsToRoadwork(SiteEntry s, HashSet<int> tilesWithActiveProjects)
            => s != null
            && SiteArchetypes.Normalize(s.Archetype) == SiteArchetypes.Roadworks
            && tilesWithActiveProjects != null
            && tilesWithActiveProjects.Contains(s.Tile)
            && RoadworkPerCycle(s) > 0;

        internal static double RoadworkPerCycle(SiteEntry s)
        {
            if (s == null || s.Workers == null) return 0;
            double crew = 0;
            foreach (string w in s.Workers)
            {
                if (!s.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) || wp == null) continue;
                if (!wp.IsActivePawnWorker) continue;
                crew += RoadworkBaseWorkPerWorker + wp.EarnedLevel * RoadworkWorkPerSkillLevel;
            }
            return crew <= 0 ? 0 : crew * SiteStability.OutputFactor(s.Stability);
        }

        // Runs outside the site lock because RoadworksStore takes its own.
        private static int ApplyRoadwork(List<(int Tile, double Work)> roadWork)
        {
            int completed = 0;
            foreach ((int tile, double work) in roadWork)
            {
                List<long> ids = Roadworks.RoadworksStore.ActiveProjectIdsForSite(tile);
                if (ids.Count == 0) continue;              // finished or cancelled between planning and here
                double each = work / ids.Count;
                if (each <= 0) continue;
                foreach (long id in ids) completed += Roadworks.RoadworksStore.AdvanceProject(id, each);
            }
            return completed;
        }

        // Returns the usernames whose treasury changed, so the sweeper can push them.
        public static HashSet<string> RunRewardCycle()
        {
            HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long now = DateTime.UtcNow.Ticks;
            double xpMult = SitesConfig.Current.WorkerXpMultiplier * World.WorldStore.WorkerXpMultiplier();

            // Planned under the lock so a concurrent join cannot mutate workers mid-enumeration.
            List<Delivery> plan = new List<Delivery>();
            // What a failed write has to undo: the timer that gates the next payout, and the XP that rode with it.
            List<(SiteEntry Site, long PrevTicks, double XpDelta)> advanced = new List<(SiteEntry, long, double)>();
            List<(int Tile, double Work)> roadWork = new List<(int, double)>();
            HashSet<int> roadProjectTiles = Roadworks.RoadworksStore.TilesWithActiveProjects();
            lock (_lock)
            {
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s.BlockedOutput) continue;
                    double cycleMs = EffectiveCycleMs(s);
                    if (!IsFinitePositive(cycleMs)) continue;   // paused, so leave the timer alone
                    double elapsedMs = (now - s.LastRewardUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
                    if (elapsedMs < cycleMs) continue;
                    long prevRewardTicks = s.LastRewardUtcTicks;
                    s.LastRewardUtcTicks = now;

                    double guildXpMult = Guilds.GuildStore.WorkerXpMultiplierFor(OwnerCurrentGuild(s));
                    advanced.Add((s, prevRewardTicks, XpPerCycle * xpMult * guildXpMult));
                    foreach (string w in s.Workers)
                    {
                        if (!s.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) || wp == null || !wp.IsActivePawnWorker) continue;
                        wp.Xp += XpPerCycle * xpMult * guildXpMult;
                        wp.CyclesCompleted += 1;
                    }

                    if (DivertsToRoadwork(s, roadProjectTiles))
                    {
                        roadWork.Add((s.Tile, RoadworkPerCycle(s)));
                        continue;
                    }

                    int totalProduced = (int)Math.Ceiling(s.BaseAmountPerCycle * OutputFactor(s));
                    if (totalProduced <= 0) continue;

                    // A guild site's owner is the guild and has no username, so the share cannot be keyed by one.
                    bool guildOwned    = SiteOwnership.IsGuildOwned(s);
                    string ownerAcct   = SiteOwnership.ProductionAccount(s);
                    bool hasOwner      = !string.IsNullOrEmpty(ownerAcct);

                    List<string> workers = new List<string>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!guildOwned && hasOwner) seen.Add(ownerAcct);   // an owner working their own site takes one slot
                    foreach (string w in s.Workers)
                        if (s.WorkerProgress.TryGetValue(w, out WorkerProgressDto awp) && awp != null && awp.IsActivePawnWorker && seen.Add(w))
                            workers.Add(w);

                    PlanShares(SitesConfig.Current.SiteRewardDistributionMode, totalProduced, hasOwner, workers,
                               s.AccessMode == SiteEntry.AccessPublic, s.OwnerTaxPercent,
                               out int ownerShare, out Dictionary<string, int> workerShares);
                    if (ownerShare <= 0 && workerShares.Count == 0) continue;   // nobody to pay

                    if (ownerShare > 0 && hasOwner)
                        plan.Add(new Delivery
                        {
                            Tile = s.Tile, Recipient = guildOwned ? "" : ownerAcct, Guild = guildOwned ? ownerAcct : "",
                            Dest = OwnerDestinationFor(s, guildOwned), ItemDefName = s.ItemDefName,
                            Amount = ownerShare, MarketplaceUnitPrice = s.MarketplaceUnitPrice,
                        });

                    foreach (KeyValuePair<string, int> kv in workerShares)
                    {
                        if (kv.Value <= 0) continue;
                        string dest = s.WorkerProgress.TryGetValue(kv.Key, out WorkerProgressDto rwp) ? rwp.Destination : SiteEntry.DestTreasury;
                        plan.Add(new Delivery { Tile = s.Tile, Recipient = kv.Key, Dest = dest, ItemDefName = s.ItemDefName, Amount = kv.Value, MarketplaceUnitPrice = s.MarketplaceUnitPrice });
                    }
                }
            }
            int roadSegments = ApplyRoadwork(roadWork);
            if (roadSegments > 0) Roadworks.RoadworksHandler.BroadcastSnapshot();
            if (plan.Count == 0) return touched;

            // Timers persist before delivery so a crash loses a cycle rather than doubling one; a failed write must roll them back too.
            if (!SaveToDisk())
            {
                lock (_lock)
                    foreach ((SiteEntry site, long prevTicks, double xpDelta) in advanced)
                    {
                        site.LastRewardUtcTicks = prevTicks;
                        foreach (string w in site.Workers)
                            if (site.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) && wp != null && wp.IsActivePawnWorker)
                            { wp.Xp -= xpDelta; wp.CyclesCompleted -= 1; }
                    }
                Diagnostics.ServerLog.Warn($"Sites: could not save the reward timers for {advanced.Count} site(s) - the cycle was skipped rather than paid twice.");
                return touched;
            }

            Dictionary<int, double> silverByTile = new Dictionary<int, double>();
            foreach (Delivery d in plan)
            {
                int net = DeliverPlanned(d);
                if (net > 0)
                {
                    touched.Add(d.Recipient);
                    if (string.Equals(d.ItemDefName, "Silver", StringComparison.OrdinalIgnoreCase))
                        silverByTile[d.Tile] = (silverByTile.TryGetValue(d.Tile, out double v) ? v : 0) + net;
                }
                // Not retried, so it is surfaced instead of lost quietly.
                else if (d.Amount > 0)
                    Diagnostics.ServerLog.Warn($"Sites: reward for {d.Recipient} (x{d.Amount} {d.ItemDefName}, tile {d.Tile}) delivered nothing - skipped this cycle (review; not auto-retried).");
            }
            if (silverByTile.Count > 0)
                lock (_lock)
                    foreach (KeyValuePair<int, double> kv in silverByTile)
                        if (_byTile.TryGetValue(kv.Key, out SiteEntry s)) s.TotalSilverGenerated += kv.Value;

            SaveToDisk();
            return touched;
        }

        // Pure, so value conservation can be proved per mode rather than argued.
        internal static void PlanShares(string distMode, int totalProduced, bool hasOwner, List<string> workers,
                                        bool publicAccess, int ownerTaxPercent,
                                        out int ownerShare, out Dictionary<string, int> workerShares)
        {
            ownerShare = 0;
            workerShares = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            workers = workers ?? new List<string>();
            if (totalProduced <= 0) return;
            bool taxed = publicAccess && ownerTaxPercent > 0;

            if (string.Equals(distMode, "OwnerOnly", StringComparison.OrdinalIgnoreCase))
            {
                // With no owner there is nobody to give it to, so the workers keep what they made.
                if (hasOwner) { ownerShare = totalProduced; return; }
                if (workers.Count == 0) return;
                int each = totalProduced / workers.Count, extra = totalProduced - each * workers.Count;
                for (int i = 0; i < workers.Count; i++) workerShares[workers[i]] = each + (i == 0 ? extra : 0);
                return;
            }

            if (string.Equals(distMode, "PerWorkerCopy", StringComparison.OrdinalIgnoreCase))
            {
                if (hasOwner) ownerShare = totalProduced;
                foreach (string w in workers)
                    workerShares[w] = taxed
                        ? Math.Max(1, (int)(totalProduced * (1.0 - ownerTaxPercent / 100.0)))
                        : totalProduced;
                return;
            }

            // SplitTotal
            int slots = workers.Count + (hasOwner ? 1 : 0);
            if (slots <= 0) return;
            int per = totalProduced / slots;
            int ownerBonus = totalProduced - per * slots;   // remainder -> owner
            foreach (string w in workers)
            {
                int wShare = per;
                if (taxed)
                {
                    int tax = (int)(per * (ownerTaxPercent / 100.0));
                    wShare = Math.Max(per > 0 ? 1 : 0, per - tax);
                    ownerBonus += per - wShare;             // owner tax
                }
                workerShares[w] = wShare;
            }
            if (hasOwner) ownerShare = per + ownerBonus;
            else if (workers.Count > 0 && ownerBonus > 0) workerShares[workers[0]] += ownerBonus;
        }

        // Exactly one of Recipient and Guild is set, so guild output never passes through a member's account.
        private struct Delivery
        {
            public int    Tile;
            public string Recipient;
            public string Guild;
            public string Dest;
            public string ItemDefName;
            public int    Amount;
            public int    MarketplaceUnitPrice;
        }

        // Returns the net amount delivered, where 0 means nothing landed.
        private static int DeliverPlanned(Delivery d)
        {
            if (d.Amount <= 0) return 0;
            if (!string.IsNullOrEmpty(d.Guild)) return DeliverGuildOwned(d);
            if (string.IsNullOrEmpty(d.Recipient)) return 0;

            if (string.Equals(d.ItemDefName, "Silver", StringComparison.OrdinalIgnoreCase))
            {
                int net = Guilds.GuildStore.ApplyGuildSiteRewardTax(d.Recipient, d.Amount);
                if (net <= 0) return 0;
                Items.KmhPayloadEscrow.DeliverSilver(d.Recipient, net, $"site#{d.Tile} reward", "site reward silver could not be credited");
                return net;
            }

            switch (d.Dest)
            {
                case SiteEntry.DestStorage:
                {
                    int stored = TryStore(d.Tile, d.Recipient, d.ItemDefName, d.Amount);
                    int rest = d.Amount - stored;
                    // Overflow goes to the treasury, so storage is never a way to lose a cycle's output.
                    if (rest > 0)
                        Items.KmhPayloadEscrow.DeliverCompact(d.Recipient, d.ItemDefName, rest,
                            $"site#{d.Tile} reward (storage full)", "site reward delivery failed");
                    return d.Amount;
                }
                case SiteEntry.DestMarketplace:
                    MarketplaceStore_PostFromSite(d.Recipient, d.ItemDefName, d.Amount, Math.Max(1, d.MarketplaceUnitPrice));
                    return d.Amount;
                case SiteEntry.DestCaravan:
                    if (TryDeliverToColony(d.Recipient, d.ItemDefName, d.Amount)) return d.Amount;
                    // The sweeper runs this, so an offline recipient is held rather than dropped.
                    Items.KmhPayloadEscrow.DeliverCompact(d.Recipient, d.ItemDefName, d.Amount,
                        $"site#{d.Tile} reward (offline -> treasury)", "site reward delivery failed");
                    return d.Amount;
                default: // treasury
                    Items.KmhPayloadEscrow.DeliverCompact(d.Recipient, d.ItemDefName, d.Amount,
                        $"site#{d.Tile} reward", "site reward delivery failed");
                    return d.Amount;
            }
        }

        // Marketplace and colony are not offered here, or guild goods would land with whichever member configured them.
        private static int DeliverGuildOwned(Delivery d)
        {
            if (d.Dest == SiteEntry.DestStorage)
            {
                int stored = TryStore(d.Tile, d.Guild, d.ItemDefName, d.Amount);
                int rest = d.Amount - stored;
                if (rest > 0 && !DepositToGuild(d, rest)) return stored;   // full storage must not silently drop the rest
                return d.Amount;
            }
            return DepositToGuild(d, d.Amount) ? d.Amount : 0;
        }

        private static bool DepositToGuild(Delivery d, int amount)
        {
            bool ok = string.Equals(d.ItemDefName, "Silver", StringComparison.OrdinalIgnoreCase)
                ? Treasury.TreasuryStore.DepositGuildSilver(d.Guild, amount, d.Guild, note: $"site#{d.Tile} production")
                : Treasury.TreasuryStore.DepositGuildItem(d.Guild, d.ItemDefName, amount, note: $"site#{d.Tile} production");
            if (!ok)
                Diagnostics.ServerLog.Warn($"Sites: guild '{d.Guild}' could not be credited {amount}x {d.ItemDefName} from site#{d.Tile} - production held at the site instead.");
            return ok;
        }

        private static string OwnerDestinationFor(SiteEntry s, bool guildOwned)
        {
            string dest = NormalizeDest(s.OwnerRewardDestination);
            if (!guildOwned) return dest;
            return dest == SiteEntry.DestStorage ? SiteEntry.DestStorage : SiteEntry.DestTreasury;
        }

        // Owner account only, or a worker's share would land in a warehouse the owner can empty.
        internal static int StorableUnits(SiteEntry s, string account, int amount)
        {
            if (s == null || amount <= 0 || string.IsNullOrEmpty(account)) return 0;
            string owner = SiteOwnership.ProductionAccount(s);
            if (string.IsNullOrEmpty(owner)
                || !string.Equals(owner, account, StringComparison.OrdinalIgnoreCase)) return 0;
            int room = SiteBuildings.FreeStorage(s);
            if (room <= 0) return 0;
            return amount < room ? amount : room;
        }

        private static int TryStore(int tile, string recipient, string defName, int amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(defName)) return 0;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return 0;
                int take = StorableUnits(s, recipient, amount);
                if (take <= 0) return 0;
                s.StoredItems.TryGetValue(defName, out int have);
                s.StoredItems[defName] = Util.KmhSafe.AddSaturating(have, take);
                return take;
            }
        }

        // Goods go to the owning account, not the member who pressed the button.
        public static (bool ok, string reason) CollectStorage(string user, int tile)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");
            var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool guildOwned;
            string ownerAcct;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                if (!SiteOwnership.CanManage(s, user)) return (false, "You can't manage this site.");
                if (s.StoredItems == null || s.StoredItems.Count == 0) return (false, "Nothing is stored here.");
                guildOwned = SiteOwnership.IsGuildOwned(s);
                ownerAcct  = SiteOwnership.ProductionAccount(s);
                if (string.IsNullOrEmpty(ownerAcct)) return (false, "This site has no owner to collect for.");
                foreach (KeyValuePair<string, int> kv in s.StoredItems) if (kv.Value > 0) taken[kv.Key] = kv.Value;
                s.StoredItems.Clear();
            }

            int units = 0;
            foreach (KeyValuePair<string, int> kv in taken)
            {
                units += kv.Value;
                if (guildOwned)
                {
                    // Put it back rather than lose it if the vault refuses.
                    if (!Treasury.TreasuryStore.DepositGuildItem(ownerAcct, kv.Key, kv.Value, $"site#{tile} storage collected"))
                        RestoreStored(tile, kv.Key, kv.Value);
                }
                else
                {
                    Items.KmhPayloadEscrow.DeliverCompact(ownerAcct, kv.Key, kv.Value,
                        $"site#{tile} storage collected", "site storage collection failed");
                }
            }
            SaveToDisk();
            RaiseSite(tile, user, "storage_collected");
            return (true, guildOwned
                ? $"Collected {units} item(s) from site storage into the {ownerAcct} vault."
                : $"Collected {units} item(s) from site storage.");
        }

        private static void RestoreStored(int tile, string defName, int qty)
        {
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null || s.StoredItems == null) return;
                s.StoredItems.TryGetValue(defName, out int have);
                s.StoredItems[defName] = Util.KmhSafe.AddSaturating(have, qty);
            }
            Diagnostics.ServerLog.Warn($"Sites: site#{tile} storage collection could not be credited - {qty}x {defName} returned to storage.");
        }

        // Off-map wealth a button press from the player, so it must not dodge raid scaling.
        public static long StoredValueFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            long total = 0;
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s?.StoredItems == null) continue;
                    if (!SiteOwnership.IsOwnedBy(s, username)) continue;
                    foreach (KeyValuePair<string, int> kv in s.StoredItems)
                        total += Items.KmhItemSafety.GetTrustedMarketValue((kv.Key ?? "").Split('|')[0]) * Math.Max(0, kv.Value);
                }
            return total;
        }

        // False when the recipient is offline, so the caller can fall back to the treasury.
        private static bool TryDeliverToColony(string username, string defName, int amount)
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true || c.Listener == null) continue;
                if (!string.Equals(c.GetData<UserFile>()?.Username, username, StringComparison.OrdinalIgnoreCase)) continue;
                return KmhRouter.SendTo(c, KmhProtocol.Kind.TreasuryGrant,
                    new { kind = "item", def_name = defName, amount });
            }
            return false;
        }

        private static void MarketplaceStore_PostFromSite(string seller, string defName, int qty, int unitPrice)
        {
            // Deposited first so a refused Post leaves the goods banked rather than lost.
            Items.KmhPayloadEscrow.DeliverCompact(seller, defName, qty, "site production (to list)", "site production deposit failed");
            Marketplace.MarketplaceStore.Post(seller, defName, qty, unitPrice, "public", 0);
        }

        // CanManage is resolved outside our lock because it reads GuildStore, which holds its own.
        internal static bool CanManageTile(int tile, string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            SiteEntry probe;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return false;
                probe = new SiteEntry
                {
                    Tile = s.Tile, OwnerKind = s.OwnerKind, OwnerUsername = s.OwnerUsername,
                    ControllingGuild = s.ControllingGuild,
                };
            }
            return SiteOwnership.CanManage(probe, username);
        }

        public static bool TryGetSiteFacts(int tile, string username, out bool canManage,
                                           out string archetype, out int tier)
        {
            canManage = false; archetype = ""; tier = 0;
            SiteEntry probe;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return false;
                archetype = SiteArchetypes.Normalize(s.Archetype);
                tier = s.OutputTier;
                probe = new SiteEntry
                {
                    Tile = s.Tile, OwnerKind = s.OwnerKind, OwnerUsername = s.OwnerUsername,
                    ControllingGuild = s.ControllingGuild,
                };
            }
            canManage = SiteOwnership.CanManage(probe, username);
            return true;
        }

        // Having no output yet is not a blocked output, or every derelict location would ask for admin review.
        internal static bool IsUnconfiguredOutpost(SiteEntry s)
            => s != null && !string.IsNullOrEmpty(s.OutpostTemplate) && string.IsNullOrEmpty(s.ItemDefName);

        // Never blank, because an unnamed place still has to be called something in a sentence.
        public static string NameOf(SiteEntry s)
            => s == null ? "" : (!string.IsNullOrEmpty(s.SiteName) ? s.SiteName : $"tile {s.Tile}");

        public static string DisplayNameOf(int tile)
        {
            lock (_lock)
                return _byTile.TryGetValue(tile, out SiteEntry s) && s != null ? NameOf(s) : $"tile {tile}";
        }

        // Every tile KMH already occupies. The Director excludes these so it never has to reason about geography.
        public static List<int> AllOccupiedTiles()
        {
            var outp = new List<int>();
            lock (_lock) foreach (int tile in _byTile.Keys) outp.Add(tile);
            return outp;
        }

        public static SiteSnapshot BuildSnapshotFor(string username)
        {
            SiteSnapshot snap = new SiteSnapshot
            {
                AllowCustomSites = SitesConfig.Current.AllowCustomSites,
                PriceMultiplier  = SitesConfig.Current.CustomSitePriceMultiplier,
                MaxRewardAmount  = SitesConfig.Current.CustomSiteMaxRewardAmount,

                BuildingCostSilver  = Math.Max(0, SitesConfig.Current.SiteBuildingCostSilver),
                RepairCostPerPoint  = Math.Max(0, SitesConfig.Current.SiteRepairCostPerPoint),
                StoragePerBuilding  = SiteBuildings.UnitsPerStorage,
                MaxStorageUnits     = SiteBuildings.MaxStorageUnits,
                WorkersPerHousing   = SiteBuildings.WorkersPerHousing,
                MaxHousingBonus     = SiteBuildings.MaxHousingBonus,
                ProductionBonusPct  = (int)Math.Round(SiteBuildings.ProductionBonusPerLevel * 100),
                MaxProductionPct    = (int)Math.Round(SiteBuildings.MaxBuildingBonus * 100),
            };
            // Resolve the caller's guild once - CanAccess would otherwise re-take GuildStore's lock for every site.
            string callerGuild = Guilds.GuildStore.CurrentGuildOf(username) ?? "";
            // Answered after the lock because it reads WorldStore, which would invert the lock order.
            var claimable = new List<SiteEntry>();
            lock (_lock)
            {
                snap.Revision = Util.KmhSnapshotRevision.Next();
                foreach (SiteEntry s in _byTile.Values)
                {
                    bool mine = SiteOwnership.CanManage(s, username)
                                || s.Workers.Contains(username, StringComparer.OrdinalIgnoreCase);
                    if (!mine && !CanAccess(s, username, callerGuild)) continue;
                    SiteEntry copy = Copy(s);
                    if (SiteOutposts.NormalizeState(copy.OutpostState) == SiteEntry.OutpostClaimable) claimable.Add(copy);
                    snap.Sites.Add(copy);
                }
            }
            foreach (SiteEntry c in claimable)
                c.CanClaim = string.Equals(EligibleClaimantFor(c), username, StringComparison.OrdinalIgnoreCase);
            return snap;
        }

        // Seeds a fresh server's picker until a client pushes real labels, which then replace these.
        private static readonly (string DefName, string Label, long Value)[] VanillaFallbackOutputs =
        {
            // Silver is excluded so a site can never print money.
            ("WoodLog", "wood", 1), ("Steel", "steel", 2), ("Plasteel", "plasteel", 8), ("Uranium", "uranium", 5),
            ("Jade", "jade", 12), ("Gold", "gold", 20), ("Cloth", "cloth", 2),
            ("Chemfuel", "chemfuel", 1), ("Hay", "hay", 1), ("RawRice", "rice", 1), ("RawCorn", "corn", 1),
            ("RawPotatoes", "potatoes", 1), ("RawBerries", "berries", 1), ("RawFungus", "fungus", 1),
            ("RawAgave", "agave", 1), ("Milk", "milk", 2), ("Leather_Plain", "plainleather", 3), ("Wool", "wool", 2),
            ("Neutroamine", "neutroamine", 5), ("MedicineHerbal", "herbal medicine", 10), ("Kibble", "kibble", 1),
            ("Pemmican", "pemmican", 1), ("BlocksGranite", "granite blocks", 1),
        };

        public static Dto.SiteCatalogSnapshot BuildOutputCatalogFor(string username, bool includeBlocked)
        {
            SitesConfig cfg = SitesConfig.Current;
            Dto.SiteCatalogSnapshot snap = new Dto.SiteCatalogSnapshot
            {
                TiersEnabled    = cfg.UseSiteOutputTiers,
                MaxAllowedTier  = cfg.MaxAllowedSiteOutputTier,
                Tier4Enabled    = cfg.OutputTiers != null && cfg.OutputTiers.Length >= 4 && cfg.OutputTiers[3].Enabled,
                IncludesBlocked = includeBlocked,
                // Without a catalog everything classifies Unknown, so a picker where nothing is eligible reads as broken.
                ArchetypesReady = cfg.ArchetypesEnabled && SiteCatalogStore.HasCatalog,
                // What the server actually ADOPTED - a client latching on a successful send believed a catalog a refused push never applied.
                CatalogFingerprint = SiteCatalogStore.Fingerprint ?? "",
            };
            if (snap.ArchetypesReady)
                foreach (SiteArchetypeDef a in SiteArchetypeRegistry.All)
                {
                    if (a.IsCustom && !cfg.AllowCustomArchetype) continue;
                    snap.Archetypes.Add(new Dto.SiteArchetypeInfo
                    {
                        Id = a.Id, DisplayName = a.DisplayName, Description = a.Description,
                        WorkerSkill = a.WorkerSkill ?? "", CostMultiplier = a.CostMultiplier,
                        PerkText = a.PerkText ?? "", IsCustom = a.IsCustom, UnlocksRoadworks = a.UnlocksRoadworks,
                    });
                }
            const int blockedCap = 250;   // don't ship the whole weapon/apparel catalog even in debug
            int blocked = 0;
            System.Collections.Generic.List<(string DefName, string Label, long Value)> source =
                Features.ItemLabels.ItemLabelCache.AllForCatalog();
            // The sparse flag lets the client say "still loading" rather than showing an empty picker.
            snap.CatalogSparse = source.Count < 12;
            if (snap.CatalogSparse)
            {
                var have = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in source) have.Add(s.DefName);
                foreach ((string DefName, string Label, long Value) v in VanillaFallbackOutputs)
                    if (have.Add(v.DefName)) source.Add(v);
            }
            foreach ((string defName, string label, long value) in source)
            {
                SiteOutputClass cls = SiteOutputRules.Classify(defName, label);
                if (!cls.IsAllowed)
                {
                    if (!includeBlocked || blocked >= blockedCap) continue;
                    blocked++;
                    snap.Entries.Add(new Dto.SiteCatalogEntry
                    {
                        DefName = defName, Label = label, Tier = cls.TierNumber, TierName = cls.TierName,
                        RelevantSkill = cls.RelevantSkill, MaxAmount = 0, Allowed = false, BlockReason = cls.BlockReason,
                        Family = SiteCatalogStore.FamilyOf(defName),
                        AllowedArchetypes = "",   // a blocked output belongs to no archetype
                    });
                    continue;
                }
                int maxAmount   = Math.Max(1, Math.Min(cfg.CustomSiteMaxRewardAmount, cls.MaxAmount));
                int estCost     = BuildCost(username, value, maxAmount, cls.CostMultiplier);
                int estCycleMin = (int)Math.Round(CycleTimeMsForValue(value, maxAmount) * cls.CycleMultiplier / 60000.0);
                // Shipped per item so the client filters on it: a client-side family test would disagree with Roadworks.
                Dto.SiteOutputMetadata meta = SiteCatalogStore.Lookup(defName);
                snap.Entries.Add(new Dto.SiteCatalogEntry
                {
                    DefName = defName, Label = label, Tier = cls.TierNumber, TierName = cls.TierName,
                    RelevantSkill = cls.RelevantSkill, MaxAmount = maxAmount, EstBuildCost = estCost,
                    EstCycleMinutes = estCycleMin, Allowed = true,
                    Family = meta?.Family ?? SiteOutputFamilies.Unknown,
                    AllowedArchetypes = snap.ArchetypesReady ? SiteArchetypeRegistry.ArchetypesFor(meta) : "",
                });
            }
            snap.Entries.Sort((a, b) => a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier)
                : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return snap;
        }

        // Read-only copies for the SDK ISitesApi (production fields filled).
        public static List<SiteEntry> AllForApi()
        {
            lock (_lock)
            {
                List<SiteEntry> result = new List<SiteEntry>(_byTile.Count);
                foreach (SiteEntry s in _byTile.Values) result.Add(Copy(s));
                return result;
            }
        }

        public static SiteEntry GetForApi(int tile)
        {
            lock (_lock) { return _byTile.TryGetValue(tile, out SiteEntry s) ? Copy(s) : null; }
        }

        // Clones everything and then overrides what is derived, so a newly added field is never silently dropped.
        internal static SiteEntry Copy(SiteEntry s)
        {
            bool producing = Producing(s);
            double cycleMin = EffectiveCycleMs(s) / 60000.0;

            // Comparers are passed explicitly because a copy does not inherit the source dictionary's.
            SiteEntry c = s.ShallowClone();
            c.Workers = s.Workers == null ? new List<string>() : new List<string>(s.Workers);
            c.WorkerProgress = new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);
            if (s.WorkerProgress != null)
                foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                    c.WorkerProgress[kv.Key] = kv.Value?.ShallowClone();
            c.Buildings = new List<SiteBuilding>();
            if (s.Buildings != null)
                foreach (SiteBuilding b in s.Buildings) c.Buildings.Add(b?.ShallowClone());
            c.StoredItems = s.StoredItems == null
                ? null : new Dictionary<string, int>(s.StoredItems, StringComparer.OrdinalIgnoreCase);

            c.OwnerGuild = OwnerCurrentGuild(s);
            c.MaxWorkers = MaxWorkersLive(s);
            // Sanitized display: paused sites report 0 (never Infinity/NaN/huge) and a clear reason.
            c.IsProducing = producing && !s.BlockedOutput;
            c.PausedReason = s.BlockedOutput ? "output_blocked" : (producing ? "" : "no_workers");
            c.ProductionMultiplier = producing && !s.BlockedOutput ? OutputFactor(s) : 0.0;
            c.EffectiveCycleMinutes = producing && !s.BlockedOutput && IsFinitePositive(EffectiveCycleMs(s)) ? cycleMin : 0.0;
            return c;
        }


        private static bool CanAccess(SiteEntry s, string username)
            => CanAccess(s, username, Guilds.GuildStore.CurrentGuildOf(username) ?? "");

        // callerGuild is passed in so a whole-board sweep resolves it once instead of per site.
        private static bool CanAccess(SiteEntry s, string username, string callerGuild)
        {
            switch (s.AccessMode)
            {
                case SiteEntry.AccessPublic:  return true;
                case SiteEntry.AccessPrivate: return SiteOwnership.CanManage(s, username);
                default:
                    // Live, so a guild loses access the moment the owner leaves it.
                    string ownerGuild = OwnerCurrentGuild(s);
                    if (string.IsNullOrEmpty(ownerGuild)) return SiteOwnership.CanManage(s, username);
                    if (string.Equals(callerGuild, ownerGuild, StringComparison.OrdinalIgnoreCase)) return true;
                    return Guilds.GuildStore.AreAllied(callerGuild, ownerGuild);
            }
        }

        private static string NormalizeAccess(string a)
        {
            switch ((a ?? "").ToLowerInvariant())
            {
                case SiteEntry.AccessPublic:  return SiteEntry.AccessPublic;
                case SiteEntry.AccessPrivate: return SiteEntry.AccessPrivate;
                default:                      return SiteEntry.AccessGuildOnly;
            }
        }

        private static string NormalizeDest(string d)
        {
            switch ((d ?? "").ToLowerInvariant())
            {
                case SiteEntry.DestMarketplace: return SiteEntry.DestMarketplace;
                case SiteEntry.DestCaravan:     return SiteEntry.DestCaravan;
                case SiteEntry.DestStorage:     return SiteEntry.DestStorage;
                default:                        return SiteEntry.DestTreasury;
            }
        }

        // Rough item->skill mapping so the skill shown to players matches the item.
        public static string DetermineRelevantSkill(string itemDefName)
        {
            if (string.IsNullOrEmpty(itemDefName)) return "Crafting";
            string l = itemDefName.ToLowerInvariant();
            if (l.Contains("steel") || l.Contains("plasteel") || l.Contains("uranium") || l.Contains("jade") || l.Contains("blocks") || l.Contains("chunk") || l.Contains("gold") || l.Contains("silver")) return "Mining";
            if (l.Contains("raw") || l.Contains("corn") || l.Contains("rice") || l.Contains("berry") || l.Contains("hay") || l.Contains("smokeleaf") || l.Contains("psychoid") || l.Contains("cotton") || l.Contains("devilstrand") || l.Contains("wood")) return "Plants";
            if (l.Contains("meal") || l.Contains("food") || l.Contains("kibble") || l.Contains("pemmican") || l.Contains("nutrient")) return "Cooking";
            if (l.Contains("medicine") || l.Contains("herbal") || l.Contains("glitter") || l.Contains("penox") || l.Contains("luci")) return "Medicine";
            if (l.Contains("meat") || l.Contains("leather") || l.Contains("wool") || l.Contains("fur") || l.Contains("skin")) return "Animals";
            if (l.Contains("component") || l.Contains("spacer") || l.Contains("archotech")) return "Intellectual";
            if (l.Contains("brick") || l.Contains("concrete")) return "Construction";
            return "Crafting";
        }

        // Needed because the catalog usually arrives with the first player, long after LoadFromDisk ran.
        public static int ReclassifySkills()
        {
            if (!SiteCatalogStore.HasCatalog) return 0;
            int changed = 0;
            lock (_lock)
            {
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s == null || string.IsNullOrEmpty(s.ItemDefName)) continue;
                    string classified = SiteCatalogStore.SkillOf(s.ItemDefName);
                    if (string.IsNullOrEmpty(classified)) continue;
                    string correct = SkillFor(s.Archetype, SiteCatalogStore.FamilyOf(s.ItemDefName), classified);
                    if (string.IsNullOrEmpty(correct) || correct == s.RelevantSkillDef) continue;
                    Diagnostics.ServerLog.Verbose($"Sites: tile {s.Tile} work skill {s.RelevantSkillDef} -> {correct} (catalog classified '{s.ItemDefName}').");
                    s.RelevantSkillDef = correct;
                    changed++;
                }
            }
            if (changed > 0)
            {
                Diagnostics.ServerLog.Info($"Sites: corrected the work skill on {changed} site(s) from the classified catalog.");
                SaveToDisk();
            }
            return changed;
        }

        public static int LegacyBlockedSiteCount { get; private set; }
        public static int LegacyWorkerCount { get; private set; }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.SitesFile, out PersistedState state) && state?.Sites != null)
            {
                int blocked = 0, legacyWorkers = 0, reskilled = 0;
                lock (_lock)
                {
                    _byTile.Clear();
                    foreach (SiteEntry s in state.Sites)
                    {
                        if (s == null || s.Tile < 0) continue;
                        s.Workers ??= new List<string>();
                        NormalizeKeys(s);
                        string label = Features.ItemLabels.ItemLabelCache.LabelFor(s.ItemDefName);
                        SiteOutputClass cls = SiteOutputRules.Classify(s.ItemDefName, label);
                        if (s.OutputTier <= 0 || s.TierMaxSpeedMultiplier <= 0 || s.TierMaxOutputMultiplier <= 0)
                        {
                            s.OutputTier = cls.TierNumber;
                            s.TierMaxSpeedMultiplier = cls.MaxSpeedMultiplier;
                            s.TierMaxOutputMultiplier = cls.MaxOutputMultiplier;
                            if (string.IsNullOrEmpty(s.RelevantSkillDef)) s.RelevantSkillDef = cls.RelevantSkill;
                        }

                        // Migration corrects the work skill only, since a site training the wrong one is invisible to its owner.
                        string classified = SiteCatalogStore.SkillOf(s.ItemDefName);
                        if (!string.IsNullOrEmpty(classified))
                        {
                            string correct = SkillFor(s.Archetype, SiteCatalogStore.FamilyOf(s.ItemDefName), classified);
                            if (!string.IsNullOrEmpty(correct) && correct != s.RelevantSkillDef)
                            {
                                Diagnostics.ServerLog.Verbose($"Sites: tile {s.Tile} work skill {s.RelevantSkillDef} -> {correct} (output '{s.ItemDefName}' is now classified).");
                                s.RelevantSkillDef = correct;
                                reskilled++;
                            }
                        }
                        // Paused rather than left producing a now-illegal output; an unconfigured outpost is not blocked.
                        if (!cls.IsAllowed && SitesConfig.Current.UseSiteOutputTiers && !IsUnconfiguredOutpost(s))
                        {
                            s.BlockedOutput = true; blocked++;
                            Diagnostics.ServerLog.Warn($"Sites: PAUSED legacy site tile {s.Tile} ({s.OwnerUsername}) - output '{s.ItemDefName}' ('{label}') is now blocked ({cls.BlockReason}). Admin review needed.");
                        }
                        else s.BlockedOutput = false;

                        // Kept rather than deleted, so an old account-worker can be replaced by a real pawn.
                        if (SitesConfig.Current.RequirePawnSiteWorkers)
                            foreach (WorkerProgressDto wp in s.WorkerProgress.Values)
                                if (wp != null && wp.PawnLoadId <= 0 && !wp.Legacy)
                                {
                                    wp.Legacy = true;
                                    wp.BlockedReason = "legacy account worker - send a caravan and assign a real pawn";
                                    legacyWorkers++;
                                }
                        _byTile[s.Tile] = s;
                    }
                }
                LegacyBlockedSiteCount = blocked;
                LegacyWorkerCount = legacyWorkers;
                Diagnostics.ServerLog.Info($"Sites: loaded {state.Sites.Count} site(s) from disk{(blocked > 0 ? $" ({blocked} paused - output now blocked)" : "")}{(legacyWorkers > 0 ? $" ({legacyWorkers} legacy account-worker(s) disabled - reassign real pawns)" : "")}{(reskilled > 0 ? $" ({reskilled} re-skilled from the classified catalog)" : "")}");
            }
        }

        // Deserialization can lose the comparer, and a site whose workers stop matching quietly pauses.
        internal static void NormalizeKeys(SiteEntry s)
        {
            if (s == null) return;

            var wp = new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);
            if (s.WorkerProgress != null)
                foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    if (wp.TryGetValue(kv.Key, out WorkerProgressDto cur) && cur != null && cur.Xp >= kv.Value.Xp) continue;
                    wp[kv.Key] = kv.Value;
                }
            s.WorkerProgress = wp;

            if (s.StoredItems == null) return;
            var stored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> kv in s.StoredItems)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                stored.TryGetValue(kv.Key, out int cur);
                stored[kv.Key] = Util.KmhSafe.AddSaturating(cur, kv.Value);
            }
            s.StoredItems = stored;
        }

        public static void ClearForNewSeason()
        {
            lock (_lock) { _byTile.Clear(); }
            SaveToDisk();
        }

        // The server holds no pawns, so assigned colonists stay in their caravan rather than being deleted.
        public static (int sites, int pawnWorkers) PurgeOwner(string user, bool dryRun)
        {
            if (string.IsNullOrEmpty(user)) return (0, 0);
            int sites = 0, pawns = 0;
            List<int> tiles = new List<int>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                    if (SiteOwnership.IsOwnedBy(s, user))
                    {
                        sites++; tiles.Add(s.Tile);
                        if (s.WorkerProgress != null)
                            foreach (WorkerProgressDto wp in s.WorkerProgress.Values)
                                if (wp != null && wp.IsActivePawnWorker) pawns++;
                    }
            if (dryRun || sites == 0) return (sites, pawns);
            lock (_lock) foreach (int t in tiles) _byTile.Remove(t);
            SaveToDisk();
            return (sites, pawns);
        }

        // Used after a save reset, where the pawns no longer exist; the sites themselves stay and auto-pause.
        public static int RemoveWorkerEverywhere(string user, bool dryRun = false)
        {
            if (string.IsNullOrEmpty(user)) return 0;
            int removed = 0;
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                {
                    bool here = (s.Workers != null && s.Workers.Any(w => string.Equals(w, user, StringComparison.OrdinalIgnoreCase)))
                             || (s.WorkerProgress != null && s.WorkerProgress.ContainsKey(user));
                    if (!here) continue;
                    removed++;
                    if (dryRun) continue;
                    s.Workers?.RemoveAll(w => string.Equals(w, user, StringComparison.OrdinalIgnoreCase));
                    s.WorkerProgress?.Remove(user);
                }
            if (removed > 0 && !dryRun) SaveToDisk();
            return removed;
        }

        // False means in-memory only; reward timers live here, so a cycle the disk never took is paid again from the restored timers.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock) { state.Sites = new List<SiteEntry>(_byTile.Values); }
            return JsonFileStore.Save(KmhDataPaths.SitesFile, state);
        }

        private sealed class PersistedState
        {
            public List<SiteEntry> Sites { get; set; } = new List<SiteEntry>();
        }
    }
}
