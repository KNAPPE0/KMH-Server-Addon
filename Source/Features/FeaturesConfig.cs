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
        public bool Mail        { get; set; } = true;
        public bool Chat        { get; set; } = true;
        // Moves nothing between servers, so it is on by default.
        public bool GuildFederation   { get; set; } = true;
        // Off by default: moving real silver between servers is the one thing here that switching back off cannot undo.
        public bool ConnectedTreasury { get; set; } = false;
        // Off hides the Report button and refuses new reports; stored ones stay.
        public bool Reports           { get; set; } = true;

        private static FeaturesConfig _current;
        public static FeaturesConfig Current => _current ?? (_current = LoadOrDefault());

        // Connected Treasury is not shipped: the switch exists for stored state, and a hand edit turning it on is refused
        // so it cannot expose silver moves between servers.
        internal const bool ConnectedTreasuryShips = false;

        public static FeaturesConfig LoadOrDefault()
        {
            FeaturesConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.FeaturesConfigFile, out FeaturesConfig loaded) && loaded != null
                ? loaded : new FeaturesConfig();
            if (cfg.ConnectedTreasury && !ConnectedTreasuryShips)
            {
                cfg.ConnectedTreasury = false;
                Diagnostics.ServerLog.Warn("Features: ConnectedTreasury=true is ignored - Connected Treasury is not part of v1.3.1 and stays off.");
            }
            return cfg;
        }

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
                case "federation":  return GuildFederation;
                case "connected":   return ConnectedTreasury;
                case "reports":     return Reports;
                default:            return true;
            }
        }

        public static string[] FeaturesForKind(string kind)
        {
            string main = FeatureForKind(kind);
            if (main == null) return null;
            // A federation acts for a guild, and a guild treasury for a federation, so each also needs the one below it.
            if (main == "federation") return new[] { "guilds", "federation" };
            if (main == "connected" && kind.StartsWith("kmh.guild.connected")) return new[] { "guilds", "federation", "connected" };
            return new[] { main };
        }

        public bool AllEnabled(string[] features)
        {
            if (features == null) return true;
            foreach (string f in features) if (!IsEnabled(f)) return false;
            return true;
        }

        public string FirstDisabled(string[] features)
        {
            if (features == null) return null;
            foreach (string f in features) if (!IsEnabled(f)) return f;
            return null;
        }

        // Null means never gated, which is why handshake and transport traffic falls through here.
        public static string FeatureForKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            if (kind.StartsWith("kmh.treasury"))    return "treasury";
            if (kind.StartsWith("kmh.marketplace")) return "marketplace";
            // Before the plain guild rule. The guild treasury needs guilds and federation too; see FeaturesForKind.
            if (kind.StartsWith("kmh.guild.connected")) return "connected";
            if (kind.StartsWith("kmh.guild"))       return "guilds";
            if (kind.StartsWith("kmh.federation"))  return "federation";
            if (kind.StartsWith("kmh.quest"))       return "quests";
            if (kind.StartsWith("kmh.auction"))     return "auctions";
            if (kind.StartsWith("kmh.want"))        return "wantboard";
            if (kind.StartsWith("kmh.world"))       return "world";
            if (kind.StartsWith("kmh.player_stats")
             || kind.StartsWith("kmh.colonist")
             || kind.StartsWith("kmh.records")      // colonist records board
             || kind.StartsWith("kmh.archive")      // season archive
             || kind.StartsWith("kmh.season")
             || kind.StartsWith("kmh.leaderboards")) return "standings";
            if (kind.StartsWith("kmh.mail"))        return "mail";
            if (kind.StartsWith("kmh.chat"))        return "chat";
            if (kind.StartsWith("kmh.connected"))   return "connected";
            if (kind.StartsWith("kmh.report"))      return "reports";
            return null;
        }

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
            if (!GuildFederation)   d.Add("federation");
            // An older client hides both of its Connected buttons on this one name, which is right for it.
            if (!ConnectedTreasury) d.Add("connected");
            if (!Reports)           d.Add("reports");
            return d;
        }
    }
}
