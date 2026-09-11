using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KMHServerAddon.Maintenance
{
    // A toggleable feature absent from status.json is invisible to external monitoring.
    internal static class KmhStatusCoverageSelfTest
    {
        // Toggle -> the status field that makes it observable; names differ where the natural metric does.
        private static readonly Dictionary<string, string> ToggleToStatusField = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Treasury"]    = "HousePoolSilver",
            ["Marketplace"] = "MarketplaceListings",
            ["Guilds"]      = "Guilds",
            ["Quests"]      = "Quests",
            ["Auctions"]    = "Auctions",
            ["WantBoard"]   = "Wants",
            ["LivingWorld"] = "ActiveEvents",
            ["Standings"]   = "Players",
            ["Wealth"]      = "ReportedWealth",
            ["Mail"]        = "MailMessages",
            ["Chat"]        = "ChatMessages",
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var results = new List<(string, bool, string)>();

            Type status = typeof(KmhStatusExport).GetNestedType("KmhStatus", BindingFlags.Public | BindingFlags.NonPublic);
            if (status == null)
            {
                results.Add(("Status coverage: KmhStatus found", false, "nested KmhStatus type is gone"));
                return results;
            }

            HashSet<string> statusFields = new HashSet<string>(
                status.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name), StringComparer.Ordinal);

            // Every bool on FeaturesConfig is a feature toggle.
            List<string> toggles = typeof(Features.FeaturesConfig)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(bool))
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

            results.Add(("Status coverage: toggles found", toggles.Count > 0, $"{toggles.Count} feature toggle(s)"));

            var unmapped = toggles.Where(t => !ToggleToStatusField.ContainsKey(t)).ToList();
            results.Add(("Status coverage: every feature toggle is observable", unmapped.Count == 0,
                unmapped.Count == 0 ? $"{toggles.Count} toggle(s) mapped"
                                    : "NOT reported in status.json: " + string.Join(", ", unmapped)));

            // A map entry naming a field that no longer exists would silently stop meaning anything.
            var deadFields = ToggleToStatusField.Where(kv => !statusFields.Contains(kv.Value))
                .Select(kv => $"{kv.Key}->{kv.Value}").ToList();
            results.Add(("Status coverage: mapped status fields all exist", deadFields.Count == 0,
                deadFields.Count == 0 ? $"{ToggleToStatusField.Count} mapping(s) resolve" : "missing: " + string.Join(", ", deadFields)));

            // And the reverse: a mapping for a toggle that was removed.
            var deadToggles = ToggleToStatusField.Keys.Where(k => !toggles.Contains(k)).ToList();
            results.Add(("Status coverage: no mapping for a removed toggle", deadToggles.Count == 0,
                deadToggles.Count == 0 ? "all live" : "stale: " + string.Join(", ", deadToggles)));

            return results;
        }
    }
}
