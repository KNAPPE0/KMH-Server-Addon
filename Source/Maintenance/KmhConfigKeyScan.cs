using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // Newtonsoft ignores keys the type lacks, so a typo like "TaxPrecent" just looks like a setting that does not work.
    internal static class KmhConfigKeyScan
    {
        public static List<(string File, string Key)> Unknown()
        {
            var found = new List<(string, string)>();
            foreach ((string name, string path, object sample) in KmhConfigValidation.ConfigTargets())
            {
                if (!File.Exists(path)) continue;
                JObject o;
                try { o = JObject.Parse(File.ReadAllText(path)); } catch { continue; }   // unreadable is the validator's finding
                Walk(o, sample.GetType(), "", name, found, 0);
            }
            return found;
        }

        internal static void Walk(JObject o, Type type, string prefix, string file, List<(string File, string Key)> found, int depth)
        {
            if (o == null || type == null || depth > 8 || AcceptsAnyKey(type)) return;
            Dictionary<string, Type> members = Members(type);
            foreach (JProperty p in o.Properties())
            {
                // Underscore keys are notes for the owner (_readme, _comment), never settings.
                if (p.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (!members.TryGetValue(p.Name, out Type mt)) { found.Add((file, prefix + p.Name)); continue; }
                if (p.Value is JObject child && IsSettingsClass(mt))
                    Walk(child, mt, prefix + p.Name + ".", file, found, depth + 1);
                else if (p.Value is JArray arr && ElementClass(mt) is Type et)
                    foreach (JToken item in arr)
                        if (item is JObject io) Walk(io, et, prefix + p.Name + "[].", file, found, depth + 1);
            }
        }

        // Mirrors Newtonsoft binding: public settable properties and fields, [JsonProperty] names, no [JsonIgnore], any case.
        private static Dictionary<string, Type> Members(Type type)
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.IsDefined(typeof(JsonIgnoreAttribute), true)) continue;
                bool named = p.GetCustomAttribute<JsonPropertyAttribute>(true) is JsonPropertyAttribute a && a.PropertyName != null;
                if (!p.CanWrite && !named && !IsSettingsClass(p.PropertyType) && ElementClass(p.PropertyType) == null) continue;
                map[named ? p.GetCustomAttribute<JsonPropertyAttribute>(true).PropertyName : p.Name] = p.PropertyType;
            }
            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsDefined(typeof(JsonIgnoreAttribute), true)) continue;
                string n = f.GetCustomAttribute<JsonPropertyAttribute>(true)?.PropertyName ?? f.Name;
                map[n] = f.FieldType;
            }
            return map;
        }

        private static bool AcceptsAnyKey(Type type)
        {
            if (typeof(IDictionary).IsAssignableFrom(type) || typeof(JToken).IsAssignableFrom(type)) return true;
            foreach (MemberInfo m in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.IsDefined(typeof(JsonExtensionDataAttribute), true)) return true;
            return false;
        }

        private static bool IsSettingsClass(Type t)
            => t != null && t.IsClass && t != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(t) && !typeof(JToken).IsAssignableFrom(t);

        private static Type ElementClass(Type t)
        {
            if (t == null || t == typeof(string) || typeof(IDictionary).IsAssignableFrom(t) || !typeof(IEnumerable).IsAssignableFrom(t)) return null;
            Type e = t.IsArray ? t.GetElementType() : t.IsGenericType ? t.GetGenericArguments()[0] : null;
            return IsSettingsClass(e) ? e : null;
        }
    }
}
