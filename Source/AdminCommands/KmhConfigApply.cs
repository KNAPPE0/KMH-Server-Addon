using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.AdminCommands
{
    // Reloads through the same table `kmh reload` uses, so an in-game edit and a hand edit take the same route.
    internal static class KmhConfigApply
    {
        // Read-modify-write is not atomic; two owners saving at once would each silently drop the other's change.
        private static readonly object _applyLock = new object();

        public static bool Apply(string area, Dictionary<string, string> edits, string actor, out string error)
            => Apply(area, edits, actor, out error, out _);

        // ownerNote says whether a restart is needed: "saved" alone reads as "applied" and sends owners bug-hunting.
        public static bool Apply(string area, Dictionary<string, string> edits, string actor,
                                 out string error, out string ownerNote)
        {
            lock (_applyLock) return ApplyLocked(area, edits, actor, out error, out ownerNote);
        }

        private static bool ApplyLocked(string area, Dictionary<string, string> edits, string actor,
                                        out string error, out string ownerNote)
        {
            error = "";
            ownerNote = "";
            if (edits == null || edits.Count == 0) { error = "Nothing to change."; return false; }

            if (!KmhConfigSchema.TryFind(area, out string path, out object sample)) { error = $"Unknown config area '{area}'."; return false; }

            object live = Load(path, sample);
            List<string> applied = new List<string>();

            foreach (KeyValuePair<string, string> kv in edits)
            {
                if (!TryResolve(live, kv.Key, out object owner, out PropertyInfo prop))
                { error = $"Unknown setting '{kv.Key}'."; return false; }

                if (KmhConfigSchema.IsSecret(prop.Name))
                { error = $"'{kv.Key}' is a credential and cannot be set from in game."; return false; }

                if (KmhConfigSchema.IsServerOwned(prop.Name))
                { error = $"'{kv.Key}' is assigned by the server itself and cannot be edited."; return false; }

                if (KmhConfigSchema.IsConsoleOnly(prop.Name))
                { error = $"'{kv.Key}' can only be changed from the server console or the config file."; return false; }

                if (!TryParse(prop.PropertyType, kv.Value, out object parsed, out string why))
                { error = $"'{kv.Key}': {why}"; return false; }

                try { prop.SetValue(owner, parsed); }
                catch (Exception ex) { error = $"'{kv.Key}' could not be set ({ex.GetType().Name})."; return false; }
                applied.Add(kv.Key);
            }

            // Through the store, never a direct write: atomic, serialised, and a failure reaches the degraded gate.
            if (!JsonFileStore.Save(path, live))
            {
                error = "The server could not write that file - check free space and permissions on KMH-Data.";
                return false;
            }

            string note = Reload(live.GetType(), out ownerNote);
            ServerLog.Warn($"Config: {actor} changed {applied.Count} setting(s) in {area} ({string.Join(", ", applied.ToArray())}){note}");
            return true;
        }

        // Empty when the area has no live reload, which is a restart-only config rather than a failure.
        private static string Reload(Type configType, out string ownerNote)
        {
            ownerNote = "Saved.";
            try
            {
                foreach (KmhConfigReload.Entry e in KmhConfigReload.All())
                {
                    if (e.ConfigType != configType) continue;
                    if (e.Reload == null)
                    {
                        ownerNote = "Saved. This area only takes effect after a server restart.";
                        return " - restart required for this area";
                    }
                    e.Reload();
                    ownerNote = string.IsNullOrEmpty(e.RestartOnly)
                        ? "Saved and applied live."
                        : $"Saved and applied live, except {e.RestartOnly} - that needs a server restart.";
                    return string.IsNullOrEmpty(e.Note) ? " - reloaded" : $" - reloaded, {e.Note}";
                }
            }
            catch (Exception ex)
            {
                ownerNote = $"Saved, but applying it now failed ({ex.GetType().Name}) - a restart will pick it up.";
                return $" - saved, but the reload threw ({ex.GetType().Name})";
            }
            return " - saved";
        }

        private static object Load(string path, object sample)
        {
            try
            {
                if (!File.Exists(path)) return sample;
                return JsonConvert.DeserializeObject(File.ReadAllText(path), sample.GetType()) ?? sample;
            }
            catch (Exception) { return sample; }
        }

        private static bool TryResolve(object root, string dotted, out object owner, out PropertyInfo prop)
        {
            owner = null; prop = null;
            if (string.IsNullOrEmpty(dotted)) return false;

            string[] parts = dotted.Split('.');
            object current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                PropertyInfo p = current?.GetType().GetProperty(parts[i],
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead || !p.CanWrite) return false;

                if (i == parts.Length - 1) { owner = current; prop = p; return true; }

                object next = p.GetValue(current);
                // Created, not refused, or a new setting could never be set on a config file written before it existed.
                if (next == null)
                {
                    try { next = Activator.CreateInstance(p.PropertyType); p.SetValue(current, next); }
                    catch (Exception) { return false; }
                }
                current = next;
            }
            return false;
        }

        internal static bool TryParse(Type t, string raw, out object value, out string why)
        {
            value = null; why = "";
            string s = (raw ?? "").Trim();

            if (t == typeof(bool))
            {
                if (bool.TryParse(s, out bool b)) { value = b; return true; }
                why = "expected true or false"; return false;
            }
            if (t == typeof(int))
            {
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) { value = i; return true; }
                why = "expected a whole number"; return false;
            }
            if (t == typeof(long))
            {
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { value = l; return true; }
                why = "expected a whole number"; return false;
            }
            // Invariant parsing both ways, or a German client's "1,5" becomes 15 on an English server.
            if (t == typeof(float))
            {
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) { value = f; return true; }
                why = "expected a number"; return false;
            }
            if (t == typeof(double))
            {
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { value = d; return true; }
                why = "expected a number"; return false;
            }
            if (t == typeof(string)) { value = s; return true; }

            if (t == typeof(string[]))  { value = SplitList(s); return true; }
            if (t == typeof(List<string>)) { value = new List<string>(SplitList(s)); return true; }

            why = "this setting cannot be edited in game";
            return false;
        }

        internal static string[] SplitList(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new string[0];
            string[] parts = s.Split(',');
            List<string> keep = new List<string>();
            foreach (string p in parts)
            {
                string v = p.Trim();
                if (v.Length > 0) keep.Add(v);
            }
            return keep.ToArray();
        }
    }
}
