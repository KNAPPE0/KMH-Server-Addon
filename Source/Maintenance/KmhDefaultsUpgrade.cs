using System;
using System.Collections.Generic;
using System.IO;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Maintenance
{
    // One-shot defaults upgrade (marker-gated, never re-runs): flips only values still at the OLD stock default.
    internal static class KmhDefaultsUpgrade
    {
        // Revision 1 = v1.2.0: living-world auto events/quests on; API transport promoted to default-on public.
        // Revision 2 = v1.2.0 fix: normalize a legacy server still at EconomyMode=Standard (backfilled by an earlier
        // build) to the recommended Balanced. Fresh migrations set Balanced pre-backfill, so they're already there.
        public const int Revision = 2;

        public static List<string> Changed   { get; } = new List<string>();
        public static List<string> Preserved { get; } = new List<string>();

        // True when this boot applied (or first stamped) the upgrade on a pre-existing server - drives the owner notice.
        public static bool RanThisBoot { get; private set; }

        public static void ApplyIfNeeded()
        {
            try
            {
                if (KmhDataMeta.AppliedDefaultsRevision >= Revision) return;

                if (KmhDataMeta.IsFreshInstall)
                {
                    // Fresh servers already generated configs with the new defaults - just stamp.
                    KmhDataMeta.StampDefaultsRevision(Revision);
                    return;
                }

                Upgrade(KmhDataPaths.WorldConfigFile, "World.json", new (string key, JToken oldDef, JToken newDef)[]
                {
                    ("AutoRollEvents",        false,       true),
                    ("EventRollEveryMinutes", 360,         240),
                    ("EventRollChance",       0.5,         0.4),
                    ("AutoGenerateQuests",    false,       true),
                    ("MaxActiveAutoQuests",   1,           2),
                    ("AllowedEventTypes",
                        new JArray("tax_holiday", "market_boom", "market_crash", "resource_shortage",
                                   "double_worker_xp", "house_stipend", "bounty_target"),
                        new JArray("tax_holiday", "market_boom", "market_crash", "resource_shortage",
                                   "double_worker_xp", "house_stipend", "bounty_target", "world_weather")),
                });
                Upgrade(KmhDataPaths.TransportConfigFile, "Transport.json", new (string key, JToken oldDef, JToken newDef)[]
                {
                    ("EnableKmhApiTransport", false,       true),
                    ("BindAddress",           "127.0.0.1", "0.0.0.0"),
                });
                // Rev 2: a legacy server the earlier build backfilled to Standard moves to the recommended Balanced.
                // A server whose owner explicitly picked any non-Standard mode is left as-is (AtOldDefault(Standard) fails).
                Upgrade(KmhDataPaths.EconomyConfigFile, "Economy.json", new (string key, JToken oldDef, JToken newDef)[]
                {
                    ("EconomyMode", "Standard", "Balanced"),
                });

                Features.Transport.TransportConfig.Reload();
                Features.World.WorldConfig.Reload();
                Features.Economy.EconomyConfig.Reload();
                KmhDataMeta.StampDefaultsRevision(Revision);
                RanThisBoot = true;

                ServerLog.Info($"Defaults upgrade: v1.2.0 recommended defaults applied " +
                               $"({Changed.Count} changed, {Preserved.Count} kept as owner-set). This runs once.");
                foreach (string c in Changed)   ServerLog.Info($"Defaults upgrade: changed   {c}");
                foreach (string p in Preserved) ServerLog.Info($"Defaults upgrade: preserved {p} (owner-set)");
            }
            catch (Exception ex) { ServerLog.Warn($"Defaults upgrade skipped: {ex.Message}"); }
        }

        // Flip each key that still holds its old stock default; leave (and record) everything else.
        private static void Upgrade(string path, string label, (string key, JToken oldDef, JToken newDef)[] flips)
        {
            if (!File.Exists(path)) return;
            JObject o = JObject.Parse(File.ReadAllText(path));
            bool dirty = false;
            foreach ((string key, JToken oldDef, JToken newDef) in flips)
            {
                JToken cur = o[key];
                if (cur == null) continue;   // backfill already added the new default
                if (AtOldDefault(cur, oldDef))
                {
                    o[key] = newDef;
                    dirty = true;
                    Changed.Add($"{label} {key}: {cur.ToString(Newtonsoft.Json.Formatting.None)} -> {newDef.ToString(Newtonsoft.Json.Formatting.None)}");
                }
                else if (!JToken.DeepEquals(cur, newDef))
                {
                    Preserved.Add($"{label} {key} = {cur.ToString(Newtonsoft.Json.Formatting.None)}");
                }
            }
            if (dirty) JsonFileStore.Save(path, o);
        }

        private static bool AtOldDefault(JToken cur, JToken oldDef)
        {
            if (cur.Type == JTokenType.Float || oldDef.Type == JTokenType.Float)
            {
                try { return Math.Abs((double)cur - (double)oldDef) < 0.0001; } catch { return false; }
            }
            return JToken.DeepEquals(cur, oldDef);
        }
    }
}
