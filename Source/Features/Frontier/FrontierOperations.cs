using KMHServerAddon.Features.World.Dto;

namespace KMHServerAddon.Features.Frontier
{
    // Most of this vocabulary is reserved, so adding a type later is not a schema change.
    internal static class FrontierOperations
    {
        public static string NormalizeType(string t)
            => Sites.KmhVocabulary.Canonical(t, ServerQuestDto.AllOperationTypes, ServerQuestDto.OpNone);

        public static string NormalizeConsequence(string c)
            => Sites.KmhVocabulary.Canonical(c, ServerQuestDto.AllConsequences, ServerQuestDto.ConsequenceNone);

        // Assault is absent on purpose: it has no resolution mechanic, so generating it would advertise nothing.
        public static readonly string[] Generatable = { ServerQuestDto.OpReclaim };

        public static bool IsGeneratable(string type) => System.Array.IndexOf(Generatable, NormalizeType(type)) >= 0;

        // A consequence outside this list is refused, so a half-built type cannot quietly resolve into nothing.
        public static readonly string[] Dispatchable = { ServerQuestDto.ConsequenceOutpostClaimable };

        // The self-test walks Generatable through here, so a type with no dispatchable consequence fails the build.
        public static string ConsequenceFor(string operationType)
        {
            switch (NormalizeType(operationType))
            {
                case ServerQuestDto.OpReclaim: return ServerQuestDto.ConsequenceOutpostClaimable;
                default:                       return ServerQuestDto.ConsequenceNone;
            }
        }

        public static bool IsDispatchable(string consequence)
            => System.Array.IndexOf(Dispatchable, NormalizeConsequence(consequence)) >= 0;

        public static bool IsWellFormed(ServerQuestDto q, out string problem)
        {
            problem = null;
            if (q == null) { problem = "no operation"; return false; }
            string type = NormalizeType(q.OperationType);
            if (type == ServerQuestDto.OpNone)
            {
                if (!string.IsNullOrEmpty(q.OperationSource) || q.TargetSiteTile >= 0
                    || NormalizeConsequence(q.Consequence) != ServerQuestDto.ConsequenceNone)
                { problem = "an ordinary server quest carries operation metadata"; return false; }
                return true;
            }
            if (q.OperationSource != ServerQuestDto.SourceWorldDirector)
            { problem = "an operation with no known source"; return false; }
            if (q.TargetSiteTile < 0) { problem = "an operation with no target"; return false; }
            if (!IsDispatchable(q.Consequence))
            { problem = "an operation whose consequence cannot be applied"; return false; }
            return true;
        }
    }

    // An operation is never part-minted: the advertised reward is fully backed, or it is not created at all.
    internal static class FrontierFunding
    {
        public static long Affordable(long wanted, long poolBalance, long minimum)
        {
            if (wanted <= 0) return 0;
            long capped = wanted < poolBalance ? wanted : poolBalance;
            return capped < minimum ? 0 : capped;
        }
    }

    // The crash boundary sits between applying the consequence and paying the reward, so a restart must resume here.
    internal static class FrontierResolution
    {
        public const string PhaseResolving          = "resolving";
        public const string PhaseConsequenceApplied = "consequence_applied";
        public const string PhaseResolved           = "resolved";

        public static readonly string[] AllPhases = { PhaseResolving, PhaseConsequenceApplied, PhaseResolved };

        // Strictly forward, because going back would re-apply a consequence or re-pay a reward.
        public static bool CanAdvance(string from, string to)
        {
            switch (from)
            {
                case PhaseResolving:          return to == PhaseConsequenceApplied;
                case PhaseConsequenceApplied: return to == PhaseResolved;
                default:                      return false;
            }
        }

        public static bool IsTerminal(string phase) => phase == PhaseResolved;

        public static bool NeedsResume(string phase)
            => phase == PhaseResolving || phase == PhaseConsequenceApplied;
    }
}
