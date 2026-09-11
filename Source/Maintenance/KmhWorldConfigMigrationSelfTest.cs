using System.Collections.Generic;
using KMHServerAddon.Features.World;
using Newtonsoft.Json;

namespace KMHServerAddon.Maintenance
{
    // Synthetic JSON only, never the live World.json.
    internal static class KmhWorldConfigMigrationSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Legacy blob: a pre-1.1.0 hour timer + a pre-1.3.0 permille reward, nothing else set.
            var legacy = JsonConvert.DeserializeObject<WorldConfig>(
                "{\"EventRollEveryHours\":2,\"QuestRewardWealthPermille\":5}");
            legacy.MigrateLegacy();
            bool hoursOk = legacy.EventRollEveryMinutes == 120;        // 2h -> 120m
            bool permOk  = legacy.QuestRewardWealthPercent == 0.5;     // 5 permille -> 0.5%
            bool flagged = legacy.DidMigrateLegacy;                    // recorded, so EnsureGenerated re-saves + logs
            r.Add(("World migrate: hours -> minutes", hoursOk, $"{legacy.EventRollEveryMinutes}m"));
            r.Add(("World migrate: permille -> percent", permOk, $"{legacy.QuestRewardWealthPercent}%"));
            r.Add(("World migrate: upgrade flagged", flagged, flagged ? "re-save triggered" : "not flagged"));

            // Idempotent: legacy keys are dropped on the first pass, so a second pass changes nothing.
            legacy.MigrateLegacy();
            bool idem = legacy.EventRollEveryMinutes == 120 && legacy.QuestRewardWealthPercent == 0.5;
            r.Add(("World migrate: idempotent rerun", idem, idem ? "no-op" : "mutated on rerun"));

            // Modern file (current keys only): values preserved, not flagged for a needless re-save.
            var modern = JsonConvert.DeserializeObject<WorldConfig>(
                "{\"EventRollEveryMinutes\":240,\"QuestRewardWealthPercent\":0.2}");
            modern.MigrateLegacy();
            bool untouched = modern.EventRollEveryMinutes == 240
                          && modern.QuestRewardWealthPercent == 0.2
                          && !modern.DidMigrateLegacy;
            r.Add(("World migrate: modern file untouched", untouched,
                   modern.DidMigrateLegacy ? "wrongly flagged" : "unchanged"));

            // A config rollback can reintroduce a legacy key after the upgrade ran, so the prune must still keep it.
            string rolled = Persistence.JsonFileStore.PruneUnknownJson(
                "{\"QuestRewardWealthPermille\":5,\"DeadUnknownKey\":1}",
                new WorldConfig(), WorldConfig.LegacyAliasKeys, out List<string> removed);
            bool keptLegacy  = rolled.Contains("QuestRewardWealthPermille");
            bool droppedDead = removed.Contains("DeadUnknownKey") && !rolled.Contains("DeadUnknownKey");
            r.Add(("World prune: keeps legacy alias, drops dead key", keptLegacy && droppedDead,
                   keptLegacy ? (droppedDead ? "permille kept, dead key dropped" : "dead key survived") : "permille wrongly stripped"));

            return r;
        }
    }
}
