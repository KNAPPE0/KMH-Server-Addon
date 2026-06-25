using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Sites
{
    // Server-authoritative ledger for KMH custom sites, keyed by world tile, persisted to KMH-Data/Sites/Sites.json.
    // A site makes its item each cycle; output scales with worker count + skill, routed to treasury or marketplace.
    // Caravan delivery needs RWT's PKT_Site (follow-up) - until then a caravan choice falls back to the treasury
    //
    // Production: speed = 1 + 0.5*ln(n) for n>1 workers, skill efficiency 0.6..1.6 over levels 0..20, multiplied.
    // Build cost = max(500, marketValue*amount*priceMult), trimmed by the owner guild's custom-site-cost perk
    internal static class SiteStore
    {
        public const double XpPerCycle = 250.0;

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, SiteEntry> _byTile = new Dictionary<int, SiteEntry>();

        // --- production helpers ---

        private static double SpeedMultiplier(int workerCount)
        {
            if (workerCount <= 0) return 0;
            if (workerCount == 1) return 1.0;
            return 1.0 + 0.5 * Math.Log(workerCount);
        }

        private static double AvgSkillLevel(SiteEntry s)
        {
            if (s.WorkerProgress == null || s.WorkerProgress.Count == 0) return 0;
            double total = 0;
            foreach (WorkerProgressDto wp in s.WorkerProgress.Values) total += wp?.CurrentLevel ?? 0;
            return total / s.WorkerProgress.Count;
        }

        private static double SkillEfficiency(SiteEntry s)
        {
            double avg = AvgSkillLevel(s);
            return avg <= 0 ? 1.0 : 0.6 + (avg / 20.0);
        }

        private static double TotalMultiplier(SiteEntry s) => SpeedMultiplier(s.Workers?.Count ?? 0) * SkillEfficiency(s);

        private static double EffectiveCycleMs(SiteEntry s)
        {
            double m = TotalMultiplier(s);
            return m <= 0 ? double.MaxValue : s.BaseCycleTimeMs / m;
        }

        public static double CycleTimeMsForValue(float marketValue)
        {
            double minutes = Math.Min(240.0, Math.Max(30.0, 30.0 + marketValue / 5.0));
            return minutes * 60.0 * 1000.0;
        }

        public static int BuildCost(string ownerUsername, float marketValue, int amount)
        {
            double mult = SitesConfig.Current.CustomSitePriceMultiplier;
            int raw = (int)Math.Ceiling(marketValue * amount * mult);
            int cost = Math.Max(500, raw);
            // Owner guild's custom-site-cost perk discounts the build.
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
            if (string.IsNullOrEmpty(owner)) return (false, "No owner.");
            if (!SitesConfig.Current.AllowCustomSites) return (false, "Custom sites are disabled on this server.");
            if (tile < 0) return (false, "Pick a valid tile.");
            if (string.IsNullOrEmpty(itemDefName)) return (false, "Pick an item to produce.");
            if (amountPerCycle <= 0) return (false, "Amount per cycle must be > 0.");
            if (amountPerCycle > SitesConfig.Current.CustomSiteMaxRewardAmount)
                amountPerCycle = SitesConfig.Current.CustomSiteMaxRewardAmount;
            if (marketValuePerUnit < 0) marketValuePerUnit = 0;
            ownerTaxPercent = Math.Max(0, Math.Min(50, ownerTaxPercent));

            lock (_lock) { if (_byTile.ContainsKey(tile)) return (false, "A site already exists on that tile."); }

            int cost = BuildCost(owner, marketValuePerUnit, amountPerCycle);
            if (!Treasury.TreasuryStore.WithdrawSilver(owner, cost, note: $"custom site build (tile {tile})"))
                return (false, $"Build costs {Util.SilverFmt.Format(cost)} - your treasury is short.");

            SiteEntry site = new SiteEntry
            {
                Tile               = tile,
                OwnerUsername      = owner,
                OwnerGuild         = Guilds.GuildStore.CurrentGuildOf(owner) ?? "",
                ItemDefName        = itemDefName,
                BaseAmountPerCycle = amountPerCycle,
                MarketValuePerUnit = marketValuePerUnit,
                BaseCycleTimeMs    = CycleTimeMsForValue(marketValuePerUnit),
                AccessMode         = NormalizeAccess(accessMode),
                OwnerTaxPercent    = ownerTaxPercent,
                MaxWorkers         = MaxWorkersFor(owner),
                OwnerRewardDestination = NormalizeDest(ownerDestination),
                MarketplaceUnitPrice   = Math.Max(1, marketplaceUnitPrice),
                RelevantSkillDef   = DetermineRelevantSkill(itemDefName),
                LastRewardUtcTicks = DateTime.UtcNow.Ticks,
            };
            lock (_lock) { _byTile[tile] = site; }
            SaveToDisk();
            RaiseSite(tile, owner, "built");
            return (true, $"Built a {itemDefName} site (cost {Util.SilverFmt.Format(cost)}).");
        }

        // A worker joins a site. baseSkillLevel is the pawn skill the client reports for the relevant skill
        // (clamped 0..20). Returns (ok, reason)
        public static (bool ok, string reason) JoinWorker(string username, int tile, int baseSkillLevel)
        {
            if (string.IsNullOrEmpty(username)) return (false, "No user.");
            baseSkillLevel = Math.Max(0, Math.Min(20, baseSkillLevel));
            string owner;
            lock (_lock)
            {
                if (!_byTile.TryGetValue(tile, out SiteEntry s)) return (false, "No site on that tile.");
                if (string.Equals(s.OwnerUsername, username, StringComparison.OrdinalIgnoreCase)) return (false, "You own this site.");
                if (s.Workers.Contains(username, StringComparer.OrdinalIgnoreCase)) return (false, "You already work here.");
                if (!CanAccess(s, username)) return (false, "This site isn't open to you.");
                if (s.Workers.Count >= s.MaxWorkers) return (false, $"This site is full ({s.MaxWorkers} workers).");

                s.Workers.Add(username);
                s.WorkerProgress[username] = new WorkerProgressDto
                {
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                    BaseSkillLevel = baseSkillLevel,
                    Destination    = SiteEntry.DestTreasury,
                };
                owner = s.OwnerUsername;
            }
            SaveToDisk();
            RaiseSite(tile, owner, "worker_joined");
            return (true, "Joined the site as a worker.");
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
                    double cycleMs = EffectiveCycleMs(s);
                    double elapsedMs = (now - s.LastRewardUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
                    if (elapsedMs < cycleMs) continue;
                    s.LastRewardUtcTicks = now;

                    double totalMult = TotalMultiplier(s);
                    if (totalMult <= 0) continue;

                    double guildXpMult = Guilds.GuildStore.WorkerXpMultiplierFor(s.OwnerGuild);
                    foreach (string w in s.Workers)
                    {
                        if (!s.WorkerProgress.TryGetValue(w, out WorkerProgressDto wp) || wp == null)
                        {
                            wp = new WorkerProgressDto { JoinedUtcTicks = now };
                            s.WorkerProgress[w] = wp;
                        }
                        wp.Xp += XpPerCycle * xpMult * guildXpMult;
                        wp.CyclesCompleted += 1;
                    }

                    int totalProduced = (int)Math.Ceiling(s.BaseAmountPerCycle * totalMult);
                    if (totalProduced <= 0) continue;

                    List<string> recipients = new List<string> { s.OwnerUsername };
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.OwnerUsername };
                    foreach (string w in s.Workers) if (seen.Add(w)) recipients.Add(w);

                    foreach (string recipient in recipients)
                    {
                        bool isOwner = string.Equals(recipient, s.OwnerUsername, StringComparison.OrdinalIgnoreCase);
                        int share = totalProduced;
                        if (!isOwner && s.AccessMode == SiteEntry.AccessPublic && s.OwnerTaxPercent > 0)
                        {
                            share = (int)(totalProduced * (1.0 - s.OwnerTaxPercent / 100.0));
                            if (share < 1) share = 1;
                        }
                        string dest = isOwner
                            ? s.OwnerRewardDestination
                            : (s.WorkerProgress.TryGetValue(recipient, out WorkerProgressDto rwp) ? rwp.Destination : SiteEntry.DestTreasury);
                        plan.Add(new Delivery { Tile = s.Tile, Recipient = recipient, Dest = dest, ItemDefName = s.ItemDefName, Amount = share, MarketplaceUnitPrice = s.MarketplaceUnitPrice });
                    }
                }
            }
            if (plan.Count == 0) return touched;

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
                ProductionMultiplier = TotalMultiplier(s),
                EffectiveCycleMinutes = EffectiveCycleMs(s) / 60000.0,
            };
            foreach (KeyValuePair<string, WorkerProgressDto> kv in s.WorkerProgress)
                c.WorkerProgress[kv.Key] = new WorkerProgressDto
                {
                    JoinedUtcTicks = kv.Value.JoinedUtcTicks, CyclesCompleted = kv.Value.CyclesCompleted,
                    Xp = kv.Value.Xp, BaseSkillLevel = kv.Value.BaseSkillLevel, Destination = kv.Value.Destination,
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

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.SitesFile, out PersistedState state) && state?.Sites != null)
            {
                lock (_lock)
                {
                    _byTile.Clear();
                    foreach (SiteEntry s in state.Sites)
                    {
                        if (s == null || s.Tile < 0) continue;
                        s.Workers ??= new List<string>();
                        s.WorkerProgress ??= new Dictionary<string, WorkerProgressDto>(StringComparer.OrdinalIgnoreCase);
                        _byTile[s.Tile] = s;
                    }
                }
                Diagnostics.ServerLog.Info($"Sites: loaded {state.Sites.Count} site(s) from disk");
            }
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
