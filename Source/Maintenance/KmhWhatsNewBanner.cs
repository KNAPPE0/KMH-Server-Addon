using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Shown once after an update. Main.cs gates this by the saved build stamp.
    internal static class KmhWhatsNewBanner
    {
        internal static string[] LinesForTest => LinesFor("1.3.0.0");

        private static readonly string[] PatchHighlights =
        {
            "New in v1.3.1.1:",
            "  - Enforcing servers show up in RimWorld Together's server browser again.",
            "  - A mod list Colony Nexus kept from before a reinstall is replaced on the next heartbeat.",
        };

        private static readonly string[] SameSeriesNote =
        {
            "Your configs and data are unchanged, and every v1.3.1 client still connects.",
            "",
        };

        private static readonly string[] Highlights =
        {
            "v1.3.1 'Connected Worlds' brings Guild Federation, and puts this server on the Colony Nexus browser if you want it there.",
            "",
            "READ BEFORE YOU UPGRADE - two things change for everyone:",
            "",
            "  1. Every player MUST update. The wire protocol moves to v3, so v1.3.0 and v1.2.x clients are refused with an explanation rather than let in. Update the server and the Colony Nexus mod together.",
            "  2. Your tunable configs are REGENERATED from v1.3.1 defaults, not migrated - values you set are replaced. Your originals are copied to a 'premigration-config' folder under KMH-Data-Backups first, and boot refuses to migrate at all if that copy cannot be made and verified. Global.json, Staff.json and the Discord config are never regenerated.",
            "",
            "New in v1.3.1:",
            "  - Guild Federation (on by default): guilds on different servers join one federation, with members, a leader, weekly objectives, renown levels and small local bonuses. Nothing - no silver, items or resources - moves between servers. To work it gives this server a private Colony Nexus Global identity that is never listed; 'kmh setup info' shows what is sent, and 'kmh global disable' or Features.json GuildFederation=false turns it off.",
            "  - The Connected Treasury (moving silver between servers) is OFF in this release, whatever your old 'Connected' setting was.",
            "  - Colony Nexus Global: run 'kmh global setup' to link an owner account and choose whether the browser lists this server. Listing is your choice; 'kmh global disable' takes you back out. Your account approves the link in a browser; the server proves itself with a signing key it generates and never sends.",
            "  - A new signing key without a new identity: 'kmh global rotate' replaces a key that still works, 'kmh global recover' one you lost or that Global refuses, and 'kmh global rebind <code>' is the operator-assisted path. SETUP.txt explains each.",
            "  - Settings are smaller and grouped: about 230 top-level values became about 127. Config/README.txt explains what each file does, and 'kmh config' shows every setting with its current value.",
            "  - Quieter logs: enforcement and media pushes stop retrying a client that has gone, instead of filling the console with one line per chunk.",
            "",
            "From v1.3.0:",
            "",
            "Frontier:",
            "  - Sites now have an archetype (Farmland, Quarry, Woodland, Ranch, Roadworks, Custom) that names the site and decides which colonist skill its work uses. Existing sites read as Custom and keep producing exactly what they produced.",
            "  - Site buildings, storage and condition: production raises output, housing raises the worker cap, storage holds output for bulk collection. A damaged site produces less but is never worthless, and nothing lowers condition on its own - no decay, no upkeep chore.",
            "  - Roadworks builds persistent roads: Trail, Road, then Highway, unlocked by site tier. Crossing existing road costs nothing, and cancelling returns the silver for whatever is still unbuilt. Turn it off with Sites.json AllowRoadworks.",
            "  - Frontier Operations: a server-side director opens objectives on the map - the first restores a derelict ruin - and hands the location to whoever earned it. The reward is capped to what the House Pool can back, and the operation does not open if that falls below the configured minimum, so nothing is minted. On by default and limited by its spawn budget, cooldowns and location caps; tune it in Frontier.json and inspect with 'kmh frontier'.",
            "",
            "Communications:",
            "  - Colony Nexus chat: server-wide, per-guild and direct-message channels, with history on reconnect, per-user rate limits and a server-side log of every message.",
            "  - Player mail: write to another player and attach silver, items or gear. Attachments are held by the server, recallable while unread, and returned automatically if never claimed.",
            "",
            "Economy:",
            "  - Off-map Colony Nexus value (treasury, guild vault, listings, auctions, wants, bounties, mail) now counts toward RimWorld raid scaling on v1.3.0+ clients, so parking wealth in Colony Nexus is not a safe haven. Set Config/Features.json Wealth=false if you would rather it were.",
            "  - Standings statistics that always read zero now record what players actually did: worker XP belongs to the colonist who earned it, 'Sites' means what you control now, and Frontier captures are history that joins the season archive.",
            "",
            "Owners:",
            "  - The download is now a folder rather than a single .exe - extract all of it beside your RWT server. Renaming it into a panel's fixed GameServer.exe slot still works.",
            "  - New commands: 'kmh roadworks', 'kmh frontier', and 'kmh worldquest deliver' to credit a delivery lost to a disconnect.",
        };

        // A server already on this series saw the v1.3.1 upgrade notes; its configs are not regenerated again.
        internal static string[] LinesFor(string fromBuild)
        {
            var cur = typeof(KmhWhatsNewBanner).Assembly.GetName().Version;
            bool sameSeries = System.Version.TryParse(fromBuild, out var v)
                && v.Major == cur.Major && v.Minor == cur.Minor && v.Build == cur.Build;
            return sameSeries ? SameSeriesNote.Concat(PatchHighlights).ToArray()
                              : PatchHighlights.Concat(new[] { "" }).Concat(Highlights).ToArray();
        }

        internal static string Title(string fromBuild) => string.IsNullOrWhiteSpace(fromBuild)
            ? $"  {Constants.DisplayName} updated to {KmhVersion.Display}. What's new:"
            : $"  {Constants.DisplayName} updated from {fromBuild} to {KmhVersion.Display}. What's new:";

        public static void Print(string fromBuild)
        {
            ServerLog.Info("======================================================================");
            ServerLog.Success(Title(fromBuild));
            ServerLog.Info("----------------------------------------------------------------------");

            foreach (string line in LinesFor(fromBuild))
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