using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Frontier.Dto;
using KMHServerAddon.Features.World.Dto;

namespace KMHServerAddon.Features.Frontier
{
    internal static class KmhFrontierSelfTest
    {
        // Mutates the live state after the report was taken, so the report must not move.
        private static int AddThenCount(DirectorState live, DirectorState taken)
        {
            live.OutpostTiles.Add(12);
            live.Resolutions.Add(new ResolutionRecord { OperationId = 33 });
            live.PendingPlacement.Proposals.Add(new PlacementProposal { Username = "Bo", Tile = 8 });
            return taken.OutpostTiles.Count == 1 && taken.Resolutions.Count == 1
                   && taken.PendingPlacement.Proposals.Count == 1 ? 1 : 0;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            r.Add(("Frontier: only Reclaim is generatable",
                   FrontierOperations.IsGeneratable(ServerQuestDto.OpReclaim)
                   && !FrontierOperations.IsGeneratable(ServerQuestDto.OpAssault),
                   "assault has no resolution mechanic, so generating it would advertise combat KMH cannot deliver"));
            r.Add(("Frontier: only the claimable consequence is dispatchable",
                   FrontierOperations.IsDispatchable(ServerQuestDto.ConsequenceOutpostClaimable)
                   && !FrontierOperations.IsDispatchable(ServerQuestDto.ConsequenceOutpostCaptured), ""));
            r.Add(("Frontier: an unknown type or consequence degrades to none",
                   FrontierOperations.NormalizeType("siege") == ServerQuestDto.OpNone
                   && FrontierOperations.NormalizeConsequence("burn_it_down") == ServerQuestDto.ConsequenceNone, ""));

            // Walked through the Director's own mapping, so a new type cannot be added without recovery.
            bool everyOpResolves = FrontierOperations.Generatable.Length > 0;
            string unmapped = "";
            foreach (string genOp in FrontierOperations.Generatable)
            {
                string c = FrontierOperations.ConsequenceFor(genOp);
                if (!FrontierOperations.IsDispatchable(c) || !KmhOperationConsequence.IsResumable(c))
                { everyOpResolves = false; unmapped += $"{genOp}->{c} "; }
            }
            r.Add(("Frontier: every generatable operation maps to a consequence that can be applied and replayed",
                   everyOpResolves, unmapped.Length > 0 ? "unmapped: " + unmapped : ""));
            r.Add(("Frontier: an operation type the Director cannot generate has no consequence",
                   FrontierOperations.ConsequenceFor(ServerQuestDto.OpAssault) == ServerQuestDto.ConsequenceNone
                   && FrontierOperations.ConsequenceFor("nonsense") == ServerQuestDto.ConsequenceNone, ""));

            var plain = new ServerQuestDto { Id = 1, GoalQty = 10 };
            r.Add(("Frontier: an ordinary server quest carries no operation metadata",
                   FrontierOperations.IsWellFormed(plain, out string whyPlain), whyPlain ?? ""));
            var contaminated = new ServerQuestDto { Id = 2, TargetSiteTile = 42 };
            r.Add(("Frontier: an ordinary quest with a target is refused",
                   !FrontierOperations.IsWellFormed(contaminated, out string whyC), whyC));

            var op = new ServerQuestDto
            {
                Id = 3, OperationType = ServerQuestDto.OpReclaim,
                OperationSource = ServerQuestDto.SourceWorldDirector,
                TargetSiteTile = 42, Consequence = ServerQuestDto.ConsequenceOutpostClaimable,
            };
            r.Add(("Frontier: a well-formed reclaim operation passes", FrontierOperations.IsWellFormed(op, out string whyOp), whyOp ?? ""));
            var noTarget = new ServerQuestDto
            { Id = 4, OperationType = ServerQuestDto.OpReclaim, OperationSource = ServerQuestDto.SourceWorldDirector,
              Consequence = ServerQuestDto.ConsequenceOutpostClaimable };
            r.Add(("Frontier: an operation with no target is refused",
                   !FrontierOperations.IsWellFormed(noTarget, out string whyT), whyT));
            var undeliverable = new ServerQuestDto
            { Id = 5, OperationType = ServerQuestDto.OpAssault, OperationSource = ServerQuestDto.SourceWorldDirector,
              TargetSiteTile = 42, Consequence = ServerQuestDto.ConsequenceOutpostCaptured };
            r.Add(("Frontier: an operation whose consequence cannot be applied is refused",
                   !FrontierOperations.IsWellFormed(undeliverable, out string whyU), whyU));

            r.Add(("Frontier: resolution advances one step at a time",
                   FrontierResolution.CanAdvance(FrontierResolution.PhaseResolving, FrontierResolution.PhaseConsequenceApplied)
                   && FrontierResolution.CanAdvance(FrontierResolution.PhaseConsequenceApplied, FrontierResolution.PhaseResolved), ""));
            r.Add(("Frontier: resolution never goes backwards",
                   !FrontierResolution.CanAdvance(FrontierResolution.PhaseConsequenceApplied, FrontierResolution.PhaseResolving)
                   && !FrontierResolution.CanAdvance(FrontierResolution.PhaseResolved, FrontierResolution.PhaseConsequenceApplied),
                   "going back would re-apply a consequence or re-pay a reward"));
            r.Add(("Frontier: resolution never skips the consequence",
                   !FrontierResolution.CanAdvance(FrontierResolution.PhaseResolving, FrontierResolution.PhaseResolved),
                   "settling a reward for a consequence that was never applied"));
            r.Add(("Frontier: a boot resumes unfinished phases and leaves resolved ones alone",
                   FrontierResolution.NeedsResume(FrontierResolution.PhaseResolving)
                   && FrontierResolution.NeedsResume(FrontierResolution.PhaseConsequenceApplied)
                   && !FrontierResolution.NeedsResume(FrontierResolution.PhaseResolved), ""));

            // The unfinished record sits first on purpose: pruning walks newest-first, so last position would pass by luck.
            var journal = new List<ResolutionRecord>();
            journal.Add(new ResolutionRecord { OperationId = 99, Phase = FrontierResolution.PhaseResolving });
            for (int i = 1; i <= 20; i++)
                journal.Add(new ResolutionRecord { OperationId = i, Phase = FrontierResolution.PhaseResolved });
            List<ResolutionRecord> pruned = KmhWorldDirector.PruneFinished(journal, 5);
            bool keptUnfinished = false;
            foreach (ResolutionRecord rec in pruned) if (rec.OperationId == 99) keptUnfinished = true;
            r.Add(("Frontier: the resolution journal is bounded", pruned.Count == 6, $"{pruned.Count} kept of 21"));
            r.Add(("Frontier: an unfinished resolution is never pruned away",
                   keptUnfinished, "losing it would let the operation resolve a second time"));

            var cfg = new FrontierConfig { Enabled = true, MaxOutposts = 3, MaxActiveOperations = 2 };
            long now = DateTime.UtcNow.Ticks;
            var st = new DirectorState { SpawnBudget = 1 };
            r.Add(("Frontier: an enabled director with budget may act",
                   KmhWorldDirector.MayAct(st, cfg, now, out string whyAct), whyAct ?? ""));
            r.Add(("Frontier: a disabled director never acts",
                   !KmhWorldDirector.MayAct(st, new FrontierConfig { Enabled = false }, now, out string whyOff), whyOff));
            r.Add(("Frontier: no budget means no action",
                   !KmhWorldDirector.MayAct(new DirectorState { SpawnBudget = 0 }, cfg, now, out string whyB), whyB));
            r.Add(("Frontier: a global cooldown holds the director back",
                   !KmhWorldDirector.MayAct(new DirectorState { SpawnBudget = 5, NextEligibleUtcTicks = now + TimeSpan.FromHours(1).Ticks },
                                            cfg, now, out string whyCd), whyCd));

            var full = new DirectorState { SpawnBudget = 5 };
            full.OutpostTiles.AddRange(new[] { 1, 2, 3 });
            r.Add(("Frontier: the outpost cap stops further spawns",
                   !KmhWorldDirector.MayAct(full, cfg, now, out string whyCap), whyCap));
            var busy = new DirectorState { SpawnBudget = 5 };
            busy.ActiveOperationIds.AddRange(new long[] { 1, 2 });
            r.Add(("Frontier: the operation cap stops further operations",
                   !KmhWorldDirector.MayAct(busy, cfg, now, out string whyOps), whyOps));

            var cooling = new DirectorState();
            cooling.TargetCooldowns["42"] = now + TimeSpan.FromHours(2).Ticks;
            r.Add(("Frontier: a target on cooldown is skipped",
                   !KmhWorldDirector.TargetIsCool(cooling, 42, now)
                   && KmhWorldDirector.TargetIsCool(cooling, 43, now), ""));

            var bcfg = new FrontierConfig { SpawnBudget = 2, BudgetRefillHours = 24 };
            long day = TimeSpan.FromHours(24).Ticks;
            var stale = new DirectorState { SpawnBudget = 0, BudgetRefilledUtcTicks = now - TimeSpan.FromDays(7).Ticks };
            KmhWorldDirector.ApplyRefill(stale, bcfg, now);
            r.Add(("Frontier: an offline week refills one window, not seven",
                   stale.SpawnBudget == 2 && stale.BudgetRefilledUtcTicks == now,
                   "a trickle-style refill would flood the map on the first tick back"));

            // A refill that leaves its own stamp alone reads as overdue forever, restoring the budget as fast as it is spent.
            var b = new DirectorState { SpawnBudget = 2, BudgetRefilledUtcTicks = now };
            b.SpawnBudget -= 1;                                                    // consume
            bool noEarly  = !KmhWorldDirector.ApplyRefill(b, bcfg, now + day / 2); // before the window
            bool onTime   =  KmhWorldDirector.ApplyRefill(b, bcfg, now + day);     // after it
            b.SpawnBudget -= 1;                                                    // consume one of the refilled
            bool noRepeat = !KmhWorldDirector.ApplyRefill(b, bcfg, now + day + 1); // must NOT top up again
            bool nextCycle =  KmhWorldDirector.ApplyRefill(b, bcfg, now + day * 2);
            r.Add(("Frontier: a refill happens once per window and never again until the next one",
                   noEarly && onTime && noRepeat && nextCycle && b.SpawnBudget == 2,
                   $"budget {b.SpawnBudget}, early={!noEarly} repeat={!noRepeat}"));

            var never = new DirectorState { SpawnBudget = 0, BudgetRefilledUtcTicks = 0 };
            r.Add(("Frontier: a director that has never refilled anchors its cycle on the first tick",
                   KmhWorldDirector.ApplyRefill(never, bcfg, now) && never.SpawnBudget == 2
                   && never.BudgetRefilledUtcTicks == now
                   && !KmhWorldDirector.ApplyRefill(never, bcfg, now + 1), ""));

            long soon = now + TimeSpan.FromMinutes(10).Ticks;
            PlacementToken tok = new PlacementToken { Id = "t1", Nonce = 7, ExpiresUtcTicks = soon };
            tok.ExcludeTiles.Add(500);

            r.Add(("Placement: a token with no proposals accepts nothing",
                   !FrontierPlacement.TryAccept(tok, true, out _, out string whyNone), whyNone));

            tok.Proposals.Add(new PlacementProposal { Username = "Ada", Tile = 42, WorldFingerprint = "w1" });
            r.Add(("Placement: one proposal is refused when corroboration is required",
                   !FrontierPlacement.TryAccept(tok, false, out _, out string whyOne), whyOne));
            r.Add(("Placement: one proposal is accepted when the owner allows it",
                   FrontierPlacement.TryAccept(tok, true, out int t1, out _) && t1 == 42, ""));

            tok.Proposals.Add(new PlacementProposal { Username = "ada", Tile = 42, WorldFingerprint = "w1" });
            r.Add(("Placement: the same player cannot vote twice",
                   !FrontierPlacement.TryAccept(tok, false, out _, out string whyDupe), whyDupe));

            tok.Proposals.Add(new PlacementProposal { Username = "Borys", Tile = 42, WorldFingerprint = "w1" });
            r.Add(("Placement: two independent clients agreeing is accepted",
                   FrontierPlacement.TryAccept(tok, false, out int t2, out _) && t2 == 42, ""));

            // Equal corroboration on purpose, since dictionary order would otherwise decide and rerun differently.
            PlacementToken tiedTiles = new PlacementToken { Id = "t-tie", ExpiresUtcTicks = soon };
            tiedTiles.Proposals.Add(new PlacementProposal { Username = "Ada",   Tile = 40, WorldFingerprint = "w1", ReceivedUtcTicks = 10 });
            tiedTiles.Proposals.Add(new PlacementProposal { Username = "Borys", Tile = 40, WorldFingerprint = "w1", ReceivedUtcTicks = 11 });
            tiedTiles.Proposals.Add(new PlacementProposal { Username = "Cai",   Tile = 7,  WorldFingerprint = "w1", ReceivedUtcTicks = 20 });
            tiedTiles.Proposals.Add(new PlacementProposal { Username = "Dai",   Tile = 7,  WorldFingerprint = "w1", ReceivedUtcTicks = 21 });
            r.Add(("Placement: equal corroboration goes to the tile proposed first, not to dictionary order",
                   FrontierPlacement.TryAccept(tiedTiles, false, out int tTie, out _) && tTie == 40, $"tile={tTie}"));

            // Identical arrival stamps, so the tiebreak has to end somewhere rather than fall back to hashing.
            PlacementToken sameInstant = new PlacementToken { Id = "t-tie2", ExpiresUtcTicks = soon };
            sameInstant.Proposals.Add(new PlacementProposal { Username = "Ada",   Tile = 40, WorldFingerprint = "w1", ReceivedUtcTicks = 5 });
            sameInstant.Proposals.Add(new PlacementProposal { Username = "Borys", Tile = 40, WorldFingerprint = "w1", ReceivedUtcTicks = 5 });
            sameInstant.Proposals.Add(new PlacementProposal { Username = "Cai",   Tile = 7,  WorldFingerprint = "w1", ReceivedUtcTicks = 5 });
            sameInstant.Proposals.Add(new PlacementProposal { Username = "Dai",   Tile = 7,  WorldFingerprint = "w1", ReceivedUtcTicks = 5 });
            r.Add(("Placement: identical arrival stamps still resolve to one tile",
                   FrontierPlacement.TryAccept(sameInstant, false, out int tSame, out _) && tSame == 7, $"tile={tSame}"));

            PlacementToken split = new PlacementToken { Id = "t2", ExpiresUtcTicks = soon };
            split.Proposals.Add(new PlacementProposal { Username = "Ada",   Tile = 42, WorldFingerprint = "w1" });
            split.Proposals.Add(new PlacementProposal { Username = "Borys", Tile = 42, WorldFingerprint = "w2" });
            r.Add(("Placement: the same tile on a different world is not agreement",
                   !FrontierPlacement.TryAccept(split, false, out _, out string whySplit), whySplit));

            PlacementToken sneaky = new PlacementToken { Id = "t3", ExpiresUtcTicks = soon };
            sneaky.ExcludeTiles.Add(500);
            sneaky.Proposals.Add(new PlacementProposal { Username = "Ada",   Tile = 500, WorldFingerprint = "w1" });
            sneaky.Proposals.Add(new PlacementProposal { Username = "Borys", Tile = 500, WorldFingerprint = "w1" });
            r.Add(("Placement: an excluded tile is refused however many clients propose it",
                   !FrontierPlacement.TryAccept(sneaky, true, out _, out string whyEx), whyEx));

            PlacementToken used = new PlacementToken { Id = "t4", Consumed = true, ExpiresUtcTicks = soon };
            used.Proposals.Add(new PlacementProposal { Username = "Ada", Tile = 42, WorldFingerprint = "w1" });
            r.Add(("Placement: a consumed token cannot be used again",
                   !FrontierPlacement.TryAccept(used, true, out _, out string whyUsed), whyUsed));
            r.Add(("Placement: an expired token is expired",
                   FrontierPlacement.IsExpired(new PlacementToken { ExpiresUtcTicks = now - 1 }, now)
                   && !FrontierPlacement.IsExpired(tok, now), ""));

            PlacementToken open = new PlacementToken { Id = "t1", ExpiresUtcTicks = soon };
            open.ExcludeTiles.Add(500);
            r.Add(("Placement: a proposal for another token is refused",
                   !FrontierPlacement.ProposalFits(open, "Ada", "not-my-token", 42, "w1", now, out string whyTok), whyTok));
            r.Add(("Placement: a proposal with no fingerprint is refused",
                   !FrontierPlacement.ProposalFits(open, "Ada", "t1", 42, "", now, out string whyFp), whyFp));
            r.Add(("Placement: a proposal for an excluded tile is refused",
                   !FrontierPlacement.ProposalFits(open, "Ada", "t1", 500, "w1", now, out string whyEx2), whyEx2));
            r.Add(("Placement: a valid proposal fits",
                   FrontierPlacement.ProposalFits(open, "Ada", "t1", 42, "w1", now, out _), ""));

            open.Proposals.Add(new PlacementProposal { Username = "Ada", Tile = 42, WorldFingerprint = "w1" });
            r.Add(("Placement: a player may answer a question only once",
                   !FrontierPlacement.ProposalFits(open, "ADA", "t1", 43, "w1", now, out string whyTwice), whyTwice));
            PlacementToken lapsed = new PlacementToken { Id = "t1", ExpiresUtcTicks = now - 1 };
            r.Add(("Placement: an expired question cannot be answered",
                   !FrontierPlacement.ProposalFits(lapsed, "Ada", "t1", 42, "w1", now, out string whyOld), whyOld));
            r.Add(("Placement: a second question cannot open while one is live",
                   !FrontierPlacement.CanOpenNew(open, now)
                   && FrontierPlacement.CanOpenNew(lapsed, now)
                   && FrontierPlacement.CanOpenNew(null, now), "expired and consumed questions free the slot"));

            var job = new Maintenance.KmhScheduler.Job
            { Name = "probe", Interval = TimeSpan.FromMinutes(1), NextDueUtcTicks = now + TimeSpan.FromMinutes(1).Ticks };
            r.Add(("Scheduler: a job is not due before its time",
                   !Maintenance.KmhScheduler.IsDue(job, now), ""));
            r.Add(("Scheduler: a job is due at its time",
                   Maintenance.KmhScheduler.IsDue(job, now + TimeSpan.FromMinutes(1).Ticks), ""));
            long overdue = now + TimeSpan.FromHours(6).Ticks;
            r.Add(("Scheduler: a long outage does not queue up catch-up runs",
                   Maintenance.KmhScheduler.NextDue(job, overdue) == overdue + TimeSpan.FromMinutes(1).Ticks,
                   "next due is one interval from NOW, not six hours of missed slots"));

            var dcfg = new FrontierConfig { Enabled = true, MaxOutposts = 3, MaxActiveOperations = 2 };
            r.Add(("Director: a disabled director does nothing",
                   KmhWorldDirector.NextStep(new DirectorState { SpawnBudget = 5 },
                                             new FrontierConfig { Enabled = false }, now) == KmhWorldDirector.Step.Idle, ""));

            var idle = new DirectorState { SpawnBudget = 5 };
            r.Add(("Director: an idle enabled director opens a placement question",
                   KmhWorldDirector.NextStep(idle, dcfg, now) == KmhWorldDirector.Step.OpenPlacement, ""));

            var resuming = new DirectorState { SpawnBudget = 5 };
            resuming.Resolutions.Add(new ResolutionRecord { OperationId = 1, Phase = FrontierResolution.PhaseResolving });
            r.Add(("Director: unfinished resolution is finished before anything new starts",
                   KmhWorldDirector.NextStep(resuming, dcfg, now) == KmhWorldDirector.Step.ResumeResolution,
                   "a second operation resolving on top of a half-applied one is the crash bug"));

            var asking = new DirectorState { SpawnBudget = 5 };
            asking.PendingPlacement = new PlacementToken { Id = "p", ExpiresUtcTicks = now + TimeSpan.FromMinutes(5).Ticks };
            r.Add(("Director: an open question is left alone until its window closes",
                   KmhWorldDirector.NextStep(asking, dcfg, now) == KmhWorldDirector.Step.Idle,
                   "deciding early would let whoever answered first choose the tile"));

            var closed = new DirectorState { SpawnBudget = 5 };
            closed.PendingPlacement = new PlacementToken { Id = "p", ExpiresUtcTicks = now - 1 };
            r.Add(("Director: a closed question is resolved",
                   KmhWorldDirector.NextStep(closed, dcfg, now) == KmhWorldDirector.Step.ResolvePlacement, ""));

            var spent = new DirectorState { SpawnBudget = 5 };
            spent.PendingPlacement = new PlacementToken { Id = "p", Consumed = true, ExpiresUtcTicks = now - 1 };
            r.Add(("Director: a consumed question frees the director to ask again",
                   KmhWorldDirector.NextStep(spent, dcfg, now) == KmhWorldDirector.Step.OpenPlacement, ""));

            r.Add(("Funding: a rich pool funds the whole reward",
                   FrontierFunding.Affordable(500, 10000, 100) == 500, ""));
            r.Add(("Funding: a short pool reduces the reward rather than minting the difference",
                   FrontierFunding.Affordable(500, 300, 100) == 300,
                   "the advertised reward is only ever what the pool can actually back"));
            r.Add(("Funding: below the minimum, no operation is created",
                   FrontierFunding.Affordable(500, 50, 100) == 0, ""));
            r.Add(("Funding: an empty pool funds nothing",
                   FrontierFunding.Affordable(500, 0, 100) == 0 && FrontierFunding.Affordable(0, 10000, 100) == 0, ""));
            r.Add(("Funding: a zero minimum still refuses an empty pool",
                   FrontierFunding.Affordable(500, 0, 0) == 0, ""));

            var ordinary = new ServerQuestDto { Id = 7, Consequence = ServerQuestDto.ConsequenceNone };
            r.Add(("Consequence: an ordinary server quest has nothing to dispatch",
                   FrontierOperations.NormalizeConsequence(ordinary.Consequence) == ServerQuestDto.ConsequenceNone,
                   "the dispatcher returns before touching the journal"));

            var qty = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
            { { "Ada", 100 }, { "Borys", 50 } };
            var first = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase)
            { { "Ada", 200 }, { "Borys", 100 } };
            r.Add(("Capture: the largest verified contributor earns the claim",
                   FrontierCapture.ResolveEligible(qty, first) == "Ada", ""));

            // The earlier contributor is listed first on purpose: a rule-free ">=" would still pass by accident.
            var tied = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
            { { "Borys", 75 }, { "Ada", 75 } };
            r.Add(("Capture: a tie goes to whoever contributed first",
                   FrontierCapture.ResolveEligible(tied, first) == "Borys",
                   "without a tiebreak the winner would depend on enumeration order"));
            r.Add(("Capture: nobody earns a claim on an empty operation",
                   FrontierCapture.ResolveEligible(null, null) == ""
                   && FrontierCapture.ResolveEligible(new System.Collections.Generic.Dictionary<string, int>(), first) == "", ""));
            r.Add(("Capture: a zero contribution earns nothing",
                   FrontierCapture.ResolveEligible(
                       new System.Collections.Generic.Dictionary<string, int> { { "Ada", 0 } }, first) == "", ""));

            long window = now + TimeSpan.FromMinutes(30).Ticks;
            r.Add(("Capture: the eligible claimant may claim inside the window",
                   FrontierCapture.RefusalFor(Sites.Dto.SiteEntry.OutpostClaimable, window, now, "Ada", "ada") == null,
                   "case-insensitive"));
            r.Add(("Capture: another player may not take someone else's claim",
                   FrontierCapture.RefusalFor(Sites.Dto.SiteEntry.OutpostClaimable, window, now, "Ada", "Borys") != null, ""));
            r.Add(("Capture: a closed window refuses the claim",
                   FrontierCapture.RefusalFor(Sites.Dto.SiteEntry.OutpostClaimable, now - 1, now, "Ada", "Ada") != null,
                   "the window is what stops a claim being banked indefinitely"));
            r.Add(("Capture: a location that is not claimable cannot be claimed",
                   FrontierCapture.RefusalFor(Sites.Dto.SiteEntry.OutpostDerelict, window, now, "Ada", "Ada") != null
                   && FrontierCapture.RefusalFor(Sites.Dto.SiteEntry.OutpostCaptured, window, now, "Ada", "Ada") != null,
                   "captured is terminal, so a second claim is refused here too"));
            r.Add(("Capture: an empty caller claims nothing",
                   FrontierCapture.RefusalFor(Sites.Dto.SiteEntry.OutpostClaimable, window, now, "Ada", "") != null, ""));

            var opCfg = new FrontierConfig { Enabled = true, MaxOutposts = 2, MaxActiveOperations = 2 };
            var opSt = new DirectorState();
            r.Add(("Frontier: an operator may place on a free tile",
                   KmhWorldDirector.PlacementRefusalFor(opSt, opCfg, 500, now) == null, ""));
            r.Add(("Frontier: an operator may not place while the feature is off",
                   KmhWorldDirector.PlacementRefusalFor(opSt, new FrontierConfig { Enabled = false, MaxOutposts = 2 }, 500, now) != null,
                   "an outpost nothing will ever operate on is worse than none"));

            var atCap = new DirectorState();
            atCap.OutpostTiles.Add(1); atCap.OutpostTiles.Add(2);
            r.Add(("Frontier: an operator may not place past the outpost cap",
                   KmhWorldDirector.PlacementRefusalFor(atCap, opCfg, 500, now) != null, ""));

            var opsAtCap = new DirectorState();
            opsAtCap.ActiveOperationIds.Add(1); opsAtCap.ActiveOperationIds.Add(2);
            r.Add(("Frontier: an operator may not place past the operation cap",
                   KmhWorldDirector.PlacementRefusalFor(opsAtCap, opCfg, 500, now) != null, ""));

            var opCooling = new DirectorState();
            opCooling.TargetCooldowns["500"] = now + TimeSpan.FromMinutes(30).Ticks;
            r.Add(("Frontier: an operator may not re-place on a cooling tile",
                   KmhWorldDirector.PlacementRefusalFor(opCooling, opCfg, 500, now) != null
                   && KmhWorldDirector.PlacementRefusalFor(opCooling, opCfg, 501, now) == null,
                   "only the cooling tile is refused, not every tile"));
            r.Add(("Frontier: an operator may not place on a negative tile",
                   KmhWorldDirector.PlacementRefusalFor(opSt, opCfg, -1, now) != null, ""));

            // Same contributors in opposite order, since the winner must not depend on insertion order.
            var forward = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { "Zoe", 40 }, { "Ada", 40 } };
            var reverse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { "Ada", 40 }, { "Zoe", 40 } };
            string wf = FrontierCapture.ResolveEligible(forward, null);
            string wr = FrontierCapture.ResolveEligible(reverse, null);
            r.Add(("Capture: an untimed tie resolves the same either way round",
                   wf == wr && wf == "Ada", $"forward '{wf}', reverse '{wr}'"));
            r.Add(("Capture: the name only breaks a tie, it never beats a bigger contribution",
                   FrontierCapture.ResolveEligible(
                       new Dictionary<string, int> { { "Zoe", 90 }, { "Ada", 40 } }, null) == "Zoe", ""));
            r.Add(("Capture: contributing first still outranks the name",
                   FrontierCapture.ResolveEligible(
                       new Dictionary<string, int> { { "Zoe", 40 }, { "Ada", 40 } },
                       new Dictionary<string, long> { { "Zoe", 10 }, { "Ada", 20 } }) == "Zoe", ""));

            var cd = new Dictionary<string, long>(StringComparer.Ordinal)
            { { "1", now - 1 }, { "2", now }, { "3", now + TimeSpan.FromHours(1).Ticks } };
            int dropped = KmhWorldDirector.PruneExpiredCooldowns(cd, now);
            r.Add(("Frontier: expired target cooldowns are dropped, live ones kept",
                   dropped == 2 && cd.Count == 1 && cd.ContainsKey("3"),
                   $"dropped {dropped}, {cd.Count} left"));
            r.Add(("Frontier: pruning an empty or absent cooldown map is safe",
                   KmhWorldDirector.PruneExpiredCooldowns(null, now) == 0
                   && KmhWorldDirector.PruneExpiredCooldowns(new Dictionary<string, long>(), now) == 0, ""));
            r.Add(("Frontier: pruning does not make a cooling tile available",
                   !KmhWorldDirector.TargetIsCool(new DirectorState { TargetCooldowns = cd }, 3, now), ""));

            // Read outside the director's lock while the tick may be adding to it.
            var live = new DirectorState { Revision = 4, SpawnBudget = 2 };
            live.OutpostTiles.Add(11);
            live.ActiveOperationIds.Add(22);
            live.TargetCooldowns["11"] = 99;
            live.Resolutions.Add(new ResolutionRecord { OperationId = 22, Phase = FrontierResolution.PhaseResolving });
            live.PendingPlacement = new PlacementToken { Id = "abc", Template = Sites.Dto.SiteEntry.TemplateRuins };
            live.PendingPlacement.ExcludeTiles.Add(5);
            live.PendingPlacement.Proposals.Add(new PlacementProposal { Username = "Ada", Tile = 7 });

            DirectorState snap = live.CopyForRead();
            r.AddRange(Maintenance.KmhCopyCoverage.Check("Director", live, snap, "PendingPlacement"));
            r.Add(("Frontier: a reported placement token is its own copy",
                   snap.PendingPlacement != null
                   && !ReferenceEquals(live.PendingPlacement, snap.PendingPlacement)
                   && !ReferenceEquals(live.PendingPlacement.Proposals, snap.PendingPlacement.Proposals)
                   && !ReferenceEquals(live.PendingPlacement.ExcludeTiles, snap.PendingPlacement.ExcludeTiles)
                   && snap.PendingPlacement.Proposals.Count == 1 && snap.PendingPlacement.ExcludeTiles.Count == 1,
                   "a proposal arriving mid-read would otherwise land inside the report"));
            r.Add(("Frontier: adding to the live state does not change a taken report",
                   AddThenCount(live, snap) == 1,
                   "the report is a snapshot, not a window onto the director"));
            // A fresh pair, because the check above deliberately mutated the previous report.
            var journalled = new DirectorState();
            journalled.Resolutions.Add(new ResolutionRecord { OperationId = 91, Phase = FrontierResolution.PhaseResolving });
            DirectorState taken = journalled.CopyForRead();
            journalled.Resolutions[0].Phase = FrontierResolution.PhaseResolved;
            r.Add(("Frontier: a reported resolution record is its own copy",
                   !ReferenceEquals(journalled.Resolutions[0], taken.Resolutions[0])
                   && taken.Resolutions[0].Phase == FrontierResolution.PhaseResolving,
                   "TryAdvanceResolution edits Phase in place, so a shared record is not a snapshot"));

            r.AddRange(Lifecycle());
            return r;
        }

        // Every step below calls the same pure rule the live code does, so this is the lifecycle the server runs.
        private static List<(string, bool, string)> Lifecycle()
        {
            var r = new List<(string, bool, string)>();
            long now = DateTime.UtcNow.Ticks;
            const string winner = "ada", loser = "bo";

            var qty   = new Dictionary<string, int>    { { winner, 90 }, { loser, 60 } };
            var first = new Dictionary<string, long>   { { winner, now - 500 }, { loser, now - 900 } };
            string decided = FrontierCapture.ResolveEligible(qty, first);

            // The decision is stamped on the site because the quest is pruned long before the claim window closes.
            var site = new Sites.Dto.SiteEntry
            {
                Tile = 4242,
                OutpostTemplate = Sites.Dto.SiteEntry.TemplateRuins,
                OutpostState = Sites.Dto.SiteEntry.OutpostClaimable,
                OriginOperationId = 77,
                ClaimEligibleUsername = decided,
                ClaimWindowEndsUtcTicks = now + TimeSpan.FromMinutes(60).Ticks,
                OwnerKind = Sites.Dto.SiteEntry.OwnerNeutral,
            };
            string stillEligible = Sites.SiteStore.EligibleClaimantFor(site);
            r.Add(("Frontier: the claim winner survives the completed operation being pruned",
                   decided == winner && stillEligible == winner,
                   $"decided '{decided}', resolved '{stillEligible}' with the quest already pruned"));

            r.Add(("Frontier: only the winner may claim, and only inside the window",
                   FrontierCapture.RefusalFor(site.OutpostState, site.ClaimWindowEndsUtcTicks, now, stillEligible, winner) == null
                   && FrontierCapture.RefusalFor(site.OutpostState, site.ClaimWindowEndsUtcTicks, now, stillEligible, loser) != null,
                   "highest verified contribution decides, and nobody else"));

            long after = site.ClaimWindowEndsUtcTicks + 1;
            r.Add(("Frontier: past its deadline a claim is refused cleanly and the location can only go dormant",
                   FrontierCapture.RefusalFor(site.OutpostState, site.ClaimWindowEndsUtcTicks, after, stillEligible, winner) != null
                   && Sites.SiteOutposts.CanTransition(Sites.Dto.SiteEntry.OutpostClaimable, Sites.Dto.SiteEntry.OutpostDormant)
                   && !Sites.SiteOutposts.CanTransition(Sites.Dto.SiteEntry.OutpostDormant, Sites.Dto.SiteEntry.OutpostClaimable),
                   "a missed window must not leave the location claimable forever"));

            // A row saved before the stamp existed must refuse rather than guess a claimant.
            var legacy = new Sites.Dto.SiteEntry
            {
                Tile = 4243,
                OutpostTemplate = Sites.Dto.SiteEntry.TemplateRuins,
                OutpostState = Sites.Dto.SiteEntry.OutpostClaimable,
                OriginOperationId = 0,
                ClaimWindowEndsUtcTicks = now + TimeSpan.FromMinutes(60).Ticks,
                OwnerKind = Sites.Dto.SiteEntry.OwnerNeutral,
            };
            r.Add(("Frontier: a pre-1.3.0 claimable row with no stamp refuses rather than letting anyone take it",
                   Sites.SiteStore.EligibleClaimantFor(legacy) == ""
                   && FrontierCapture.RefusalFor(legacy.OutpostState, legacy.ClaimWindowEndsUtcTicks, now, "", winner) != null, ""));

            r.Add(("Frontier: a player at their site limit cannot take another location",
                   FrontierCapture.CapRefusalFor(3, 3, false) != null
                   && FrontierCapture.CapRefusalFor(2, 3, false) == null
                   && FrontierCapture.CapRefusalFor(9, 0, false) == null
                   && FrontierCapture.CapRefusalFor(5, 5, true) != null,
                   "0 means the owner turned the cap off"));

            r.AddRange(CrashRecovery());
            r.AddRange(Housekeeping());
            return r;
        }

        // A restart at any journal point must converge on the same final state for the replay to be safe.
        private static List<(string, bool, string)> CrashRecovery()
        {
            var r = new List<(string, bool, string)>();
            const string derelict  = Sites.Dto.SiteEntry.OutpostDerelict;
            const string claimable = Sites.Dto.SiteEntry.OutpostClaimable;

            r.Add(("Frontier: a crash before the consequence applied replays it",
                   Sites.SiteOutposts.VerdictFor(derelict, derelict, claimable) == Sites.SiteOutposts.Move.Allowed, ""));

            r.Add(("Frontier: a crash after the consequence applied recognises the work rather than failing",
                   Sites.SiteOutposts.VerdictFor(claimable, derelict, claimable) == Sites.SiteOutposts.Move.AlreadyThere, ""));

            bool everyDispatchableResumes = FrontierOperations.Dispatchable.Length > 0;
            string missing = "";
            foreach (string c in FrontierOperations.Dispatchable)
                if (!KmhOperationConsequence.IsResumable(c)) { everyDispatchableResumes = false; missing += c + " "; }
            r.Add(("Frontier: every consequence the dispatcher applies can also be replayed after a crash",
                   everyDispatchableResumes, missing.Length > 0 ? "no resume handling for: " + missing : ""));

            r.Add(("Frontier: an unknown consequence is refused rather than resolved into nothing",
                   !FrontierOperations.IsDispatchable("outpost_assault_won")
                   && !KmhOperationConsequence.IsResumable("outpost_assault_won")
                   && FrontierOperations.NormalizeConsequence("nonsense") == ServerQuestDto.ConsequenceNone, ""));

            string phase = FrontierResolution.PhaseResolving;
            bool fwd = FrontierResolution.CanAdvance(phase, FrontierResolution.PhaseConsequenceApplied);
            phase = FrontierResolution.PhaseConsequenceApplied;
            fwd &= FrontierResolution.CanAdvance(phase, FrontierResolution.PhaseResolved);
            bool back = FrontierResolution.CanAdvance(FrontierResolution.PhaseResolved, FrontierResolution.PhaseResolving)
                     || FrontierResolution.CanAdvance(FrontierResolution.PhaseConsequenceApplied, FrontierResolution.PhaseResolving);
            r.Add(("Frontier: every resolution phase converges on resolved and none of them reverses",
                   fwd && !back && FrontierResolution.IsTerminal(FrontierResolution.PhaseResolved), ""));

            r.Add(("Frontier: only an unfinished phase asks to be resumed",
                   FrontierResolution.NeedsResume(FrontierResolution.PhaseResolving)
                   && FrontierResolution.NeedsResume(FrontierResolution.PhaseConsequenceApplied)
                   && !FrontierResolution.NeedsResume(FrontierResolution.PhaseResolved),
                   "a resolved record that still asked to resume would stop the director for good"));

            var stuck = new DirectorState { SpawnBudget = 5 };
            stuck.Resolutions.Add(new ResolutionRecord { OperationId = 1, Phase = FrontierResolution.PhaseResolving });
            r.Add(("Frontier: an unfinished resolution outranks starting anything new",
                   KmhWorldDirector.NextStep(stuck, new FrontierConfig { Enabled = true }, DateTime.UtcNow.Ticks)
                       == KmhWorldDirector.Step.ResumeResolution, ""));
            return r;
        }

        private static List<(string, bool, string)> Housekeeping()
        {
            var r = new List<(string, bool, string)>();

            // The director holds only references, so a crash is corrected against the stores that own the data.
            var drifted = new DirectorState();
            drifted.OutpostTiles.AddRange(new[] { 10, 11, 12 });        // 11 no longer exists
            drifted.ActiveOperationIds.AddRange(new long[] { 5, 6 });    // 6 already ended
            bool changed = KmhWorldDirector.ReconcileLists(drifted, new List<int> { 10, 12, 13 },
                                                           new List<long> { 5, 7 }, out int tf, out int of);
            bool matches = drifted.OutpostTiles.Count == 3 && drifted.OutpostTiles.Contains(13)
                        && !drifted.OutpostTiles.Contains(11)
                        && drifted.ActiveOperationIds.Count == 2 && drifted.ActiveOperationIds.Contains(7)
                        && !drifted.ActiveOperationIds.Contains(6);
            r.Add(("Frontier: reconciliation drops stale references and restores missing ones",
                   changed && matches && tf == 2 && of == 2, $"{tf} tile(s), {of} operation(s)"));

            var healthy = new DirectorState();
            healthy.OutpostTiles.Add(10);
            healthy.ActiveOperationIds.Add(5);
            r.Add(("Frontier: a healthy director reconciles silently",
                   !KmhWorldDirector.ReconcileLists(healthy, new List<int> { 10 }, new List<long> { 5 }, out _, out _),
                   "logging a correction every clean boot is noise that hides the real ones"));

            r.Add(("Frontier: a dormant location can be woken and holds no slot while it sleeps",
                   Sites.SiteOutposts.CanTransition(Sites.Dto.SiteEntry.OutpostDormant, Sites.Dto.SiteEntry.OutpostDerelict)
                   && !Sites.SiteOutposts.IsContested(Sites.Dto.SiteEntry.OutpostDormant)
                   && Sites.SiteOutposts.IsContested(Sites.Dto.SiteEntry.OutpostDerelict), ""));

            var coop = new ServerQuestDto
            { Kind = ServerQuestDto.KindCooperative, Objective = ServerQuestDto.ObjDeliver, GoalQty = 150, ProgressQty = 149 };
            var full = new ServerQuestDto
            { Kind = ServerQuestDto.KindCooperative, Objective = ServerQuestDto.ObjDeliver, GoalQty = 150, ProgressQty = 150 };
            r.Add(("Frontier: a cooperative objective accepts only what it still has room for",
                   World.WorldStore.RoomLeftFor(coop, 0) == 1
                   && World.WorldStore.RoomLeftFor(full, 0) == 0,
                   "over-contributing would inflate progress past the goal and buy the claim outright"));

            // A competitive quest is run alone, so the room left is theirs rather than the shared pool's.
            var race = new ServerQuestDto
            { Kind = ServerQuestDto.KindCompetitive, Objective = ServerQuestDto.ObjDeliver, GoalQty = 150, ProgressQty = 149 };
            r.Add(("Frontier: a competitive objective measures room per player, not per pool",
                   World.WorldStore.RoomLeftFor(race, 20) == 130 && World.WorldStore.RoomLeftFor(race, 150) == 0, ""));

            r.AddRange(PlacementLoopChecks());
            return r;
        }

        // The automatic loop starves if a placement question never reaches a client, so these pin the whole cycle.
        private static List<(string, bool, string)> PlacementLoopChecks()
        {
            var r = new List<(string, bool, string)>();
            long now = DateTime.UtcNow.Ticks;
            var cfg = new FrontierConfig { Enabled = true, MaxOutposts = 5, MaxActiveOperations = 4, SpawnBudget = 10 };

            var idle = new DirectorState { SpawnBudget = 5, NextEligibleUtcTicks = 0 };
            r.Add(("Frontier: an unblocked director opens a placement",
                   KmhWorldDirector.NextStep(idle, cfg, now) == KmhWorldDirector.Step.OpenPlacement, ""));

            // An unanswered question is the observed failure: it must expire into a resolve, not sit open forever.
            var waiting = new DirectorState { SpawnBudget = 5 };
            waiting.PendingPlacement = new PlacementToken { Id = "p", ExpiresUtcTicks = now + TimeSpan.FromMinutes(5).Ticks };
            r.Add(("Frontier: an open placement holds the director until its window closes",
                   KmhWorldDirector.NextStep(waiting, cfg, now) == KmhWorldDirector.Step.Idle, ""));

            var lapsed = new DirectorState { SpawnBudget = 5 };
            lapsed.PendingPlacement = new PlacementToken { Id = "p", ExpiresUtcTicks = now - 1 };
            r.Add(("Frontier: an expired placement resolves rather than stalling",
                   KmhWorldDirector.NextStep(lapsed, cfg, now) == KmhWorldDirector.Step.ResolvePlacement, ""));

            var spent = new DirectorState { SpawnBudget = 5 };
            spent.PendingPlacement = new PlacementToken { Id = "p", Consumed = true, ExpiresUtcTicks = now - 1 };
            r.Add(("Frontier: a resolved placement lets the next attempt open",
                   KmhWorldDirector.NextStep(spent, cfg, now) == KmhWorldDirector.Step.OpenPlacement,
                   "a no-op has to schedule another attempt, not end the loop"));

            // Shortening the cadence has to take effect immediately, or a stamp from the old config outlives the reload.
            var stamped = new DirectorState { SpawnBudget = 5, NextEligibleUtcTicks = now + TimeSpan.FromMinutes(90).Ticks };
            r.Add(("Frontier: a future eligibility stamp still gates the director",
                   !KmhWorldDirector.MayAct(stamped, cfg, now, out _), ""));
            r.Add(("Frontier: that stamp is respected as a cooldown, not a permanent stop",
                   KmhWorldDirector.MayAct(stamped, cfg, now + TimeSpan.FromMinutes(91).Ticks, out _), ""));

            var broke = new DirectorState { SpawnBudget = 0 };
            r.Add(("Frontier: an exhausted budget names itself as the blocker",
                   !KmhWorldDirector.MayAct(broke, cfg, now, out string bwhy) && bwhy == "no spawn budget left", bwhy));
            r.Add(("Frontier: a refill restores the budget and re-anchors the window",
                   KmhWorldDirector.ApplyRefill(broke, new FrontierConfig { Enabled = true, SpawnBudget = 7, BudgetRefillHours = 1 }, now)
                   && broke.SpawnBudget == 7 && broke.BudgetRefilledUtcTicks == now, ""));

            KmhWorldDirector.StatusView v = KmhWorldDirector.Status();
            r.Add(("Frontier: status reports a step and its limits", v != null && v.BudgetMax >= 0 && !string.IsNullOrEmpty(v.Step), v?.Step ?? "null"));

            // Interest is only set by a client's own recent traffic, so filtering an unsolicited question by it asks nobody.
            string src = Maintenance.KmhSourceProbe.Read(System.IO.Path.Combine("Features", "Frontier", "FrontierHandler.cs"));
            if (src == null)
                r.Add(("Frontier: the placement question reaches every verified client", true, "not asked - no source tree beside this build"));
            else
            {
                int ask = src.IndexOf("AskForPlacement", StringComparison.Ordinal);
                string body = ask < 0 ? "" : src.Substring(ask);
                r.Add(("Frontier: the placement question reaches every verified client",
                       ask >= 0 && body.IndexOf("BroadcastToInterested", StringComparison.Ordinal) < 0
                       && body.IndexOf("IsVerified", StringComparison.Ordinal) >= 0,
                       ask < 0 ? "AskForPlacement not found" : "interest-filtered broadcasts never reach an idle client"));
            }

            // Cost and reward ride one multiplier, so a busier server is a bigger contest and never a cheaper one.
            var scaleCfg = new FrontierConfig
            {
                ReclaimMaterialQty = 150, ReclaimRewardSilver = 500,
                ReclaimScalePerActivePlayer = 0.15, ReclaimScalePerKnownPlayer = 0.05, ReclaimScaleMax = 5.0,
            };
            double solo  = FrontierScale.For(1,  1,  scaleCfg);
            double busy  = FrontierScale.For(5,  20, scaleCfg);
            double huge  = FrontierScale.For(80, 900, scaleCfg);
            double empty = FrontierScale.For(0,  0,  scaleCfg);
            r.Add(("Frontier: reclaim scales with who is on and who has ever joined, and is bounded both ways",
                   Math.Abs(solo - 1.0) < 0.0001 && Math.Abs(empty - 1.0) < 0.0001
                   && Math.Abs(busy - (1.0 + 4 * 0.15 + 19 * 0.05)) < 0.0001
                   && Math.Abs(huge - 5.0) < 0.0001,
                   $"solo={solo:0.###}, busy={busy:0.###}, huge={huge:0.###}, empty={empty:0.###}"));

            r.Add(("Frontier: a scaled reclaim costs more and pays more by the same multiplier",
                   FrontierScale.Cost(scaleCfg, solo) == 150 && FrontierScale.Reward(scaleCfg, solo) == 500
                   && FrontierScale.Cost(scaleCfg, 2.0) == 300 && FrontierScale.Reward(scaleCfg, 2.0) == 1000
                   && FrontierScale.Cost(scaleCfg, 5.0) == 750 && FrontierScale.Reward(scaleCfg, 5.0) == 2500,
                   $"x2 -> {FrontierScale.Cost(scaleCfg, 2.0)} for {FrontierScale.Reward(scaleCfg, 2.0)}"));

            // Zeroed weights are how an owner turns this off, and it must then behave exactly as it did before.
            var flatCfg = new FrontierConfig
            {
                ReclaimMaterialQty = 150, ReclaimRewardSilver = 500,
                ReclaimScalePerActivePlayer = 0, ReclaimScalePerKnownPlayer = 0, ReclaimScaleMax = 5.0,
            };
            double flat = FrontierScale.For(50, 400, flatCfg);
            r.Add(("Frontier: zeroed scaling weights leave reclaim exactly as it was",
                   Math.Abs(flat - 1.0) < 0.0001
                   && FrontierScale.Cost(flatCfg, flat) == 150 && FrontierScale.Reward(flatCfg, flat) == 500,
                   $"{flat:0.###}"));

            return r;
        }
    }
}
