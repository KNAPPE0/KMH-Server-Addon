using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Sites
{
    // Server-authoritative custom-site ledger keyed by world tile. A site makes its item each cycle, output scaling
    // with worker count + skill, routed to treasury or marketplace.
    internal static class SiteStore
    {
        public const double XpPerCycle = 250.0;

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, SiteEntry> _byTile = new Dictionary<int, SiteEntry>();
        private static readonly Dictionary<string, long> _lastBuildUtc =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // owner -> last build ticks (cooldown)

        // Counts under _lock.
        private static int CountOwnedLocked(string owner)
        {
            int n = 0;
            foreach (SiteEntry s in _byTile.Values)
                if (string.Equals(s.OwnerUsername, owner, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
        private static int CountGuildOwnedLocked(string guild)
        {
            int n = 0;
            foreach (SiteEntry s in _byTile.Values)
                if (string.Equals(s.OwnerGuild, guild, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
        private static int CountWorkedLocked(string user)
        {
            int n = 0;
            foreach (SiteEntry s in _byTile.Values)
                if (s.Workers.Contains(user, StringComparer.OrdinalIgnoreCase)) n++;
            return n;
        }

        // --- production helpers ---

        // Only real assigned pawns count - legacy/blocked account-workers don't drive speed or output, so a site with
        // only legacy workers is paused until real pawns are assigned.
        private static int WorkerCount(SiteEntry s)
        {
            if (s.Workers == null || s.WorkerProgress == null) return 0;
            int n = 0;
            foreach (string w in s.Workers)
                if (s.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) && wp != null && wp.IsActivePawnWorker) n++;
            return n;
        }
        private static bool Producing(SiteEntry s) => WorkerCount(s) > 0;

        // A worker count -> raw speed factor (more workers = faster cycles). 0 workers = paused.
        private static double RawSpeed(int workerCount)
        {
            if (workerCount <= 0) return 0;
            if (workerCount == 1) return 1.0;
            return 1.0 + 0.5 * Math.Log(workerCount);
        }

        private static double AvgSkillLevel(SiteEntry s)
        {
            if (s.WorkerProgress == null) return 0;
            double total = 0; int n = 0;
            foreach (WorkerProgressDto wp in s.WorkerProgress.Values)
                if (wp != null && wp.IsActivePawnWorker) { total += wp.CurrentLevel; n++; }
            return n == 0 ? 0 : total / n;
        }

        // Workers drive SPEED (cycle time), capped by the site's tier. Skill drives OUTPUT (amount), separately capped
        // by the tier - so a high tier can be "speed only" (output cap 1.0) and Tier 4 never multiplies output. This
        // splits the old double-dip where worker count AND skill both sped cycles AND multiplied yield.
        private static double SpeedFactor(SiteEntry s)
        {
            double raw = RawSpeed(WorkerCount(s));
            if (raw <= 0) return 0;
            double cap = s.TierMaxSpeedMultiplier > 0 ? s.TierMaxSpeedMultiplier : 3.0;
            return Math.Min(cap, Math.Max(1.0, raw));
        }

        private static double OutputFactor(SiteEntry s)
        {
            if (!Producing(s)) return 0;
            double cap = s.TierMaxOutputMultiplier > 0 ? s.TierMaxOutputMultiplier : 2.0;
            double raw = 1.0 + AvgSkillLevel(s) / 20.0;   // +0..+100% over skill 0..20
            return Math.Min(cap, Math.Max(1.0, raw));
        }

        private static double EffectiveCycleMs(SiteEntry s)
        {
            double sp = SpeedFactor(s);
            return sp <= 0 ? double.PositiveInfinity : s.BaseCycleTimeMs / sp;   // display path sanitizes
        }

        // Guard for any value that gets serialized/displayed: reject NaN/Infinity/negative/absurd so the client never
        // sees a garbage cycle time.
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
            int raw = (int)Math.Ceiling(marketValue * amount * mult);
            // Throughput floor: client can under-report value, not amount.
            int throughputFloor = (int)Math.Ceiling(Math.Max(0, amount) * SitesConfig.Current.MinBuildCostPerUnit);
            int cost = Math.Max(500, Math.Max(raw, throughputFloor));
            // Tier surcharge (higher tiers cost more), then the owner guild's custom-site-cost perk discount.
            if (tierCostMult > 0) cost = (int)Math.Ceiling(cost * tierCostMult);
            string guild = Guilds.GuildStore.CurrentGuildOf(ownerUsername);
            double discount = Guilds.GuildStore.CustomSiteCostMultiplierFor(guild);
            return Math.Max(1, (int)Math.Round(cost * discount));
        }

        private static int MaxWorkersFor(string ownerUsername)
        {
            // Base 5 plus the owner guild's site-max-workers perk (+0/2/4/6).
            string guild = Guilds.GuildStore.CurrentGuildOf(ownerUsername);
            return 5 + Guilds.GuildStore.SiteMaxWorkersBonusFor(guild);
        }

        // --- lifecycle ---

        // Build a custom site at a tile. Charges the build cost from the owner's treasury. Returns (ok, reason)
        public static (bool ok, string reason) Build(string owner, int tile, string itemDefName,
            int amountPerCycle, float marketValuePerUnit, string accessMode, int ownerTaxPercent,
            string ownerDestination, int marketplaceUnitPrice)
        {
            SitesConfig cfg = SitesConfig.Current;
            if (string.IsNullOrEmpty(owner)) return (false, "No owner.");
            if (!cfg.AllowCustomSites) return (false, "Custom sites are disabled on this server.");
            if (tile < 0) return (false, "Pick a valid tile.");
            if (string.IsNullOrEmpty(itemDefName)) return (false, "Pick an item to produce.");
            if (amountPerCycle <= 0) return (false, "Amount per cycle must be > 0.");

            // Server-authoritative output classification: decides allow/block, the amount cap, and the cost/cycle +
            // worker-scaling multipliers. Unknown/rare/tech items land in a disabled tier, so a site can't print gear.
            SiteOutputClass cls;
            int tierMaxAmount;
            if (cfg.UseSiteOutputTiers)
            {
                // Resolve the human label server-side so owners can allow/block by exact label, not just raw defName.
                string label = Features.ItemLabels.ItemLabelCache.LabelFor(itemDefName);
                cls = SiteOutputRules.Classify(itemDefName, label, marketValuePerUnit);
                if (!cls.IsAllowed)
                {
                    Diagnostics.ServerLog.Info($"Sites: {owner} build of '{itemDefName}' ('{label}') rejected - {cls.BlockReason} (tier {cls.TierNumber} {cls.TierName}).");
                    return (false, cls.BlockReason);
                }
                tierMaxAmount = Math.Min(cfg.CustomSiteMaxRewardAmount, cls.MaxAmount);
            }
            else
            {
                // Legacy path (tiers off). The simple-resource safety gate STAYS ON unless the owner has explicitly
                // set AllowUnsafeLegacySiteOutputs=true - so tiers-off can't silently reopen all-def output.
                if (!cfg.AllowUnsafeLegacySiteOutputs
                    && !Items.KmhItemSafety.IsAllowedSiteOutput(itemDefName, out string outReason))
                    return (false, outReason);
                cls = new SiteOutputClass
                {
                    IsAllowed = true, TierNumber = 1, TierName = "Basic", RelevantSkill = DetermineRelevantSkill(itemDefName),
                    MaxAmount = cfg.CustomSiteMaxRewardAmount, CostMultiplier = 1.0, CycleMultiplier = 1.0,
                    MaxSpeedMultiplier = 3.0, MaxOutputMultiplier = 2.0,
                };
                tierMaxAmount = cfg.CustomSiteMaxRewardAmount;
            }

            // Reject (never silently clamp) an over-limit amount - a manual-typed value that beats the UI cap is
            // rejected outright, the treasury isn't charged, and the reason is logged.
            if (amountPerCycle > tierMaxAmount)
            {
                Diagnostics.ServerLog.Warn($"Sites: {owner} build REJECTED - amount {amountPerCycle} > max {tierMaxAmount} for '{itemDefName}' (tier {cls.TierNumber} {cls.TierName}, tile {tile}).");
                return (false, $"Site amount exceeds max for this item tier - {cls.TierName} allows up to {tierMaxAmount} per cycle.");
            }

            // Never trust the client's market value for economy math; use the server's catalog when known.
            marketValuePerUnit = Items.KmhItemSafety.ValidateClientMarketValue(itemDefName, marketValuePerUnit, out bool valueSuspicious);
            if (valueSuspicious)
                Diagnostics.ServerLog.Warn($"Sites: {owner} reported an off market value for {itemDefName} on build (tile {tile}) - using trusted catalog value.");
            ownerTaxPercent = Math.Max(0, Math.Min(50, ownerTaxPercent));

            // Anti-spam / economy caps + build cooldown.
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

            int cost = BuildCost(owner, marketValuePerUnit, amountPerCycle, cls.CostMultiplier);
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
                RelevantSkillDef   = cls.RelevantSkill,
                OutputTier             = cls.TierNumber,
                TierMaxSpeedMultiplier = cls.MaxSpeedMultiplier,
                TierMaxOutputMultiplier= cls.MaxOutputMultiplier,
                LastRewardUtcTicks = DateTime.UtcNow.Ticks,
            };
            // Owner counts as a worker (config), so a solo or private site actually produces instead of sitting idle.
            if (cfg.AutoAddOwnerAsSiteWorker)
            {
                site.Workers.Add(owner);
                site.WorkerProgress[owner] = new WorkerProgressDto { JoinedUtcTicks = DateTime.UtcNow.Ticks, Destination = SiteEntry.DestTreasury };
            }
            lock (_lock) { _byTile[tile] = site; _lastBuildUtc[owner] = DateTime.UtcNow.Ticks; }
            SaveToDisk();
            RaiseSite(tile, owner, "built");
            return (true, $"Built a {itemDefName} site (cost {Util.SilverFmt.Format(cost)}).");
        }

        public const string BlockedPawnAway = "pawn away from site";

        // A worker joins a site (or re-validates an existing assignment). baseSkillLevel is the pawn skill the
        // client reports for the relevant skill (clamped 0..20). present=false marks the assigned pawn as away
        // from the site tile - the worker stays assigned but stops producing/earning until the pawn returns.
        // Returns (ok, reason, changed): changed=false is a silent no-op re-validation (no toast, no broadcast).
        public static (bool ok, string reason, bool changed) JoinWorker(string username, int tile, int baseSkillLevel,
            string pawnName = "", int pawnLoadId = -1, bool present = true)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.", false);
            // Only real pawns work a site. An account-only join (no pawn load id) is rejected - send a caravan to the
            // site tile and assign a colonist. Client-reported skill/pawn stay advisory (headless server can't verify).
            if (SitesConfig.Current.RequirePawnSiteWorkers && pawnLoadId <= 0)
                return (false, "This site needs a real colonist. Send a caravan to the site tile, then assign a pawn.", false);
            baseSkillLevel = Items.KmhItemSafety.ValidateClientSkill(baseSkillLevel, out _);
            pawnName = Util.KmhSafe.Cap(pawnName ?? "", 48);
            int workerCap = SitesConfig.Current.MaxWorkerSitesPerPlayer;
            string owner; bool refreshed = false, changed = true;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.", false);
                bool isOwner = string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase);
                if (!isOwner && !CanAccess(s, username)) return (false, "This site isn't open to you.", false);

                // Already a worker -> refresh the assigned pawn / skill / validation instead of rejecting (lets the
                // client re-validate the pawn periodically or swap which colonist works).
                if (s.WorkerProgress.TryGetValue(username, out WorkerProgressDto existing) && existing != null)
                {
                    string newBlocked = present ? "" : BlockedPawnAway;
                    changed = existing.PawnLoadId != pawnLoadId
                              || !string.Equals(existing.BlockedReason ?? "", newBlocked, StringComparison.Ordinal)
                              || existing.Legacy;
                    // Reflect the CURRENT pawn's skill (don't keep a stale max forever if the pawn changed/died);
                    // server-owned XP still drives long-term progression.
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
                    // Count active pawn workers only - legacy/disabled account-workers must not block a real assignment.
                    if (WorkerCount(s) >= s.MaxWorkers) return (false, $"This site is full ({s.MaxWorkers} workers).", false);
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

        // Set the reward destination for the caller - the owner sets the site's default, a worker sets their
        // personal override
        public static (bool ok, string reason) SetDestination(string username, int tile, string destination)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.");
            string dest = NormalizeDest(destination);
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                if (string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                    s.OwnerRewardDestination = dest;
                else if (s.WorkerProgress.TryGetValue(username, out WorkerProgressDto wp) && wp != null)
                    wp.Destination = dest;
                else return (false, "You're not part of this site.");
            }
            SaveToDisk();
            return (true, $"Reward destination set to {dest}.");
        }

        public static (bool ok, string reason) Cancel(string owner, int tile)
        {
            if (string.IsNullOrEmpty(owner)) return (false, "No user.");
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                if (!string.Equals(s.OwnerUsername, owner, StringComparison.OrdinalIgnoreCase)) return (false, "Only the owner can remove a site.");
                _byTile.Remove(tile);
            }
            SaveToDisk();
            RaiseSite(tile, owner, "removed");
            return (true, "Site removed.");
        }

        private static void RaiseSite(int tile, string owner, string reason)
            => Extensibility.KmhEventBus.Instance.RaiseSiteChanged(
                new KMH.Sdk.Server.Events.SiteChangedEvent { Tile = tile, OwnerUsername = owner ?? "", Reason = reason });

        // --- reward cycle (called by the sweeper) ---

        // Runs one production pass over every site whose cycle has elapsed, crediting owners + workers and awarding
        // XP. Returns the set of usernames whose treasury changed so the sweeper can push them
        public static HashSet<string> RunRewardCycle()
        {
            HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long now = DateTime.UtcNow.Ticks;
            // World event: a double-XP event multiplies the configured worker XP rate.
            double xpMult = SitesConfig.Current.WorkerXpMultiplier * World.WorldStore.WorkerXpMultiplier();

            // Plan everything that touches site state (LastReward, XP, shares) UNDER the lock, so we never
            // enumerate a site's workers while a join/leave on another thread mutates them. The actual delivery I/O
            // (treasury / marketplace / packet) runs after the lock
            List<Delivery> plan = new List<Delivery>();
            lock (_lock)
            {
                foreach (SiteEntry s in _byTile.Values)
                {
                    if (s.BlockedOutput) continue;              // legacy site paused - output now blocked, needs review
                    double cycleMs = EffectiveCycleMs(s);
                    if (!IsFinitePositive(cycleMs)) continue;   // paused (no workers) - skip, don't reset the timer
                    double elapsedMs = (now - s.LastRewardUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
                    if (elapsedMs < cycleMs) continue;
                    s.LastRewardUtcTicks = now;

                    double guildXpMult = Guilds.GuildStore.WorkerXpMultiplierFor(s.OwnerGuild);
                    foreach (string w in s.Workers)
                    {
                        // Legacy/blocked account-workers don't work, so they earn no XP and take no share.
                        if (!s.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) || wp == null || !wp.IsActivePawnWorker) continue;
                        wp.Xp += XpPerCycle * xpMult * guildXpMult;
                        wp.CyclesCompleted += 1;
                    }

                    int totalProduced = (int)Math.Ceiling(s.BaseAmountPerCycle * OutputFactor(s));
                    if (totalProduced <= 0) continue;

                    List<string> recipients = new List<string> { s.OwnerUsername };
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.OwnerUsername };
                    foreach (string w in s.Workers)
                        if (s.WorkerProgress.TryGetValue(w, out WorkerProgressDto awp) && awp != null && awp.IsActivePawnWorker && seen.Add(w))
                            recipients.Add(w);

                    // Distribution: SplitTotal (default) = ONE pool split, so production NEVER multiplies by worker
                    // count; OwnerOnly = owner takes it all (workers still earned XP above); PerWorkerCopy = legacy
                    // multiplier where every recipient gets a full copy (opt-in only).
                    string distMode = SitesConfig.Current.SiteRewardDistributionMode;
                    Dictionary<string, int> shares = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (string.Equals(distMode, "OwnerOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        shares[s.OwnerUsername] = totalProduced;
                    }
                    else if (string.Equals(distMode, "PerWorkerCopy", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string r in recipients)
                        {
                            bool isOwner = string.Equals(r, s.OwnerUsername, StringComparison.OrdinalIgnoreCase);
                            int share = totalProduced;
                            if (!isOwner && s.AccessMode == SiteEntry.AccessPublic && s.OwnerTaxPercent > 0)
                                share = Math.Max(1, (int)(totalProduced * (1.0 - s.OwnerTaxPercent / 100.0)));
                            shares[r] = share;
                        }
                    }
                    else // SplitTotal
                    {
                        int per = totalProduced / recipients.Count;   // equal base slice
                        int ownerBonus = totalProduced - per * recipients.Count;   // remainder -> owner
                        foreach (string r in recipients)
                        {
                            bool isOwner = string.Equals(r, s.OwnerUsername, StringComparison.OrdinalIgnoreCase);
                            if (isOwner) { shares[r] = per; continue; }
                            int wShare = per;
                            if (s.AccessMode == SiteEntry.AccessPublic && s.OwnerTaxPercent > 0)
                            {
                                int tax = (int)(per * (s.OwnerTaxPercent / 100.0));
                                wShare = Math.Max(per > 0 ? 1 : 0, per - tax);
                                ownerBonus += per - wShare;   // owner tax
                            }
                            shares[r] = wShare;
                        }
                        if (shares.ContainsKey(s.OwnerUsername)) shares[s.OwnerUsername] += ownerBonus;
                    }

                    foreach (KeyValuePair<string, int> kv in shares)
                    {
                        if (kv.Value <= 0) continue;
                        bool isOwner = string.Equals(kv.Key, s.OwnerUsername, StringComparison.OrdinalIgnoreCase);
                        string dest = isOwner
                            ? s.OwnerRewardDestination
                            : (s.WorkerProgress.TryGetValue(kv.Key, out WorkerProgressDto rwp) ? rwp.Destination : SiteEntry.DestTreasury);
                        plan.Add(new Delivery { Tile = s.Tile, Recipient = kv.Key, Dest = dest, ItemDefName = s.ItemDefName, Amount = kv.Value, MarketplaceUnitPrice = s.MarketplaceUnitPrice });
                    }
                }
            }
            if (plan.Count == 0) return touched;

            // Idempotency: persist the advanced timers+XP before the out-of-lock delivery so a crash mid-cycle can't
            // replay and double-credit treasuries (a rare one-cycle partial loss is safer for the economy than dupes).
            SaveToDisk();

            // Deliver outside the lock. Fold the net silver back into per-site stats afterward (under the lock) so
            // the displayed total is right
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
                // No dupe by design (timers persisted above), but a delivery that produced nothing is skipped this cycle,
                // not retried - surface it so an owner can investigate rather than lose it silently.
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

        private struct Delivery
        {
            public int    Tile;
            public string Recipient;
            public string Dest;
            public string ItemDefName;
            public int    Amount;
            public int    MarketplaceUnitPrice;
        }

        // Routes one planned share. Silver pays the recipient guild's site-reward tax and lands in the treasury
        // balance; items go to colony / marketplace / treasury. Returns the net amount delivered (0 = nothing)
        private static int DeliverPlanned(Delivery d)
        {
            if (d.Amount <= 0 || string.IsNullOrEmpty(d.Recipient)) return 0;

            if (string.Equals(d.ItemDefName, "Silver", StringComparison.OrdinalIgnoreCase))
            {
                int net = Guilds.GuildStore.ApplyGuildSiteRewardTax(d.Recipient, d.Amount);
                if (net <= 0) return 0;
                Treasury.TreasuryStore.DepositSilver(d.Recipient, net, note: $"site#{d.Tile} reward");
                return net;
            }

            switch (d.Dest)
            {
                case SiteEntry.DestMarketplace:
                    MarketplaceStore_PostFromSite(d.Recipient, d.ItemDefName, d.Amount, Math.Max(1, d.MarketplaceUnitPrice));
                    return d.Amount;
                case SiteEntry.DestCaravan:
                    // Deliver into the recipient's colony via KMH's own grant
                    // (caravan or drop pod, same path as treasury withdrawals);
                    // offline recipients fall back to the treasury.
                    if (TryDeliverToColony(d.Recipient, d.ItemDefName, d.Amount)) return d.Amount;
                    Treasury.TreasuryStore.DepositItem(d.Recipient, d.ItemDefName, d.Amount, note: $"site#{d.Tile} reward (offline -> treasury)");
                    return d.Amount;
                default: // treasury
                    Treasury.TreasuryStore.DepositItem(d.Recipient, d.ItemDefName, d.Amount, note: $"site#{d.Tile} reward");
                    return d.Amount;
            }
        }

        // Deliver a reward to the recipient's colony via the KMH grant channel - the patch materializes it through
        // ColonyGoods (selected caravan, else a drop pod on the home map), the same path treasury withdrawals use.
        // Returns false if the recipient isn't online, so the caller can fall back to crediting the treasury
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
            // Try to auto-list; if it fails (e.g. seller hit the listing cap), fall back to the treasury so
            // production is never lost
            long id = Marketplace.MarketplaceStore.Post(seller, defName, qty, unitPrice, "public", 0);
            if (id == 0) Treasury.TreasuryStore.DepositItem(seller, defName, qty, note: "site reward (marketplace fallback)");
        }

        // --- snapshot ---

        // Sites visible to the caller (their own + ones they can access/work), with derived production fields
        // filled for display
        public static SiteSnapshot BuildSnapshotFor(string username)
        {
            SiteSnapshot snap = new SiteSnapshot
            {
                AllowCustomSites = SitesConfig.Current.AllowCustomSites,
                PriceMultiplier  = SitesConfig.Current.CustomSitePriceMultiplier,
                MaxRewardAmount  = SitesConfig.Current.CustomSiteMaxRewardAmount,
            };
            lock (_lock)
            {
                foreach (SiteEntry s in _byTile.Values)
                {
                    bool mine = string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase)
                                || s.Workers.Contains(username, StringComparer.OrdinalIgnoreCase);
                    if (!mine && !CanAccess(s, username)) continue;
                    snap.Sites.Add(Copy(s));
                }
            }
            return snap;
        }

        // Approximate vanilla base values, used ONLY to seed a fresh server's Site picker before any client pushes
        // labels. Real values replace these the moment a client reports (first-seen-wins).
        private static readonly (string DefName, string Label, long Value)[] VanillaFallbackOutputs =
        {
            // NOTE: Silver is intentionally excluded - a Site must never print money.
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
            };
            const int blockedCap = 250;   // don't ship the whole weapon/apparel catalog even in debug
            int blocked = 0;
            System.Collections.Generic.List<(string DefName, string Label, long Value)> source =
                Features.ItemLabels.ItemLabelCache.AllForCatalog();
            // Fresh-server fallback: if clients haven't pushed labels yet, seed the picker with common vanilla
            // resources so it's never empty (the client also PushOnce()s on open). Real values overwrite these once a
            // client reports. IncludesBlocked stays false here; the sparse flag lets the client show "still loading".
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
                SiteOutputClass cls = SiteOutputRules.Classify(defName, label, value);
                if (!cls.IsAllowed)
                {
                    if (!includeBlocked || blocked >= blockedCap) continue;
                    blocked++;
                    snap.Entries.Add(new Dto.SiteCatalogEntry
                    {
                        DefName = defName, Label = label, Tier = cls.TierNumber, TierName = cls.TierName,
                        RelevantSkill = cls.RelevantSkill, MaxAmount = 0, Allowed = false, BlockReason = cls.BlockReason,
                    });
                    continue;
                }
                int maxAmount   = Math.Max(1, Math.Min(cfg.CustomSiteMaxRewardAmount, cls.MaxAmount));
                int estCost     = BuildCost(username, value, maxAmount, cls.CostMultiplier);
                int estCycleMin = (int)Math.Round(CycleTimeMsForValue(value, maxAmount) * cls.CycleMultiplier / 60000.0);
                snap.Entries.Add(new Dto.SiteCatalogEntry
                {
                    DefName = defName, Label = label, Tier = cls.TierNumber, TierName = cls.TierName,
                    RelevantSkill = cls.RelevantSkill, MaxAmount = maxAmount, EstBuildCost = estCost,
                    EstCycleMinutes = estCycleMin, Allowed = true,
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

        private static SiteEntry Copy(SiteEntry s)
        {
            bool producing = Producing(s);
            double cycleMin = EffectiveCycleMs(s) / 60000.0;
            SiteEntry c = new SiteEntry
            {
                Tile = s.Tile, OwnerUsername = s.OwnerUsername, OwnerGuild = s.OwnerGuild,
                ItemDefName = s.ItemDefName, BaseAmountPerCycle = s.BaseAmountPerCycle,
                MarketValuePerUnit = s.MarketValuePerUnit, BaseCycleTimeMs = s.BaseCycleTimeMs,
                AccessMode = s.AccessMode, OwnerTaxPercent = s.OwnerTaxPercent,
                Workers = new List<string>(s.Workers), MaxWorkers = s.MaxWorkers,
                OwnerRewardDestination = s.OwnerRewardDestination, MarketplaceUnitPrice = s.MarketplaceUnitPrice,
                RelevantSkillDef = s.RelevantSkillDef, LastRewardUtcTicks = s.LastRewardUtcTicks,
                TotalSilverGenerated = s.TotalSilverGenerated,
                OutputTier = s.OutputTier, TierMaxSpeedMultiplier = s.TierMaxSpeedMultiplier, TierMaxOutputMultiplier = s.TierMaxOutputMultiplier,
                BlockedOutput = s.BlockedOutput,
                // Sanitized display: paused sites report 0 (never Infinity/NaN/huge) and a clear reason.
                IsProducing = producing && !s.BlockedOutput,
                PausedReason = s.BlockedOutput ? "output_blocked" : (producing ? "" : "no_workers"),
                ProductionMultiplier = producing && !s.BlockedOutput ? OutputFactor(s) : 0.0,
                EffectiveCycleMinutes = producing && !s.BlockedOutput && IsFinitePositive(EffectiveCycleMs(s)) ? cycleMin : 0.0,
            };
            foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                c.WorkerProgress[kv.Key] = new WorkerProgressDto
                {
                    JoinedUtcTicks = kv.Value.JoinedUtcTicks, CyclesCompleted = kv.Value.CyclesCompleted,
                    Xp = kv.Value.Xp, BaseSkillLevel = kv.Value.BaseSkillLevel, Destination = kv.Value.Destination,
                    PawnName = kv.Value.PawnName, PawnLoadId = kv.Value.PawnLoadId, LastValidatedUtc = kv.Value.LastValidatedUtc,
                    Legacy = kv.Value.Legacy, BlockedReason = kv.Value.BlockedReason,
                };
            return c;
        }

        // --- helpers ---

        private static bool CanAccess(SiteEntry s, string username)
        {
            switch (s.AccessMode)
            {
                case SiteEntry.AccessPublic:  return true;
                case SiteEntry.AccessPrivate: return string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase);
                default: // guild_only: owner's guild or an allied guild
                    string callerGuild = Guilds.GuildStore.CurrentGuildOf(username);
                    if (string.IsNullOrEmpty(s.OwnerGuild)) return string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase);
                    if (string.Equals(callerGuild, s.OwnerGuild, StringComparison.OrdinalIgnoreCase)) return true;
                    return Guilds.GuildStore.AreAllied(callerGuild, s.OwnerGuild);
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

        // --- persistence ---

        // Count of legacy sites paused because their output is now blocked (for the boot banner). 0 = none.
        public static int LegacyBlockedSiteCount { get; private set; }
        // Count of old account-workers (no real pawn) loaded as legacy/disabled - needs reassignment. 0 = none.
        public static int LegacyWorkerCount { get; private set; }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.SitesFile, out PersistedState state) && state?.Sites != null)
            {
                int blocked = 0, legacyWorkers = 0;
                lock (_lock)
                {
                    _byTile.Clear();
                    foreach (SiteEntry s in state.Sites)
                    {
                        if (s == null || s.Tile < 0) continue;
                        s.Workers ??= new List<string>();
                        s.WorkerProgress ??= new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);
                        // Reclassify with the REAL label (ItemLabelCache), never the defName as label, and re-check
                        // against current rules. Backfill tier/skill for pre-tier sites.
                        string label = Features.ItemLabels.ItemLabelCache.LabelFor(s.ItemDefName);
                        SiteOutputClass cls = SiteOutputRules.Classify(s.ItemDefName, label, s.MarketValuePerUnit);
                        if (s.OutputTier <= 0 || s.TierMaxSpeedMultiplier <= 0 || s.TierMaxOutputMultiplier <= 0)
                        {
                            s.OutputTier = cls.TierNumber;
                            s.TierMaxSpeedMultiplier = cls.MaxSpeedMultiplier;
                            s.TierMaxOutputMultiplier = cls.MaxOutputMultiplier;
                            if (string.IsNullOrEmpty(s.RelevantSkillDef)) s.RelevantSkillDef = cls.RelevantSkill;
                        }
                        // A legacy site whose output is now blocked is PAUSED for admin review - never silently keeps
                        // producing a now-illegal output.
                        if (!cls.IsAllowed && SitesConfig.Current.UseSiteOutputTiers)
                        {
                            s.BlockedOutput = true; blocked++;
                            Diagnostics.ServerLog.Warn($"Sites: PAUSED legacy site tile {s.Tile} ({s.OwnerUsername}) - output '{s.ItemDefName}' ('{label}') is now blocked ({cls.BlockReason}). Admin review needed.");
                        }
                        else s.BlockedOutput = false;

                        // Old account-workers have no real pawn (PawnLoadId <= 0). Load them as legacy/disabled (kept,
                        // never deleted) so they stop producing until a real pawn is assigned.
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
                Diagnostics.ServerLog.Info($"Sites: loaded {state.Sites.Count} site(s) from disk{(blocked > 0 ? $" ({blocked} paused - output now blocked)" : "")}{(legacyWorkers > 0 ? $" ({legacyWorkers} legacy account-worker(s) disabled - reassign real pawns)" : "")}");
            }
        }

        // Season reset: remove all custom sites.
        public static void ClearForNewSeason()
        {
            lock (_lock) { _byTile.Clear(); }
            SaveToDisk();
        }

        // Admin: remove sites owned by a user. The server holds no pawns, so client-side assigned pawns stay in their
        // caravan (not deleted). dryRun -> count only. Returns (sites removed, active pawn-worker assignments dropped).
        public static (int sites, int pawnWorkers) PurgeOwner(string user, bool dryRun)
        {
            if (string.IsNullOrEmpty(user)) return (0, 0);
            int sites = 0, pawns = 0;
            List<int> tiles = new List<int>();
            lock (_lock)
                foreach (SiteEntry s in _byTile.Values)
                    if (string.Equals(s.OwnerUsername, user, StringComparison.OrdinalIgnoreCase))
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

        // Drop a user's worker slot from EVERY site (save-reset/wipe: their pawns no longer exist, so they must not
        // produce). Sites remain; a site left with no active workers auto-pauses via EffectiveCycleMs.
        // dryRun counts the sites they'd be dropped from without mutating (for wipe verify/preview).
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

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock) { state.Sites = new List<SiteEntry>(_byTile.Values); }
            JsonFileStore.Save(KmhDataPaths.SitesFile, state);
        }

        private sealed class PersistedState
        {
            public List<SiteEntry> Sites { get; set; } = new List<SiteEntry>();
        }
    }
}
