using System;
using System.Collections.Generic;
using KMHServerAddon.Features.PlayerStats.Dto;

namespace KMHServerAddon.Features.Leaderboards
{
    internal static class LeaderboardScope
    {
        public const string Player = "player";
        public const string Guild  = "guild";
    }

    // Verified: this server measured it. Reported: the player's own game sent it and the server only clamped it.
    internal static class LeaderboardSource
    {
        public const string Verified = "verified";
        public const string Reported = "reported";
    }

    internal static class LeaderboardUnit
    {
        public const string Silver = "silver";
        public const string Count  = "count";
        public const string Points = "points";
        public const string Hours  = "hours";   // the value is seconds, shown as hours
        public const string Days   = "days";
    }

    internal sealed class LeaderboardCategory
    {
        public string Key, Title, Group, Unit, Scope, Source, Description;
        // "contract|contracts": what a count is of, so "4" on a podium reads as "4 members".
        public string Noun = "";
        // Player boards read a stat row; guild and reputation boards are built from their own stores by the engine.
        public Func<PlayerLeaderboardEntry, long> PlayerValue;

        public string Format(long value)
        {
            switch (Unit)
            {
                case LeaderboardUnit.Silver: return Util.SilverFmt.Format(value);
                case LeaderboardUnit.Hours:
                    if (value < 3600) return Math.Max(1, value / 60) + " min";
                    return (value / 3600.0).ToString(value < 36000 ? "0.0" : "N0", System.Globalization.CultureInfo.InvariantCulture) + " h";
                case LeaderboardUnit.Days:   return value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + (value == 1 ? " day" : " days");
                case LeaderboardUnit.Points: return value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " pts";
                default:
                    string n = value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                    string[] noun = (Noun ?? "").Split('|');
                    return noun[0].Length == 0 ? n : n + " " + (value == 1 || noun.Length < 2 ? noun[0] : noun[1]);
            }
        }
    }

    // Keys are public URLs and Discord choices, so they never change once shipped.
    internal static class LeaderboardCatalog
    {
        public const string Reputation = "reputation", GuildMembers = "guild-members", GuildTreasury = "guild-treasury";

        public static readonly IReadOnlyList<LeaderboardCategory> All = new List<LeaderboardCategory>
        {
            P("economy",      "Economy score",       "Economy",   LeaderboardUnit.Points, LeaderboardSource.Verified, e => e.EconomyScore,
              "Silver donated, half of trade earnings, 100 per contract, 50 per site built and a tenth of worker XP."),
            P("donated",      "Silver donated",      "Economy",   LeaderboardUnit.Silver, LeaderboardSource.Verified, e => e.SilverDonated,
              "Silver given to guilds, net of withdrawals."),
            P("vault",        "Vault value",         "Economy",   LeaderboardUnit.Silver, LeaderboardSource.Verified, e => e.KmhWealth,
              "What the player holds in Colony Nexus storage on this server, valued by the server."),
            P("trade",        "Trade earnings",      "Trade",     LeaderboardUnit.Silver, LeaderboardSource.Verified, e => e.SalesEarned,
              "Silver earned from marketplace and auction sales."),
            P("largest-sale", "Largest sale",        "Trade",     LeaderboardUnit.Silver, LeaderboardSource.Verified, e => e.LargestSale,
              "The single most valuable sale."),
            P("purchases",    "Purchases",           "Trade",     LeaderboardUnit.Silver, LeaderboardSource.Verified, e => e.PurchasesSpent,
              "Silver spent buying from other players."),
            P("contracts",    "Contracts completed", "Contracts", LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.QuestsCompleted,
              "Bounties, deliveries, hunts and defences completed.", "contract|contracts"),
            P("streak",       "Contract streak",     "Contracts", LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.ContractStreak,
              "Contracts completed in a row since the last failure.", "in a row"),
            P("posted",       "Contracts posted",    "Contracts", LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.QuestsPosted,
              "Contracts offered to other players.", "contract|contracts"),
            P("sites-built",  "Sites built",         "Sites",     LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.SitesBuilt,
              "New sites established.", "site|sites"),
            P("sites-held",   "Sites held",          "Sites",     LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.SitesOwned,
              "Sites the player owns right now.", "site|sites"),
            P("site-output",  "Site production",     "Sites",     LeaderboardUnit.Silver, LeaderboardSource.Verified, e => e.SiteSilverProduced,
              "Silver produced by the player's sites."),
            P("frontier",     "Frontier captures",   "Sites",     LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.FrontierCaptures,
              "Sites captured on the frontier.", "capture|captures"),
            P("worker-xp",    "Worker XP",           "Sites",     LeaderboardUnit.Count,  LeaderboardSource.Verified, e => e.WorkerXp,
              "Experience the player's colonists earned working sites.", "XP"),
            P("active",       "Active time",         "Activity",  LeaderboardUnit.Hours,  LeaderboardSource.Verified, e => e.ActiveSeconds,
              "Time spent actively playing on this server."),
            new LeaderboardCategory { Key = Reputation, Title = "Reputation", Group = "Activity", Unit = LeaderboardUnit.Points,
              Scope = LeaderboardScope.Player, Source = LeaderboardSource.Verified,
              Description = "Contracts honoured, less contracts rejected or abandoned." },
            P("colony-wealth","Colony wealth",       "Colony",    LeaderboardUnit.Silver, LeaderboardSource.Reported, e => e.Wealth,
              "The colony's wealth as the player's game reports it."),
            P("kills",        "Kills",               "Colony",    LeaderboardUnit.Count,  LeaderboardSource.Reported, e => e.Kills,
              "Kills by the colony's colonists, hunted animals included, as its game reports.", "kill|kills"),
            P("raids",        "Raids survived",      "Colony",    LeaderboardUnit.Count,  LeaderboardSource.Reported, e => e.RaidsSurvived,
              "Raids the colony has survived, as its game reports.", "raid|raids"),
            P("colony-age",   "Colony age",          "Colony",    LeaderboardUnit.Days,   LeaderboardSource.Reported, e => e.ColonyAgeDays,
              "Days since the colony was founded, as its game reports."),
            new LeaderboardCategory { Key = GuildMembers, Title = "Guild members", Group = "Guilds", Unit = LeaderboardUnit.Count,
              Scope = LeaderboardScope.Guild, Source = LeaderboardSource.Verified, Description = "Members in each guild.", Noun = "member|members" },
            new LeaderboardCategory { Key = GuildTreasury, Title = "Guild treasury", Group = "Guilds", Unit = LeaderboardUnit.Silver,
              Scope = LeaderboardScope.Guild, Source = LeaderboardSource.Verified, Description = "Silver in each guild's vault." },
        };

        private static LeaderboardCategory P(string key, string title, string group, string unit, string source,
                                             Func<PlayerLeaderboardEntry, long> value, string description, string noun = "")
            => new LeaderboardCategory { Key = key, Title = title, Group = group, Unit = unit, Scope = LeaderboardScope.Player,
                                         Source = source, PlayerValue = value, Description = description, Noun = noun };

        public static LeaderboardCategory Find(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            foreach (LeaderboardCategory c in All)
                if (string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }
}
