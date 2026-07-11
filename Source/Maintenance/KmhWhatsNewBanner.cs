using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Shown once after an update. Main.cs gates this by the saved build stamp.
    internal static class KmhWhatsNewBanner
    {
        private static readonly string[] Highlights =
        {
            "v1.2.0 turns on the living world by default and hardens the economy.",
            "",
            "IMPORTANT - update EVERY player's KMH-Patch mod to 1.2.0. The wire protocol advanced to v2 because deposit rules changed; older clients now show KMH as disabled with a clear version-mismatch notice instead of half-working, so no one loses items.",
            "",
            "Living world (new defaults - a one-time migration applies them and prints a notice below):",
            "  - World events auto-roll (~one per 10h) and global quests auto-generate (up to 2 active), announced in-game + Discord. Quieter server: Config/World.json AutoRollEvents/AutoGenerateQuests=false.",
            "  - Global weather: events can now blanket every colony with a real GameCondition (aurora/eclipse/cold snap/heat wave by default; add any def via World.json WeatherConditionDefs). 12 stock quest templates across hunt/build/deliver.",
            "  - KMH API transport now on by default, public-ready: auth, caps, throttling and limits required out of the box. Forward TCP 5099 for remote players; verify with 'kmh transport-test'.",
            "",
            "Economy integrity:",
            "  - Balanced is the recommended default for new servers. Treasury silver fees (Balanced 1%/1%, Hardcore 2%/2%; item deposits/withdraws are free) now feed the house pool, the same sink as marketplace tax - a deposit fee only lands when the deposit durably commits.",
            "  - Deposits now PREFLIGHT: the server approves (cooldown/access/caps/item-safety) BEFORE the client removes any goods, so a rejected deposit never even touches your colony. If anything still slips through after removal it's parked in Recovery, never dropped. Unsafe items can't sneak through the compact deposit path.",
            "  - Site output catalog expanded: meats, leathers, wools, meals, raw crops, stone blocks and more safe stackables (incl. modded, by family) are now offerable by default, still tier-gated with Tier 4 (gear/tech/genes/relics) blocked. Off-map KMH value is shown per player in 'kmh audit-player' (raid/threat scaling of it is still deferred).",
            "  - Optional auto-reset of a player's personal treasury when they start a new save (Config/Economy.json: ResetEconomyOnNewSave). Stops the deposit -> reset -> repeat silver farm. Off by default; backs up first.",
            "  - Custom sites: build cost and cycle time now also scale with amount/cycle, so a modified client can't under-report an item's value to buy a cheap, fast, high-output site. Tune in Config/Sites.json (MinBuildCostPerUnit, CycleMinutesPerRewardUnit).",
            "",
            "Also:",
            "  - Guild invites: officers pick from a player list (online or offline - offline invites persist), invitees get notified in-game or on next connect, and accept/decline lives in the Guild Hall.",
            "  - Systems you turn off in Config/Features.json now read clearly as 'disabled by server' on the client dashboard instead of appearing to load forever.",
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