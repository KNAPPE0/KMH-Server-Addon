using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features
{
    // Turning a feature off blocks its requests but never touches its stored data, so re-enabling restores everything.
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

        // Off makes KMH a safe place to hide wealth from the storyteller; the maths itself runs client-side.
        public bool Wealth      { get; set; } = true;
        public bool Mail        { get; set; } = true;   // self-hosted player-to-player mail
        public bool Chat        { get; set; } = true;   // KMH live chat channels

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
                case "wealth":      return Wealth;
                case "mail":        return Mail;
                case "chat":        return Chat;
                default:            return true;
            }
        }

        // Null means the kind is never gated, which is why handshake and transport traffic falls through here.
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
             || kind.StartsWith("kmh.records")      // colonist records board
             || kind.StartsWith("kmh.archive")      // season archive
             || kind.StartsWith("kmh.season"))      return "standings";
            if (kind.StartsWith("kmh.mail"))        return "mail";
            if (kind.StartsWith("kmh.chat"))        return "chat";
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
            if (!Wealth)      d.Add("wealth");
            if (!Mail)        d.Add("mail");
            if (!Chat)        d.Add("chat");
            return d;
        }
    }
}
