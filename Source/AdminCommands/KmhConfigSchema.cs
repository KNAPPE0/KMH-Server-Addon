using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Maintenance;
using Newtonsoft.Json;

namespace KMHServerAddon.AdminCommands
{
    internal sealed class ConfigFieldSpec
    {
        public string Path  = "";
        public string Type  = "";      // bool | int | number | text | list
        public string Value = "";
        public string Default = "";
        public bool   ReadOnly;
        public bool   Secret;
    }

    // Reflected, not listed: a hand-written field list silently stops matching the classes it describes.
    internal static class KmhConfigSchema
    {
        // Server-assigned: a hand edit only makes the file disagree with reality, so these are shown, never accepted.
        private static readonly HashSet<string> ServerOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SchemaVersion",
            "RegisteredServerId", "LastSequence", "LastMetadataRevision", "SigningGeneration",
        };

        // Console or file only, like any *Path: a settings grant must never redirect what the server dials or runs.
        private static readonly HashSet<string> ConsoleOnlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BaseUrl", "Url", "PublicHost", "PublicApiHost",
        };

        internal static bool IsServerOwned(string name) => !string.IsNullOrEmpty(name) && ServerOwned.Contains(name);

        internal static bool IsConsoleOnly(string name)
            => !string.IsNullOrEmpty(name)
            && (ConsoleOnlyNames.Contains(name) || name.EndsWith("Path", StringComparison.OrdinalIgnoreCase));

        internal static bool IsReadOnly(string name) => IsServerOwned(name) || IsConsoleOnly(name);

        public static List<string> Areas()
        {
            var names = new List<string>();
            foreach (var t in KmhConfigValidation.ConfigTargets()) names.Add(t.name);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        internal static bool TryFind(string area, out string path, out object sample)
        {
            foreach (var t in KmhConfigValidation.ConfigTargets())
                if (string.Equals(t.name, area, StringComparison.OrdinalIgnoreCase))
                {
                    path = t.path; sample = t.sample; return true;
                }
            path = null; sample = null; return false;
        }

        // The file, not the in-memory copy: it is what an owner edits by hand and what a reload re-reads.
        private static object LoadLive(string path, object sample)
        {
            try
            {
                if (!File.Exists(path)) return sample;
                string text = File.ReadAllText(path);
                object loaded = JsonConvert.DeserializeObject(text, sample.GetType());
                return loaded ?? sample;
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"Config editor: could not read {Path.GetFileName(path)} ({ex.GetType().Name}); showing defaults.");
                return sample;
            }
        }

        public static List<ConfigFieldSpec> Describe(string area)
        {
            var fields = new List<ConfigFieldSpec>();
            if (!TryFind(area, out string path, out object sample)) return fields;
            Walk(LoadLive(path, sample), sample, "", fields, depth: 0);
            return fields;
        }

        private const int MaxDepth = 2;

        private static void Walk(object live, object defaults, string prefix, List<ConfigFieldSpec> into, int depth)
        {
            foreach (PropertyInfo p in live.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;                       // computed views are not settings
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                string dotted = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                object value = SafeGet(p, live);
                object fallback = defaults == null ? null : SafeGet(p, defaults);

                string kind = KindOf(p.PropertyType);
                if (kind == null)
                {
                    // Depth-bounded so "Bot.GuildId" is reachable but a deep graph cannot become thousands of rows.
                    if (depth >= MaxDepth || value == null || !p.PropertyType.IsClass) continue;
                    Walk(value, fallback, dotted, into, depth + 1);
                    continue;
                }

                bool secret = IsSecret(p.Name);
                into.Add(new ConfigFieldSpec
                {
                    Path     = dotted,
                    Type     = kind,
                    Value    = secret ? "" : Render(value),
                    Default  = secret ? "" : Render(fallback),
                    ReadOnly = IsReadOnly(p.Name),
                    Secret   = secret,
                });
            }
        }

        // Matched on the name, so a credential added later is withheld without anyone remembering to list it.
        internal static bool IsSecret(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string KindOf(Type t)
        {
            if (t == typeof(bool)) return "bool";
            if (t == typeof(int) || t == typeof(long)) return "int";
            if (t == typeof(float) || t == typeof(double)) return "number";
            if (t == typeof(string)) return "text";
            if (t == typeof(string[])) return "list";
            if (t == typeof(List<string>)) return "list";
            return null;
        }

        private static object SafeGet(PropertyInfo p, object on)
        {
            try { return p.GetValue(on); } catch (Exception) { return null; }
        }

        private static string Render(object v)
        {
            if (v == null) return "";
            if (v is bool b) return b ? "true" : "false";
            if (v is string[] arr) return string.Join(", ", arr);
            if (v is List<string> list) return string.Join(", ", list.ToArray());
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }
}
