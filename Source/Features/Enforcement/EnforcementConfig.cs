using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Enforcement
{
    // Owner-tunable enforcement (KMH-Data/Config/Enforcement.json). When Enabled, clients lock Mod Options except
    // SafeMods (matched by packageId or name) and,
    // if AdminBypass is on, connected admins.
    internal class EnforcementConfig
    {
        public bool     Enabled     { get; set; } = false;
        public bool     AdminBypass { get; set; } = true;
        // On: the client merges each config so the server's gameplay fields win
        // while the player keeps personal values (window/colour/audio). UI stays locked.
        public bool     PreservePersonalFields { get; set; } = false;
        public string[] SafeMods    { get; set; } = Array.Empty<string>();

        // Set one of the boolean flags by name (used by the in-game admin dialog).
        public bool SetFlag(string flag, bool value)
        {
            switch ((flag ?? "").Trim().ToLowerInvariant())
            {
                case "admin_bypass":      if (AdminBypass == value) return false; AdminBypass = value; Save(); return true;
                case "preserve_personal": if (PreservePersonalFields == value) return false; PreservePersonalFields = value; Save(); return true;
                default: return false;
            }
        }

        private static EnforcementConfig _current;
        public static EnforcementConfig Current => _current ??= LoadOrDefault();

        public static EnforcementConfig LoadOrDefault()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.EnforcementConfigFile, out EnforcementConfig cfg) && cfg != null)
            {
                cfg.SafeMods ??= Array.Empty<string>();
                return cfg;
            }
            return new EnforcementConfig();
        }

        public static void Reload() => _current = LoadOrDefault();

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KmhDataPaths.EnforcementConfigFile));
                JsonFileStore.Save(KmhDataPaths.EnforcementConfigFile, this);
                _current = this;
            }
            catch (Exception ex) { ServerLog.Warn($"Could not save Enforcement.json: {ex.Message}"); }
        }

        public bool IsSafe(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId) || SafeMods == null) return false;
            foreach (string s in SafeMods)
                if (!string.IsNullOrWhiteSpace(s) && string.Equals(s.Trim(), modId.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Add a mod to the safe list (no-op if already present). Persists.
        public bool AddSafe(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return false;
            if (IsSafe(modId)) return false;
            SafeMods = (SafeMods ?? Array.Empty<string>()).Append(modId.Trim()).ToArray();
            Save();
            return true;
        }

        public bool RemoveSafe(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId) || SafeMods == null) return false;
            string[] kept = SafeMods.Where(s => !string.Equals((s ?? "").Trim(), modId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kept.Length == SafeMods.Length) return false;
            SafeMods = kept;
            Save();
            return true;
        }

        public static void EnsureGenerated()
        {
            string path = KmhDataPaths.EnforcementConfigFile;
            if (File.Exists(path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, DefaultJson);
            }
            catch (Exception ex) { ServerLog.Warn($"Could not generate Enforcement.json: {ex.Message}"); }
        }

        private const string DefaultJson =
@"{
  ""_readme"": ""Config enforcement for KMH-Patch clients. Set Enabled to true to lock players' Mod Options to your server profile. AdminBypass lets connected admins still edit. PreservePersonalFields keeps enforced mods editable but merges each config so your gameplay settings win while players keep window position / colours / audio. SafeMods lists mods players may freely change (by packageId or the display name shown in Mods); everything else is locked. Manage live with: /kmh server enforce on|off|status and /kmh server enforce safe add|remove|list <mod>."",
  ""Enabled"": false,
  ""AdminBypass"": true,
  ""PreservePersonalFields"": false,
  ""SafeMods"": []
}
";
    }
}
