using System.Collections.Generic;
using K = KMHServerAddon.Policy.KmhPolicyKeys;

namespace KMHServerAddon.Policy
{
    // The one place a system asks for its effective policy, so no caller can skip the safety bounds.
    internal static class KmhPolicyBook
    {
        // Keys with a hard safe range. Safety wins over an owner value only when the owner's value is out of range.
        private static readonly HashSet<string> SafetyKeys = new HashSet<string>
        {
            K.FeePercent, K.CooldownSeconds, K.MaxPerTransaction,
        };

        public static ResolvedPolicy Resolve(string system, string profile,
            IDictionary<string, object> owner = null, IDictionary<string, object> migration = null)
        {
            Dictionary<string, object> defaults = KmhProfiles.Defaults(system);
            Dictionary<string, object> prof     = KmhProfiles.Overrides(profile, system);

            ResolvedPolicy pre = PolicyResolver.Resolve(defaults, prof, migration, owner);
            Dictionary<string, object> safety = BuildSafetyCorrections(pre);

            return PolicyResolver.Resolve(defaults, prof, migration, owner, safety, SafetyKeys);
        }

        private static Dictionary<string, object> BuildSafetyCorrections(ResolvedPolicy pre)
        {
            var s = new Dictionary<string, object>();

            if (pre.Get(K.FeePercent) is double fee)
            {
                double c = Clamp(fee, 0.0, 50.0);
                if (c != fee) s[K.FeePercent] = c;
            }
            if (pre.Get(K.CooldownSeconds) is int cd)
            {
                int c = cd < 0 ? 0 : (cd > 3600 ? 3600 : cd);
                if (c != cd) s[K.CooldownSeconds] = c;
            }
            if (pre.Get(K.MaxPerTransaction) is long max && max < -1L)
                s[K.MaxPerTransaction] = -1L;   // anything below -1 is meaningless; -1 means unlimited

            return s;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
