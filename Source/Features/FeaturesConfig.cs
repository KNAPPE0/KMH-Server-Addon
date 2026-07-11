using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features
{
    // Owner on/off switches for the major KMH systems (all default on). Turning one off blocks its requests server-side
    // and shows it disabled client-side; stored data is untouched, so re-enabling restores everything.
    internal sealed class FeaturesConfig
    {
        public int  SchemaVersion { get; set; } = 1;
        public bool Treasury    { get; set; } = true;
        public bool Marketplace { get; set; } = true;
        public bool Guilds      { get; set; } = true;
        public bool Quests      { get; set; } = true;
        public bool Auctions    { get; set; } = true;
        public bool WantBoard   { get; set; } = true;
        public bool LivingWorld { get; set; } = true;   // world events + global quests
        public bool Standings   { get; set; } = true;   // leaderboards / records boards

        private static FeaturesConfig _current;
        public static FeaturesConfig Current => _current ?? (_current = LoadOrDefault());

        public static FeaturesConfig LoadOrDefault()
            => JsonFileStore.TryLoad(KmhDataPaths.FeaturesConfigFile, out FeaturesConfig cfg) && cfg != null
                ? cfg : new FeaturesConfig();

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.FeaturesConfigFile))
                JsonFileStore.Save(KmhDataPaths.FeaturesConfigFile, new FeaturesConfig());
        }

        public static void Reload() => _current = LoadOrDefault();

        public bool IsEnabled(string feature)
        {
            switch (feature)
            {
                case "treasury":    return Treasury;
                case "marketplace": return Marketplace;
                case "guilds":      return Guilds;
                case "quests":      return Quests;
                case "auctions":    return Auctions;
                case "wantboard":   return WantBoard;
                case "world":       return LivingWorld;
                case "standings":   return Standings;
                default:            return true;
            }
        }

        // Feature a request kind belongs to, or null for kinds that are never gated (handshake, notices, transport,
        // enforcement, linked accounts, item catalog, etc.). Prefix-matched against KmhProtocol.Kind values.
        public static string FeatureForKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            if (kind.StartsWith("kmh.treasury"))    return "treasury";
            if (kind.StartsWith("kmh.marketplace")) return "marketplace";
            if (kind.StartsWith("kmh.guild"))       return "guilds";
            if (kind.StartsWith("kmh.quest"))       return "quests";
            if (kind.StartsWith("kmh.auction"))     return "auctions";
            if (kind.StartsWith("kmh.want"))        return "wantboard";
            if (kind.StartsWith("kmh.world"))       return "world";
            if (kind.StartsWith("kmh.player_stats")
             || kind.StartsWith("kmh.colonist")
             || kind.StartsWith("kmh.season"))      return "standings";
            return null;
        }

        // Names of the disabled features, for the handshake so the client can show them as off.
        public List<string> DisabledList()
        {
            var d = new List<string>();
            if (!Treasury)    d.Add("treasury");
            if (!Marketplace) d.Add("marketplace");
            if (!Guilds)      d.Add("guilds");
            if (!Quests)      d.Add("quests");
            if (!Auctions)    d.Add("auctions");
            if (!WantBoard)   d.Add("wantboard");
            if (!LivingWorld) d.Add("world");
            if (!Standings)   d.Add("standings");
            return d;
        }
    }
}
