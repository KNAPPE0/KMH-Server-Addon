using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Sites
{
    internal static class SiteStore
    {
        public const double XpPerCycle = 250.0;

        private static readonly object _lock = new object();
        // Below _lock on purpose: it captures it, so payouts save in the same write as what produced them.
        private static readonly Transactions.KmhOwedPayouts _owed = new Transactions.KmhOwedPayouts("Sites", _lock, SaveToDisk);
        private static readonly Dictionary<int, SiteEntry> _byTile = new Dictionary<int, SiteEntry>();
        private static readonly Dictionary<string, long> _lastBuildUtc =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Bound to the guild incarnation, never the reusable name; written under _lock, read without it.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _guildOwner
            = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        // Never equal to a live incarnation, so a site migrated with no guild left stays ownerless if the name returns.
        internal const string OrphanBindingPrefix = "orphan:";

        internal static string GuildBindingOf(int tile) => _guildOwner.TryGetValue(tile, out string id) ? id : null;

        // An unbound guild site is nobody's: only a binding to the guild's live incarnation counts.
        internal static bool GuildBindingIsLive(SiteEntry s)
            => s != null && !string.IsNullOrEmpty(s.ControllingGuild)
               && _guildOwner.TryGetValue(s.Tile, out string id) && Guilds.GuildIdentity.IsLive(s.ControllingGuild, id);

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
                if (WorksLocked(s, user)) n++;
            return n;
        }

        // One site counts once however many of that player's colonists are on it.
        internal static bool WorksLocked(SiteEntry s, string user)
        {
            if (s?.Workers == null || string.IsNullOrEmpty(user)) return false;
            foreach (string w in s.Workers)
                if (SiteWorkerKey.IsOwnedBy(w, user)) return true;
            return false;
        }

        // Blocked and legacy rows included, or parking a worker as "away" sidesteps the per-site cap.
        internal static int MineCount(SiteEntry s, string user)
        {
            if (s?.Workers == null || string.IsNullOrEmpty(user)) return 0;
            int n = 0;
            foreach (string w in s.Workers) if (SiteWorkerKey.IsOwnedBy(w, user)) n++;
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
            double raw = 1.0 + AvgSkillLevel(s) / 20.0;
            double skill = Math.Min(cap, Math.Max(1.0, raw));
            // Outside the tier cap on purpose, so damage, buildings and perks count at any tier; federation is local only.
            return skill * (1.0 + SiteBuildings.ProductionBonus(s)) * SiteStability.OutputFactor(s.Stability)
                 * SiteArchetypeRegistry.ProductionPerk(s.Archetype)
                 * Global.Federation.FederationBenefits.SiteOutputFactor(OwnerCurrentGuild(s));
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
            double perUnit = SitesConfig.Current.Output.CycleMinutesPerRewardUnit;
            double minutes = 30.0 + marketValue / 5.0 + Math.Max(0, amount) * perUnit;
            minutes = Math.Min(240.0, Math.Max(30.0, minutes));
            return minutes * 60.0 * 1000.0;
        }

        public static int BuildCost(string ownerUsername, float marketValue, int amount, double tierCostMult = 1.0)
        {
            double mult = SitesConfig.Current.Costs.PriceMultiplier;
            // Saturating, because an overflowing double->int cast would price a huge order at the floor.
            int raw = ToCost(marketValue * (double)amount * mult);
            // A client can under-report value but not amount, so amount sets a floor of its own.
            int throughputFloor = ToCost(Math.Max(0, amount) * (double)SitesConfig.Current.Costs.MinPerUnit);
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

        // Server-only, saved with the sites: the ledger rows whose charge paid for a change this file holds. Boot finds
        // the row's change here (the silver is spent) or refunds the row; settled ones are pruned after a week.
        private static readonly Dictionary<string, long> _purchaseTx = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly long PurchaseEvidenceTtl = TimeSpan.FromDays(7).Ticks;

        internal static bool HoldsPurchase(string txId) { lock (_lock) return !string.IsNullOrEmpty(txId) && _purchaseTx.ContainsKey(txId); }

        // Row before debit, debit carrying its take marker: a crash before the change saves is refunded at boot, one
        // after keeps it paid for - never both. True with a null row means it was free.
        private static bool ChargeForSite(string user, int tile, int cost, string summary, out Transactions.KmhTransaction tx, out string refusal)
        {
            tx = null; refusal = null;
            if (cost <= 0) return true;
            tx = Transactions.KmhTransactionRepository.OpenTake(user, Transactions.KmhTxType.Purchase, summary, tile, cost, null, null,
                     destination: "site", targetKind: Transactions.KmhTransactionRepository.TargetSitePurchase);
            if (tx == null) { refusal = "The server couldn't record that charge - nothing was taken. Try again shortly."; return false; }
            if (Treasury.TreasuryStore.WithdrawBundleForTxn(user, cost, new List<KeyValuePair<string, int>>(), tx.TakeMarker, summary)) return true;
            Transactions.KmhTransactionRepository.Abort(tx);
            tx = null;
            return false;
        }

        // Caller holds _lock, inside the change it records, and saves next.
        private static void NamePurchaseLocked(Transactions.KmhTransaction tx) { if (tx != null) _purchaseTx[tx.Id] = DateTime.UtcNow.Ticks; }

        private static void SettlePurchase(Transactions.KmhTransaction tx)
        {
            if (tx == null) return;
            Transactions.KmhTransactionRepository.Settle(tx);
            lock (_lock) _purchaseTx.Remove(tx.Id);
        }

        // On the row's own refund marker, so this and boot recovery return the charge once between them.
        private static void RefundPurchase(Transactions.KmhTransaction tx)
        {
            if (tx == null) return;
            lock (_lock) _purchaseTx.Remove(tx.Id);
            if (!Transactions.KmhTransactionRepository.Compensate(tx))
                Diagnostics.ServerLog.Error($"Sites: charge {tx.Id} could not be refunded yet - the row stays open and the next boot refunds it.");
        }

        internal static Action<string> AtPurchasePhaseForTest;

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

            int cost = SiteStability.RepairCost(before, SitesConfig.Current.Costs.RepairPerPoint);
            if (!ChargeForSite(user, tile, cost, $"site repair (tile {tile})", out Transactions.KmhTransaction tx, out string refusal))
                return (false, refusal ?? $"Repairs cost {Util.SilverFmt.Format(cost)} - your treasury is short.");
            AtPurchasePhaseForTest?.Invoke("charged");

            // The change and the row that paid for it are one save, so boot tells a paid repair from a refundable charge.
            bool repaired = false, saved = false;
            lock (_lock)
            {
                if (_byTile.TryGetValue(tile, out SiteEntry s) && s != null
                    && SiteOwnership.CanManage(s, user)
                    && SiteStability.Clamp(s.Stability) == before)
                {
                    s.Stability = SiteStability.Healthy;
                    NamePurchaseLocked(tx);
                    repaired = true;
                    saved = SaveToDisk();
                    if (!saved) { s.Stability = before; if (tx != null) _purchaseTx.Remove(tx.Id); }
                }
            }
            if (!repaired)
            {
                RefundPurchase(tx);
                return (false, "The site's condition changed - nothing was charged.");
            }
            if (!saved)
            {
                RefundPurchase(tx);
                return (false, "The server couldn't save that repair - your silver was returned. Try again shortly.");
            }
            AtPurchasePhaseForTest?.Invoke("saved");
            SettlePurchase(tx);
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

            // Resolved before _lock, since WorldStore takes its own.
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

        // Stamped when it became claimable; the operation lookup is only a fallback for older rows and dies on pruning.
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

            // Eligibility first, outside _lock.
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
                string capWhy = Frontier.FrontierCapture.CapRefusalFor(owned, ccfg.Limits.PerPlayer, false);
                if (capWhy != null) return (false, capWhy);
            }

            // Guild claims are validated against CURRENT membership and rank; the client never names the guild.
            string guild = "";
            if (forGuild)
            {
                guild = Guilds.GuildStore.CurrentGuildOf(user) ?? "";
                if (string.IsNullOrEmpty(guild)) return (false, "You are not in a guild.");
                string rank = Guilds.GuildStore.RankOf(user) ?? "";
                if (!Guilds.GuildPermissions.Can(rank, Guilds.GuildCapability.ManageSites))
                    return (false, "Only a guild officer or above can claim for the guild.");
                int gOwned;
                lock (_lock) gOwned = CountGuildOwnedLocked(guild);
                string gCapWhy = Frontier.FrontierCapture.CapRefusalFor(gOwned, ccfg.Limits.PerGuild, true);
                if (gCapWhy != null) return (false, gCapWhy);
            }

            // Read once here: a guild that disbands after this leaves the site bound to an incarnation nobody holds.
            string identity = forGuild ? Guilds.GuildIdentity.Of(guild) : null;
            if (forGuild && identity == null) return (false, "Your guild no longer exists.");

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
                string wasBinding = GuildBindingOf(tile);

                s.OutpostState = SiteEntry.OutpostCaptured;
                s.ClaimWindowEndsUtcTicks = 0;
                s.CapturedBy = forGuild ? guild : user;

                if (forGuild) { s.OwnerKind = SiteEntry.OwnerGuildKind; s.ControllingGuild = guild; s.OwnerUsername = ""; _guildOwner[tile] = identity; }
                else          { s.OwnerKind = SiteEntry.OwnerPlayer;    s.OwnerUsername = user;     s.ControllingGuild = ""; _guildOwner.TryRemove(tile, out _); }

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
                    if (wasBinding != null) _guildOwner[tile] = wasBinding; else _guildOwner.TryRemove(tile, out _);
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
            Global.Federation.FederationService.RecordGuildWork("frontier", user);
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

            int cost = Math.Max(0, SitesConfig.Current.Costs.BuildingSilver);
            if (!ChargeForSite(user, tile, cost, $"site building ({k}, tile {tile})", out Transactions.KmhTransaction tx, out string refusal))
                return (false, refusal ?? $"That building costs {Util.SilverFmt.Format(cost)} - your treasury is short.");
            AtPurchasePhaseForTest?.Invoke("charged");

            bool added = false, saved = false;
            lock (_lock)
            {
                if (_byTile.TryGetValue(tile, out SiteEntry s) && s != null
                    && SiteOwnership.CanManage(s, user)
                    && SiteBuildings.CanAdd(s, k, s.OutputTier, out _))
                {
                    var building = new SiteBuilding { Kind = k, Level = 1, State = SiteBuilding.StateOperational };
                    s.Buildings.Add(building);
                    NamePurchaseLocked(tx);
                    added = true;
                    saved = SaveToDisk();
                    if (!saved) { s.Buildings.Remove(building); if (tx != null) _purchaseTx.Remove(tx.Id); }
                }
            }
            if (!added)
            {
                // Lost the race, so the silver goes straight back rather than being kept for nothing.
                RefundPurchase(tx);
                return (false, "That slot was just taken - nothing was charged.");
            }
            if (!saved)
            {
                RefundPurchase(tx);
                return (false, "The server couldn't save that building - your silver was returned. Try again shortly.");
            }
            AtPurchasePhaseForTest?.Invoke("saved");
            SettlePurchase(tx);
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
                if (!SiteOwnership.CanAdminister(s, user)) return (false, SiteOwnership.CanManage(s, user) ? OfficerOnly : "That isn't your site.");
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

        // Fixed order: a preset's skill, then the classified family's, then the legacy guess for an uncatalogued server.
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
                if (cfg.Limits.ServerWide > 0 && _byTile.Count >= cfg.Limits.ServerWide)
                    return (false, "The server has reached its site limit.");
                if (cfg.Limits.PerPlayer > 0 && CountOwnedLocked(owner) >= cfg.Limits.PerPlayer)
                    return (false, $"You already own the maximum of {cfg.Limits.PerPlayer} site(s).");
                if (cfg.Limits.PerGuild > 0 && !string.IsNullOrEmpty(ownerGuild) && CountGuildOwnedLocked(ownerGuild) >= cfg.Limits.PerGuild)
                    return (false, $"Your guild has reached its {cfg.Limits.PerGuild}-site limit.");
                if (cfg.Limits.BuildCooldownMinutes > 0 && _lastBuildUtc.TryGetValue(owner, out long last)
                    && DateTime.UtcNow.Ticks - last < TimeSpan.FromMinutes(cfg.Limits.BuildCooldownMinutes).Ticks)
                    return (false, $"Build cooldown - wait a bit before building another site (every {cfg.Limits.BuildCooldownMinutes} min).");
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
            q.MaxAmount    = Math.Max(1, Math.Min(SitesConfig.Current.Output.CustomMaxRewardAmount, plan.Cls.MaxAmount));
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
            // Shown on the world map to every player, so it must be an item name and nothing that renders.
            if (!Util.KmhSafe.IsItemKey(itemDefName)) { why = "That isn't an item name."; return false; }

            // Unknown, rare and tech items land in a disabled tier, so a site cannot print gear.
            SiteOutputClass cls;
            int tierMaxAmount;
            if (cfg.Output.UseTiers)
            {
                // Resolve the human label server-side so owners can allow/block by exact label, not just raw defName.
                string label = Features.ItemLabels.ItemLabelCache.LabelFor(itemDefName);
                cls = SiteOutputRules.Classify(itemDefName, label);
                if (!cls.IsAllowed)
                {
                    if (!quiet) Diagnostics.ServerLog.Info($"Sites: {owner} output '{itemDefName}' ('{label}') rejected - {cls.BlockReason} (tier {cls.TierNumber} {cls.TierName}).");
                    why = cls.BlockReason; return false;
                }
                tierMaxAmount = Math.Min(cfg.Output.CustomMaxRewardAmount, cls.MaxAmount);
            }
            else
            {
                // Turning tiers off must not silently reopen all-def output, so the safety gate stays on by default.
                if (!cfg.Output.AllowUnsafeLegacy
                    && !Items.KmhItemSafety.IsAllowedSiteOutput(itemDefName, out string outReason))
                { why = outReason; return false; }
                cls = new SiteOutputClass
                {
                    IsAllowed = true, TierNumber = 1, TierName = "Basic", RelevantSkill = DetermineRelevantSkill(itemDefName),
                    MaxAmount = cfg.Output.CustomMaxRewardAmount, CostMultiplier = 1.0, CycleMultiplier = 1.0,
                    MaxSpeedMultiplier = 3.0, MaxOutputMultiplier = 2.0,
                };
                tierMaxAmount = cfg.Output.CustomMaxRewardAmount;
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
            if (cfg.Archetypes.Enabled && SiteCatalogStore.HasCatalog)
            {
                if (archId == SiteArchetypes.Custom && !cfg.Archetypes.AllowCustom)
                { why = "Custom sites are disabled on this server - pick one of the site types."; return false; }

                Dto.SiteOutputMetadata meta = SiteCatalogStore.Lookup(itemDefName);
                family = meta?.Family ?? SiteOutputFamilies.Unknown;
                if (!SiteArchetypeRegistry.AcceptsOutput(archId, meta))
                {
                    SiteArchetypeDef def = SiteArchetypeRegistry.Get(archId);
                    why = family == SiteOutputFamilies.Unknown
                        ? $"Colony Nexus has not classified '{itemDefName}' yet, so no site type can be built on it."
                        : $"A {def.DisplayName} cannot produce {SiteOutputFamilies.DisplayName(family).ToLowerInvariant()} items.";
                    if (!quiet) Diagnostics.ServerLog.Info($"Sites: {owner} output '{itemDefName}' rejected - archetype '{archId}' does not accept family '{family}'.");
                    return false;
                }
                archCostMult = SiteArchetypeRegistry.Get(archId).CostMultiplier;
            }

            // Refused without a trusted value: an under-reported client figure would buy a cheaper, faster, richer site.
            marketValuePerUnit = Items.KmhItemSafety.TrustedMarketValueOrZero(itemDefName, marketValuePerUnit, out bool valueSuspicious);
            if (valueSuspicious && !quiet)
                Diagnostics.ServerLog.Warn($"Sites: {owner} reported an off market value for {itemDefName} (tile {tile}) - using the trusted catalog value.");
            if (marketValuePerUnit <= 0f)
            {
                why = $"Colony Nexus has no trusted market value for '{itemDefName}' yet, so it cannot price a site on it. "
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
            if (cfg.Limits.ServerWide > 0 && _byTile.Count >= cfg.Limits.ServerWide)
                return "The server reached its site limit";
            if (cfg.Limits.PerPlayer > 0 && CountOwnedLocked(owner) >= cfg.Limits.PerPlayer)
                return $"You already own the maximum of {cfg.Limits.PerPlayer} site(s)";
            if (cfg.Limits.PerGuild > 0 && !string.IsNullOrEmpty(ownerGuild)
                && CountGuildOwnedLocked(ownerGuild) >= cfg.Limits.PerGuild)
                return $"Your guild reached its {cfg.Limits.PerGuild}-site limit";
            if (cfg.Limits.BuildCooldownMinutes > 0 && _lastBuildUtc.TryGetValue(owner, out long last)
                && DateTime.UtcNow.Ticks - last < TimeSpan.FromMinutes(cfg.Limits.BuildCooldownMinutes).Ticks)
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
                if (!SiteOwnership.CanAdminister(pre, user)) return (false, SiteOwnership.CanManage(pre, user) ? OfficerOnly : "That isn't your site.");
                // One-time: after this it is an ordinary Site, changed through the ordinary Site controls.
                if (!string.IsNullOrEmpty(pre.ItemDefName))
                    return (false, "That outpost is already set up.");
            }

            if (!PlanOutput(user, itemDefName, amountPerCycle, marketValuePerUnit, archetype, tile, out OutputPlan plan, out string why))
                return (false, why);

            // Re-asked because classification above released the lock, and membership can change in that window.
            if (!CanAdministerTile(tile, user)) return (false, "That isn't your site.");

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
            if (!ChargeForSite(owner, tile, cost, $"site build (tile {tile})", out Transactions.KmhTransaction tx, out string chargeRefusal))
                return (false, chargeRefusal ?? $"Build costs {Util.SilverFmt.Format(cost)} - your treasury is short.");
            AtPurchasePhaseForTest?.Invoke("charged");

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
            if (cfg.Workers.AutoAddOwner)
            {
                site.Workers.Add(owner);
                site.WorkerProgress[owner] = new WorkerProgressDto { JoinedUtcTicks = DateTime.UtcNow.Ticks, Destination = SiteEntry.DestTreasury };
            }
            // Re-checked in the same lock that inserts, or two builds on different tiles could both pass one cap.
            string refusal;
            bool saved = false;
            lock (_lock)
            {
                refusal = CommitRefusalLocked(owner, tile, cfg, ownerGuild);
                if (refusal == null)
                {
                    _byTile[tile] = site; _lastBuildUtc[owner] = DateTime.UtcNow.Ticks;
                    NamePurchaseLocked(tx);
                    saved = SaveToDisk();
                    if (!saved) { _byTile.Remove(tile); _lastBuildUtc.Remove(owner); if (tx != null) _purchaseTx.Remove(tx.Id); }
                }
            }
            if (refusal != null)
            {
                RefundPurchase(tx);
                return (false, refusal + " - your build cost was refunded.");
            }
            // The build cost has already left the treasury, so a site the disk never took is refunded, not kept.
            if (!saved)
            {
                RefundPurchase(tx);
                return (false, "The server couldn't save that site - your build cost was refunded. Try again shortly.");
            }
            AtPurchasePhaseForTest?.Invoke("saved");
            SettlePurchase(tx);
            RaiseSite(tile, owner, "built");
            PlayerStats.PlayerStatsStore.BumpSitesBuilt(owner);
            // Named by its type, not the item's internal def name, and saying what starts it.
            string kind = SiteArchetypeRegistry.Get(plan.ArchId)?.DisplayName;
            return (true, $"Built your {(string.IsNullOrEmpty(kind) ? "new" : kind)} site for {Util.SilverFmt.Format(cost)}. "
                        + "Send a caravan there and assign a colonist to start it.");
        }

        public const string BlockedPawnAway = "pawn away from site";

        // Client-named and shown to everyone viewing the site, so made inert like quest titles.
        internal static string WorkerName(string raw) => Util.KmhSafe.Cap((raw ?? "").Replace('<', '(').Replace('>', ')'), 48);

        // present=false keeps the worker assigned but stops production; changed=false is a silent re-validation.
        public static (bool ok, string reason, bool changed) JoinWorker(string username, int tile, int baseSkillLevel,
            string pawnName = "", int pawnLoadId = -1, bool present = true)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.", false);
            // A headless server cannot verify the reported pawn, so skill and name stay advisory.
            if (SitesConfig.Current.Workers.RequirePawns && pawnLoadId <= 0)
                return (false, "This site needs a real colonist. Send a caravan to the site tile, then assign a pawn.", false);
            baseSkillLevel = Items.KmhItemSafety.ValidateClientSkill(baseSkillLevel, out _);
            pawnName = WorkerName(pawnName);
            int workerCap = SitesConfig.Current.Limits.WorkerSitesPerPlayer;
            string owner; bool refreshed = false, changed = true;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.", false);
                bool isOwner = SiteOwnership.CanManage(s, username);
                if (!isOwner && !CanAccess(s, username)) return (false, "This site isn't open to you.", false);

                // Keyed on the PAWN, or a second colonist overwrites the first instead of filling the next slot.
                string key = SiteWorkerKey.For(username, pawnLoadId);
                if (s.WorkerProgress.TryGetValue(key, out WorkerProgressDto existing) && existing != null)
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
                    existing.Username         = username;   // backfills a migrated row
                    refreshed = true;
                    owner = s.OwnerUsername;
                }
                else
                {
                    // A pawn already working here for another account is a collision or a claim; both lose.
                    if (pawnLoadId > 0)
                        foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                            if (kv.Value != null && kv.Value.PawnLoadId == pawnLoadId && !SiteWorkerKey.IsOwnedBy(kv.Key, username))
                                return (false, "That colonist is already working this site for another player.", false);

                    int perPlayer = SitesConfig.Current.Limits.WorkersPerPlayerPerSite;
                    if (perPlayer > 0 && MineCount(s, username) >= perPlayer)
                        return (false, $"You already have {perPlayer} colonist(s) on this site.", false);

                    int maxWorkers = MaxWorkersLive(s);
                    if (WorkerCount(s) >= maxWorkers) return (false, $"This site is full ({maxWorkers} workers).", false);
                    // Counts sites, not colonists: three on one site still cost one site-work slot.
                    if (workerCap > 0 && !WorksLocked(s, username) && CountWorkedLocked(username) >= workerCap)
                        return (false, $"You're at your {workerCap}-site work limit.", false);
                    // The same worker finally naming a colonist; keeping both would double their slot.
                    string legacy = SiteWorkerKey.For(username, -1);
                    if (SiteWorkerKey.IsPerPawn(key) && s.WorkerProgress.TryGetValue(legacy, out WorkerProgressDto old) && old != null && old.Legacy)
                    {
                        s.Workers.RemoveAll(w => string.Equals(w, legacy, StringComparison.OrdinalIgnoreCase));
                        s.WorkerProgress.Remove(legacy);
                    }
                    s.Workers.Add(key);
                    s.WorkerProgress[key] = new WorkerProgressDto
                    {
                        JoinedUtcTicks   = DateTime.UtcNow.Ticks,
                        BaseSkillLevel   = baseSkillLevel,
                        Destination      = SiteEntry.DestTreasury,
                        Username         = username,
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

        // pawnLoadId <= 0 pulls every colonist this player has here; naming one removes only that colonist.
        public static (bool ok, string reason) LeaveWorker(string username, int tile, int pawnLoadId = -1)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.");
            string owner; int removed; string left;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");

                bool One(string w) => pawnLoadId > 0
                    ? string.Equals(w, SiteWorkerKey.For(username, pawnLoadId), StringComparison.OrdinalIgnoreCase)
                    : SiteWorkerKey.IsOwnedBy(w, username);

                var going = new List<string>();
                foreach (string w in s.Workers) if (One(w)) going.Add(w);
                if (going.Count == 0)
                    return (false, pawnLoadId > 0 ? "That colonist doesn't work here." : "You don't work here.");

                left = pawnLoadId > 0 && s.WorkerProgress.TryGetValue(going[0], out WorkerProgressDto lwp) && lwp != null
                       && !string.IsNullOrEmpty(lwp.PawnName) ? lwp.PawnName : "";
                foreach (string w in going) s.WorkerProgress.Remove(w);
                removed = s.Workers.RemoveAll(One);
                owner = s.OwnerUsername;
            }
            SaveToDisk();
            RaiseSite(tile, owner, "worker_left");
            return (true, !string.IsNullOrEmpty(left) ? $"{left} left the site."
                        : removed > 1 ? $"Pulled {removed} colonists off the site." : "Left the site.");
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
            // Resolved before _lock: CanManage reads GuildStore, which takes its own.
            bool mayManage = CanManageTile(tile, username);
            string what;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                // All of theirs: shares pay once per account, so one row pointed elsewhere would depend on read order.
                var mine = new List<WorkerProgressDto>();
                foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                    if (kv.Value != null && SiteWorkerKey.IsOwnedBy(kv.Key, username)) mine.Add(kv.Value);

                DestTarget target = ResolveDestTarget(scope, mine.Count > 0, SiteOwnership.IsOwnedBy(s, username),
                                                      SiteOwnership.IsGuildOwned(s), mayManage, dest, out string refusal);
                if (target == DestTarget.None) return (false, refusal);

                if (target == DestTarget.WorkerShare) { foreach (WorkerProgressDto w in mine) w.Destination = dest; what = "Your share"; }
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
                if (!SiteOwnership.CanAdminister(s, owner)) return (false, SiteOwnership.CanManage(s, owner) ? OfficerOnly : "You can't manage this site.");
                // Removing the site would destroy what it holds.
                if (s.StoredItems != null && s.StoredItems.Values.Any(q => q > 0))
                    return (false, "Collect this site's stored goods first.");
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
            return crew <= 0 ? 0 : crew * SiteStability.OutputFactor(s.Stability) * SiteArchetypeRegistry.RoadworksPerk(s.Archetype)
                                 * Global.Federation.FederationBenefits.RoadworksFactor(OwnerCurrentGuild(s));
        }

        // Caller holds _lock.
        private static string WorkerDestinationFor(SiteEntry s, string account)
        {
            if (s?.WorkerProgress == null) return SiteEntry.DestTreasury;
            foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                if (kv.Value != null && SiteWorkerKey.IsOwnedBy(kv.Key, account)) return kv.Value.Destination;
            return SiteEntry.DestTreasury;
        }

        // Returns the usernames whose treasury changed, so the sweeper can push them.
        public static HashSet<string> RunRewardCycle()
        {
            HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long now = DateTime.UtcNow.Ticks;
            double xpMult = SitesConfig.Current.Workers.XpMultiplier * World.WorldStore.WorkerXpMultiplier();

            // Planned under the lock so a concurrent join cannot mutate workers mid-enumeration.
            List<Delivery> plan = new List<Delivery>();
            // What a failed write has to undo: the timer that gates the next payout, and the XP that rode with it.
            List<(SiteEntry Site, long PrevTicks, double XpDelta)> advanced = new List<(SiteEntry, long, double)>();
            List<(int Tile, double Work)> roadWork = new List<(int, double)>();
            // Cycles completed per guild, reported to its federation once the timers are on disk.
            Dictionary<string, int> cyclesByGuild = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> roadProjectTiles = Roadworks.RoadworksStore.TilesWithActiveProjects();
            lock (_lock)
            {
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s.BlockedOutput) continue;
                    // Its guild disbanded or the name now belongs to another: paused, timer untouched, nobody paid.
                    if (SiteOwnership.IsGuildOwned(s) && !GuildBindingIsLive(s)) continue;
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

                    // Shares split per ACCOUNT, speed and skill per PAWN, so one player is never several claimants.
                    List<string> workers = new List<string>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!guildOwned && hasOwner) seen.Add(ownerAcct);   // an owner working their own site takes one slot
                    foreach (string w in s.Workers)
                        if (s.WorkerProgress.TryGetValue(w, out WorkerProgressDto awp) && awp != null && awp.IsActivePawnWorker
                            && seen.Add(SiteWorkerKey.AccountOf(w)))
                            workers.Add(SiteWorkerKey.AccountOf(w));

                    PlanShares(SitesConfig.Current.Output.RewardDistributionMode, totalProduced, hasOwner, workers,
                               s.AccessMode == SiteEntry.AccessPublic, s.OwnerTaxPercent,
                               out int ownerShare, out Dictionary<string, int> workerShares);
                    if (ownerShare <= 0 && workerShares.Count == 0) continue;

                    string siteGuild = OwnerCurrentGuild(s);
                    if (!string.IsNullOrEmpty(siteGuild))
                        cyclesByGuild[siteGuild] = (cyclesByGuild.TryGetValue(siteGuild, out int c) ? c : 0) + 1;

                    if (ownerShare > 0 && hasOwner)
                        plan.Add(new Delivery
                        {
                            Tile = s.Tile, Recipient = guildOwned ? "" : ownerAcct, Guild = guildOwned ? ownerAcct : "",
                            GuildIdentity = guildOwned ? GuildBindingOf(s.Tile) : null,
                            Dest = OwnerDestinationFor(s, guildOwned), ItemDefName = s.ItemDefName,
                            Amount = ownerShare, MarketplaceUnitPrice = s.MarketplaceUnitPrice,
                        });

                    foreach (KeyValuePair<string, int> kv in workerShares)
                    {
                        if (kv.Value <= 0) continue;
                        // kv.Key is an account; SetDestination keeps every row of theirs on one destination.
                        plan.Add(new Delivery { Tile = s.Tile, Recipient = kv.Key, Dest = WorkerDestinationFor(s, kv.Key),
                                                ItemDefName = s.ItemDefName, Amount = kv.Value, MarketplaceUnitPrice = s.MarketplaceUnitPrice });
                    }
                }
            }
            if (plan.Count == 0 && roadWork.Count == 0) return touched;

            // Guild tax splits off outside _lock (GuildStore takes its own), taken only in the record that pays the guild.
            var taxes = new Dictionary<int, (string Guild, string Identity, int Tax)>();
            for (int i = 0; i < plan.Count; i++)
            {
                var guildTax = GuildTaxFor(plan[i]);
                if (guildTax.Tax > 0) taxes[i] = guildTax;
            }

            // The cycle's payouts are written down in the SAME save as the timers that produced them: a crash after it
            // pays them at boot, a crash before it loses the cycle - never both, never twice.
            string stamp = NextCycleStamp();
            var owed = new List<OwedDelivery>();
            var stored = new List<(int Tile, string Def, int Qty)>();
            lock (_lock)
            {
                for (int i = 0; i < plan.Count; i++)
                    owed.AddRange(OweDeliveryLocked(plan[i], stamp, i, taxes.TryGetValue(i, out var t) ? t : default, stored));
                // Road work is owed the same way, on its marker after this save, so a crash neither skips nor doubles it.
                foreach ((int tile, double work) in roadWork)
                {
                    string m = _owed.RecordLocked(new Transactions.KmhOwedPayout
                    {
                        Marker = $"site:{tile}:{stamp}:road", Route = RouteRoad, Tile = tile, Work = work, Note = $"site#{tile} road work",
                    });
                    if (m != null) owed.Add(new OwedDelivery { Marker = m, Tile = tile });
                }
                if (!SaveToDisk())
                {
                    foreach (OwedDelivery o in owed) _owed.ForgetLocked(o.Marker);
                    foreach ((int tile, string def, int qty) in stored) UnstoreLocked(tile, def, qty);
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
            }
            AtCyclePhaseForTest?.Invoke("recorded");

            foreach (KeyValuePair<string, int> g in cyclesByGuild)
                Global.Federation.FederationService.Record("site", g.Key, "", g.Value);

            Dictionary<int, double> silverByTile = new Dictionary<int, double>();
            long roadRevision = Roadworks.RoadworksStore.Revision;
            foreach (OwedDelivery o in owed)
            {
                Transactions.KmhPayResult res = _owed.Settle(o.Marker, p => PaySite(p, live: true), HoldRefusedLocked);
                if (res == Transactions.KmhPayResult.Failed) continue;   // kept, and paid by the next sweep or boot
                if (!string.IsNullOrEmpty(o.Recipient)) touched.Add(o.Recipient);
                if (o.Silver > 0) silverByTile[o.Tile] = (silverByTile.TryGetValue(o.Tile, out double v) ? v : 0) + o.Silver;
            }
            if (Roadworks.RoadworksStore.Revision != roadRevision) Roadworks.RoadworksHandler.BroadcastSnapshot();
            if (silverByTile.Count > 0)
            {
                lock (_lock)
                    foreach (KeyValuePair<int, double> kv in silverByTile)
                        if (_byTile.TryGetValue(kv.Key, out SiteEntry s)) s.TotalSilverGenerated += kv.Value;
                SaveToDisk();
            }
            return touched;
        }

        internal static Action<string> AtCyclePhaseForTest;

        private static long _cycleSeq;
        // Unique per cycle across restarts: the clock does not repeat, and the counter separates two cycles in one tick.
        private static string NextCycleStamp() => DateTime.UtcNow.Ticks + "." + System.Threading.Interlocked.Increment(ref _cycleSeq);

        private struct OwedDelivery
        {
            public string Marker, Recipient;
            public int Tile;
            public long Silver;
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
                    ownerBonus += per - wShare;
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
            // The incarnation the site is bound to, so a vault re-created under the same name is never paid.
            public string GuildIdentity;
            public string Dest;
            public string ItemDefName;
            public int    Amount;
            public int    MarketplaceUnitPrice;
        }

        private const string RouteCaravan = "caravan", RouteMarket = "marketplace", RouteRoad = "roadwork";

        // Owed in the caller's lock and save; storage fills in that same save, and each marker is unique per cycle.
        private static List<OwedDelivery> OweDeliveryLocked(Delivery d, string stamp, int index,
                                                            (string Guild, string Identity, int Tax) tax,
                                                            List<(int Tile, string Def, int Qty)> stored)
        {
            var outp = new List<OwedDelivery>();
            if (d.Amount <= 0) return outp;
            string baseMarker = $"site:{d.Tile}:{stamp}:{index}";
            bool silver = IsSilver(d.ItemDefName);

            void Owe(Transactions.KmhOwedPayout p, string recipient, long silverUnits)
            {
                if (_owed.RecordLocked(p) != null)
                    outp.Add(new OwedDelivery { Marker = p.Marker, Recipient = recipient, Tile = d.Tile, Silver = silverUnits });
            }
            Dictionary<string, int> Units(int qty) => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [d.ItemDefName] = qty };

            if (!string.IsNullOrEmpty(d.Guild))
            {
                // No marketplace or colony here, or guild goods would land with whichever member configured them.
                int kept = d.Dest == SiteEntry.DestStorage ? TryStore(d.Tile, d.Guild, d.ItemDefName, d.Amount) : 0;
                if (kept > 0) stored.Add((d.Tile, d.ItemDefName, kept));
                int rest = d.Amount - kept;
                if (rest > 0)
                    Owe(new Transactions.KmhOwedPayout
                    {
                        Marker = baseMarker + ":guild", Guild = d.Guild, GuildIdentity = d.GuildIdentity ?? "",
                        Silver = silver ? rest : 0, Items = silver ? new Dictionary<string, int>() : Units(rest),
                        Tile = d.Tile, Note = $"site#{d.Tile} production",
                    }, d.Guild, silver ? rest : 0);
                return outp;
            }
            if (string.IsNullOrEmpty(d.Recipient)) return outp;
            string who = d.Recipient.ToLowerInvariant();

            if (silver)
            {
                int cut = tax.Tax > 0 && tax.Tax < d.Amount ? tax.Tax : 0;
                if (cut > 0)
                    // Back to the player if their guild is gone by the time it is paid: the tax had nobody to go to.
                    Owe(new Transactions.KmhOwedPayout
                    {
                        Marker = baseMarker + ":" + who + ":tax", Guild = tax.Guild, GuildIdentity = tax.Identity, Fallback = d.Recipient,
                        Silver = cut, Tile = d.Tile, Note = "site reward tax",
                    }, "", 0);
                Owe(new Transactions.KmhOwedPayout
                {
                    Marker = baseMarker + ":" + who, User = d.Recipient, Silver = d.Amount - cut,
                    Tile = d.Tile, Note = $"site#{d.Tile} reward",
                }, d.Recipient, d.Amount - cut);
                return outp;
            }

            int qty = d.Amount;
            string route = "", note = $"site#{d.Tile} reward";
            switch (d.Dest)
            {
                case SiteEntry.DestStorage:
                    // Overflow goes to the treasury, so storage is never a way to lose a cycle's output.
                    int inStore = TryStore(d.Tile, d.Recipient, d.ItemDefName, qty);
                    if (inStore > 0) stored.Add((d.Tile, d.ItemDefName, inStore));
                    qty -= inStore;
                    note = $"site#{d.Tile} reward (storage full)";
                    break;
                case SiteEntry.DestMarketplace: route = RouteMarket; break;
                case SiteEntry.DestCaravan:     route = RouteCaravan; break;
            }
            if (qty > 0)
                Owe(new Transactions.KmhOwedPayout
                {
                    Marker = baseMarker + ":" + who, User = d.Recipient, Items = Units(qty),
                    Route = route, Tile = d.Tile, UnitPrice = Math.Max(1, d.MarketplaceUnitPrice), Note = note,
                }, d.Recipient, 0);
            return outp;
        }

        // A live settle may send to the colony or list on the market first; a resumed one always pays the vault, so a
        // colony grant or a listing is never repeated by a replay.
        private static Transactions.KmhPayResult PaySite(Transactions.KmhOwedPayout p, bool live)
        {
            if (p == null) return Transactions.KmhPayResult.Paid;
            // Laid once per record by the roadworks store, which keeps the marker in the save that lays it.
            if (p.Route == RouteRoad)
                return Roadworks.RoadworksStore.ApplyWorkOnce(p.Marker, p.Tile, p.Work) ? Transactions.KmhPayResult.Paid : Transactions.KmhPayResult.Failed;
            if (!string.IsNullOrEmpty(p.Guild) || (p.Route != RouteCaravan && p.Route != RouteMarket) || p.Items == null || p.Items.Count != 1)
                return Transactions.KmhOwedPayouts.PayToVault(p);
            string def = ""; int qty = 0;
            foreach (KeyValuePair<string, int> kv in p.Items) { def = kv.Key; qty = kv.Value; }
            if (p.Route == RouteCaravan)
            {
                // Already minted for this record: the colony delivery owns it, owed until the colony acks it.
                if (Features.Delivery.DeliveryStore.IdForSource(p.Marker) != null) return Transactions.KmhPayResult.Paid;
                if (live && TryOweToColony(p.User, def, qty, p.Marker)) return Transactions.KmhPayResult.Paid;
                // Offline, or resumed: the vault holds it.
                return Transactions.KmhOwedPayouts.PayToVault(p);
            }
            Transactions.KmhPayResult res = Transactions.KmhOwedPayouts.PayToVault(p);
            // Deposited first, so a refused listing leaves the goods banked rather than lost.
            if (res == Transactions.KmhPayResult.Paid && live)
                Marketplace.MarketplaceStore.Post(p.User, def, qty, Math.Max(1, p.UnitPrice), "public", 0);
            return res;
        }

        // Under _lock: a guild payout whose guild is gone (or whose name now belongs to another) is kept in the
        // site's storage, in the write that clears the record. False keeps the record when the site itself is gone.
        private static bool HoldRefusedLocked(Transactions.KmhOwedPayout p)
        {
            if (p == null || p.Tile < 0) return false;
            var put = new List<KeyValuePair<string, int>>();
            if (p.Silver > 0) put.Add(new KeyValuePair<string, int>("Silver", (int)Math.Min(int.MaxValue, p.Silver)));
            if (p.Items != null) foreach (KeyValuePair<string, int> kv in p.Items) if (kv.Value > 0) put.Add(kv);
            if (!_byTile.TryGetValue(p.Tile, out SiteEntry s) || s == null)
            {
                Diagnostics.ServerLog.Error($"Sites: {p.Note} owed to guild '{p.Guild}', which is gone, and site#{p.Tile} is gone too - kept as {p.Marker} for 'kmh orphans'.");
                return false;
            }
            foreach (KeyValuePair<string, int> kv in put) RestoreStored(p.Tile, kv.Key, kv.Value, logged: false);
            Diagnostics.ServerLog.Warn($"Sites: guild '{p.Guild}' can no longer be paid {p.Note} - kept in site#{p.Tile}'s storage.");
            return true;
        }

        // The undo of a TryStore whose save failed. Caller holds _lock.
        private static void UnstoreLocked(int tile, string def, int qty)
        {
            if (!_byTile.TryGetValue(tile, out SiteEntry s) || s?.StoredItems == null || !s.StoredItems.TryGetValue(def, out int have)) return;
            if (have - qty <= 0) s.StoredItems.Remove(def); else s.StoredItems[def] = have - qty;
        }

        // A member's silver share only, and none when the guild has no live identity to pay.
        private static (string Guild, string Identity, int Tax) GuildTaxFor(Delivery d)
        {
            if (!string.IsNullOrEmpty(d.Guild) || string.IsNullOrEmpty(d.Recipient) || !IsSilver(d.ItemDefName)) return default;
            (string guild, int tax) = Guilds.GuildStore.SiteRewardTaxFor(d.Recipient, d.Amount);
            string gid = Guilds.GuildIdentity.Of(guild);
            return tax > 0 && gid != null ? (guild, gid, tax) : default;
        }

        private static int DeliverNow(Delivery d)
        {
            var tax = GuildTaxFor(d);
            var owed = new List<OwedDelivery>();
            var stored = new List<(int Tile, string Def, int Qty)>();
            lock (_lock)
            {
                owed = OweDeliveryLocked(d, NextCycleStamp(), 0, tax, stored);
                if (!SaveToDisk())
                {
                    foreach (OwedDelivery o in owed) _owed.ForgetLocked(o.Marker);
                    foreach ((int tile, string def, int qty) in stored) UnstoreLocked(tile, def, qty);
                    return 0;
                }
            }
            foreach (OwedDelivery o in owed) _owed.Settle(o.Marker, p => PaySite(p, live: true), HoldRefusedLocked);
            return d.Amount;
        }

        internal static int DeliverForTest(string recipient, int tile, string itemDefName, int amount)
            => DeliverNow(new Delivery { Recipient = recipient, Tile = tile, ItemDefName = itemDefName, Amount = amount, Dest = SiteEntry.DestCaravan });

        private static bool IsSilver(string def) => string.Equals(def, "Silver", StringComparison.OrdinalIgnoreCase);

        internal static int DeliverGuildOwnedForTest(int tile, string guild, string def, int amount, string dest)
            => DeliverNow(new Delivery { Tile = tile, Guild = guild, GuildIdentity = GuildBindingOf(tile), ItemDefName = def, Amount = amount, Dest = dest });

        public static void RecoverOnBoot()
        {
            (int paid, int left) = _owed.Resume(p => PaySite(p, live: false), HoldRefusedLocked);
            if (paid > 0 || left > 0) Diagnostics.ServerLog.Warn($"Sites: recovery paid {paid} interrupted payout(s), {left} still owed.");
        }

        public static int RetryOwed() => _owed.Count == 0 ? 0 : _owed.Resume(p => PaySite(p, live: false), HoldRefusedLocked, quiet: true).paid;
        internal static int OwedCount => _owed.Count;

        // The caller saves: a reset writes this down as one of its steps.
        internal static int DropOwedFor(string user) { lock (_lock) return _owed.DropForLocked(user); }

        internal static int OwedCountForTest => _owed.Count;
        internal static (long Silver, int Units) UnpaidOwedForTest(string user) => _owed.UnpaidTotals(user);
        internal static Action<string> AtPayoutPhaseForTest { set => _owed.AtPhaseForTest = value; }

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

        // Goods go to the owning account. The storage debit and what it owes are ONE save under _lock, so a failed save
        // leaves storage as it was; a guild's goods are one record per kind, so each lands - or goes back - whole.
        public static (bool ok, string reason) CollectStorage(string user, int tile)
        {
            if (string.IsNullOrEmpty(user)) return (false, "No caller.");
            var markers = new List<string>();
            bool guildOwned;
            string ownerAcct;
            int units = 0;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return (false, "No site on that tile.");
                if (!SiteOwnership.CanManage(s, user)) return (false, "You can't manage this site.");
                if (s.StoredItems == null || s.StoredItems.Count == 0) return (false, "Nothing is stored here.");
                guildOwned = SiteOwnership.IsGuildOwned(s);
                ownerAcct  = SiteOwnership.ProductionAccount(s);
                if (string.IsNullOrEmpty(ownerAcct)) return (false, "This site has no owner to collect for.");
                var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> kv in s.StoredItems) if (kv.Value > 0) taken[kv.Key] = kv.Value;
                if (taken.Count == 0) return (false, "Nothing is stored here.");

                string baseMarker = $"site:{tile}:collect:{NextCycleStamp()}";
                if (guildOwned)
                {
                    string identity = GuildBindingOf(tile) ?? "";
                    foreach (KeyValuePair<string, int> kv in taken)
                        markers.Add(_owed.RecordLocked(new Transactions.KmhOwedPayout
                        {
                            Marker = baseMarker + ":" + kv.Key.ToLowerInvariant(), Guild = ownerAcct, GuildIdentity = identity,
                            Silver = IsSilver(kv.Key) ? kv.Value : 0,
                            Items = IsSilver(kv.Key) ? new Dictionary<string, int>()
                                                     : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [kv.Key] = kv.Value },
                            Tile = tile, Note = $"site#{tile} storage collected",
                        }));
                }
                else
                {
                    var items = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    long silver = 0;
                    foreach (KeyValuePair<string, int> kv in taken) { if (IsSilver(kv.Key)) silver += kv.Value; else items[kv.Key] = kv.Value; }
                    markers.Add(_owed.RecordLocked(new Transactions.KmhOwedPayout
                    {
                        Marker = baseMarker, User = ownerAcct, Silver = silver, Items = items,
                        Tile = tile, Note = $"site#{tile} storage collected",
                    }));
                }
                markers.RemoveAll(m => m == null);
                foreach (int q in taken.Values) units += q;

                var before = new Dictionary<string, int>(s.StoredItems, StringComparer.OrdinalIgnoreCase);
                s.StoredItems.Clear();
                if (!SaveToDisk())
                {
                    foreach (string m in markers) _owed.ForgetLocked(m);
                    s.StoredItems = before;
                    return (false, "The server couldn't save that - nothing was collected. Try again shortly.");
                }
            }
            AtCollectPhaseForTest?.Invoke("recorded");
            foreach (string m in markers) _owed.Settle(m, p => PaySite(p, live: true), HoldRefusedLocked);
            RaiseSite(tile, user, "storage_collected");
            return (true, guildOwned
                ? $"Collected {units} item(s) from site storage into the {ownerAcct} vault."
                : $"Collected {units} item(s) from site storage.");
        }

        internal static Action<string> AtCollectPhaseForTest;

        private static bool RestoreStored(int tile, string defName, int qty, bool logged = true)
        {
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return false;
                if (s.StoredItems == null) s.StoredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                s.StoredItems.TryGetValue(defName, out int have);
                s.StoredItems[defName] = Util.KmhSafe.AddSaturating(have, qty);
            }
            if (logged)
                Diagnostics.ServerLog.Warn($"Sites: site#{tile} storage collection could not be credited - {qty}x {defName} returned to storage.");
            return true;
        }

        // Off-map wealth one button press from the player, so it must not dodge raid scaling.
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

        internal static Func<string, ServerClient> OnlineClientForTest;

        private static ServerClient OnlineClient(string username)
        {
            if (OnlineClientForTest != null) return OnlineClientForTest(username);
            foreach (ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true && c.Listener != null
                    && string.Equals(c.GetData<UserFile>()?.Username, username, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // Owed until the colony acks it, like a withdrawal: sent bare, a dropped link or failed drop would lose it.
        private static bool TryOweToColony(string username, string defName, int amount, string sourceMarker)
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            ServerClient c = OnlineClient(username);
            if (c == null) return false;
            // Minted once per owed record: a replay finds the delivery it already made instead of making another.
            Features.Delivery.OutboundDelivery owed = Features.Delivery.DeliveryStore.OweItemsOnce(username,
                new Dictionary<string, int> { { defName, amount } }, sourceMarker);
            if (owed == null) return false;   // could not be written down, so the treasury takes it instead
            Features.Delivery.DeliveryHandler.Send(c, owed);
            return true;
        }

        private const string OfficerOnly = "Only a guild officer or above can do that to a guild site.";

        internal static bool CanAdministerTile(int tile, string username)
            => ProbeTile(tile, out SiteEntry probe) && SiteOwnership.CanAdminister(probe, username);

        private static bool ProbeTile(int tile, out SiteEntry probe)
        {
            probe = null;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) return false;
                probe = ProbeOf(s);
            }
            return true;
        }

        // Only the fields the ownership rules read, so they can run outside _lock.
        private static SiteEntry ProbeOf(SiteEntry s) => new SiteEntry
        {
            Tile = s.Tile, OwnerKind = s.OwnerKind, OwnerUsername = s.OwnerUsername,
            ControllingGuild = s.ControllingGuild,
        };

        // CanManage is resolved outside _lock because it reads GuildStore, which takes its own.
        internal static bool CanManageTile(int tile, string username)
            => !string.IsNullOrEmpty(username)
               && ProbeTile(tile, out SiteEntry probe) && SiteOwnership.CanManage(probe, username);

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
                probe = ProbeOf(s);
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

        // The Director excludes these so it never has to reason about geography.
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
                PriceMultiplier  = SitesConfig.Current.Costs.PriceMultiplier,
                MaxRewardAmount  = SitesConfig.Current.Output.CustomMaxRewardAmount,

                BuildingCostSilver  = Math.Max(0, SitesConfig.Current.Costs.BuildingSilver),
                RepairCostPerPoint  = Math.Max(0, SitesConfig.Current.Costs.RepairPerPoint),
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
                    bool mine = SiteOwnership.CanManage(s, username) || WorksLocked(s, username);
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
                TiersEnabled    = cfg.Output.UseTiers,
                MaxAllowedTier  = cfg.Output.MaxAllowedTier,
                Tier4Enabled    = cfg.Output.Tiers != null && cfg.Output.Tiers.Length >= 4 && cfg.Output.Tiers[3].Enabled,
                IncludesBlocked = includeBlocked,
                // Without a catalog everything classifies Unknown, so a picker where nothing is eligible reads as broken.
                ArchetypesReady = cfg.Archetypes.Enabled && SiteCatalogStore.HasCatalog,
                // What the server ADOPTED, not what a client sent: a refused push never applies.
                CatalogFingerprint = SiteCatalogStore.Fingerprint ?? "",
            };
            if (snap.ArchetypesReady)
                foreach (SiteArchetypeDef a in SiteArchetypeRegistry.All)
                {
                    if (a.IsCustom && !cfg.Archetypes.AllowCustom) continue;
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
                int maxAmount   = Math.Max(1, Math.Min(cfg.Output.CustomMaxRewardAmount, cls.MaxAmount));
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
                int legacy;
                lock (_lock)
                {
                    _byTile.Clear();
                    _guildOwner.Clear();
                    _owed.LoadLocked(state.Owed);
                    _purchaseTx.Clear();
                    if (state.PurchaseTx != null) foreach (KeyValuePair<string, long> kv in state.PurchaseTx) _purchaseTx[kv.Key] = kv.Value;
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

                        // Only the work skill is migrated: a site training the wrong one is invisible to its owner.
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
                        if (!cls.IsAllowed && SitesConfig.Current.Output.UseTiers && !IsUnconfiguredOutpost(s))
                        {
                            s.BlockedOutput = true; blocked++;
                            Diagnostics.ServerLog.Warn($"Sites: PAUSED legacy site tile {s.Tile} ({s.OwnerUsername}) - output '{s.ItemDefName}' ('{label}') is now blocked ({cls.BlockReason}). Admin review needed.");
                        }
                        else s.BlockedOutput = false;

                        // Kept rather than deleted, so an old account-worker can be replaced by a real pawn.
                        if (SitesConfig.Current.Workers.RequirePawns)
                            foreach (WorkerProgressDto wp in s.WorkerProgress.Values)
                                if (wp != null && wp.PawnLoadId <= 0 && !wp.Legacy)
                                {
                                    wp.Legacy = true;
                                    wp.BlockedReason = "legacy account worker - send a caravan and assign a real pawn";
                                    legacyWorkers++;
                                }
                        _byTile[s.Tile] = s;
                    }
                    // Guilds load first, so their identities are published before any site is judged against them.
                    legacy = MarkLegacyGuildSitesLocked(state.GuildOwners);
                }
                // Written now, or a guild re-created under the orphaned name could claim the site next boot.
                if (legacy > 0 && !SaveToDisk())
                    Diagnostics.ServerLog.Error($"Sites: {legacy} paused legacy guild site(s) could not be saved - they are re-derived on the next boot.");
                LegacyBlockedSiteCount = blocked;
                LegacyWorkerCount = legacyWorkers;
                Diagnostics.ServerLog.Diag($"Sites: loaded {state.Sites.Count} site(s) from disk{(blocked > 0 ? $" ({blocked} paused - output now blocked)" : "")}{(legacyWorkers > 0 ? $" ({legacyWorkers} legacy account-worker(s) disabled - reassign real pawns)" : "")}{(reskilled > 0 ? $" ({reskilled} re-skilled from the classified catalog)" : "")}");
                if (MigratedWorkerKeys > 0)
                    Diagnostics.ServerLog.Info($"Sites: re-keyed {MigratedWorkerKeys} site worker(s) from account to colonist - each keeps its XP, cycles and destination, and players can now assign more than one colonist per site.");
                if (UnmigratedLegacyWorkers > 0)
                    Diagnostics.ServerLog.Info($"Sites: {UnmigratedLegacyWorkers} worker row(s) named no colonist and stayed account-keyed - they re-key themselves when that player assigns a real pawn.");
            }
        }

        internal static int MigratedWorkerKeys { get; private set; }
        internal static int UnmigratedLegacyWorkers { get; private set; }
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
            MigrateWorkerKeys(s);

            if (s.StoredItems != null) NormalizeStored(s);
        }

        // Re-keys an account-keyed row onto its own pawn, keeping its progress. A row naming no pawn keeps the
        // account key: an invented id could collide with a real colonist and hand someone else's work away.
        internal static void MigrateWorkerKeys(SiteEntry s)
        {
            if (s?.WorkerProgress == null) return;
            var moved = new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);
            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
            {
                WorkerProgressDto w = kv.Value;
                if (string.IsNullOrEmpty(w.Username)) w.Username = SiteWorkerKey.AccountOf(kv.Key);

                string want = SiteWorkerKey.IsPerPawn(kv.Key) ? kv.Key : SiteWorkerKey.For(w.Username, w.PawnLoadId);
                if (!string.Equals(want, kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    // A clash means a corrupt file; keep the row rather than overwrite a stranger's progress.
                    if (moved.ContainsKey(want) || s.WorkerProgress.ContainsKey(want))
                    { UnmigratedLegacyWorkers++; moved[kv.Key] = w; continue; }
                    rename[kv.Key] = want;
                    MigratedWorkerKeys++;
                    moved[want] = w;
                    continue;
                }
                if (!SiteWorkerKey.IsPerPawn(want) && w.PawnLoadId <= 0) UnmigratedLegacyWorkers++;
                moved[kv.Key] = w;
            }

            if (rename.Count == 0) { s.WorkerProgress = moved; return; }
            s.WorkerProgress = moved;
            if (s.Workers == null) return;
            for (int i = 0; i < s.Workers.Count; i++)
                if (rename.TryGetValue(s.Workers[i] ?? "", out string to)) s.Workers[i] = to;
        }

        private static void NormalizeStored(SiteEntry s)
        {
            var stored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> kv in s.StoredItems)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                stored.TryGetValue(kv.Key, out int cur);
                stored[kv.Key] = Util.KmhSafe.AddSaturating(cur, kv.Value);
            }
            s.StoredItems = stored;
        }

        // Skips Build so a worker test does not measure the build cost path instead.
        internal static int InsertSiteForTest(int tile, string owner, int maxWorkers)
            => InsertSiteForTest(tile, owner, maxWorkers, SiteArchetypes.Quarry, 1);

        // Roadworks reads archetype and tier through TryGetSiteFacts, so a roadworks test sets both directly.
        internal static int InsertSiteForTest(int tile, string owner, int maxWorkers, string archetype, int tier)
        {
            SiteEntry site = new SiteEntry
            {
                Tile = tile,
                OwnerUsername = owner,
                ItemDefName = "Steel",
                BaseAmountPerCycle = 1,
                MarketValuePerUnit = 1f,
                BaseCycleTimeMs = 60000,
                AccessMode = SiteEntry.AccessPublic,
                MaxWorkers = maxWorkers,
                Archetype = SiteArchetypes.Normalize(archetype),
                OutputTier = tier,
                LastRewardUtcTicks = DateTime.UtcNow.Ticks,
            };
            lock (_lock) _byTile[tile] = site;
            return tile;
        }

        internal static void RemoveSiteForTest(int tile)
        {
            lock (_lock) _byTile.Remove(tile);
        }

        internal static SiteEntry SiteForTest(int tile)
        {
            lock (_lock) return _byTile.TryGetValue(tile, out SiteEntry s) ? s : null;
        }

        internal static List<string> WorkerKeysForTest(int tile)
        {
            lock (_lock)
                return _byTile.TryGetValue(tile, out SiteEntry s) ? new List<string>(s.Workers) : new List<string>();
        }

        // Writes the shape a pre-migration save left behind: a worker keyed on the account with no pawn.
        internal static void InsertLegacyWorkerForTest(int tile, string username)
        {
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return;
                string key = SiteWorkerKey.For(username, -1);
                if (!s.Workers.Contains(key)) s.Workers.Add(key);
                s.WorkerProgress[key] = new WorkerProgressDto
                {
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                    Destination = SiteEntry.DestTreasury,
                    Username = username,
                    Legacy = true,
                };
            }
        }

        public static void ClearForNewSeason()
        {
            // A season wipe empties every vault these would pay into, so they go with it.
            lock (_lock) { _byTile.Clear(); _owed.ClearLocked(); _purchaseTx.Clear(); }
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
                    // All of theirs: after a save reset none of those pawns exist, so a leftover row fakes staffing.
                    bool here = (s.Workers != null && s.Workers.Any(w => SiteWorkerKey.IsOwnedBy(w, user)))
                             || (s.WorkerProgress != null && s.WorkerProgress.Keys.Any(k => SiteWorkerKey.IsOwnedBy(k, user)));
                    if (!here) continue;
                    removed++;
                    if (dryRun) continue;
                    s.Workers?.RemoveAll(w => SiteWorkerKey.IsOwnedBy(w, user));
                    if (s.WorkerProgress != null)
                        foreach (string k in s.WorkerProgress.Keys.Where(k => SiteWorkerKey.IsOwnedBy(k, user)).ToList())
                            s.WorkerProgress.Remove(k);
                }
            if (removed > 0 && !dryRun) SaveToDisk();
            return removed;
        }

        // False means in-memory only; reward timers live here, so a cycle the disk never took is paid again on restore.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Sites = KmhDeepCopy.OfList(_byTile.Values);
                // Only bindings of sites that are still guild-owned, so a removed site's binding never outlives it.
                foreach (KeyValuePair<int, string> kv in _guildOwner)
                {
                    if (_byTile.TryGetValue(kv.Key, out SiteEntry s) && SiteOwnership.IsGuildOwned(s)) state.GuildOwners[kv.Key] = kv.Value;
                    else _guildOwner.TryRemove(kv.Key, out _);
                }
                state.Owed = _owed.SnapshotLocked();
                // Every row settles or refunds by the first boot after it, so a week covers it many times over.
                long cutoff = DateTime.UtcNow.Ticks - PurchaseEvidenceTtl;
                foreach (string stale in _purchaseTx.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _purchaseTx.Remove(stale);
                state.PurchaseTx = new Dictionary<string, long>(_purchaseTx, StringComparer.Ordinal);
                seq = JsonFileStore.NextSequence();
            }
            return JsonFileStore.Save(KmhDataPaths.SitesFile, state, seq);
        }

        private sealed class PersistedState
        {
            public List<SiteEntry> Sites { get; set; } = new List<SiteEntry>();
            // Absent in a file written before guild sites were bound; the load binds those once, from evidence.
            public Dictionary<int, string> GuildOwners { get; set; } = new Dictionary<int, string>();
            // Absent in older files, which simply had nothing in flight.
            public List<Transactions.KmhOwedPayout> Owed { get; set; } = new List<Transactions.KmhOwedPayout>();
            public Dictionary<string, long> PurchaseTx { get; set; } = new Dictionary<string, long>();
        }

        // A pre-binding site naming a live guild; only an explicit, audited 'kmh orphans' rebind binds it.
        internal const string UnprovenBindingPrefix = OrphanBindingPrefix + "unproven:";

        // Neither a matching name nor a guild's age proves it took the site, so nothing is bound here: each is paused
        // and logged ("unproven" if the name exists, orphaned if not) until an explicit, audited admin rebind.
        private static int MarkLegacyGuildSitesLocked(Dictionary<int, string> persisted)
        {
            int paused = 0;
            long now = DateTime.UtcNow.Ticks;
            foreach (SiteEntry s in _byTile.Values)
            {
                if (!SiteOwnership.IsGuildOwned(s)) continue;
                if (persisted != null && persisted.TryGetValue(s.Tile, out string id) && !string.IsNullOrEmpty(id)) { _guildOwner[s.Tile] = id; continue; }
                paused++;
                if (Guilds.GuildIdentity.Of(s.ControllingGuild) != null)
                {
                    _guildOwner[s.Tile] = UnprovenBindingPrefix + now;
                    Diagnostics.ServerLog.Warn($"Sites: tile {s.Tile} names guild '{s.ControllingGuild}', but nothing on record proves today's guild of that name took it - paused, stored goods kept, until an admin decides ('kmh orphans').");
                }
                else
                {
                    _guildOwner[s.Tile] = OrphanBindingPrefix + now;
                    Diagnostics.ServerLog.Warn($"Sites: tile {s.Tile} names guild '{s.ControllingGuild}', which no longer exists - paused as orphaned, its stored goods kept ('kmh orphans').");
                }
            }
            return paused;
        }

        // Admin-only and explicit: every pre-binding site paused as unproven is bound to the live guild it names.
        public static List<(int Tile, string Guild)> RebindUnprovenLegacy(bool commit, string actor, out string message)
        {
            var candidates = new List<(int Tile, string Guild, string Identity)>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                    if (SiteOwnership.IsGuildOwned(s) && (GuildBindingOf(s.Tile) ?? "").StartsWith(UnprovenBindingPrefix, StringComparison.Ordinal))
                    {
                        string live = Guilds.GuildIdentity.Of(s.ControllingGuild);
                        if (live != null) candidates.Add((s.Tile, s.ControllingGuild, live));
                    }
            var listed = candidates.ConvertAll(c => (c.Tile, c.Guild));
            if (!commit || candidates.Count == 0) { message = $"{candidates.Count} unproven legacy site(s) would be bound to the guild they name."; return listed; }
            lock (_lock)
            {
                var was = new List<(int Tile, string Binding)>();
                foreach (var c in candidates)
                    if (_byTile.ContainsKey(c.Tile) && (GuildBindingOf(c.Tile) ?? "").StartsWith(UnprovenBindingPrefix, StringComparison.Ordinal))
                    { was.Add((c.Tile, GuildBindingOf(c.Tile))); _guildOwner[c.Tile] = c.Identity; }
                if (!SaveToDisk())
                {
                    foreach (var w in was) _guildOwner[w.Tile] = w.Binding;
                    message = "The server could not save that - nothing was bound.";
                    return new List<(int, string)>();
                }
                message = $"Bound {was.Count} unproven legacy site(s) to the guild each names - by {actor}.";
                foreach (var w in was)
                    if (_byTile.TryGetValue(w.Tile, out SiteEntry s))
                        Diagnostics.ServerLog.Info($"Sites: tile {w.Tile} bound to guild '{s.ControllingGuild}' by {actor} (legacy rebind, was {w.Binding}).");
            }
            Diagnostics.ServerLog.Info("Sites: " + message);
            return listed;
        }

        public static int CountGuildSites(string guild)
        {
            if (string.IsNullOrEmpty(guild)) return 0;
            int n = 0;
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                    if (SiteOwnership.IsGuildOwned(s) && string.Equals(s.ControllingGuild, guild, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        // Guild sites no live guild holds: disbanded, or the name now belongs to someone else.
        public static List<(int Tile, string Name, string Guild, int StoredUnits)> OrphanedGuildSites()
        {
            var outp = new List<(int, string, string, int)>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                    if (SiteOwnership.IsGuildOwned(s) && !GuildBindingIsLive(s))
                        outp.Add((s.Tile, NameOf(s), s.ControllingGuild ?? "", s.StoredItems?.Values.Sum() ?? 0));
            return outp;
        }

        // Admin-only and explicit: hands an orphaned guild site, with what it stores, to a guild that exists now.
        public static bool RebindOrphanedSite(int tile, string guild, string actor, out string message)
        {
            string identity = Guilds.GuildIdentity.Of(guild);
            if (identity == null) { message = $"There is no guild named '{guild}'."; return false; }
            string canonical = guild;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s) || s == null) { message = "No site on that tile."; return false; }
                if (!SiteOwnership.IsGuildOwned(s)) { message = "That site is not guild-owned."; return false; }
                if (GuildBindingIsLive(s)) { message = $"That site still belongs to the live guild '{s.ControllingGuild}'."; return false; }
                string wasGuild = s.ControllingGuild, wasBinding = GuildBindingOf(tile);
                s.ControllingGuild = canonical;
                _guildOwner[tile] = identity;
                if (!SaveToDisk())
                {
                    s.ControllingGuild = wasGuild;
                    if (wasBinding != null) _guildOwner[tile] = wasBinding; else _guildOwner.TryRemove(tile, out _);
                    message = "The server could not save that - nothing changed.";
                    return false;
                }
                message = $"Site on tile {tile} ({NameOf(s)}) now belongs to guild '{canonical}' (was '{wasGuild}') - by {actor}.";
            }
            Diagnostics.ServerLog.Info("Sites: " + message);
            RaiseSite(tile, canonical, "rebound");
            return true;
        }

        internal static void BindGuildForTest(int tile, string identity)
        {
            lock (_lock) { if (identity == null) _guildOwner.TryRemove(tile, out _); else _guildOwner[tile] = identity; }
        }
    }
}
