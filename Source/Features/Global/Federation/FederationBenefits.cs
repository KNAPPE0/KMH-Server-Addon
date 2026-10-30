using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Global.Guilds;

namespace KMHServerAddon.Features.Global.Federation
{
    // Read by the site cycle under its own lock, so this is a snapshot rebuilt outside every lock, not a lookup.
    internal static class FederationBenefits
    {
        private sealed class Entry
        {
            public int  Level;
            public long ViewReadUtcTicks;
        }

        private static volatile Dictionary<string, Entry> _byGuild = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // Never call under another store's lock.
        public static void Rebuild()
        {
            var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (FederatedGuildLink l in FederatedGuildStore.All())
            {
                if (l.State != FederatedLinkState.Active || !l.FederationActive || l.View?.Federation == null) continue;
                if (!string.Equals(l.Incarnation, Features.Guilds.GuildStore.IncarnationOf(l.LocalGuild), StringComparison.Ordinal)) continue;
                next[l.LocalGuild] = new Entry { Level = Math.Max(1, l.View.Federation.Level), ViewReadUtcTicks = l.ViewReadUtcTicks };
            }
            _byGuild = next;
        }

        public static int LevelFor(string guild)
        {
            if (string.IsNullOrEmpty(guild) || !FederationConfig.Current.Benefits) return 0;
            if (!FeaturesConfig.Current.GuildFederation) return 0;
            if (!_byGuild.TryGetValue(guild, out Entry e)) return 0;
            if (DateTime.UtcNow.Ticks - e.ViewReadUtcTicks > FederationService.BenefitGrace.Ticks) return 0;
            return e.Level;
        }

        public static double SiteOutputFactor(string guild)
        {
            int level = LevelFor(guild);
            if (level <= 0) return 1.0;
            double pct = Math.Min(FederationRules.SiteOutputPercent(level), FederationConfig.Current.MaxSiteOutputBonusPercent);
            return 1.0 + Math.Max(0, pct) / 100.0;
        }

        public static double RoadworksFactor(string guild)
        {
            int level = LevelFor(guild);
            if (level <= 0) return 1.0;
            double pct = Math.Min(FederationRules.RoadworksPercent(level), FederationConfig.Current.MaxRoadworksBonusPercent);
            return 1.0 + Math.Max(0, pct) / 100.0;
        }

        public static List<object> Describe(string guild, int level)
        {
            bool live = LevelFor(guild) > 0;
            FederationConfig cfg = FederationConfig.Current;
            var rows = new List<object>();
            foreach ((string key, int at, double value) in FederationRules.BenefitTable())
            {
                double v = value;
                if (key == "site_output") v = Math.Min(value, cfg.MaxSiteOutputBonusPercent);
                if (key == "roadworks")   v = Math.Min(value, cfg.MaxRoadworksBonusPercent);
                bool reached = level >= at;
                bool applies = key == "extra_objective" || key == "tag" || key == "renowned" ? reached : reached && live && v > 0;
                rows.Add(new { key, level = at, active = applies, value = v });
            }
            return rows;
        }

        internal static void ResetForTest() => _byGuild = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }
}
