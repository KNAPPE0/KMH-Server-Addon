using System.Collections.Generic;

namespace KMHServerAddon.Policy
{
    internal sealed class ResolvedPolicy
    {
        public readonly Dictionary<string, object>      Values  = new Dictionary<string, object>();
        public readonly Dictionary<string, ValueOrigin> Origins = new Dictionary<string, ValueOrigin>();

        public object Get(string key) => Values.TryGetValue(key, out object v) ? v : null;
        public ValueOrigin OriginOf(string key) => Origins.TryGetValue(key, out ValueOrigin o) ? o : ValueOrigin.Default;
    }

    // Precedence lives here and nowhere else: Default < Profile < Migration < Owner < Safety.
    internal static class PolicyResolver
    {
        public static ResolvedPolicy Resolve(
            IDictionary<string, object> defaults,
            IDictionary<string, object> profile   = null,
            IDictionary<string, object> migration = null,
            IDictionary<string, object> owner     = null,
            IDictionary<string, object> safety    = null,
            ICollection<string> safetyKeys        = null)
        {
            var r = new ResolvedPolicy();

            Apply(r, defaults,  ValueOrigin.Default);
            Apply(r, profile,   ValueOrigin.Profile);
            Apply(r, migration, ValueOrigin.Migration);
            Apply(r, owner,     ValueOrigin.Owner);

            // Only when it changes something, or an owner who already set the safe value gets a phantom Safety origin.
            if (safety != null && safetyKeys != null)
                foreach (string key in safetyKeys)
                    if (safety.TryGetValue(key, out object safeVal)
                        && !(r.Values.TryGetValue(key, out object cur) && Equals(cur, safeVal)))
                    {
                        r.Values[key]  = safeVal;
                        r.Origins[key] = ValueOrigin.Safety;
                    }

            return r;
        }

        private static void Apply(ResolvedPolicy r, IDictionary<string, object> layer, ValueOrigin origin)
        {
            if (layer == null) return;
            foreach (KeyValuePair<string, object> kv in layer)
            {
                r.Values[kv.Key]  = kv.Value;
                r.Origins[kv.Key] = origin;
            }
        }
    }
}
