using System;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // Layered over the archetype, and absent means an ordinary Site so nothing existing becomes an outpost by accident.
    internal static class SiteOutposts
    {
        public static string NormalizeTemplate(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return SiteEntry.TemplateNone;
            string k = t.Trim().ToLowerInvariant();
            foreach (string known in SiteEntry.AllTemplates) if (k == known) return k;
            return SiteEntry.TemplateNone;   // a template from a newer build must not become a live outpost here
        }

        public static string NormalizeState(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return SiteEntry.OutpostNone;
            string k = s.Trim().ToLowerInvariant();
            foreach (string known in SiteEntry.AllOutpostStates) if (k == known) return k;
            return SiteEntry.OutpostNone;
        }

        // The rest exist in the vocabulary so adding one later is not a schema change, and are refused until then.
        public static readonly string[] Spawnable = { SiteEntry.TemplateRuins };

        public static bool IsSpawnable(string template)
        {
            string t = NormalizeTemplate(template);
            foreach (string s in Spawnable) if (t == s) return true;
            return false;
        }

        // The state a freshly established outpost of this template starts in.
        public static string EntryStateFor(string template)
        {
            switch (NormalizeTemplate(template))
            {
                case SiteEntry.TemplateRuins: return SiteEntry.OutpostDerelict;
                default:                      return SiteEntry.OutpostNone;
            }
        }

        public static bool IsOutpost(SiteEntry s)
            => s != null && NormalizeTemplate(s.OutpostTemplate) != SiteEntry.TemplateNone;

        // Not yet anyone's. A contested location must be system or neutral controlled, never player owned.
        public static bool IsContested(string state)
        {
            switch (NormalizeState(state))
            {
                case SiteEntry.OutpostDerelict:
                case SiteEntry.OutpostHostile:
                case SiteEntry.OutpostDefeated:
                case SiteEntry.OutpostClaimable: return true;
                default:                         return false;
            }
        }

        // The dangerous shortcuts are reaching Claimable without the work, and re-entering it after a capture.
        public static bool CanTransition(string from, string to)
        {
            string f = NormalizeState(from), t = NormalizeState(to);
            if (f == t) return false;
            switch (f)
            {
                case SiteEntry.OutpostDerelict:  return t == SiteEntry.OutpostClaimable || t == SiteEntry.OutpostDormant;
                case SiteEntry.OutpostHostile:   return t == SiteEntry.OutpostDefeated  || t == SiteEntry.OutpostDormant;
                case SiteEntry.OutpostDefeated:  return t == SiteEntry.OutpostClaimable || t == SiteEntry.OutpostDormant;
                case SiteEntry.OutpostClaimable: return t == SiteEntry.OutpostCaptured  || t == SiteEntry.OutpostDormant;
                // A dormant location can be brought back by the Director, but never straight to Claimable.
                case SiteEntry.OutpostDormant:   return t == SiteEntry.OutpostDerelict  || t == SiteEntry.OutpostHostile;
                default:                         return false;   // none and captured are terminal here
            }
        }

        // Pure, because the replay case is what keeps a resolution safe across a restart.
        internal enum Move { Allowed, AlreadyThere, WrongFrom, Illegal }

        public static Move VerdictFor(string current, string expectedFrom, string to)
        {
            string c = NormalizeState(current), f = NormalizeState(expectedFrom), t = NormalizeState(to);
            if (c == t) return Move.AlreadyThere;          // a resumed resolution finding its work already done
            if (c != f) return Move.WrongFrom;             // something else moved it; do not force it
            return CanTransition(c, t) ? Move.Allowed : Move.Illegal;
        }

        // The two models must agree: a contested location is nobody's, and an owned one is not contested.
        public static bool IsConsistent(SiteEntry s, out string problem)
        {
            problem = null;
            if (s == null) { problem = "no site"; return false; }
            string state = NormalizeState(s.OutpostState);
            bool outpost = IsOutpost(s);

            if (!outpost && state != SiteEntry.OutpostNone)
            { problem = "an ordinary site carries an outpost state"; return false; }
            if (outpost && state == SiteEntry.OutpostNone)
            { problem = "an outpost has no lifecycle state"; return false; }
            if (IsContested(state) && SiteOwnership.IsPlayerControlled(s))
            { problem = "a contested outpost is player-controlled - it should be nobody's until claimed"; return false; }
            if (state == SiteEntry.OutpostCaptured && !SiteOwnership.IsPlayerControlled(s))
            { problem = "a captured outpost has no player or guild controller"; return false; }
            return true;
        }

        // Derived from the tile so it never changes between snapshots and the world does not read as generated.
        private static readonly string[] Heads =
        { "Black", "Ash", "Gray", "Red", "North", "Cold", "Iron", "Dust", "Pale", "Storm", "Bitter", "Green" };
        private static readonly string[] Tails =
        { "ridge", "fall", "stone", "water", "pass", "reach", "hollow", "moor", "crag", "vale", "mire", "bank" };
        private static readonly string[] Kinds =
        { "Depot", "Station", "Works", "Ruins", "Relay", "Outpost", "Post", "Hold" };

        public static string GenerateName(int tile, string template)
        {
            uint h = (uint)tile * 2654435761u;                 // Knuth multiplicative; stable across runs
            string head = Heads[(int)(h % (uint)Heads.Length)];
            string tail = Tails[(int)((h / 13u) % (uint)Tails.Length)];
            string kind = NormalizeTemplate(template) == SiteEntry.TemplateRuins
                ? "Ruins"
                : Kinds[(int)((h / 173u) % (uint)Kinds.Length)];
            return head + tail + " " + kind;
        }
    }
}
