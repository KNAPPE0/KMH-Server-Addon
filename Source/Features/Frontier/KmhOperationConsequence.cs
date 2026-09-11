using KMHServerAddon.Features.World.Dto;

namespace KMHServerAddon.Features.Frontier
{
    // Only the world change is journalled here; the quest engine settles the reward atomically with completion.
    internal static class KmhOperationConsequence
    {
        public static void Apply(ServerQuestDto q)
        {
            if (q == null) return;
            string consequence = FrontierOperations.NormalizeConsequence(q.Consequence);
            if (consequence == ServerQuestDto.ConsequenceNone) return;

            if (!FrontierOperations.IsDispatchable(consequence))
            {
                Diagnostics.ServerLog.Warn($"Frontier: operation #{q.Id} completed with consequence '{q.Consequence}', which this build cannot apply - the world is unchanged.");
                return;
            }

            // Journalled before the world changes, so a refusal here is the at-most-once guarantee working.
            if (!KmhWorldDirector.TryBeginResolution(q.Id, consequence, q.TargetSiteTile, out string why))
            { Diagnostics.ServerLog.Verbose($"Frontier: operation #{q.Id} not resolved - {why}"); return; }

            bool applied = false;
            switch (consequence)
            {
                case ServerQuestDto.ConsequenceOutpostClaimable:
                    applied = Sites.SiteStore.TryAdvanceOutpost(
                        q.TargetSiteTile, Sites.Dto.SiteEntry.OutpostDerelict,
                        Sites.Dto.SiteEntry.OutpostClaimable, q.Id, out string outpostWhy);
                    if (!applied) Diagnostics.ServerLog.Warn($"Frontier: operation #{q.Id} consequence not applied - {outpostWhy}");
                    break;
            }

            KmhWorldDirector.TryAdvanceResolution(q.Id, FrontierResolution.PhaseConsequenceApplied);
            KmhWorldDirector.TryAdvanceResolution(q.Id, FrontierResolution.PhaseResolved);
            KmhWorldDirector.NoteOperation(q.Id, false);

            if (applied) Sites.SiteHandler.BroadcastSnapshot();
            // Never retried, so without this the tile stays derelict holding a slot nothing can free.
            else ReleaseLocation(q, "its consequence could not be applied");
        }

        // Finding the work already done counts as success, since a pre-crash run may have applied it.
        public static void Resume(Dto.ResolutionRecord rec)
        {
            if (rec == null || rec.OperationId <= 0) return;

            if (rec.Phase == FrontierResolution.PhaseResolving)
            {
                if (!IsResumable(rec.Consequence))
                {
                    // The record still terminates below, because leaving it would stop the director acting again.
                    Diagnostics.ServerLog.Error($"Frontier: operation #{rec.OperationId} has consequence '{rec.Consequence}', "
                        + $"which this build cannot replay. Tile {rec.TargetTile} is UNTOUCHED and needs an admin. "
                        + "Add it to KmhOperationConsequence.Resumable with real recovery.");
                }
                else if (!ReapplyConsequence(rec, out string why))
                {
                    Diagnostics.ServerLog.Warn($"Frontier: operation #{rec.OperationId} could not be resumed - {why}; releasing tile {rec.TargetTile}.");
                    if (Sites.SiteStore.TryAdvanceOutpost(rec.TargetTile, Sites.Dto.SiteEntry.OutpostDerelict,
                                                          Sites.Dto.SiteEntry.OutpostDormant, 0, out _))
                        KmhWorldDirector.NoteOutpost(rec.TargetTile, false);
                }
                else Diagnostics.ServerLog.Info($"Frontier: resumed operation #{rec.OperationId} - consequence applied on tile {rec.TargetTile}.");

                KmhWorldDirector.TryAdvanceResolution(rec.OperationId, FrontierResolution.PhaseConsequenceApplied);
                rec.Phase = FrontierResolution.PhaseConsequenceApplied;
            }

            if (rec.Phase == FrontierResolution.PhaseConsequenceApplied)
            {
                KmhWorldDirector.NoteOperation(rec.OperationId, false);
                KmhWorldDirector.TryAdvanceResolution(rec.OperationId, FrontierResolution.PhaseResolved);
                Sites.SiteHandler.BroadcastSnapshot();
                Diagnostics.ServerLog.Info($"Frontier: operation #{rec.OperationId} resolved after restart.");
            }
        }

        // Asserted against Dispatchable by the self-test, so a consequence added without recovery fails the build.
        public static readonly string[] Resumable = { ServerQuestDto.ConsequenceOutpostClaimable };

        public static bool IsResumable(string consequence)
        {
            string c = FrontierOperations.NormalizeConsequence(consequence);
            foreach (string s in Resumable) if (c == s) return true;
            return false;
        }

        private static bool ReapplyConsequence(Dto.ResolutionRecord rec, out string why)
        {
            why = null;
            switch (FrontierOperations.NormalizeConsequence(rec.Consequence))
            {
                case ServerQuestDto.ConsequenceOutpostClaimable:
                    return Sites.SiteStore.TryAdvanceOutpost(
                        rec.TargetTile, Sites.Dto.SiteEntry.OutpostDerelict,
                        Sites.Dto.SiteEntry.OutpostClaimable, rec.OperationId, out why);
                default:
                    why = $"this build cannot replay '{rec.Consequence}'";
                    return false;
            }
        }

        // Only completion reaches Apply, so without this an expired operation holds its slot until the cap fills.
        public static void Abandon(ServerQuestDto q)
        {
            if (q == null) return;
            if (FrontierOperations.NormalizeConsequence(q.Consequence) == ServerQuestDto.ConsequenceNone) return;
            KmhWorldDirector.NoteOperation(q.Id, false);
            ReleaseLocation(q, "the operation ended unfinished");
        }

        // Conditional on the tile still waiting on this operation, since whoever moved it on already owns the slot.
        private static void ReleaseLocation(ServerQuestDto q, string why)
        {
            if (Sites.SiteStore.TryAdvanceOutpost(q.TargetSiteTile, Sites.Dto.SiteEntry.OutpostDerelict,
                                                  Sites.Dto.SiteEntry.OutpostDormant, 0, out string refusal))
            {
                KmhWorldDirector.NoteOutpost(q.TargetSiteTile, false);
                Diagnostics.ServerLog.Info($"Frontier: operation #{q.Id} - {why}; tile {q.TargetSiteTile} is dormant and no longer holds a slot.");
                Sites.SiteHandler.BroadcastSnapshot();
            }
            else Diagnostics.ServerLog.Info($"Frontier: operation #{q.Id} - {why}; location not released ({refusal}).");
        }
    }
}
