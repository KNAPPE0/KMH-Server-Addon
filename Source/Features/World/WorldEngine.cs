using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.World.Dto;
using KMHServerAddon.Maintenance;
using KMHServerAddon.SubProtocol;
using static KMHServerAddon.Util.KmhSafe;

namespace KMHServerAddon.Features.World
{
    internal static class WorldEngine
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
        private static long _lastRollUtcTicks;
        private static long _lastQuestGenUtcTicks;
        private static readonly Random _rng = new Random();

        public static void Start()
        {
            // Back-date so the first event/quest considers ~15/~30 min after boot, not a full interval later.
            WorldConfig cfg = WorldConfig.Current;
            long now = DateTime.UtcNow.Ticks;
            _lastRollUtcTicks     = now - TimeSpan.FromMinutes(Math.Max(0, cfg.Events.RollEveryMinutes - 15)).Ticks;
            _lastQuestGenUtcTicks = now - TimeSpan.FromMinutes(Math.Max(0, cfg.WorldQuests.AutoGen.EveryMinutes  - 30)).Ticks;
            KmhScheduler.Register("world-engine", TickInterval, Tick, KmhJobKind.Mutating, TickInterval);
        }

        public static void Stop() => KmhScheduler.Stop();

        // Never rolls a random event: one fired by a test tick stays live for hours and untaxes every later sale.
        internal static void TickForTest() => Tick(rollEvents: false);

        private static void Tick() => Tick(rollEvents: true);

        private static void Tick(bool rollEvents)
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
                    Announce($"Event ended: {e.Title}", "The effect is no longer active.");
                    Extensibility.KmhEventBus.Instance.RaiseWorldEventEnded(new KMH.Sdk.Server.Events.WorldEventEndedEvent
                    { Type = e.Type, Title = e.Title });
                }
                dirty = true;
            }

            List<ServerQuestDto> endedQuests = WorldStore.CollectEndedQuests(now);
            foreach (ServerQuestDto q in endedQuests)
            {
                // Only the pool-backed part returns; a minted portion was never real silver.
                if (q.ReservedFromPool > 0)
                    Marketplace.MarketplaceStore.ReturnToHousePool(q.ReservedFromPool, $"quest #{q.Id} expired - reserve returned");
                ServerLog.Info($"World: quest #{q.Id} '{q.Title}' expired unfinished, refunded {q.ReservedFromPool} to house pool");
                Announce($"⌛ {q.Title} expired", "The global quest ran out of time. Its reward returned to the house pool.");
                Extensibility.KmhEventBus.Instance.RaiseGlobalQuestExpired(new KMH.Sdk.Server.Events.GlobalQuestExpiredEvent
                { QuestId = q.Id, Title = q.Title });
                // Only the dispatcher knows how to hand back the director slot and location an expiry releases.
                Frontier.KmhOperationConsequence.Abandon(q);
                dirty = true;
            }

            if (dirty) WorldHandler.BroadcastSnapshot();

            if (rollEvents && cfg.Events.Enabled && cfg.Events.AutoRoll
                && now - _lastRollUtcTicks >= TimeSpan.FromMinutes(cfg.Events.RollEveryMinutes).Ticks)
            {
                _lastRollUtcTicks = now;
                if (_rng.NextDouble() <= cfg.Events.RollChance)
                {
                    string type = RollEventType(cfg);
                    if (type != null) FireEvent(type, 0, "", 0, "auto");
                }
            }

            MaybeAutoGenerateQuest(cfg, now);
        }

        private static string RollEventType(WorldConfig cfg)
        {
            var weighted = new List<(string type, int w)>
            {
                (WorldEventDto.TaxHoliday,     cfg.Events.Weights.TaxHoliday),
                (WorldEventDto.MarketBoom,     cfg.Events.Weights.MarketBoom),
                (WorldEventDto.MarketCrash,    cfg.Events.Weights.MarketCrash),
                (WorldEventDto.DoubleWorkerXp, cfg.Events.Weights.DoubleWorkerXp),
                (WorldEventDto.HouseStipend,   cfg.Events.Weights.HouseStipend),
                (WorldEventDto.WorldWeather,   cfg.Events.Weights.WorldWeather),
            };
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

        public static (bool ok, string reason) FireEvent(string type, double magnitude, string target,
                                                         int durationMinutes, string actor)
        {
            if (!FeaturesConfig.Current.LivingWorld) return (false, "Living World is disabled in Features.json.");
            WorldConfig cfg = WorldConfig.Current;
            if (!cfg.Events.Enabled) return (false, "Events are disabled in World.json.");
            type = (type ?? "").Trim().ToLowerInvariant();
            if (!cfg.EventTypeAllowed(type))
                return (false, $"The '{type}' event is disabled in World.json (Events.AllowedTypes).");

            // A bounty is really a quest ("first to slay X"), so route it through the quest system.
            if (type == WorldEventDto.BountyTarget)
            {
                if (string.IsNullOrWhiteSpace(target))
                    return (false, "bounty_target needs a target defName, e.g. kmh event bounty_target 60 1000 Thrumbo");
                long pot           = magnitude > 0 ? (long)magnitude : cfg.Events.StipendSilver;
                int  bountyMinutes = durationMinutes > 0 ? durationMinutes : cfg.WorldQuests.DefaultMinutes;
                return CreateWorldQuest(ServerQuestDto.KindCompetitive, ServerQuestDto.ObjHunt, target,
                                        goalQty: 1, requestedReward: pot, durationMinutes: bountyMinutes,
                                        title: $"Bounty: {target}", description: $"First to slay {target} claims the pot.",
                                        actor: actor);
            }

            int minutes = durationMinutes > 0 ? durationMinutes : cfg.Events.DefaultMinutes;
            string title, desc; double mag = magnitude;

            switch (type)
            {
                case WorldEventDto.TaxHoliday:
                    title = "Tax Holiday"; mag = 0;
                    desc  = $"Marketplace house tax is waived for {FmtDuration(minutes)}. Sell while it lasts.";
                    break;
                case WorldEventDto.MarketBoom:
                    if (mag <= 0) mag = cfg.Events.MarketSwingPercent;
                    title = "Market Boom";
                    desc  = $"Sellers earn +{mag:F0}% on marketplace payouts for {FmtDuration(minutes)}.";
                    break;
                case WorldEventDto.MarketCrash:
                    if (mag <= 0) mag = cfg.Events.MarketSwingPercent;
                    title = "Market Crash";
                    desc  = $"Marketplace payouts are cut by {mag:F0}% for {FmtDuration(minutes)}. Buy the dip.";
                    break;
                case WorldEventDto.DoubleWorkerXp:
                    if (mag <= 0) mag = cfg.Events.WorkerXpMultPercent;
                    title = $"x{mag / 100.0:F1} Worker XP";
                    desc  = $"Site workers earn {mag / 100.0:F1}x XP for {FmtDuration(minutes)}.";
                    break;
                case WorldEventDto.HouseStipend:
                    if (mag <= 0) mag = cfg.Events.StipendSilver;
                    minutes = 0; // instantaneous
                    title = "House Stipend";
                    desc  = $"Every online player receives {Util.SilverFmt.Format((long)mag)}.";
                    break;
                case WorldEventDto.ResourceShortage:
                    if (mag <= 0) mag = cfg.Events.MarketSwingPercent;
                    title = "Resource Shortage";
                    desc  = string.IsNullOrEmpty(target)
                        ? $"Demand spikes server-wide for {FmtDuration(minutes)}."
                        : $"Demand spikes for {target} for {FmtDuration(minutes)}.";
                    break;
                case WorldEventDto.WorldWeather:
                {
                    string[] pool = BuildWeatherPool(cfg);
                    if (pool.Length == 0) return (false, "No Events.Weather.ConditionDefs configured in World.json.");
                    string entry = null;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        foreach (string p in pool)
                            if (string.Equals(p.Split('|')[0].Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
                            { entry = p; break; }
                        if (entry == null) return (false, $"'{target}' is not in World.json Events.Weather.ConditionDefs.");
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
            NotifyAll($"World event: {title} - {desc}");
            Announce(title, desc);
            ServerLog.Info($"World: event '{type}' fired by {actor} (magnitude {mag}, {FmtDuration(minutes)})");
            Extensibility.KmhEventBus.Instance.RaiseWorldEventFired(new KMH.Sdk.Server.Events.WorldEventFiredEvent
            { Type = e.Type, Title = e.Title, Magnitude = e.Magnitude, Target = e.Target, EndsUtcTicks = e.EndsUtcTicks });
            return (true, $"Fired '{title}'.");
        }

        public static (bool ok, string reason) EndEvent(string type)
        {
            type = (type ?? "").Trim().ToLowerInvariant();

            List<WorldEventDto> matches = new List<WorldEventDto>();
            foreach (WorldEventDto e in WorldStore.ActiveEvents())
                if (string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
                    matches.Add(e);
            if (matches.Count == 0) return (false, $"No active '{type}' event.");

            if (WorldStore.RemoveEvent(type) == 0) return (false, $"No active '{type}' event.");
            foreach (WorldEventDto e in matches)
                Extensibility.KmhEventBus.Instance.RaiseWorldEventEnded(new KMH.Sdk.Server.Events.WorldEventEndedEvent
                { Type = e.Type, Title = e.Title });

            WorldHandler.BroadcastSnapshot();
            return (true, $"Ended '{type}'.");
        }

        // The reward is reserved up front, so a quest is never posted that the pool cannot back.
        public static (bool ok, string reason) CreateWorldQuest(string kind, string objective, string targetDef,
            int goalQty, long requestedReward, int durationMinutes, string title, string description, string actor)
        {
            if (!FeaturesConfig.Current.LivingWorld) return (false, "Living World is disabled in Features.json.");
            WorldConfig qcfg = WorldConfig.Current;
            if (!qcfg.WorldQuests.Enabled) return (false, "Global quests are disabled in World.json (WorldQuests.Enabled=false).");
            kind      = NormalizeKind(kind);
            objective = (objective ?? "").Trim().ToLowerInvariant();
            if (objective != ServerQuestDto.ObjHunt && objective != ServerQuestDto.ObjBuild && objective != ServerQuestDto.ObjDeliver)
                return (false, $"Objective must be 'hunt', 'build' or 'deliver' (got '{objective}').");
            if (!qcfg.ObjectiveAllowed(objective))
                return (false, $"The '{objective}' objective is disabled in World.json (AllowedObjectives).");
            if (string.IsNullOrWhiteSpace(targetDef))
                return (false, "A target defName is required (e.g. Muffalo to hunt, Sandbags to build, Steel to deliver).");
            if (goalQty <= 0) return (false, "Goal must be greater than 0.");

            int minutes = durationMinutes > 0 ? durationMinutes : WorldConfig.Current.WorldQuests.DefaultMinutes;

            if (requestedReward > 0 && IsSelfReportedObjective(objective) && !WorldConfig.Current.WorldQuests.PayForSelfReported)
                return (false, $"'{objective}' progress is self-reported and WorldQuests.PayForSelfReported is off in Config/World.json, "
                             + "so that quest would never pay. Set the reward to 0, or use a deliver quest.");

            // Only the reserved part is refundable on expiry, so minting the shortfall cannot inflate the pool.
            long want     = Math.Max(0, requestedReward);
            long reserved = 0;
            if (want > 0)
            {
                long pool = Math.Max(0, Marketplace.MarketplaceStore.HousePoolBalance());
                reserved  = Math.Min(want, pool);
                if (reserved > 0 && !Marketplace.MarketplaceStore.TryDebitHousePool(reserved, "global quest reward reserve"))
                {
                    // A concurrent sale moved the pool, so re-read and take what is actually there.
                    reserved = Math.Max(0, Marketplace.MarketplaceStore.HousePoolBalance());
                    if (reserved > 0 && !Marketplace.MarketplaceStore.TryDebitHousePool(reserved, "global quest reward reserve")) reserved = 0;
                }
            }
            long minted = WorldConfig.Current.WorldQuests.Rewards.AllowMinted ? Math.Max(0, want - reserved) : 0;
            long reward = reserved + minted;

            string finalTitle = string.IsNullOrWhiteSpace(title) ? DefaultTitle(objective, targetDef, goalQty) : title.Trim();
            string finalDesc  = string.IsNullOrWhiteSpace(description)
                ? DefaultDesc(kind, objective, targetDef, goalQty, reward) : description.Trim();

            ServerQuestDto q = WorldStore.CreateQuest(kind, objective, targetDef, finalTitle, finalDesc, goalQty, reward, minutes, reserved);
            WorldHandler.BroadcastSnapshot();
            NotifyAll($"New global quest: {q.Title} - {q.Description}");
            Announce($"📜 {q.Title}", $"{q.Description}\nReward pool: {Util.SilverFmt.Format(reward)}");
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
            if (refund > 0) Marketplace.MarketplaceStore.ReturnToHousePool(refund, $"quest #{id} cancelled - reserve returned");
            WorldHandler.BroadcastSnapshot();
            ServerLog.Info($"World: quest #{id} cancelled by {actor}, refunded {refund} to house pool");
            return (true, refund > 0
                ? $"Cancelled quest #{id}, refunded {Util.SilverFmt.Format(refund)} to the house pool."
                : $"Cancelled quest #{id}.");
        }

        public static void ApplyContribution(string user, long questId, int cumulative)
        {
            WorldStore.QuestContribution r = WorldStore.ApplyContribution(questId, user, cumulative);
            if (!r.Changed) return;
            if (r.Completed && r.Quest != null) FinishCompletion(r.Quest, r.Payouts);
            WorldHandler.BroadcastSnapshot();
        }

        // Credits only goods already taken from the treasury, or a crafted packet could mint items outright.
        public static bool ApplyDelivery(string user, long questId, string itemDef, int qty)
        {
            if (string.IsNullOrEmpty(user) || qty <= 0) return false;

            int room = WorldStore.DeliverableRoom(questId, user, itemDef);
            if (room <= 0)
            {
                NotifyUser(user, "That global quest isn't taking that item right now - nothing was withdrawn.");
                return false;
            }

            // Never more than the objective has room for, so an over-large request cannot park goods in transit.
            int want = Math.Min(qty, room);
            string note = $"global quest #{questId} delivery";

            // The row comes first and the take carries its marker; the quest's own save then records which row it
            // credited, so recovery can tell a delivery that landed from one that did not.
            Transactions.KmhTransaction tx = null;
            List<KeyValuePair<string, int>> plan = Treasury.TreasuryStore.PlanMatching(user, itemDef, 0, want);
            if (plan != null)
            {
                var planned = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, int> kv in plan) { planned.TryGetValue(kv.Key, out int n); planned[kv.Key] = Util.KmhSafe.AddSaturating(n, kv.Value); }
                tx = Transactions.KmhTransactionRepository.OpenTake(user, Transactions.KmhTxType.QuestReward, note, questId, 0, planned, null,
                    destination: "world", targetKind: Transactions.KmhTransactionRepository.TargetWorldDelivery);
                if (tx == null) { NotifyUser(user, "The server couldn't record that delivery - nothing was taken."); return false; }
                if (!Treasury.TreasuryStore.WithdrawItemsForTxn(user, plan, tx.TakeMarker, note)) { Transactions.KmhTransactionRepository.Abort(tx); tx = null; }
            }
            if (tx == null)
            {
                // The same def can sit in the vault as full-state payloads, which the compact withdraw cannot see.
                tx = Transactions.KmhTransactionRepository.PrepareTake(user, Transactions.KmhTxType.QuestReward, note, questId);
                if (tx == null) { NotifyUser(user, "The server couldn't record that delivery - nothing was taken."); return false; }
                tx.Destination = "world";
                tx.TargetKind  = Transactions.KmhTransactionRepository.TargetWorldDelivery;
                List<Items.KmhThingPayload> tookPayloads = Treasury.TreasuryStore.TryWithdrawMatchingPayloads(
                    user, itemDef, "", 0, allowTainted: true, allowDamaged: true, want, note, tx.TakeMarker, tx.RefundMarker);
                int got = 0; foreach (Items.KmhThingPayload p in tookPayloads) got += p.StackCount;
                if (got < want || !Transactions.KmhTransactionRepository.CommitTake(tx, tookPayloads))
                {
                    if (tookPayloads.Count > 0) Treasury.TreasuryStore.ReturnPendingTake(user, tx.TakeMarker, tx.RefundMarker, tookPayloads, note + " returned");
                    NotifyUser(user, got < want
                        ? $"Deposit the {ItemLabels.ItemLabelCache.LabelFor(itemDef)} into your treasury first - global quest deliveries are paid out of it."
                        : "The server couldn't record that delivery - your items were returned.");
                    return false;
                }
                Treasury.TreasuryStore.ClearPendingTake(user, tx.TakeMarker);
            }
            AtDeliveryPhaseForTest?.Invoke("taken");

            WorldStore.DeliveryResult r = WorldStore.AddDelivery(questId, user, itemDef, want, tx.Id);
            if (r.State != WorldStore.DeliveryResult.Outcome.Applied)
            {
                // Room can close between the read above and the credit, so what was withdrawn goes straight back.
                if (!Transactions.KmhTransactionRepository.Compensate(tx))
                    ServerLog.Error($"World: delivery {tx.Id} could not be returned yet - the row stays open and the next boot returns it.");
                NotifyUser(user, r.SaveFailed
                    ? "The server couldn't record that delivery - your items were returned. Try again shortly."
                    : r.Quest != null
                        ? $"'{r.Quest.Title}' already has everything it needs - your {ItemLabels.ItemLabelCache.LabelFor(itemDef)} stayed in your treasury."
                        : "That global quest is no longer taking deliveries - nothing was withdrawn.");
                return false;
            }
            AtDeliveryPhaseForTest?.Invoke("credited");
            FinishDeliveryRow(tx, r.Amount);
            if (r.Completed && r.Quest != null) FinishCompletion(r.Quest, r.Payouts);
            WorldHandler.BroadcastSnapshot();
            PushTreasury(user);
            string what = ItemLabels.ItemLabelCache.LabelFor(itemDef);
            NotifyUser(user, r.Quest != null ? $"Delivered {r.Amount}x {what} to '{r.Quest.Title}'." : $"Delivered {r.Amount}x {what}.");
            return true;
        }

        internal static Action<string> AtDeliveryPhaseForTest;

        // The quest took `credited` units; anything else the row took goes back on the row's surplus marker, once.
        internal static bool FinishDeliveryRow(Transactions.KmhTransaction tx, int credited)
        {
            int taken = 0;
            foreach (int q in tx.EscrowItems.Values) taken += q;
            foreach (Items.KmhThingPayload p in tx.EscrowPayloads) taken += p.StackCount;
            int surplus = taken - Math.Max(0, credited);
            if (surplus > 0)
            {
                var items = new Dictionary<string, int>(StringComparer.Ordinal);
                int left = surplus;
                var keys = new List<string>(tx.EscrowItems.Keys);
                for (int i = keys.Count - 1; i >= 0 && left > 0; i--)
                {
                    int give = Math.Min(tx.EscrowItems[keys[i]], left);
                    if (give > 0) { items[keys[i]] = give; left -= give; }
                }
                List<Items.KmhThingPayload> loads = left > 0
                    ? Items.KmhPayloadEscrow.PopUnits(new List<Items.KmhThingPayload>(tx.EscrowPayloads), left)
                    : new List<Items.KmhThingPayload>();
                if (!Treasury.TreasuryStore.DepositEscrowOnce(tx.Player, tx.SurplusMarker, 0, items, loads,
                                                             $"global quest #{tx.TargetId} over-delivery returned"))
                {
                    ServerLog.Error($"World: delivery {tx.Id} surplus could not be returned yet - the row stays open and the next boot returns it.");
                    return false;
                }
            }
            Transactions.KmhTransactionRepository.Settle(tx);
            return true;
        }

        // Rewards a completion recorded but did not finish paying, from a crash or a failed credit; each is paid once.
        internal static void ResumePendingPayouts()
        {
            foreach (KeyValuePair<long, Dictionary<string, long>> q in WorldStore.PendingPayoutsCopy())
                PayOut(q.Key, q.Value);
        }

        // Hunt and build are the client's own word; deliver is not, since it moves treasury goods the server owns.
        internal static bool IsSelfReportedObjective(string objective)
            => string.Equals(objective, ServerQuestDto.ObjHunt,  StringComparison.OrdinalIgnoreCase)
            || string.Equals(objective, ServerQuestDto.ObjBuild, StringComparison.OrdinalIgnoreCase);

        private static void FinishCompletion(ServerQuestDto q, System.Collections.Generic.Dictionary<string, long> payouts)
        {
            // A modified client can report "total = goal", so paying for it is opt-in; the quest completes either way.
            bool paid = !IsSelfReportedObjective(q.Objective) || WorldConfig.Current.WorldQuests.PayForSelfReported;
            if (paid) PayOut(q.Id, payouts);
            else
            {
                WorldStore.DropPendingPayouts(q.Id);
                long back = WorldStore.TakeUnpaidReserve(q.Id);
                q.RewardPool = 0;
                if (back > 0) Marketplace.MarketplaceStore.ReturnToHousePool(back, $"quest #{q.Id} completed unpaid - reserve returned");
                ServerLog.Info($"World: quest #{q.Id} '{q.Title}' completed on self-reported {q.Objective} progress - "
                             + $"no reward paid (WorldQuests.PayForSelfReported=false in Config/World.json); {back} returned to the house pool.");
            }
            Frontier.KmhOperationConsequence.Apply(q);
            string unpaid = paid ? "" : " Progress was self-reported, so no reward was paid.";
            if (string.Equals(q.Kind, ServerQuestDto.KindCompetitive, StringComparison.OrdinalIgnoreCase))
            {
                NotifyAll(paid
                    ? $"Global quest won: {q.Title} - {q.Winner} took {Util.SilverFmt.Format(q.RewardPool)}!"
                    : $"Global quest won: {q.Title} - {q.Winner} got there first.{unpaid}");
                Announce($"🏆 {q.Title} - won by {q.Winner}",
                         paid ? $"First to {q.GoalQty} took the pot of {Util.SilverFmt.Format(q.RewardPool)}."
                              : $"First to {q.GoalQty}.{unpaid}");
            }
            else
            {
                int n = q.Contributors.Count;
                NotifyAll(paid
                    ? $"Global quest complete: {q.Title} - {n} colonist(s) shared {Util.SilverFmt.Format(q.RewardPool)}!"
                    : $"Global quest complete: {q.Title} - {n} colonist(s) took part.{unpaid}");
                Announce($"✅ {q.Title} - complete",
                         paid ? $"{n} contributor(s) shared the reward pool of {Util.SilverFmt.Format(q.RewardPool)}."
                              : $"{n} contributor(s) took part.{unpaid}");
            }
            ServerLog.Info($"World: quest #{q.Id} '{q.Title}' completed");
            // Only progress the server itself measured counts toward a federation; a client's own word never does.
            if (!IsSelfReportedObjective(q.Objective))
            {
                if (string.Equals(q.Kind, ServerQuestDto.KindCompetitive, StringComparison.OrdinalIgnoreCase))
                    Global.Federation.FederationService.RecordForPlayer("world", q.Winner);
                else
                    foreach (System.Collections.Generic.KeyValuePair<string, int> c in q.Contributors)
                        if (c.Value > 0) Global.Federation.FederationService.RecordForPlayer("world", c.Key);
            }
            Extensibility.KmhEventBus.Instance.RaiseGlobalQuestCompleted(new KMH.Sdk.Server.Events.GlobalQuestCompletedEvent
            { QuestId = q.Id, Kind = q.Kind, Objective = q.Objective, Winner = q.Winner ?? "", ContributorCount = q.Contributors.Count, RewardPool = q.RewardPool });
        }

        private static void MaybeAutoGenerateQuest(WorldConfig cfg, long now)
        {
            if (!cfg.WorldQuests.Enabled || !cfg.WorldQuests.AutoGen.Enabled) return;
            if (now - _lastQuestGenUtcTicks < TimeSpan.FromMinutes(cfg.WorldQuests.AutoGen.EveryMinutes).Ticks) return;
            _lastQuestGenUtcTicks = now;
            if (WorldStore.ActiveQuests().Count >= cfg.WorldQuests.AutoGen.MaxActive) return;
            // Don't ask for a big hunt on an empty server.
            int activePlayers = ActivePlayerCount();
            if (activePlayers < Math.Max(0, cfg.WorldQuests.Scaling.MinActivePlayers)) return;

            string objective, defName, title, desc;
            int goal;
            if (!TryGenerateCatalogQuest(cfg, out objective, out defName, out title, out desc, out goal))
            {
                string[] tpl = cfg.WorldQuests.Templates;
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
                goal = (cfg.WorldQuests.Scaling.ByActivePlayers
                        && (objective == ServerQuestDto.ObjHunt || objective == ServerQuestDto.ObjBuild))
                    ? ScaledTargetCount(cfg, defName, activePlayers)
                    : _rng.Next(cfg.WorldQuests.AutoGen.GoalMin, cfg.WorldQuests.AutoGen.GoalMax + 1);
            }

            if (!cfg.WorldQuests.AutoGen.AllowSameTargetRepeat
                && TargetOnCooldown(defName, cfg.WorldQuests.AutoGen.RepeatCooldownMinutes))
            {
                ServerLog.Verbose($"World: skipped auto-quest - target '{defName}' on repeat cooldown.");
                return;
            }

            long   target    = ComputeAutoReward(cfg, defName, goal);

            long reward;
            if (cfg.WorldQuests.Rewards.AllowMinted)
            {
                // Re-clamped after the floor, so Rewards.Min can never push a reward past Rewards.Max.
                reward = Math.Max(target, cfg.WorldQuests.Rewards.Min);
                if (cfg.WorldQuests.Rewards.Max > 0) reward = Math.Min(reward, cfg.WorldQuests.Rewards.Max);
            }
            else
            {
                // Closed loop: promise only what the house pool can back. Skip rather than post a sub-floor grind.
                long pool = Marketplace.MarketplaceStore.HousePoolBalance();
                reward = Math.Min(target, pool);
                if (cfg.WorldQuests.Rewards.Max > 0) reward = Math.Min(reward, cfg.WorldQuests.Rewards.Max);
                if (reward < cfg.WorldQuests.Rewards.Min)
                {
                    ServerLog.Info($"World: skipped auto-quest - house pool ({pool}) below the {cfg.WorldQuests.Rewards.Min} minimum reward (minting off).");
                    return;
                }
            }

            int durationMin = AutoGenMinutes(cfg.WorldQuests.AutoGen, _rng.NextDouble());
            RememberTarget(defName);
            CreateWorldQuest(ServerQuestDto.KindCooperative, objective, defName, goal, reward,
                             durationMin, title, desc, "auto");
        }

        // Counting recent activity rather than only who is online keeps a quest posted at a quiet hour sized sensibly.
        private static int ActivePlayerCount()
        {
            int online = 0;
            try
            {
                foreach (ServerClient c in Network.ServerClients.Keys)
                    if (c?.IsVerified == true && Simulation.SimulatedAccounts.IsPerson(c.GetData<UserFile>()?.Username)) online++;
            }
            catch { }

            WorldConfig cfg = WorldConfig.Current;
            if (cfg.WorldQuests.Scaling.OnlinePlayersOnly) return online;

            int recent = RecentlyActivePlayerCount(cfg.WorldQuests.Scaling.ActivityWindowMinutes);
            // Never size below who is actually here - a stale window must not shrink a busy server.
            return recent > online ? recent : online;
        }

        // Reads the report time KMH already keeps, rather than adding a second activity tracker to drift from it.
        private static int RecentlyActivePlayerCount(int windowMinutes)
        {
            if (windowMinutes <= 0) return 0;
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(windowMinutes).Ticks;
            int n = 0;
            try
            {
                foreach (PlayerStats.Dto.PlayerLeaderboardEntry e in PlayerStats.PlayerStatsStore.BuildSnapshot().Entries)
                    if (e != null && e.LastReportUtcTicks >= cutoff && Simulation.SimulatedAccounts.IsPerson(e.Username)) n++;
            }
            catch { }
            return n;
        }

        // Dangerous or rare game asks for fewer kills, common small game for more; the owner's multiplier scales both.
        internal static int ScaledTargetCount(WorldConfig cfg, string defName, int activePlayers)
        {
            WorldQuestScaling s = cfg.WorldQuests.Scaling;
            double weight = (s.DifficultyMultiplier <= 0 ? 1.0 : s.DifficultyMultiplier) / TargetDifficulty(defName);
            double raw  = s.BaseTargetCount + Math.Max(0, activePlayers) * s.TargetsPerActivePlayer * weight;
            return Clamp((int)Math.Round(raw), s.MinTargetCount, s.MaxTargetCount);
        }

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

        // A triangular draw over MinMinutes..MaxMinutes peaking at DefaultMinutes.
        internal static int AutoGenMinutes(WorldQuestAutoGen g, double u)
        {
            int min = g.MinMinutes, max = Math.Max(g.MinMinutes, g.MaxMinutes);
            int mode = Clamp(g.DefaultMinutes, min, max);
            if (max <= min) return min;
            double span = max - min, c = (mode - min) / span;
            u = Math.Min(1.0, Math.Max(0.0, u));
            double x = u < c ? min + Math.Sqrt(u * span * (mode - min)) : max - Math.Sqrt((1 - u) * span * (max - mode));
            return Clamp((int)Math.Round(x), min, max);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (lo > hi) { int t = lo; lo = hi; hi = t; }
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static readonly System.Collections.Generic.Dictionary<string, long> _recentTargets
            = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static bool TargetOnCooldown(string defName, int cooldownMinutes)
        {
            if (string.IsNullOrEmpty(defName) || cooldownMinutes <= 0) return false;
            lock (_recentTargets)
                return _recentTargets.TryGetValue(defName, out long ticks)
                    && DateTime.UtcNow.Ticks - ticks < TimeSpan.FromMinutes(cooldownMinutes).Ticks;
        }
        private static void RememberTarget(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return;
            lock (_recentTargets)
            {
                _recentTargets[defName] = DateTime.UtcNow.Ticks;
                if (_recentTargets.Count > 64)
                {
                    string oldest = null; long oldestT = long.MaxValue;
                    foreach (var kv in _recentTargets) if (kv.Value < oldestT) { oldestT = kv.Value; oldest = kv.Key; }
                    if (oldest != null) _recentTargets.Remove(oldest);
                }
            }
        }

        private static string[] BuildWeatherPool(WorldConfig cfg)
        {
            List<string> pool = new List<string>(cfg.Events.Weather.ConditionDefs ?? Array.Empty<string>());
            if (cfg.Events.Weather.AutoDiscover)
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

        private static bool TryGenerateCatalogQuest(WorldConfig cfg, out string objective, out string defName,
                                                    out string title, out string desc, out int goal)
        {
            objective = ServerQuestDto.ObjDeliver; defName = null; title = null; desc = null; goal = 0;
            if (cfg.WorldQuests.AutoGen.FromCatalogPercent <= 0 || _rng.Next(100) >= cfg.WorldQuests.AutoGen.FromCatalogPercent) return false;
            if (!cfg.ObjectiveAllowed(ServerQuestDto.ObjDeliver)) return false;

            var pick = ItemLabels.ItemLabelCache.RandomDeliverable(cfg.WorldQuests.AutoGen.CatalogItemMinValue, cfg.WorldQuests.AutoGen.CatalogItemMaxValue, _rng);
            if (pick == null) return false;
            (string def, string label, long value) = pick.Value;

            defName = def;
            goal    = (int)Math.Max(1, Math.Min(cfg.WorldQuests.AutoGen.CatalogGoalValue / Math.Max(1, value), cfg.WorldQuests.AutoGen.GoalMax));
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

        private static string PickAllowedTemplate(WorldConfig cfg, string[] tpl)
        {
            List<string> ok = new List<string>();
            foreach (string t in tpl)
            {
                int bar = t?.IndexOf('|') ?? -1;
                if (bar <= 0) continue;
                string obj = t.Substring(0, bar).Trim();
                if (!cfg.ObjectiveAllowed(obj)) continue;
                // A quest that completes on the client's word and pays nothing reads as broken, so it is never generated.
                if (IsSelfReportedObjective(obj) && !cfg.WorldQuests.PayForSelfReported) continue;
                ok.Add(t);
            }
            return ok.Count == 0 ? null : ok[_rng.Next(ok.Count)];
        }

        // Every input is server-owned: client-reported wealth would pin every reward at the cap.
        internal static long ComputeAutoRewardForTest(WorldConfig cfg, string targetDef, int goal)
            => ComputeAutoReward(cfg, targetDef, goal);

        private static long ComputeAutoReward(WorldConfig cfg, string targetDef, int goal)
        {
            long pool   = Marketplace.MarketplaceStore.HousePoolBalance();
            long wealth = Treasury.TreasuryStore.TotalSilverHeld();
            long unit   = ItemLabels.ItemLabelCache.BaseValue(targetDef);
            long byValue = unit > 0 ? unit * Math.Max(1, goal) * cfg.WorldQuests.Rewards.ValuePercent / 100 : 0;
            long perTarget = (long)Math.Max(0, cfg.WorldQuests.Rewards.PerTarget) * Math.Max(1, goal);
            long target = Math.Max(pool * cfg.WorldQuests.Rewards.HousePoolPercent / 100,
                          Math.Max((long)(wealth * cfg.WorldQuests.Rewards.WealthPercent / 100), Math.Max(byValue, perTarget)));
            if (target > cfg.WorldQuests.Rewards.Max) target = cfg.WorldQuests.Rewards.Max;
            return target;
        }

        // On a marker per quest, so a payout resumed after a crash cannot land twice; it stays owed until paid.
        private static void PayOut(long questId, System.Collections.Generic.Dictionary<string, long> payouts)
        {
            if (payouts == null) return;
            foreach (System.Collections.Generic.KeyValuePair<string, long> kv in payouts)
            {
                // Earned in a save that is being destroyed: dropped with it, never paid into the new one.
                if (Economy.KmhEconomyReset.Resetting(kv.Key)) { WorldStore.ClearPendingPayout(questId, kv.Key); continue; }
                if (kv.Value > 0 && !Treasury.TreasuryStore.DepositEscrowOnce(kv.Key, $"wq:{questId}:reward", kv.Value, null, null,
                                                                            "world quest reward"))
                {
                    ServerLog.Error($"World: quest #{questId} reward of {kv.Value} for '{kv.Key}' could not be credited - it stays owed and the next boot pays it.");
                    continue;
                }
                AtDeliveryPhaseForTest?.Invoke("reward-paid");
                WorldStore.ClearPendingPayout(questId, kv.Key);
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
                Items.KmhPayloadEscrow.DeliverSilver(u, amount, "world event stipend", "world event stipend could not be credited");
                KmhRouter.SendTo(c, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(u));
            }
        }

        private static void NotifyAll(string text) => Notifications.KmhNotify.ToEveryoneOnline("neutral", text);

        private static void NotifyUser(string user, string text)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, user, StringComparison.OrdinalIgnoreCase))
                { KmhRouter.Notify(c, "neutral", text); return; }
            }
        }

        // Plain text: names and quest titles can carry markdown or an @, so the title is escaped and nothing pings.
        private static void Announce(string title, string desc)
        {
            ulong ch = Features.Discord.DiscordBridge.Config?.AnnouncementsChannelId ?? 0;
            if (ch == 0) return;
            Features.Discord.DiscordBridge.PostToChannel(ch,
                $"🌍 **{Features.Discord.DiscordText.SafeName(title)}**\n{Features.Discord.DiscordText.NoMentions(desc)}");
        }
    }
}
