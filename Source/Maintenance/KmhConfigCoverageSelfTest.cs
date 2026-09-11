using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KMHServerAddon.Maintenance
{
    // A config missing from the boot load-test reverts to defaults silently, since a wrong field type still parses as JSON.
    internal static class KmhConfigCoverageSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var results = new List<(string, bool, string)>();

            HashSet<Type> covered;
            try
            {
                covered = new HashSet<Type>(KmhConfigValidation.ConfigTargets()
                    .Where(t => t.sample != null).Select(t => t.sample.GetType()));
            }
            catch (Exception ex)
            {
                results.Add(("Config coverage: ConfigTargets runs", false, ex.GetType().Name + ": " + ex.Message));
                return results;
            }

            // A config class is anything that seeds its own file on first boot.
            List<Type> configs;
            try
            {
                configs = typeof(KmhConfigCoverageSelfTest).Assembly.GetTypes()
                    .Where(t => t.IsClass && t.GetMethod("EnsureGenerated",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null)
                    .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            }
            catch (ReflectionTypeLoadException ex)
            {
                configs = ex.Types.Where(t => t != null && t.IsClass && t.GetMethod("EnsureGenerated",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null)
                    .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            }

            results.Add(("Config coverage: config classes found", configs.Count > 0, $"{configs.Count} with EnsureGenerated"));

            var missing = configs.Where(c => !covered.Contains(c)).Select(c => c.Name).ToList();
            results.Add(("Config coverage: every config is load-tested at boot", missing.Count == 0,
                missing.Count == 0 ? $"{configs.Count} config(s), {covered.Count} in ConfigTargets"
                                   : "NOT load-tested: " + string.Join(", ", missing)));

            // A config that seeds its file inside Load() slips past the check above, but not the file catalog.
            var notConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Persistence.KmhDataPaths.PoliciesFile,   // an owner-override store, not a typed config file
            };
            var targetPaths = new HashSet<string>(
                KmhConfigValidation.ConfigTargets().Select(t => t.path ?? ""), StringComparer.OrdinalIgnoreCase);
            var uncovered = Persistence.KmhDataPaths.KnownDataFiles
                .Where(f => (f.Label ?? "").StartsWith("Config/", StringComparison.OrdinalIgnoreCase)
                            && !notConfigs.Contains(f.Path) && !targetPaths.Contains(f.Path))
                .Select(f => f.Label).ToList();
            results.Add(("Config coverage: every Config/ file in the catalog is load-tested", uncovered.Count == 0,
                uncovered.Count == 0 ? $"{targetPaths.Count} target(s) cover the catalog"
                                     : "in the catalog but never load-tested: " + string.Join(", ", uncovered)));

            // A target pointing at no file would silently pass the load-test forever.
            var blankPaths = KmhConfigValidation.ConfigTargets()
                .Where(t => string.IsNullOrWhiteSpace(t.path)).Select(t => t.name).ToList();
            results.Add(("Config coverage: every target has a path", blankPaths.Count == 0,
                blankPaths.Count == 0 ? "all resolve" : "blank path: " + string.Join(", ", blankPaths)));

            // Owners edit these files while the server is up, so a config the reload command never heard of means a restart.
            try
            {
                var reloadCovered = new HashSet<Type>();
                var exemptNoReason = new List<string>();
                foreach (AdminCommands.KmhConfigReload.Entry e in AdminCommands.KmhConfigReload.All())
                {
                    if (e.ConfigType != null) reloadCovered.Add(e.ConfigType);
                    if (e.Reload == null && string.IsNullOrWhiteSpace(e.ExemptReason)) exemptNoReason.Add(e.Area);
                }

                var notReloadable = configs.Where(c => !reloadCovered.Contains(c)).Select(c => c.Name).ToList();
                results.Add(("Config coverage: every config is in the reload table", notReloadable.Count == 0,
                    notReloadable.Count == 0
                        ? $"{reloadCovered.Count} area(s) declared"
                        : "MISSING from `kmh reload`: " + string.Join(", ", notReloadable)));

                results.Add(("Config coverage: a restart-only config says WHY", exemptNoReason.Count == 0,
                    exemptNoReason.Count == 0 ? "" : "no reason given for: " + string.Join(", ", exemptNoReason)));

                // The usage line is built from the table, so an area an owner can type must actually do something.
                var areas = AdminCommands.KmhConfigReload.ReloadableAreas();
                bool allRun = areas.Count > 0;
                foreach (string a in areas)
                {
                    AdminCommands.KmhConfigReload.Entry e = AdminCommands.KmhConfigReload.Find(a);
                    if (e == null || e.Reload == null) allRun = false;
                }
                results.Add(("Config coverage: every advertised reload area resolves to a real action", allRun,
                             string.Join(", ", areas)));
            }
            catch (Exception ex)
            {
                results.Add(("Config coverage: reload table is readable", false, ex.GetType().Name + ": " + ex.Message));
            }

            return results;
        }
    }
}
