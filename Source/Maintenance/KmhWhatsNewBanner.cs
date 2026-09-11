using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Shown once after an update. Main.cs gates this by the saved build stamp.
    internal static class KmhWhatsNewBanner
    {
        private static readonly string[] Highlights =
        {
            "v1.3.0 grows the world on its own and puts the frontier in players' hands.",
            "",
            "Your players do NOT all have to update. The wire protocol stays at v2, so a v1.2.x client still connects and plays - the new features simply stay hidden until they update their KMH-Patch mod.",
            "",
            "Frontier:",
            "  - Sites now have an archetype (Farmland, Quarry, Woodland, Ranch, Roadworks, Custom) that names the site and decides which colonist skill its work uses. Existing sites read as Custom and keep producing exactly what they produced.",
            "  - Site buildings, storage and condition: production raises output, housing raises the worker cap, storage holds output for bulk collection. A damaged site produces less but is never worthless, and nothing lowers condition on its own - no decay, no upkeep chore.",
            "  - Roadworks builds persistent roads: Trail, Road, then Highway, unlocked by site tier. Crossing existing road costs nothing, and cancelling returns the silver for whatever is still unbuilt. Turn it off with Sites.json AllowRoadworks.",
            "  - Frontier Operations: a server-side director opens objectives on the map - the first restores a derelict ruin - and hands the location to whoever earned it. The reward is capped to what the House Pool can back, and the operation does not open if that falls below the configured minimum, so nothing is minted. On by default and limited by its spawn budget, cooldowns and location caps; tune it in Frontier.json and inspect with 'kmh frontier'.",
            "",
            "Communications:",
            "  - KMH chat: server-wide, per-guild and direct-message channels, with history on reconnect, per-user rate limits and a server-side log of every message.",
            "  - Player mail: write to another player and attach silver, items or gear. Attachments are held by the server, recallable while unread, and returned automatically if never claimed.",
            "",
            "Economy:",
            "  - Off-map KMH value (treasury, guild vault, listings, auctions, wants, bounties, mail) now counts toward RimWorld raid scaling on v1.3.0+ clients, so parking wealth in KMH is not a safe haven. Set Config/Features.json Wealth=false if you would rather it were.",
            "  - Standings statistics that always read zero now record what players actually did: worker XP belongs to the colonist who earned it, 'Sites' means what you control now, and Frontier captures are history that joins the season archive.",
            "",
            "Owners:",
            "  - The download is now a folder rather than a single .exe - extract all of it beside your RWT server. Renaming it into a panel's fixed GameServer.exe slot still works.",
            "  - New commands: 'kmh roadworks', 'kmh frontier', and 'kmh worldquest deliver' to credit a delivery lost to a disconnect.",
        };

        public static void Print(string fromBuild)
        {
            string toBuild = KmhProtocol.BuildVersion;

            ServerLog.Info("======================================================================");
            ServerLog.Success(string.IsNullOrWhiteSpace(fromBuild)
                ? $"  KMH Server Addon updated to {toBuild}. What's new:"
                : $"  KMH Server Addon updated from {fromBuild} to {toBuild}. What's new:");
            ServerLog.Info("----------------------------------------------------------------------");

            foreach (string line in Highlights)
            {
                ServerLog.Info(string.IsNullOrEmpty(line) ? "" : "  " + line);
            }

            ServerLog.Info("----------------------------------------------------------------------");
            ServerLog.Info("  Full notes: https://github.com/KNAPPE0/KMH-Server-Addon");
            ServerLog.Info("  This message only shows once per update.");
            ServerLog.Info("======================================================================");
        }
    }
}