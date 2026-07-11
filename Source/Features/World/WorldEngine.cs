using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.World.Dto;
using KMHServerAddon.SubProtocol;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.World
{
    // Economy storyteller: 1-min scheduler that expires/auto-rolls events + auto-generates quests; single fire path.
    internal static class WorldEngine
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
        private static CancellationTokenSource _cts;
        private static long _lastRollUtcTicks;
        private static long _lastQuestGenUtcTicks;
        private static readonly Random _rng = new Random();

        public static void Start()
        {
            if (_cts != null) return;
            // Back-date so the first event/quest considers ~15/~30 min after boot, not a full interval later.
            WorldConfig cfg = WorldConfig.Current;
            long now = DateTime.UtcNow.Ticks;
            _lastRollUtcTicks     = now - TimeSpan.FromMinutes(Math.Max(0, cfg.EventRollEveryMinutes - 15)).Ticks;
            _lastQuestGenUtcTicks = now - TimeSpan.FromMinutes(Math.Max(0, cfg.QuestGenEveryMinutes  - 30)).Ticks;
            _cts = new CancellationTokenSource();
            Task.Run(() => RunLoop(_cts.Token));
            ServerLog.Verbose("World: engine started");
        }

        public static void Stop() { _cts?.Cancel(); _cts = null; }

        private static async Task RunLoop(CancellationToken ct)
        {
            try { await Task.Delay(TickInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { ServerLog.Error("World engine tick threw", ex); }
                try { await Task.Delay(TickInterval, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
            }
        }

        private static void Tick()
        {
            long now = DateTime.UtcNow.Ticks;
            WorldConfig cfg = WorldConfig.Current;
            bool dirty = false;

            List<WorldEventDto> ended = WorldStore.CollectEndedEvents(now);
            if (ended.Count > 0)
            {
                foreach (WorldEventDto e in ended)
                {
                    ServerLog.Info($"World: event '{e.Type}' ended");
                    Announce($"Event ended: {e.Title}", "The effect is no longer active.", new Color(0x7A, 0x7A, 0x7A));
                    Extensibility.KmhEventBus.Instance.RaiseWorldEventEnded(new KMH.Sdk.Server.Events.WorldEventEndedEvent
                    { Type = e.Type, Title = e.Title });
                }
                dirty = true;
            }

            // Expired-unfinished quests refund their reserved reward to the house pool.
            List<ServerQuestDto> endedQuests = WorldStore.CollectEndedQuests(now);
            foreach (ServerQuestDto q in endedQuests)
            {
                // Refund only the pool-backed part; any minted portion simply ceases to exist (never was real silver).
                if (q.ReservedFromPool > 0) Marketplace.MarketplaceStore.CreditHousePool(q.ReservedFromPool);
                ServerLog.Info($"World: quest #{q.Id} '{q.Title}' expired unfinished, refunded {q.ReservedFromPool} to house pool");
                Announce($"⌛ {q.Title} expired", "The global quest ran out of time. Its reward returned to the house pool.",
                         new Color(0x7A, 0x7A, 0x7A));
                Extensibility.KmhEventBus.Instance.RaiseGlobalQuestExpired(new KMH.Sdk.Server.Events.GlobalQuestExpiredEvent
                { QuestId = q.Id, Title = q.Title });
                dirty = true;
            }

            if (dirty) WorldHandler.BroadcastSnapshot();

            if (cfg.EventsEnabled && cfg.AutoRollEvents
                && now - _lastRollUtcTicks >= TimeSpan.FromMinutes(cfg.EventRollEveryMinutes).Ticks)
            {
                _lastRollUtcTicks = now;
                if (_rng.NextDouble() <= cfg.EventRollChance)
                {
                    string type = RollEventType(cfg);
                    if (type != null) FireEvent(type, 0, "", 0, "auto");
                }
            }

            MaybeAutoGenerateQuest(cfg, now);
        }

        // Weighted pick from the auto-roll table; null when every weight is 0.
        private static string RollEventType(WorldConfig cfg)
        {
            var weighted = new List<(string type, int w)>
            {
                (WorldEventDto.TaxHoliday,     cfg.WeightTaxHoliday),
                (WorldEventDto.MarketBoom,     cfg.WeightMarketBoom),
                (WorldEventDto.MarketCrash,    cfg.WeightMarketCrash),
                (WorldEventDto.DoubleWorkerXp, cfg.WeightDoubleWorkerXp),
                (WorldEventDto.HouseStipend,   cfg.WeightHouseStipend),
                (WorldEventDto.WorldWeather,   cfg.WeightWorldWeather),
            };
            // A disabled event type never auto-rolls, regardless of its weight.
            for (int i = 0; i < weighted.Count; i++)
                if (!cfg.EventTypeAllowed(weighted[i].type)) weighted[i] = (weighted[i].type, 0);
            int total = 0;
            foreach (var (_, w) in weighted) total += Math.Max(0, w);
            if (total <= 0) return null;
            int roll = _rng.Next(total);
            foreach (var (type, w) in weighted)
            {
                roll -= Math.Max(0, w);
                if (roll < 0) return type;
            }
            return null;
        }

        // Single fire path. magnitude/durationMinutes <= 0 fall back to config defaults. actor = "auto" or admin name.
        public static (bool ok, string reason) FireEvent(string type, double magnitude, string target,
                                                         int durationMinutes, string actor)
        {
            if (!FeaturesConfig.Current.LivingWorld) return (false, "Living World is disabled in Features.json.");
            WorldConfig cfg = WorldConfig.Current;
            if (!cfg.EventsEnabled) return (false, "Events are disabled in World.json.");
            type = (type ?? "").Trim().ToLowerInvariant();
            if (!cfg.EventTypeAllowed(type))
                return (false, $"The '{type}' event is disabled in World.json (AllowedEventTypes).");

            // A bounty is really a quest ("first to slay X"), so route it through the quest system.
            if (type == WorldEventDto.BountyTarget)
            {
                if (string.IsNullOrWhiteSpace(target))
                    return (false, "bounty_target needs a target defName, e.g. kmh event bounty_target 60 1000 Thrumbo");
                long pot           = magnitude > 0 ? (long)magnitude : cfg.StipendSilver;
                int  bountyMinutes = durationMinutes > 0 ? durationMinutes : cfg.QuestDefaultMinutes;
                return CreateWorldQuest(ServerQuestDto.KindCompetitive, ServerQuestDto.ObjHunt, target,
                                        goalQty: 1, requestedReward: pot, durationMinutes: bountyMinutes,
                                        title: $"Bounty: {target}", description: $"First to slay {target} claims the pot.",
                                        actor: actor);
            }

            int minutes = durationMinutes > 0 ? durationMinutes : cfg.EventDefaultMinutes;
            string title, desc; double mag = magnitude;

            switch (type)
            {
                case WorldEventDto.TaxHoliday:
                    title = "Tax Holiday"; mag = 0;
                    desc  = $"Marketplace house tax is waived for {FmtDuration(minutes)}. Sell while it lasts.";
                    break;
                case WorldEventDto.MarketBoom:
                    if (mag <= 0) mag = cfg.MarketSwingPercent;
                    title = "Market Boom";
                    desc  = $"Sellers earn +{mag:F0}% on marketplace payouts for {FmtDuration(minutes)}.";
                    break;
                case WorldEventDto.MarketCrash:
                    if (mag <= 0) mag = cfg.MarketSwingPercent;
                    title = "Market Crash";
                    desc  = $"Marketplace payouts are cut by {mag:F0}% for {FmtDuration(minutes)}. Buy the dip.";
                    break;
                case WorldEventDto.DoubleWorkerXp:
                    if (mag <= 0) mag = cfg.WorkerXpMultPercent;
                    title = $"x{mag / 100.0:F1} Worker XP";
                    desc  = $"Site workers earn {mag / 100.0:F1}x XP for {FmtDuration(minutes)}.";
                    break;
                case WorldEventDto.HouseStipend:
                    if (mag <= 0) mag = cfg.StipendSilver;
                    minutes = 0; // instantaneous
                    title = "House Stipend";
                    desc  = $"Every online player receives {Util.SilverFmt.Format((long)mag)}.";
                    break;
                case WorldEventDto.ResourceShortage:
                    if (mag <= 0) mag = cfg.MarketSwingPercent;
                    title = "Resource Shortage";
                    desc  = string.IsNullOrEmpty(target)
                        ? $"Demand spikes server-wide for {FmtDuration(minutes)}."
                        : $"Demand spikes for {target} for {FmtDuration(minutes)}.";
                    break;
                case WorldEventDto.WorldWeather:
                {
                    // Random pick from the pool (configured + discovered), or honor an explicit target defName.
                    string[] pool = BuildWeatherPool(cfg);
                    if (pool.Length == 0) return (false, "No WeatherConditionDefs configured in World.json.");
                    string entry = null;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        foreach (string p in pool)
                            if (string.Equals(p.Split('|')[0].Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
                            { entry = p; break; }
                        if (entry == null) return (false, $"'{target}' is not in World.json WeatherConditionDefs.");
                    }
                    else entry = pool[_rng.Next(pool.Length)];

                    string[] wp = entry.Split('|');
                    target = wp[0].Trim();
                    mag    = 0;
                    title  = wp.Length > 1 && !string.IsNullOrWhiteSpace(wp[1]) ? wp[1].Trim() : target;
                    desc   = (wp.Length > 2 && !string.IsNullOrWhiteSpace(wp[2]) ? wp[2].Trim() + " " : "")
                           + $"Affects every colony for {FmtDuration(minutes)}.";
                    break;
                }
                default:
                    return (false, $"Unknown event type '{type}'. Try: tax_holiday, market_boom, market_crash, double_worker_xp, house_stipend, resource_shortage, bounty_target, world_weather.");
            }

            WorldEventDto e = WorldStore.AddEvent(type, title, desc, mag, target, minutes);
            ApplyInstantEffect(e);
            WorldHandler.BroadcastSnapshot();
            NotifyAll($"🌍 {title} - {desc}");
            Announce(title, desc, new Color(0x4A, 0x90, 0xE2));
            ServerLog.Info($"World: event '{type}' fired by {actor} (magnitude {mag}, {FmtDuration(minutes)})");
            Extensibility.KmhEventBus.Instance.RaiseWorldEventFired(new KMH.Sdk.Server.Events.WorldEventFiredEvent
            { Type = e.Type, Title = e.Title, Magnitude = e.Magnitude, Target = e.Target, EndsUtcTicks = e.EndsUtcTicks });
            return (true, $"Fired '{title}'.");
        }

        public static (bool ok, string reason) EndEvent(string type)
        {
            type = (type ?? "").Trim().ToLowerInvariant();
            bool removed = false;
            foreach (WorldEventDto e in WorldStore.ActiveEvents())
            {
                if (string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
                {
                    // force-expire: re-add with a 0 duration, then sweep
                    WorldStore.AddEvent(e.Type, e.Title, e.Description, e.Magnitude, e.Target, 0);
                    WorldStore.CollectEndedEvents(DateTime.UtcNow.Ticks + 1);
                    removed = true;
                    Extensibility.KmhEventBus.Instance.RaiseWorldEventEnded(new KMH.Sdk.Server.Events.WorldEventEndedEvent
                    { Type = e.Type, Title = e.Title });
                }
            }
            if (!removed) return (false, $"No active '{type}' event.");
            WorldHandler.BroadcastSnapshot();
            return (true, $"Ended '{type}'.");
        }

        // ---- server quests ----

        // Reward is reserved from the house pool up front (capped to what it holds; 0 = glory-only), so rewards are
        // always backed by real tax revenue. objective hunt/build/deliver; kind cooperative/competitive.
        public static (bool ok, string reason) CreateWorldQuest(string kind, string objective, string targetDef,
            int goalQty, long requestedReward, int durationMinutes, string title, string description, string actor)
        {
            if (!FeaturesConfig.Current.LivingWorld) return (false, "Living World is disabled in Features.json.");
            WorldConfig qcfg = WorldConfig.Current;
            if (!qcfg.QuestsEnabled) return (false, "Global quests are disabled in World.json (QuestsEnabled=false).");
            kind      = NormalizeKind(kind);
            objective = (objective ?? "").Trim().ToLowerInvariant();
            if (objective != ServerQuestDto.ObjHunt && objective != ServerQuestDto.ObjBuild && objective != ServerQuestDto.ObjDeliver)
                return (false, $"Objective must be 'hunt', 'build' or 'deliver' (got '{objective}').");
            if (!qcfg.ObjectiveAllowed(objective))
                return (false, $"The '{objective}' objective is disabled in World.json (AllowedObjectives).");
            if (string.IsNullOrWhiteSpace(targetDef))
                return (false, "A target defName is required (e.g. Muffalo to hunt, Sandbags to build, Steel to deliver).");
            if (goalQty <= 0) return (false, "Goal must be greater than 0.");

            int minutes = durationMinutes > 0 ? durationMinutes : WorldConfig.Current.QuestDefaultMinutes;

            // Fund the reward: reserve as much as the house pool can back, then mint the shortfall if the central
            // bank is enabled. Only the reserved part is refundable on expiry, so minting never inflates the pool.
            long want     = Math.Max(0, requestedReward);
            long reserved = 0;
            if (want > 0)
            {
                long pool = Math.Max(0, Marketplace.MarketplaceStore.HousePoolBalance());
                reserved  = Math.Min(want, pool);
                if (reserved > 0 && !Marketplace.MarketplaceStore.TryDebitHousePool(reserved))
                {
                    // a concurrent sale moved the pool - re-read and take what's actually there
                    reserved = Math.Max(0, Marketplace.MarketplaceStore.HousePoolBalance());
                    if (reserved > 0 && !Marketplace.MarketplaceStore.TryDebitHousePool(reserved)) reserved = 0;
                }
            }
            long minted = WorldConfig.Current.AllowMintedRewards ? Math.Max(0, want - reserved) : 0;
            long reward = reserved + minted;

            string finalTitle = string.IsNullOrWhiteSpace(title) ? DefaultTitle(objective, targetDef, goalQty) : title.Trim();
            string finalDesc  = string.IsNullOrWhiteSpace(description)
                ? DefaultDesc(kind, objective, targetDef, goalQty, reward) : description.Trim();

            ServerQuestDto q = WorldStore.CreateQuest(kind, objective, targetDef, finalTitle, finalDesc, goalQty, reward, minutes, reserved);
            WorldHandler.BroadcastSnapshot();
            NotifyAll($"📜 New global quest: {q.Title} - {q.Description}");
            Announce($"📜 {q.Title}", $"{q.Description}\nReward pool: {Util.SilverFmt.Format(reward)}", new Color(0xC8, 0x8A, 0x2A));
            ServerLog.Info($"World: quest #{q.Id} '{q.Title}' ({kind}/{objective} {targetDef} x{goalQty}, reward {reward}" +
                           (minted > 0 ? $" [{reserved} pool + {minted} minted]" : "") + $") by {actor}");
            Extensibility.KmhEventBus.Instance.RaiseGlobalQuestCreated(new KMH.Sdk.Server.Events.GlobalQuestCreatedEvent
            { QuestId = q.Id, Kind = q.Kind, Objective = q.Objective, TargetDefName = q.TargetDefName, GoalQty = q.GoalQty, RewardPool = q.RewardPool });
            return (true, $"Created quest #{q.Id} '{q.Title}' (reward {Util.SilverFmt.Format(reward)}).");
        }

        public static (bool ok, string reason) EndWorldQuest(long id, string actor)
        {
            long refund = WorldStore.CancelQuest(id);
            if (refund < 0) return (false, $"No active quest #{id}.");
            if (refund > 0) Marketplace.MarketplaceStore.CreditHousePool(refund);
            WorldHandler.BroadcastSnapshot();
            ServerLog.Info($"World: quest #{id} cancelled by {actor}, refunded {refund} to house pool");
            return (true, refund > 0
                ? $"Cancelled quest #{id}, refunded {Util.SilverFmt.Format(refund)} to the house pool."
                : $"Cancelled quest #{id}.");
        }

        // Client contribution report (hunt kills / standing builds). Pays + announces on completion; rebroadcasts on change.
        public static void ApplyContribution(string user, long questId, int cumulative)
        {
            WorldStore.QuestContribution r = WorldStore.ApplyContribution(questId, user, cumulative);
            if (!r.Changed) return;
            if (r.Completed && r.Quest != null) FinishCompletion(r.Quest, r.Payouts);
            WorldHandler.BroadcastSnapshot();
        }

        // Credit a delivery to an active quest; if it just ended, the goods deposit to the player's treasury instead.
        public static void ApplyDelivery(string user, long questId, string itemDef, int qty)
        {
            if (string.IsNullOrEmpty(user) || qty <= 0) return;
            WorldStore.DeliveryResult r = WorldStore.AddDelivery(questId, user, itemDef, qty);
            if (r.State == WorldStore.DeliveryResult.Outcome.Applied)
            {
                if (r.Completed && r.Quest != null) FinishCompletion(r.Quest, r.Payouts);
                WorldHandler.BroadcastSnapshot();
                NotifyUser(user, r.Quest != null ? $"Delivered {r.Amount}x {itemDef} to '{r.Quest.Title}'." : $"Delivered {r.Amount}x {itemDef}.");
            }
            else if (r.Quest != null)
            {
                int cap = Economy.EconomyConfig.Current.MaxItemDepositQtyPerTx;
                if ((cap <= 0 || qty <= cap) && Treasury.TreasuryStore.DepositItem(user, itemDef, qty, "quest-ended delivery refund"))
                {
                    ServerLog.Info($"World: quest #{questId} ended mid-delivery - {qty}x {itemDef} from {user} refunded to their treasury");
                    NotifyUser(user, $"That global quest just ended - your {qty}x {itemDef} was deposited to your treasury instead.");
                }
                else NotifyUser(user, "That global quest just ended - your delivery wasn't applied.");
            }
        }

        // Pay the reward + announce a just-completed quest (shared by contribute + deliver).
        private static void FinishCompletion(ServerQuestDto q, System.Collections.Generic.Dictionary<string, long> payouts)
        {
            PayOut(payouts);
            if (string.Equals(q.Kind, ServerQuestDto.KindCompetitive, StringComparison.OrdinalIgnoreCase))
            {
                NotifyAll($"🏆 Global quest won: {q.Title} - {q.Winner} took {Util.SilverFmt.Format(q.RewardPool)}!");
                Announce($"🏆 {q.Title} - won by {q.Winner}",
                         $"First to {q.GoalQty} took the pot of {Util.SilverFmt.Format(q.RewardPool)}.",
                         new Color(0xF5, 0xC2, 0x42));
            }
            else
            {
                int n = q.Contributors.Count;
                NotifyAll($"✅ Global quest complete: {q.Title} - {n} colonist(s) shared {Util.SilverFmt.Format(q.RewardPool)}!");
                Announce($"✅ {q.Title} - complete",
                         $"{n} contributor(s) shared the reward pool of {Util.SilverFmt.Format(q.RewardPool)}.",
                         new Color(0x7C, 0xD3, 0x7C));
            }
            ServerLog.Info($"World: quest #{q.Id} '{q.Title}' completed");
            Extensibility.KmhEventBus.Instance.RaiseGlobalQuestCompleted(new KMH.Sdk.Server.Events.GlobalQuestCompletedEvent
            { QuestId = q.Id, Kind = q.Kind, Objective = q.Objective, Winner = q.Winner ?? "", ContributorCount = q.Contributors.Count, RewardPool = q.RewardPool });
        }

        // Auto-generate one quest from the template table when enabled + cadence elapsed (one at a time).
        private static void MaybeAutoGenerateQuest(WorldConfig cfg, long now)
        {
            if (!cfg.QuestsEnabled || !cfg.AutoGenerateQuests) return;
            if (now - _lastQuestGenUtcTicks < TimeSpan.FromMinutes(cfg.QuestGenEveryMinutes).Ticks) return;
            _lastQuestGenUtcTicks = now;
            if (WorldStore.ActiveQuests().Count >= cfg.MaxActiveAutoQuests) return;
            // Don't ask for a big hunt on an empty server.
            int activePlayers = ActivePlayerCount();
            if (activePlayers < Math.Max(0, cfg.GlobalQuestMinActivePlayers)) return;

            // Generated quest from the live item catalog first (owner-tunable chance), else a template.
            string objective, defName, title, desc;
            int goal;
            if (!TryGenerateCatalogQuest(cfg, out objective, out defName, out title, out desc, out goal))
            {
                string[] tpl = cfg.QuestTemplates;
                if (tpl == null || tpl.Length == 0) return;
                string chosen = PickAllowedTemplate(cfg, tpl);
                if (chosen == null) return;
                string[] parts = chosen.Split('|');
                if (parts.Length < 2) return;

                objective = parts[0].Trim().ToLowerInvariant();
                defName   = parts[1].Trim();
                title     = parts.Length > 2 ? parts[2].Trim() : "";
                desc      = parts.Length > 3 ? parts[3].Trim() : "";
                // Hunt/build goal scales with active players (clamped), so it stays achievable at any population.
                goal = (cfg.GlobalQuestScaleByActivePlayers
                        && (objective == ServerQuestDto.ObjHunt || objective == ServerQuestDto.ObjBuild))
                    ? ScaledTargetCount(cfg, defName, activePlayers)
                    : _rng.Next(cfg.QuestAutoGoalMin, cfg.QuestAutoGoalMax + 1);
            }

            // Avoid hammering the same target back-to-back.
            if (!cfg.GlobalQuestAllowSameTargetRepeat && TargetOnCooldown(defName, cfg.GlobalQuestRepeatTargetCooldownHours))
            {
                ServerLog.Verbose($"World: skipped auto-quest - target '{defName}' on repeat cooldown.");
                return;
            }

            long   target    = ComputeAutoReward(cfg, defName, goal);

            long reward;
            if (cfg.AllowMintedRewards)
            {
                // Central bank tops the reward up to at least the floor, so global quests are always worth doing.
                // Re-clamp to the max AFTER the floor so QuestMinReward can never bypass QuestRewardMaxReward.
                reward = Math.Max(target, cfg.QuestMinReward);
                if (cfg.QuestRewardMaxReward > 0) reward = Math.Min(reward, cfg.QuestRewardMaxReward);
            }
            else
            {
                // Closed loop: promise only what the house pool can back. Skip rather than post a sub-floor grind.
                long pool = Marketplace.MarketplaceStore.HousePoolBalance();
                reward = Math.Min(target, pool);
                if (cfg.QuestRewardMaxReward > 0) reward = Math.Min(reward, cfg.QuestRewardMaxReward);
                if (reward < cfg.QuestMinReward)
                {
                    ServerLog.Info($"World: skipped auto-quest - house pool ({pool}) below the {cfg.QuestMinReward} minimum reward (minting off).");
                    return;
                }
            }

            int durationMin = Clamp(cfg.GlobalQuestDefaultDurationHours, cfg.GlobalQuestMinDurationHours, cfg.GlobalQuestMaxDurationHours) * 60;
            if (durationMin <= 0) durationMin = cfg.QuestDefaultMinutes;   // fallback if hours misconfigured
            RememberTarget(defName);
            CreateWorldQuest(ServerQuestDto.KindCooperative, objective, defName, goal, reward,
                             durationMin, title, desc, "auto");
        }

        // Active players = verified online clients (the reliable signal; "recently active" maps to online for now).
        private static int ActivePlayerCount()
        {
            int n = 0;
            try { foreach (ServerClient c in Network.ServerClients.Keys) if (c?.IsVerified == true) n++; }
            catch { }
            return n;
        }

        // goal = clamp(Base + activePlayers * PerPlayer * targetDifficulty * DifficultyMultiplier, Min, Max).
        private static int ScaledTargetCount(WorldConfig cfg, string defName, int activePlayers)
        {
            double diff = TargetDifficulty(defName) * (cfg.GlobalQuestDifficultyMultiplier <= 0 ? 1.0 : cfg.GlobalQuestDifficultyMultiplier);
            double raw  = cfg.GlobalQuestBaseTargetCount + Math.Max(0, activePlayers) * cfg.GlobalQuestTargetsPerActivePlayer * diff;
            return Clamp((int)Math.Round(raw), cfg.GlobalQuestMinTargetCount, cfg.GlobalQuestMaxTargetCount);
        }

        // Rough difficulty from the target name: small/common animals ask for more, big/dangerous ones fewer.
        private static double TargetDifficulty(string defName)
        {
            string d = (defName ?? "").ToLowerInvariant();
            if (d.Contains("thrumbo") || d.Contains("megasloth") || d.Contains("rhino") || d.Contains("elephant")
                || d.Contains("bear") || d.Contains("scaria") || d.Contains("mech")) return 1.5;
            if (d.Contains("muffalo") || d.Contains("bison") || d.Contains("caribou") || d.Contains("boomalope")
                || d.Contains("cow") || d.Contains("horse") || d.Contains("deer") || d.Contains("ostrich")) return 1.25;
            if (d.Contains("rat") || d.Contains("squirrel") || d.Contains("hare") || d.Contains("chicken")
                || d.Contains("rabbit") || d.Contains("chinchilla")) return 0.75;
            return 1.0;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (lo > hi) { int t = lo; lo = hi; hi = t; }
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // Recent auto-quest targets, so the same def isn't chosen again within the cooldown window.
        private static readonly System.Collections.Generic.Dictionary<string, long> _recentTargets
            = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static bool TargetOnCooldown(string defName, int cooldownHours)
        {
            if (string.IsNullOrEmpty(defName) || cooldownHours <= 0) return false;
            lock (_recentTargets)
                return _recentTargets.TryGetValue(defName, out long ticks)
                    && DateTime.UtcNow.Ticks - ticks < TimeSpan.FromHours(cooldownHours).Ticks;
        }
        private static void RememberTarget(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return;
            lock (_recentTargets)
            {
                _recentTargets[defName] = DateTime.UtcNow.Ticks;
                if (_recentTargets.Count > 64)   // bound
                {
                    string oldest = null; long oldestT = long.MaxValue;
                    foreach (var kv in _recentTargets) if (kv.Value < oldestT) { oldestT = kv.Value; oldest = kv.Key; }
                    if (oldest != null) _recentTargets.Remove(oldest);
                }
            }
        }

        // Configured weather entries + (opt-in) client-discovered GameConditionDefs minus the owner's exclusions.
        private static string[] BuildWeatherPool(WorldConfig cfg)
        {
            List<string> pool = new List<string>(cfg.WeatherConditionDefs ?? Array.Empty<string>());
            if (cfg.AutoDiscoverWeather)
            {
                HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string p in pool) have.Add(p.Split('|')[0].Trim());
                foreach (KeyValuePair<string, string> kv in ItemLabels.WeatherDefCache.All())
                {
                    if (have.Contains(kv.Key) || !cfg.WeatherDefAllowed(kv.Key)) continue;
                    pool.Add($"{kv.Key}|{kv.Value}|A wave of {kv.Value.ToLowerInvariant()} sweeps every colony.");
                }
            }
            return pool.ToArray();
        }

        // Deliver quest from the live item catalog; false when the chance misses, deliver is off, or no item fits.
        private static bool TryGenerateCatalogQuest(WorldConfig cfg, out string objective, out string defName,
                                                    out string title, out string desc, out int goal)
        {
            objective = ServerQuestDto.ObjDeliver; defName = null; title = null; desc = null; goal = 0;
            if (cfg.GeneratedQuestChance <= 0 || _rng.Next(100) >= cfg.GeneratedQuestChance) return false;
            if (!cfg.ObjectiveAllowed(ServerQuestDto.ObjDeliver)) return false;

            var pick = ItemLabels.ItemLabelCache.RandomDeliverable(cfg.GeneratedItemMinValue, cfg.GeneratedItemMaxValue, _rng);
            if (pick == null) return false;
            (string def, string label, long value) = pick.Value;

            defName = def;
            goal    = (int)Math.Max(1, Math.Min(cfg.GeneratedQuestGoalValue / Math.Max(1, value), cfg.QuestAutoGoalMax));
            switch (_rng.Next(3))
            {
                case 0:  title = $"Supply Run: {label}";
                         desc  = $"The realm requisitions {label} - every delivery counts toward the shared reward."; break;
                case 1:  title = $"Shortage: {label}";
                         desc  = $"Stockpiles of {label} are running dry across the realm. Deliver what you can spare."; break;
                default: title = $"Requisition: {label}";
                         desc  = $"A standing order for {label} has been posted. Fill it before it expires."; break;
            }
            ServerLog.Info($"World: generated deliver quest from the live catalog ({goal}x {def}, unit value {value})");
            return true;
        }

        // Random template whose objective the owner currently allows; null if none qualify.
        private static string PickAllowedTemplate(WorldConfig cfg, string[] tpl)
        {
            List<string> ok = new List<string>();
            foreach (string t in tpl)
            {
                int bar = t?.IndexOf('|') ?? -1;
                if (bar > 0 && cfg.ObjectiveAllowed(t.Substring(0, bar).Trim())) ok.Add(t);
            }
            return ok.Count == 0 ? null : ok[_rng.Next(ok.Count)];
        }

        // Desired reward = max(house-pool %, wealth per-mille, goal x reported item value), clamped to the cap.
        private static long ComputeAutoReward(WorldConfig cfg, string targetDef, int goal)
        {
            long pool   = Marketplace.MarketplaceStore.HousePoolBalance();
            long wealth = PlayerStats.PlayerStatsStore.TotalReportedWealth();
            long unit   = ItemLabels.ItemLabelCache.BaseValue(targetDef);
            long byValue = unit > 0 ? unit * Math.Max(1, goal) * cfg.QuestRewardValuePercent / 100 : 0;
            long perTarget = (long)Math.Max(0, cfg.GlobalQuestRewardPerTarget) * Math.Max(1, goal);   // bigger quest -> bigger pool
            long target = Math.Max(pool * cfg.QuestRewardHousePoolPercent / 100,
                          Math.Max(wealth * cfg.QuestRewardWealthPermille / 1000, Math.Max(byValue, perTarget)));
            if (target > cfg.QuestRewardMaxReward) target = cfg.QuestRewardMaxReward;
            return target;
        }

        // Deposit each payout (int-chunked) and push the player a fresh treasury snapshot.
        private static void PayOut(System.Collections.Generic.Dictionary<string, long> payouts)
        {
            if (payouts == null) return;
            foreach (System.Collections.Generic.KeyValuePair<string, long> kv in payouts)
            {
                long remaining = kv.Value;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, int.MaxValue);
                    Treasury.TreasuryStore.DepositSilver(kv.Key, chunk, note: "world quest reward");
                    remaining -= chunk;
                }
                PushTreasury(kv.Key);
                if (kv.Value > 0)
                    NotifyUser(kv.Key, $"+{Util.SilverFmt.Format(kv.Value)} silver deposited to your treasury (global quest reward).");
            }
        }

        private static void PushTreasury(string user)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, user, StringComparison.OrdinalIgnoreCase))
                {
                    KmhRouter.SendTo(c, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));
                    return;
                }
            }
        }

        private static string NormalizeKind(string kind)
        {
            kind = (kind ?? "").Trim().ToLowerInvariant();
            return (kind == "comp" || kind == "competitive" || kind == "race")
                ? ServerQuestDto.KindCompetitive : ServerQuestDto.KindCooperative;
        }

        private static string DefaultTitle(string objective, string targetDef, int goal)
            => objective == ServerQuestDto.ObjBuild   ? $"Build {goal}x {targetDef}"
             : objective == ServerQuestDto.ObjDeliver ? $"Deliver {goal}x {targetDef}"
             :                                          $"Hunt {goal}x {targetDef}";

        private static string DefaultDesc(string kind, string objective, string targetDef, int goal, long reward)
        {
            string verb = objective == ServerQuestDto.ObjBuild   ? "build"
                        : objective == ServerQuestDto.ObjDeliver ? "deliver"
                        :                                          "hunt";
            string mode = string.Equals(kind, ServerQuestDto.KindCompetitive, StringComparison.OrdinalIgnoreCase)
                ? $"Race: first to {verb} {goal} {targetDef} wins"
                : $"Together, {verb} {goal} {targetDef}";
            return reward > 0 ? $"{mode} for {Util.SilverFmt.Format(reward)}." : $"{mode}.";
        }

        // One-shot fire-time effects (vs the duration modifiers the economy reads live).
        private static void ApplyInstantEffect(WorldEventDto e)
        {
            if (!string.Equals(e.Type, WorldEventDto.HouseStipend, StringComparison.OrdinalIgnoreCase)) return;
            int amount = (int)Math.Min(int.MaxValue, Math.Max(0, e.Magnitude));
            if (amount <= 0) return;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u)) continue;
                Treasury.TreasuryStore.DepositSilver(u, amount, note: "world event stipend");
                KmhRouter.SendTo(c, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(u));
            }
        }

        private static void NotifyAll(string text)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true) KmhRouter.Notify(c, "neutral", text);
        }

        private static void NotifyUser(string user, string text)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, user, StringComparison.OrdinalIgnoreCase))
                { KmhRouter.Notify(c, "neutral", text); return; }
            }
        }

        private static void Announce(string title, string desc, Color color)
        {
            ulong ch = Features.Discord.DiscordBridge.Config?.AnnouncementsChannelId ?? 0;
            if (ch == 0) return;
            Embed embed = new EmbedBuilder()
                .WithTitle($"🌍 {title}")
                .WithDescription(desc)
                .WithColor(color)
                .WithCurrentTimestamp()
                .Build();
            Features.Discord.DiscordBridge.PostEmbedToChannel(ch, embed);
        }
    }
}
