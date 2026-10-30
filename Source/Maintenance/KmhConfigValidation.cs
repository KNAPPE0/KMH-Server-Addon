using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Parsing as JSON is not enough: {"FeePercent":"abc"} is well-formed and still silently loses an owner's setting.
    internal static class KmhConfigValidation
    {
        // Unknown and missing members are fine here, because the prune and backfill steps handle those.
        public static bool LoadTest(string json, object typeSample, out string error)
        {
            error = null;
            if (typeSample == null) { error = "no type"; return false; }
            if (string.IsNullOrWhiteSpace(json)) { error = "empty file"; return false; }
            try
            {
                object result = JsonConvert.DeserializeObject(json, typeSample.GetType());
                if (result == null) { error = "deserialized to null"; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // A missing file is not a failure, because boot generates it with safe defaults.
        public static List<string> ValidateAll(Action<string> reply = null, bool recover = false)
        {
            var failed = new List<string>();
            foreach ((string name, string path, object sample) in ConfigTargets())
            {
                if (!File.Exists(path)) { reply?.Invoke($"  {name}: not generated yet (safe)"); continue; }
                string json = null;
                try { json = File.ReadAllText(path); } catch (Exception ex) { reply?.Invoke($"  {name}: unreadable - {ex.Message}"); failed.Add(name); continue; }

                if (LoadTest(json, sample, out string err)) { reply?.Invoke($"  {name}: OK"); continue; }

                reply?.Invoke($"  {name}: [FAIL] {err}");
                failed.Add(name);
                if (recover) KmhConfigRecovery.RecoverFromLatestBackup(name, path, sample);
            }
            return failed;
        }

        internal static IEnumerable<(string name, string path, object sample)> ConfigTargets()
        {
            yield return ("Economy",     KmhDataPaths.EconomyConfigFile,     new Features.Economy.EconomyConfig());
            yield return ("Sites",       KmhDataPaths.SitesConfigFile,       new Features.Sites.SitesConfig());
            yield return ("Frontier",    KmhDataPaths.FrontierConfigFile,    new Features.Frontier.FrontierConfig());
            yield return ("Discord",     KmhDataPaths.DiscordConfigFile,     new Features.Discord.DiscordConfig());
            yield return ("Reputation",  KmhDataPaths.ReputationConfigFile,  new Features.Reputation.ReputationConfig());
            yield return ("Quests",      KmhDataPaths.QuestsConfigFile,      new Features.Quests.QuestsConfig());
            yield return ("Enforcement", KmhDataPaths.EnforcementConfigFile, new Features.Enforcement.EnforcementConfig());
            yield return ("World",       KmhDataPaths.WorldConfigFile,       new Features.World.WorldConfig());
            yield return ("Maintenance", KmhDataPaths.MaintenanceConfigFile, new MaintenanceConfig());
            yield return ("Transport",   KmhDataPaths.TransportConfigFile,   new Features.Transport.TransportConfig());
            yield return ("Features",    KmhDataPaths.FeaturesConfigFile,    new Features.FeaturesConfig());
            yield return ("Chat",        KmhDataPaths.ChatConfigFile,        new Features.Chat.ChatConfig());
            yield return ("Media",       KmhDataPaths.MediaConfigFile,       new Features.Media.MediaConfig());
            yield return ("Mail",        KmhDataPaths.MailConfigFile,        new Features.Mail.MailConfig());
            yield return ("Federation",  KmhDataPaths.FederationConfigFile,  new Features.Global.Federation.FederationConfig());
            yield return ("Mods",        KmhDataPaths.ModPolicyConfigFile,   new Features.Mods.ModPolicyConfig());
            yield return ("Staff",       KmhDataPaths.StaffConfigFile,       new Features.Identity.StaffConfig());
            yield return ("Global",      KmhDataPaths.GlobalConfigFile,      new Features.Global.GlobalConfig());
        }

        // A failed config loads safe defaults so the server still starts, rather than halting the boot.
        public static void GateOnBoot()
        {
            List<string> failed = ValidateAll(recover: true);
            // Each recovery line already says what happened; this only matters as a count when several failed.
            if (failed.Count > 1)
                ServerLog.Warn($"Config validation: {failed.Count} configs did not load ({string.Join(", ", failed)}) - see the recovery lines above.");

            // Warn, not Verbose: owners must see a value boot silently corrected; none of these fire on a default config.
            foreach (KmhValidationIssue i in KmhConfigValidator.ValidateFiles())
            {
                if (i.Severity == KmhValidationSeverity.Info) ServerLog.Info($"Config check {i}");
                else ServerLog.Warn($"Config check {i}");
            }
        }
    }
}
