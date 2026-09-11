using System.Collections.Generic;
using K = KMHServerAddon.Policy.KmhPolicyKeys;

namespace KMHServerAddon.Policy
{
    // Only fields the owner actually changed are stored, so a profile change still flows into everything else.
    internal static class KmhPolicyStore
    {
        private static readonly Dictionary<string, Dictionary<string, object>> _overrides =
            new Dictionary<string, Dictionary<string, object>>();

        public static IDictionary<string, object> Overrides(string system)
            => _overrides.TryGetValue(system, out Dictionary<string, object> d) ? d : null;

        public static IReadOnlyDictionary<string, Dictionary<string, object>> All() => _overrides;

        // Typed from the field's own default, so a `kmh policy set` string can never corrupt resolution.
        public static bool TryCoerce(string system, string key, string raw, out object value, out string error)
        {
            value = null; error = null;
            Dictionary<string, object> defaults = KmhProfiles.Defaults(system);
            if (!KmhProfiles.IsKnown(KmhProfiles.Balanced)) { error = "policy system unavailable"; return false; }
            if (!defaults.ContainsKey(key)) { error = $"unknown policy key '{key}'"; return false; }

            object def = defaults[key];
            raw = (raw ?? "").Trim();
            switch (def)
            {
                case bool _:
                    // A persisted bool comes back from .ToString() as "True"/"False", so this must be case-insensitive.
                    string b = raw.ToLowerInvariant();
                    if (b == "yes" || b == "true" || b == "on")  { value = true;  return true; }
                    if (b == "no"  || b == "false" || b == "off") { value = false; return true; }
                    error = "expected yes/no"; return false;
                case int _:
                    if (int.TryParse(raw, out int i)) { value = i; return true; }
                    error = "expected a whole number"; return false;
                case long _:
                    if (long.TryParse(raw, out long l)) { value = l; return true; }
                    error = "expected a whole number"; return false;
                case double _:
                    if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dv))
                    { value = dv; return true; }
                    error = "expected a number"; return false;
                default:   // string
                    value = raw; return true;
            }
        }

        public static bool Set(string system, string key, string raw, out string error)
        {
            if (!TryCoerce(system, key, raw, out object value, out error)) return false;
            if (!_overrides.TryGetValue(system, out Dictionary<string, object> d))
                _overrides[system] = d = new Dictionary<string, object>();
            d[key] = value;
            return true;
        }

        public static bool Clear(string system, string key)
        {
            if (!_overrides.TryGetValue(system, out Dictionary<string, object> d)) return false;
            bool removed = d.Remove(key);
            if (d.Count == 0) _overrides.Remove(system);
            return removed;
        }

        public static bool ClearSystem(string system) => _overrides.Remove(system);

        private sealed class FileShape { public Dictionary<string, Dictionary<string, object>> Systems { get; set; } }

        // Re-coerced on load, so a hand-edited Policies.json with a quoted number is corrected rather than trusted.
        public static void LoadFromDisk()
        {
            if (!Persistence.JsonFileStore.TryLoad(Persistence.KmhDataPaths.PoliciesFile, out FileShape shape) || shape?.Systems == null)
            { _overrides.Clear(); return; }

            _overrides.Clear();
            foreach (KeyValuePair<string, Dictionary<string, object>> sys in shape.Systems)
            {
                if (sys.Value == null) continue;
                foreach (KeyValuePair<string, object> kv in sys.Value)
                    Set(sys.Key, kv.Key, kv.Value?.ToString(), out _);   // Set re-coerces + validates
            }
        }

        public static void SaveToDisk()
            => Persistence.JsonFileStore.Save(Persistence.KmhDataPaths.PoliciesFile, new FileShape { Systems = new Dictionary<string, Dictionary<string, object>>(_overrides) });
    }
}
