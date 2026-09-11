using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Frontier
{
    // The only place a winner is decided; a client asks whether it is eligible rather than working it out.
    internal static class FrontierCapture
    {
        // The name tiebreak is load-bearing: without it two contributors that both read "never" fall to enumeration order.
        public static string ResolveEligible(Dictionary<string, int> contributors, Dictionary<string, long> firstUtc)
        {
            if (contributors == null) return "";
            string best = ""; int bestQty = 0; long bestWhen = long.MaxValue;
            foreach (KeyValuePair<string, int> kv in contributors)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                long when = firstUtc != null && firstUtc.TryGetValue(kv.Key, out long t) ? t : long.MaxValue;
                bool better = kv.Value > bestQty
                              || (kv.Value == bestQty && when < bestWhen)
                              || (kv.Value == bestQty && when == bestWhen
                                  && (best.Length == 0 || string.CompareOrdinal(kv.Key, best) < 0));
                if (better) { best = kv.Key; bestQty = kv.Value; bestWhen = when; }
            }
            return best;
        }

        // A captured outpost counts as an ordinary Site everywhere else, so it obeys the ordinary cap.
        public static string CapRefusalFor(int ownedNow, int max, bool forGuild)
        {
            if (max <= 0 || ownedNow < max) return null;
            return forGuild
                ? $"Your guild has reached its {max}-site limit."
                : $"You already own the maximum of {max} site(s) - claim it for your guild, or remove one first.";
        }

        public static string RefusalFor(string outpostState, long windowEndsUtc, long nowTicks,
                                        string eligibleUser, string caller)
        {
            if (string.IsNullOrEmpty(caller)) return "No caller.";
            if (outpostState != Sites.Dto.SiteEntry.OutpostClaimable) return "That location cannot be claimed.";
            if (windowEndsUtc > 0 && nowTicks >= windowEndsUtc) return "The claim window has closed.";
            if (string.IsNullOrEmpty(eligibleUser)) return "Nobody has earned the claim on that location.";
            if (!string.Equals(eligibleUser, caller, StringComparison.OrdinalIgnoreCase))
                return "Someone else earned the claim on that location.";
            return null;
        }
    }
}
